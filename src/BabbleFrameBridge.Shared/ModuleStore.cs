// Copyright (c) 2026 Frame Bridge contributors.
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;

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
    bool SupportsHeadset = false);

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
            if (ReadRelease(release.Id) != release || release.ModuleId != BaballoniaModuleId || !release.SupportsHeadset)
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
                var supports = HasHeadsetSupport(Path.Combine(release.DirectoryPath, release.DllFileName));
                release = release with { Kind = source == TrackingInputSource.BaballoniaDesktop ? ModuleKind.BaballoniaDesktop : ModuleKind.BabbleHeadsetRuntime, SupportsHeadset = supports };
                WriteAtomic(Path.Combine(_releasesRoot, id, "release.json"), release);
            }
            var next = new StoreState(2, _state.Current, _state.Previous, ids);
            WriteAtomic(_statePath, next); _state = next;
            if (Current is { SupportsHeadset: true } current)
                Register(CreateCompanion(current));
        }
    }

    /// <summary>Validates and extracts a new release; does not change the active module.</summary>
    public ModuleRelease Prepare(string zipPath, ModuleKind? requestedKind = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        using var source = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (source.Length is <= 0 or > MaxArchiveBytes)
            throw new InvalidDataException("The module ZIP must be between 1 byte and 256 MB.");
        var hash = Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant();
        source.Position = 0;
        using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: false);
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
            checked { expanded += entry.Length; }
            if (expanded > MaxExpandedBytes)
                throw new InvalidDataException("The module ZIP expands beyond 512 MB.");
            if (!relative.EndsWith('/')) files.Add((entry, relative));
        }
        var manifests = files.Where(f => string.Equals(Path.GetFileName(f.Relative), "module.json", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (manifests.Length != 1)
            throw new InvalidDataException("The module ZIP must contain exactly one module.json.");
        if (manifests[0].Entry.Length > 1024 * 1024)
            throw new InvalidDataException("The module manifest is too large.");

        ModuleManifest manifest;
        try
        {
            using var manifestStream = manifests[0].Entry.Open();
            manifest = JsonSerializer.Deserialize<ModuleManifest>(manifestStream, JsonOptions)
                ?? throw new InvalidDataException("The module manifest is empty.");
        }
        catch (JsonException ex) { throw new InvalidDataException("The module manifest is not valid JSON.", ex); }
        var definition = ValidateManifest(manifest);
        if (requestedKind is { } requested && ModuleCatalog.Get(requested).ModuleId != definition.ModuleId)
            throw new InvalidDataException("The ZIP does not match the selected module.");

        var manifestDirectory = Path.GetDirectoryName(manifests[0].Relative.Replace('/', Path.DirectorySeparatorChar)) ?? "";
        var expectedDllPath = Path.Combine(manifestDirectory, manifest.DllFileName!).Replace('\\', '/');
        if (!files.Any(f => string.Equals(f.Relative, expectedDllPath, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The module entry DLL is missing beside module.json.");

        var id = Guid.NewGuid().ToString("N");
        var releaseRoot = Path.Combine(_releasesRoot, id);
        var staging = Path.Combine(_storeRoot, ".staging-" + id);
        var payload = Path.Combine(staging, "payload");
        Directory.CreateDirectory(payload);
        try
        {
            foreach (var file in files)
            {
                var destination = GetContainedPath(payload, file.Relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var input = file.Entry.Open();
                using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                CopyBounded(input, output, file.Entry.Length);
            }
            var moduleDirectory = GetContainedPath(payload, manifestDirectory);
            ValidateAssembly(Path.Combine(moduleDirectory, manifest.DllFileName!), definition.AssemblyName);
            var supports = definition.ModuleId == BaballoniaModuleId && HasHeadsetSupport(Path.Combine(moduleDirectory, manifest.DllFileName!));
            var kind = requestedKind ?? (definition.Kind == ModuleKind.BabbleHeadsetRuntime && !supports ? ModuleKind.BaballoniaDesktop : definition.Kind);
            if (kind == ModuleKind.BabbleHeadsetRuntime && !supports)
                throw new InvalidDataException("This Baballonia package supports desktop input only. Import a package with Babble Frame Runtime support.");
            var release = new ModuleRelease(id, manifest.Version!, definition.ModuleId, manifest.DllFileName!,
                DateTimeOffset.UtcNow, hash, Path.Combine(releaseRoot, "payload", manifestDirectory), manifest.ModuleName ?? definition.Name, kind, supports);
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
            ValidateAssembly(Path.Combine(stored.DirectoryPath, stored.DllFileName), ModuleCatalog.Get(stored.Kind).AssemblyName);
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
            ValidateAssembly(Path.Combine(release.DirectoryPath, release.DllFileName), ModuleCatalog.Get(release.Kind).AssemblyName);
            var next = new StoreState(2, _state.Previous, _state.Current, InstalledIds());
            WriteAtomic(_statePath, next);
            _state = next;
            return release;
        }
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
        if (release.Id != id || release.ModuleId != definition.ModuleId || release.DllFileName != definition.DllFileName ||
            !(directory.Equals(payloadRoot, StringComparison.OrdinalIgnoreCase) || directory.StartsWith(payloadRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) ||
            !File.Exists(Path.Combine(directory, release.DllFileName)))
            throw new InvalidDataException("The saved module release is invalid or missing.");
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
        var definition = ModuleCatalog.Identify(manifest.ModuleId, manifest.DllFileName);
        if (string.IsNullOrWhiteSpace(manifest.Version) || manifest.Version.Length > 80 || manifest.Version.Any(char.IsControl))
            throw new InvalidDataException("The module manifest must specify a valid version.");
        if (manifest.ModuleName?.Length > 200 || manifest.ModuleName?.Any(char.IsControl) == true)
            throw new InvalidDataException("The module name is invalid.");
        return definition;
    }

    private static void ValidateAssembly(string path, string expectedName)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) throw new InvalidDataException("The module DLL is not a managed .NET assembly.");
            var metadata = pe.GetMetadataReader();
            if (!metadata.IsAssembly || metadata.GetString(metadata.GetAssemblyDefinition().Name) != expectedName)
                throw new InvalidDataException("The DLL does not match the module's declared entry assembly.");
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
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(output, value, JsonOptions);
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
