// Copyright (c) 2026 Frame Bridge contributors.
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using System.Reflection;
using System.Text.Json.Serialization;

namespace BabbleFrameBridge.Shared;

public sealed record ModuleRelease(
    string Id,
    string Version,
    string ModuleId,
    string DllFileName,
    DateTimeOffset InstalledAt,
    string Sha256,
    string DirectoryPath,
    string ModuleName,
    ModuleKind Kind = ModuleKind.BabbleHeadsetRuntime,
    bool SupportsHeadset = false,
    string? AssemblyName = null)
{
    [JsonIgnore]
    public bool IsAvailable => File.Exists(Path.Combine(DirectoryPath, DllFileName));
}

/// <summary>
/// Stages packages without loading code, then switches the active release with an atomic
/// manifest replacement. Existing package directories are never edited during an update.
/// </summary>
public sealed class ModuleStore
{
    public const string BaballoniaModuleId = "360b014b-b57b-450f-8f12-9904618ff370";
    public const string BaballoniaDll = "VRCFaceTracking.Baballonia.dll";
    private const long MaxArchiveBytes = 256L * 1024 * 1024;
    private const long MaxExpandedBytes = 512L * 1024 * 1024;
    private const int MaxEntries = 4096;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly object _gate = new();
    private readonly string _storeRoot;
    private readonly string _releasesRoot;
    private readonly string _statePath;
    private StoreState _state;

