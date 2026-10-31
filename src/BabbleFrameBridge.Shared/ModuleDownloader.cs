// Copyright (c) 2026 Babble Bridge contributors.
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Net;
using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace BabbleFrameBridge.Shared;

public sealed record ModuleDownloadSource(Uri DownloadUri, string FileName);
public sealed record ModuleDownloadProgress(long ReceivedBytes, long? TotalBytes);

/// <summary>Downloads fixed official compiled packages. Installation and module loading remain explicit caller actions.</summary>
public sealed class ModuleDownloader : IDisposable
{
    private const long MaxArchiveBytes = 256L * 1024 * 1024;
    private const long MaxExpandedBytes = 512L * 1024 * 1024;
    private const int MaxEntries = 4096;
    private const int MaxRedirects = 5;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private bool _disposed;

    public ModuleDownloader(HttpClient? client = null)
    {
        _ownsClient = client == null;
        _client = client ?? new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(15)
        }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public static ModuleDownloadSource SourceFor(ModuleKind kind) => kind switch
    {
        ModuleKind.BabbleHeadsetRuntime => new(
            new Uri("https://github.com/Project-Babble/Baballonia/releases/download/vrcft-module-4.0.0/VRCFaceTracking.Baballonia.zip"),
            "VRCFaceTracking.Baballonia.zip"),
        // The desktop Expressions Alpha sends lowercase-initial eye addresses.
        // Its dedicated module accepts those and the older desktop eye format.
        ModuleKind.BaballoniaDesktop => new(
            new Uri("https://github.com/Project-Babble/Baballonia/releases/download/vrcft-module-3.2.0/VRCFaceTracking.Baballonia.zip"),
            "VRCFaceTracking.Baballonia.zip"),
        ModuleKind.VirtualDesktop => new(
            new Uri("https://download.vrdesktop.net/files/VirtualDesktop.VRCFaceTracking.zip"),
            "VirtualDesktop.VRCFaceTracking.zip"),
        ModuleKind.SteamLink => new(
            new Uri("https://github.com/ykeara/LinkFT/releases/download/1.0.6/LinkFTModule.zip"), "LinkFTModule.zip"),
        ModuleKind.SRanipal => new(
            new Uri("https://github.com/VRCFaceTracking/SRanipalTrackingModule/releases/download/1.5/SRanipalTrackingModule.zip"), "SRanipalTrackingModule.zip"),
        ModuleKind.Beyond => new(
            new Uri("https://github.com/BigscreenVR/VRCFT-Beyond/releases/download/v1.1.0/Build.zip"), "BeyondTrackingModule.zip"),
        ModuleKind.EyeTrackVR => new(
            new Uri("https://github.com/EyeTrackVR/ETVRTrackingModule/releases/download/0.0.5/ETVRTrackingModule.dll"), "ETVRTrackingModule.dll"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), "Choose a supported module.")
    };

    public async Task<string> DownloadAsync(ModuleKind kind, string dataRoot,
        IProgress<ModuleDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        var source = SourceFor(kind);
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(120));
        var token = timeout.Token;
        var root = Path.GetFullPath(dataRoot);
        EnsureNoReparseAncestors(root);
        var downloads = Path.Combine(root, "downloads");
        Directory.CreateDirectory(downloads);
        EnsureNoReparseAncestors(downloads);
        var folder = Path.Combine(downloads, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var finalPath = Path.Combine(folder, source.FileName);
        var partialPath = finalPath + ".partial";
        var completed = false;
        try
        {
            using var response = await GetResponseAsync(source.DownloadUri, kind, token);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            if (total is <= 0 or > MaxArchiveBytes)
                throw new InvalidDataException("The module download must be between 1 byte and 256 MB.");
            if (response.Content.Headers.ContentType?.MediaType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true)
                throw new InvalidDataException("The official download returned a web page instead of a compiled module file.");
            progress?.Report(new(0, total));
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                var buffer = new byte[81920];
                var progressClock = Stopwatch.StartNew();
                long received = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, token)) != 0)
                {
                    received += count;
                    if (received > MaxArchiveBytes)
                        throw new InvalidDataException("The module download exceeded 256 MB.");
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                    if (progressClock.Elapsed >= TimeSpan.FromMilliseconds(100))
                    {
                        progress?.Report(new(received, total));
                        progressClock.Restart();
                    }
                }
                if (received == 0 || (total is { } expected && received != expected))
                    throw new InvalidDataException("The module download ended with an incomplete or mismatched file size. Try downloading again.");
                await output.FlushAsync(token);
                progress?.Report(new(received, total));
            }
            if (source.FileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) ValidateDll(partialPath, kind);
            else ValidateZip(partialPath, token);
            token.ThrowIfCancellationRequested();
            EnsureNoReparseAncestors(folder);
            File.Move(partialPath, finalPath);
            completed = true;
            return finalPath;
        }
        finally
        {
            if (!completed) CleanupDownload(folder, downloads, partialPath);
        }
    }

    private async Task<HttpResponseMessage> GetResponseAsync(Uri uri, ModuleKind kind, CancellationToken token)
    {
        for (var redirects = 0; ; redirects++)
        {
            ValidateDownloadUri(uri, kind);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("FaceTrackingBridge/1.0");
            request.Headers.Accept.ParseAdd("application/zip, application/octet-stream");
            var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            try
            {
                // Also check clients supplied by tests/hosts which may use their own handler.
                if (response.RequestMessage?.RequestUri is { } actualUri) ValidateDownloadUri(actualUri, kind);
                if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect
                    or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
                    return response;
                if (redirects >= MaxRedirects || response.Headers.Location == null)
                    throw new HttpRequestException("The official module download redirected too many times or omitted its destination.");
                uri = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(uri, response.Headers.Location);
            }
            catch { response.Dispose(); throw; }
            response.Dispose();
        }
    }

    private static void ValidateDownloadUri(Uri uri, ModuleKind kind)
    {
        var officialHost = kind == ModuleKind.VirtualDesktop
            ? uri.Host.Equals("download.vrdesktop.net", StringComparison.OrdinalIgnoreCase)
            : uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase);
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443 || uri.UserInfo.Length != 0 || !officialHost)
            throw new HttpRequestException("The module download redirected outside its official HTTPS download hosts.");
    }

    private static void ValidateZip(string path, CancellationToken token)
    {
        using var stream = File.OpenRead(path);
        Span<byte> signature = stackalloc byte[4];
        if (stream.Read(signature) != 4 || !signature.SequenceEqual(new byte[] { 0x50, 0x4b, 0x03, 0x04 }))
            throw new InvalidDataException("The downloaded file is not a compiled module ZIP.");
        stream.Position = 0;
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        if (archive.Entries.Count is 0 or > MaxEntries)
            throw new InvalidDataException("The module ZIP is empty or has too many entries.");
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (entry.Length > MaxExpandedBytes - expanded)
                throw new InvalidDataException("The module ZIP expands beyond 512 MB.");
            expanded += entry.Length;
            if (!entry.FullName.EndsWith('/'))
            {
                using var data = entry.Open();
                // Check local ZIP headers without extracting or running package contents.
            }
        }
    }

    private static void ValidateDll(string path, ModuleKind kind)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) throw new InvalidDataException("The downloaded module DLL is not a managed .NET assembly.");
            var metadata = pe.GetMetadataReader();
            if (!metadata.IsAssembly || metadata.GetString(metadata.GetAssemblyDefinition().Name) != ModuleCatalog.Get(kind).AssemblyName)
                throw new InvalidDataException("The downloaded module DLL does not match the selected module.");
            var tracking = metadata.TypeDefinitions.Any(handle =>
            {
                var type = metadata.GetTypeDefinition(handle);
                if ((type.Attributes & System.Reflection.TypeAttributes.Abstract) != 0 || type.BaseType.Kind != HandleKind.TypeReference) return false;
                var reference = metadata.GetTypeReference((TypeReferenceHandle)type.BaseType);
                return metadata.GetString(reference.Name) == "ExtTrackingModule" && metadata.GetString(reference.Namespace) == "VRCFaceTracking";
            });
            if (!tracking) throw new InvalidDataException("The downloaded DLL does not contain a VRCFaceTracking module.");
        }
        catch (BadImageFormatException error) { throw new InvalidDataException("The downloaded module DLL is not a valid .NET assembly.", error); }
    }

    private static void EnsureNoReparseAncestors(string path)
    {
        for (var current = new DirectoryInfo(path); current != null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Module downloads cannot use symbolic links or junctions.");
    }

    private static void CleanupDownload(string folder, string downloads, string partialPath)
    {
        try
        {
            var parent = Path.GetDirectoryName(Path.GetFullPath(folder));
            if (!string.Equals(parent, downloads, StringComparison.OrdinalIgnoreCase)
                || !Guid.TryParseExact(Path.GetFileName(folder), "N", out _)) return;
            EnsureNoReparseAncestors(folder);
            if (File.Exists(partialPath))
            {
                if ((File.GetAttributes(partialPath) & FileAttributes.ReparsePoint) != 0) return;
                File.Delete(partialPath);
            }
            // A nonrecursive removal preserves unexpected subdirectories or concurrent files.
            Directory.Delete(folder);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        { /* Preserve foreign/replaced paths rather than following them during failed-download cleanup. */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsClient) _client.Dispose();
    }
}