    public ModuleStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _storeRoot = Path.Combine(Path.GetFullPath(root), "modules");
        _releasesRoot = Path.Combine(_storeRoot, "releases");
        _statePath = Path.Combine(_storeRoot, "module-state.json");
        Directory.CreateDirectory(_releasesRoot);
        _state = ReadState();
        CleanupRemovedReleases();
    }

    public ModuleRelease? Current { get { lock (_gate) return ReadRelease(_state.Current); } }
    public ModuleRelease? Previous { get { lock (_gate) return ReadRelease(_state.Previous); } }
    public IReadOnlyList<ModuleRelease> Installed { get { lock (_gate) return InstalledIds().Select(id => ReadRelease(id)!).OrderBy(x => x.Kind).ThenByDescending(x => x.InstalledAt).ToArray(); } }

    private string[] InstalledIds() => (_state.Installed ?? []).Concat(new[] { _state.Current, _state.Previous }.OfType<string>()).Distinct().ToArray();

    public void Register(ModuleRelease release)
    {
        lock (_gate)
        {
            if (ReadRelease(release.Id) != release) throw new InvalidDataException("The module does not belong to this library.");
            var next = new StoreState(2, _state.Current, _state.Previous, InstalledIds().Append(release.Id).Distinct().ToArray());
            WriteAtomic(_statePath, next); _state = next;
        }
    }

    /// <summary>Babble's two transports share a DLL, but keep their mutable configuration in separate profiles.</summary>
    public ModuleRelease CreateCompanion(ModuleRelease release)
    {
        lock (_gate)
        {
            if (ReadRelease(release.Id) != release || release.ModuleId != BaballoniaModuleId || !release.SupportsHeadset || !release.IsAvailable)
                throw new InvalidDataException("This package does not support both Babble inputs.");
            var id = Guid.NewGuid().ToString("N");
            var source = Path.Combine(_releasesRoot, release.Id, "payload");
            var target = Path.Combine(_releasesRoot, id, "payload");
            try
            {
                foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                {
                    var destination = GetContainedPath(target, Path.GetRelativePath(source, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(file, destination);
                }
                var kind = release.Kind == ModuleKind.BabbleHeadsetRuntime ? ModuleKind.BaballoniaDesktop : ModuleKind.BabbleHeadsetRuntime;
                var companion = release with { Id = id, Kind = kind, DirectoryPath = Path.Combine(target, Path.GetRelativePath(source, release.DirectoryPath)) };
                WriteAtomic(Path.Combine(_releasesRoot, id, "release.json"), companion, false);
                return companion;
            }
            catch { if (Directory.Exists(target)) Directory.Delete(Path.GetDirectoryName(target)!, true); throw; }
        }
    }

    public void MigrateLegacyProfiles(TrackingInputSource source)
    {
        lock (_gate)
        {
            if (_state.Schema != 1) return;
            var ids = InstalledIds();
            foreach (var id in ids)
            {
                var release = ReadRelease(id)!;
                var supports = release.IsAvailable && HasHeadsetSupport(Path.Combine(release.DirectoryPath, release.DllFileName));
                release = release with { Kind = source == TrackingInputSource.BaballoniaDesktop ? ModuleKind.BaballoniaDesktop : ModuleKind.BabbleHeadsetRuntime, SupportsHeadset = supports };
                WriteAtomic(Path.Combine(_releasesRoot, id, "release.json"), release);
            }
            var next = new StoreState(2, _state.Current, _state.Previous, ids);
            WriteAtomic(_statePath, next); _state = next;
            if (Current is { SupportsHeadset: true, IsAvailable: true } current)
                Register(CreateCompanion(current));
        }
    }

    /// <summary>Stages a DLL or ZIP without loading module code or changing the selected module.</summary>
    public ModuleRelease Prepare(string path, ModuleKind? requestedKind = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (requestedKind is { } requested) _ = ModuleCatalog.Get(requested);
        var bareDll = string.Equals(Path.GetExtension(path), ".dll", StringComparison.OrdinalIgnoreCase);
        if (!bareDll && !string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose a compiled module DLL or a module ZIP.");
        using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (source.Length is <= 0 or > MaxArchiveBytes)
            throw new InvalidDataException("The module file must be between 1 byte and 256 MB.");
        var hash = Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant();
        source.Position = 0;
        var id = Guid.NewGuid().ToString("N");
        var releaseRoot = Path.Combine(_releasesRoot, id);
        var staging = Path.Combine(_storeRoot, ".staging-" + id);
        var payload = Path.Combine(staging, "payload");
        Directory.CreateDirectory(payload);
        try
        {
            ModuleManifest? manifest = null;
            string? primaryPath = null;
            if (bareDll)
            {
                var filename = ValidateDllFileName(Path.GetFileName(path));
                primaryPath = GetContainedPath(payload, filename);
                using var output = new FileStream(primaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                CopyBounded(source, output, source.Length);
            }
            else
            {
                using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
                if (archive.Entries.Count is 0 or > MaxEntries)
                    throw new InvalidDataException("The module ZIP has no files or too many entries.");
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var files = new List<(ZipArchiveEntry Entry, string Relative)>();
                long expanded = 0;
                foreach (var entry in archive.Entries)
                {
                    var relative = ValidateArchivePath(entry);
                    if (!paths.Add(relative.TrimEnd('/')))
                        throw new InvalidDataException("The module ZIP contains duplicate paths.");
                    if (entry.Length > MaxExpandedBytes - expanded)
                        throw new InvalidDataException("The module ZIP expands beyond 512 MB.");
                    expanded += entry.Length;
                    if (!relative.EndsWith('/')) files.Add((entry, relative));
                }
                var manifests = files.Where(f => string.Equals(Path.GetFileName(f.Relative), "module.json", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (manifests.Length > 1) throw new InvalidDataException("The module ZIP contains multiple module.json manifests.");
                if (manifests.Length == 1)
                {
                    if (manifests[0].Entry.Length > 1024 * 1024) throw new InvalidDataException("The module manifest is too large.");
                    try
                    {
                        using var input = manifests[0].Entry.Open();
                        manifest = JsonSerializer.Deserialize<ModuleManifest>(input, JsonOptions)
                            ?? throw new InvalidDataException("The module manifest is empty.");
                    }
                    catch (JsonException error) { throw new InvalidDataException("The module manifest is not valid JSON.", error); }
                    var declared = ValidateManifest(manifest);
                    var manifestDirectory = Path.GetDirectoryName(manifests[0].Relative.Replace('/', Path.DirectorySeparatorChar)) ?? "";
                    var expected = Path.Combine(manifestDirectory, manifest.DllFileName!).Replace('\\', '/');
                    var matching = files.FirstOrDefault(f => string.Equals(f.Relative, expected, StringComparison.OrdinalIgnoreCase));
                    if (matching.Entry == null)
                        throw new InvalidDataException(declared.Kind == ModuleKind.VirtualDesktop
                            ? "The Virtual Desktop module DLL is missing. Download the compiled module package, not GitHub's Source code ZIP: https://download.vrdesktop.net/files/VirtualDesktop.VRCFaceTracking.zip"
                            : "The entry DLL named in module.json is missing beside the manifest. Import the compiled package or choose its module DLL directly.");
                    primaryPath = GetContainedPath(payload, matching.Relative);
                }
                foreach (var file in files)
                {
                    var destination = GetContainedPath(payload, file.Relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    using var input = file.Entry.Open();
                    using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    CopyBounded(input, output, file.Entry.Length);
                }
            }
            primaryPath ??= SelectEntryDll(payload);
            var identity = ReadAssemblyIdentity(primaryPath);
            var knownAssembly = ModuleCatalog.All.FirstOrDefault(x => x.AssemblyName == identity.Name);
            ModuleDefinition definition;
            if (manifest != null)
            {
                definition = ValidateManifest(manifest);
                if (definition.Kind != ModuleKind.Custom && definition.AssemblyName != identity.Name
                    || knownAssembly != null && knownAssembly.ModuleId != definition.ModuleId)
                    throw new InvalidDataException("The DLL does not match the module's declared identity.");
            }
            else if (knownAssembly != null)
            {
                definition = knownAssembly;
                var canonical = Path.Combine(Path.GetDirectoryName(primaryPath)!, definition.DllFileName);
                if (!string.Equals(primaryPath, canonical, StringComparison.OrdinalIgnoreCase))
                {
                    if (File.Exists(canonical)) throw new InvalidDataException("The ZIP has conflicting entry DLL names. Select the intended module DLL directly.");
                    File.Move(primaryPath, canonical);
                    primaryPath = canonical;
                }
            }
            else
            {
                var moduleId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("BabbleBridge.Module|" + identity.StableIdentity)))[..32];
                definition = ModuleCatalog.Identify(Guid.ParseExact(moduleId, "N").ToString("D"), Path.GetFileName(primaryPath));
            }
            var supports = definition.ModuleId == BaballoniaModuleId && identity.Name == "VRCFaceTracking.Baballonia" && HasHeadsetSupport(primaryPath);
            var kind = requestedKind ?? (definition.Kind == ModuleKind.BabbleHeadsetRuntime && !supports ? ModuleKind.BaballoniaDesktop : definition.Kind);
            if (requestedKind is { } selected && (selected == ModuleKind.Custom
                    ? definition.Kind != ModuleKind.Custom : ModuleCatalog.Get(selected).ModuleId != definition.ModuleId))
                throw new InvalidDataException("The package does not match the selected module.");
            if (kind == ModuleKind.BabbleHeadsetRuntime && !supports)
                throw new InvalidDataException("This Baballonia package supports desktop input only. Import a package with Babble Frame Runtime support.");
            var version = manifest?.Version ?? identity.Version;
            var name = manifest?.ModuleName ?? (kind == ModuleKind.Custom ? identity.Name : definition.Name);
            var moduleDirectory = Path.GetDirectoryName(primaryPath)!;
            var release = new ModuleRelease(id, version, definition.ModuleId, Path.GetFileName(primaryPath), DateTimeOffset.UtcNow,
                hash, GetContainedPath(Path.Combine(releaseRoot, "payload"), Path.GetRelativePath(payload, moduleDirectory)), name, kind, supports, identity.Name);
            WriteAtomic(Path.Combine(staging, "release.json"), release, keepBackup: false);
            Directory.Move(staging, releaseRoot);
            return release;
        }
        catch
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            throw;
        }
    }

    /// <summary>Atomically activates a prepared release, preserving the current release for rollback.</summary>
    public void Activate(ModuleRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);
        lock (_gate)
        {
            var stored = ReadRelease(release.Id) ?? throw new InvalidDataException("The prepared module no longer exists.");
            if (stored != release) throw new InvalidDataException("The prepared module does not belong to this store.");
            ValidateAssembly(Path.Combine(stored.DirectoryPath, stored.DllFileName), stored.Kind == ModuleKind.Custom ? stored.AssemblyName! : ModuleCatalog.Get(stored.Kind).AssemblyName);
            if (_state.Current == stored.Id) return;
            var next = new StoreState(2, stored.Id, _state.Current, InstalledIds().Append(stored.Id).Distinct().ToArray());
            WriteAtomic(_statePath, next);
            _state = next;
        }
    }

    public ModuleRelease Install(string zipPath)
    {
        var release = Prepare(zipPath);
        Activate(release);
        return release;
    }

    public ModuleRelease Rollback()
    {
        lock (_gate)
        {
            var release = ReadRelease(_state.Previous) ?? throw new InvalidOperationException("There is no previous module to restore.");
            ValidateAssembly(Path.Combine(release.DirectoryPath, release.DllFileName), release.Kind == ModuleKind.Custom ? release.AssemblyName! : ModuleCatalog.Get(release.Kind).AssemblyName);
            var next = new StoreState(2, _state.Previous, _state.Current, InstalledIds());
            WriteAtomic(_statePath, next);
            _state = next;
            return release;
        }
    }

    /// <summary>Removes one imported profile. The caller must stop its worker before removing an active profile.</summary>
    public void Remove(ModuleRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);
        lock (_gate)
        {
            var stored = ReadRelease(release.Id);
            if (stored != release || !InstalledIds().Contains(release.Id, StringComparer.Ordinal))
                throw new InvalidDataException("The module does not belong to this library.");
            var releaseRoot = Path.Combine(_releasesRoot, stored.Id);
            var removedRoot = Path.Combine(_storeRoot, ".removed-" + stored.Id);
            EnsureRemovalTree(releaseRoot);
            EnsureNoReparsePoint(_statePath);
            EnsureNoReparsePoint(_statePath + ".backup");
            if (Directory.Exists(removedRoot) || File.Exists(removedRoot))
                throw new IOException("A previous removal is still being cleaned up. Reopen the app and try again.");
            var next = new StoreState(2, _state.Current == stored.Id ? null : _state.Current,
                _state.Previous == stored.Id ? null : _state.Previous,
                InstalledIds().Where(id => id != stored.Id).ToArray());
            var backupPath = _statePath + ".backup";
            var oldBackup = File.Exists(backupPath) ? File.ReadAllBytes(backupPath) : null;
            var moved = false;
            try
            {
                // Prepare safe recovery before moving any files: if the process stops between
                // the move and main-state commit, this backup already excludes the removed ID.
                WriteAtomic(backupPath, next, keepBackup: false);
                Directory.Move(releaseRoot, removedRoot);
                moved = true;
                WriteAtomic(_statePath, next, keepBackup: false);
                _state = next;
            }
            catch
            {
                if (moved)
                {
                    try { Directory.Move(removedRoot, releaseRoot); }
                    catch (Exception restoreError) when (restoreError is IOException or UnauthorizedAccessException)
                    {
                        // Keep the sanitized backup valid if a concurrent file lock prevents
                        // rollback. Reopening can finish recovery without losing another profile.
                        _state = next;
                        throw new IOException("Module removal was interrupted. Reopen the app to finish recovering the module library.", restoreError);
                    }
                }
                try
                {
                    if (oldBackup != null) WriteAtomicContent(backupPath, stream => stream.Write(oldBackup), false);
                    else if (File.Exists(backupPath)) File.Delete(backupPath);
                }
                catch (Exception restoreError) when (restoreError is IOException or UnauthorizedAccessException)
                { /* The unchanged primary and restored payload still describe the old library. */ }
                throw;
            }
            TryDeleteRemovedRelease(removedRoot, stored.Id);
        }
    }

    private void CleanupRemovedReleases()
    {
        EnsureNoReparsePoint(_storeRoot);
        var installed = InstalledIds();
        foreach (var directory in Directory.EnumerateDirectories(_storeRoot, ".removed-*", SearchOption.TopDirectoryOnly))
        {
            var id = Path.GetFileName(directory)[".removed-".Length..];
            if (Guid.TryParseExact(id, "N", out _) && !installed.Contains(id, StringComparer.Ordinal)
                && !Directory.Exists(Path.Combine(_releasesRoot, id)))
                TryDeleteRemovedRelease(directory, id);
        }
    }

    private void TryDeleteRemovedRelease(string directory, string id)
    {
        try
        {
            EnsureRemovalTree(directory);
            var metadata = JsonSerializer.Deserialize<ModuleRelease>(File.ReadAllText(Path.Combine(directory, "release.json")), JsonOptions);
            if (metadata?.Id != id) return;
            var originalPayload = Path.Combine(_releasesRoot, id, "payload");
            _ = GetContainedPath(originalPayload, Path.GetRelativePath(originalPayload, metadata.DirectoryPath));
            DeleteRemovedTree(directory, Path.Combine(directory, "release.json"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException)
        { /* Locked payloads stay quarantined and are retried when the library is reopened. */ }
    }

    private static void DeleteRemovedTree(string directory, string metadataPath)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if (string.Equals(entry, metadataPath, StringComparison.OrdinalIgnoreCase)) continue;
            EnsureNoReparsePoint(entry);
            if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0) DeleteRemovedTree(entry, metadataPath);
            else File.Delete(entry);
        }
        // Retain ownership metadata until every payload entry is gone, allowing
        // a partially deleted, locked directory to be cleaned safely on reopen.
        if (File.Exists(metadataPath) && Path.GetDirectoryName(metadataPath) == directory) File.Delete(metadataPath);
        Directory.Delete(directory);
    }

    private void EnsureRemovalTree(string directory)
    {
        var root = Path.GetFullPath(directory);
        _ = GetContainedPath(_storeRoot, Path.GetRelativePath(_storeRoot, root));
        EnsureNoReparsePoint(Path.GetDirectoryName(_storeRoot)!);
        EnsureNoReparsePoint(_storeRoot);
        EnsureNoReparsePoint(_releasesRoot);
        EnsureNoReparsePoint(root);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var parent))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(parent))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Module removal cannot follow symbolic links or junctions.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
            }
        }
    }

    private static void EnsureNoReparsePoint(string path)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return; }
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Module removal cannot follow symbolic links or junctions.");
    }

    private StoreState ReadState()
    {
        if (!File.Exists(_statePath) && !File.Exists(_statePath + ".backup")) return new StoreState(1, null, null);
        Exception? firstError = null;
        foreach (var path in new[] { _statePath, _statePath + ".backup" })
        {
            if (!File.Exists(path)) continue;
            try
            {
                var state = JsonSerializer.Deserialize<StoreState>(File.ReadAllText(path), JsonOptions)
                    ?? throw new InvalidDataException("Empty module state.");
                if (state.Schema is not 1 and not 2) throw new InvalidDataException("Unsupported module state version.");
                _ = ReadRelease(state.Current);
                _ = ReadRelease(state.Previous);
                foreach (var id in state.Installed ?? []) _ = ReadRelease(id);
                // Repair the primary after recovery, so a later normal save cannot replace
                // the valid backup with a corrupt or interrupted-removal manifest.
                if (path != _statePath)
                {
                    EnsureNoReparsePoint(Path.GetDirectoryName(_storeRoot)!);
                    EnsureNoReparsePoint(_storeRoot);
                    EnsureNoReparsePoint(_statePath);
                    EnsureNoReparsePoint(_statePath + ".backup");
                    WriteAtomic(_statePath, state, keepBackup: false);
                }
                return state;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or ArgumentException)
            { firstError ??= ex; }
        }
        throw new InvalidDataException("The saved module state cannot be read. Existing releases are preserved in the modules folder.", firstError);
    }

    private ModuleRelease? ReadRelease(string? id)
    {
        if (id is null) return null;
        if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Invalid saved release ID.");
        var releaseRoot = Path.Combine(_releasesRoot, id);
        var release = JsonSerializer.Deserialize<ModuleRelease>(File.ReadAllText(Path.Combine(releaseRoot, "release.json")), JsonOptions)
            ?? throw new InvalidDataException("Empty saved release.");
        var payloadRoot = Path.Combine(releaseRoot, "payload");
        var directory = Path.GetFullPath(release.DirectoryPath);
        var definition = ModuleCatalog.Get(release.Kind);
        var custom = release.Kind == ModuleKind.Custom;
        _ = ValidateDllFileName(release.DllFileName);
        if (release.Id != id || !Guid.TryParse(release.ModuleId, out _)
            || !custom && (release.ModuleId != definition.ModuleId || release.DllFileName != definition.DllFileName
                || release.AssemblyName != null && release.AssemblyName != definition.AssemblyName)
            || custom && (string.IsNullOrWhiteSpace(release.AssemblyName) || release.AssemblyName.Length > 200 || release.AssemblyName.Any(char.IsControl))
            ||
            !(directory.Equals(payloadRoot, StringComparison.OrdinalIgnoreCase) || directory.StartsWith(payloadRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The saved module release is invalid or missing.");
        if (custom)
        {
            if (ModuleCatalog.Identify(release.ModuleId, release.DllFileName).Kind != ModuleKind.Custom
                || ModuleCatalog.All.Any(x => x.AssemblyName == release.AssemblyName))
                throw new InvalidDataException("The saved custom module conflicts with a known module identity.");
            if (release.IsAvailable) ValidateAssembly(Path.Combine(directory, release.DllFileName), release.AssemblyName!);
        }
        return release;
    }

    private static string ValidateArchivePath(ZipArchiveEntry entry)
    {
        var name = entry.FullName.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith('/') || name.Contains(':') || name.Length > 220)
            throw new InvalidDataException("The module ZIP contains an unsafe path.");
        var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
        if (unixType is not 0 and not 0x8000 and not 0x4000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Symbolic links and special files are not supported in module ZIPs.");
        var components = name.TrimEnd('/').Split('/');
        foreach (var part in components)
        {
            if (part is "" or "." or ".." || part.EndsWith(' ') || part.EndsWith('.') || part.IndexOfAny(['<', '>', '"', '|', '?', '*', '\0']) >= 0)
                throw new InvalidDataException("The module ZIP contains an unsafe path.");
            var stem = part.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" ||
                (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '0' and <= '9'))
                throw new InvalidDataException("The module ZIP contains a reserved Windows filename.");
        }
        return name;
    }

    private static ModuleDefinition ValidateManifest(ModuleManifest manifest)
    {
        _ = ValidateDllFileName(manifest.DllFileName);
        var definition = ModuleCatalog.Identify(manifest.ModuleId, manifest.DllFileName);
        if (manifest.Version != null && (string.IsNullOrWhiteSpace(manifest.Version) || manifest.Version.Length > 80 || manifest.Version.Any(char.IsControl)))
            throw new InvalidDataException("The module manifest must specify a valid version.");
        if (manifest.ModuleName?.Length > 200 || manifest.ModuleName?.Any(char.IsControl) == true)
            throw new InvalidDataException("The module name is invalid.");
        return definition;
    }

    private static void ValidateAssembly(string path, string expectedName)
    {
        if (ReadAssemblyIdentity(path).Name != expectedName)
            throw new InvalidDataException("The DLL does not match the module's declared entry assembly.");
    }

    private static string ValidateDllFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || name.IndexOfAny(['/', '\\', ':', '<', '>', '"', '|', '?', '*']) >= 0
            || name.Any(char.IsControl) || name.EndsWith(' ') || name.EndsWith('.') || !name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The entry DLL must be a safe DLL filename without a folder path.");
        var stem = name.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '0' and <= '9')
            throw new InvalidDataException("The entry DLL uses a reserved Windows filename.");
        return name;
    }

    private static string SelectEntryDll(string payload)
    {
        var assemblies = new List<(string Path, AssemblyIdentity Identity)>();
        foreach (var path in Directory.EnumerateFiles(payload, "*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
        {
            try { assemblies.Add((path, ReadAssemblyIdentity(path))); }
            catch (InvalidDataException) { /* Native dependencies are allowed beside the managed entry DLL. */ }
        }
        var modules = assemblies.Where(x => x.Identity.TrackingModule).ToArray();
        if (modules.Length == 1) return modules[0].Path;
        if (modules.Length == 0 && assemblies.Count == 1) return assemblies[0].Path;
        throw new InvalidDataException(assemblies.Count == 0
            ? "No managed module DLL was found. Download a compiled module package instead of source code."
            : "The ZIP has multiple possible module DLLs. Select the module DLL directly, or add module.json naming its entry DLL.");
    }

    private sealed record AssemblyIdentity(string Name, string Version, string StableIdentity, bool TrackingModule);
    private static AssemblyIdentity ReadAssemblyIdentity(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) throw new InvalidDataException("The module DLL is not a managed .NET assembly.");
            var metadata = pe.GetMetadataReader();
            if (!metadata.IsAssembly) throw new InvalidDataException("The module DLL is not a managed assembly.");
            var assembly = metadata.GetAssemblyDefinition();
            var name = metadata.GetString(assembly.Name);
            if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || name.Any(char.IsControl))
                throw new InvalidDataException("The module assembly name is invalid.");
            bool IsTrackingBase(EntityHandle handle, HashSet<EntityHandle> seen)
            {
                if (handle.IsNil || !seen.Add(handle)) return false;
                if (handle.Kind == HandleKind.TypeReference)
                {
                    var reference = metadata.GetTypeReference((TypeReferenceHandle)handle);
                    return metadata.GetString(reference.Name) == "ExtTrackingModule" && metadata.GetString(reference.Namespace) == "VRCFaceTracking";
                }
                return handle.Kind == HandleKind.TypeDefinition && IsTrackingBase(metadata.GetTypeDefinition((TypeDefinitionHandle)handle).BaseType, seen);
            }
            var tracking = metadata.TypeDefinitions.Any(handle =>
            {
                var type = metadata.GetTypeDefinition(handle);
                return (type.Attributes & TypeAttributes.Abstract) == 0 && IsTrackingBase(type.BaseType, []);
            });
            return new(name, assembly.Version.ToString(), name + "|" + Convert.ToHexString(metadata.GetBlobBytes(assembly.PublicKey)), tracking);
        }
        catch (BadImageFormatException ex) { throw new InvalidDataException("The module DLL is not a valid .NET assembly.", ex); }
    }

    private static bool HasHeadsetSupport(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        return metadata.TypeDefinitions.Any(handle => metadata.GetString(metadata.GetTypeDefinition(handle).Name).Contains("BabbleLink", StringComparison.OrdinalIgnoreCase))
            || metadata.FieldDefinitions.Any(handle => metadata.GetString(metadata.GetFieldDefinition(handle).Name).Equals("babbleLink", StringComparison.OrdinalIgnoreCase));
    }

    private static string GetContainedPath(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.Equals(root, StringComparison.OrdinalIgnoreCase) && !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The module ZIP contains a path outside its release folder.");
        return full;
    }

    private static void CopyBounded(Stream input, Stream output, long expectedLength)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
        {
            total += read;
            if (total > expectedLength || total > MaxExpandedBytes)
                throw new InvalidDataException("A ZIP entry expanded beyond its advertised size.");
            output.Write(buffer, 0, read);
        }
        if (total != expectedLength) throw new InvalidDataException("A ZIP entry is incomplete.");
    }

    internal static void WriteAtomic<T>(string path, T value, bool keepBackup = true)
        => WriteAtomicContent(path, stream => JsonSerializer.Serialize(stream, value, JsonOptions), keepBackup);

    private static void WriteAtomicContent(string path, Action<Stream> write, bool keepBackup)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                write(output);
                output.Flush(flushToDisk: true);
            }
            if (File.Exists(path) && keepBackup)
                File.Replace(temporary, path, path + ".backup", ignoreMetadataErrors: true);
            else File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed record StoreState(int Schema, string? Current, string? Previous, string[]? Installed = null);
    private sealed class ModuleManifest
    {
        public string? ModuleId { get; set; }
        public string? Version { get; set; }
        public string? DllFileName { get; set; }
        public string? ModuleName { get; set; }
    }
}
