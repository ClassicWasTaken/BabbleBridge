// Copyright (c) 2026 Babble Bridge contributors.
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using BabbleFrameBridge.Shared;

const long MaxArchiveBytes = 256L * 1024 * 1024;
var workspace = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "work", "download-tests"));
Directory.CreateDirectory(workspace);
var root = Path.Combine(workspace, Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var passed = 0;
var failed = 0;
var package = Zip(("module.json", Encoding.UTF8.GetBytes("{}")), ("compiled.dll", new byte[] { 1, 2, 3 }));
var eyeTrackVrDll = ModuleDll("ETVRTrackingModule");

try
{
    await Test("provider downloads pin separate headset and desktop alpha compatible packages", () =>
    {
        var expected = new Dictionary<ModuleKind, (string Uri, string FileName)>
        {
            [ModuleKind.BabbleHeadsetRuntime] = ("https://github.com/Project-Babble/Baballonia/releases/download/vrcft-module-4.0.0/VRCFaceTracking.Baballonia.zip", "VRCFaceTracking.Baballonia.zip"),
            [ModuleKind.BaballoniaDesktop] = ("https://github.com/Project-Babble/Baballonia/releases/download/vrcft-module-3.2.0/VRCFaceTracking.Baballonia.zip", "VRCFaceTracking.Baballonia.zip"),
            [ModuleKind.VirtualDesktop] = ("https://download.vrdesktop.net/files/VirtualDesktop.VRCFaceTracking.zip", "VirtualDesktop.VRCFaceTracking.zip"),
            [ModuleKind.SteamLink] = ("https://github.com/ykeara/LinkFT/releases/download/1.0.6/LinkFTModule.zip", "LinkFTModule.zip"),
            [ModuleKind.SRanipal] = ("https://github.com/VRCFaceTracking/SRanipalTrackingModule/releases/download/1.5/SRanipalTrackingModule.zip", "SRanipalTrackingModule.zip"),
            [ModuleKind.Beyond] = ("https://github.com/BigscreenVR/VRCFT-Beyond/releases/download/v1.1.0/Build.zip", "BeyondTrackingModule.zip"),
            [ModuleKind.EyeTrackVR] = ("https://github.com/EyeTrackVR/ETVRTrackingModule/releases/download/0.0.5/ETVRTrackingModule.dll", "ETVRTrackingModule.dll")
        };
        foreach (var (kind, source) in expected)
        {
            var actual = ModuleDownloader.SourceFor(kind);
            Check(actual.DownloadUri.AbsoluteUri == source.Uri && actual.FileName == source.FileName, "Wrong compiled package for " + kind);
            Check(actual.DownloadUri.Scheme == "https", "Provider download is not HTTPS.");
        }
        ExpectFailure(() => ModuleDownloader.SourceFor((ModuleKind)999));
        ExpectFailure(() => ModuleDownloader.SourceFor(ModuleKind.Custom));
        return Task.CompletedTask;
    });

    await Test("successful download retains exact ZIP bytes and reports completed progress", async () =>
    {
        var data = DataRoot("success");
        var progress = new CaptureProgress();
        var handler = new FakeHandler((_, _) => Task.FromResult(Response(package)));
        using var client = new HttpClient(handler);
        string archive;
        using (var downloader = new ModuleDownloader(client))
            archive = await downloader.DownloadAsync(ModuleKind.VirtualDesktop, data, progress, CancellationToken.None);
        var relative = Path.GetRelativePath(data, archive).Split(Path.DirectorySeparatorChar);
        Check(relative.Length == 3 && relative[0] == "downloads" && Guid.TryParseExact(relative[1], "N", out _), "Download is not in an isolated owned folder.");
        Check(relative[2] == "VirtualDesktop.VRCFaceTracking.zip", "Response renamed the compiled package.");
        Check(File.ReadAllBytes(archive).SequenceEqual(package), "Downloaded ZIP bytes changed.");
        Check(progress.Values.Count > 0 && progress.Values[^1].ReceivedBytes == package.Length
            && progress.Values[^1].TotalBytes == package.Length, "Completed progress is inaccurate.");
        Check(progress.Values.Zip(progress.Values.Skip(1)).All(pair => pair.First.ReceivedBytes <= pair.Second.ReceivedBytes), "Progress moved backward.");
        Check(handler.Uris.Single() == ModuleDownloader.SourceFor(ModuleKind.VirtualDesktop).DownloadUri, "Download used an unexpected URI.");
        Check(!Directory.EnumerateFiles(data, "*.partial", SearchOption.AllDirectories).Any(), "Completed download left a partial file.");
        Check(!Directory.Exists(Path.Combine(data, "modules")), "Downloader installed or executed a package.");
        // An injected client remains caller-owned after the downloader is disposed.
        using var response = await client.GetAsync("https://download.vrdesktop.net/files/test-client-ownership.zip");
        Check(response.IsSuccessStatusCode && File.Exists(archive), "Disposal removed the user's completed archive or disposed the caller's client.");
    });

    await Test("each selected provider requests its own pinned package", async () =>
    {
        var handler = new FakeHandler((request, _) => Task.FromResult(Response(
            request.RequestUri!.AbsolutePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? eyeTrackVrDll : package)));
        using var client = new HttpClient(handler);
        using var downloader = new ModuleDownloader(client);
        foreach (var kind in ModuleCatalog.All.Select(item => item.Kind))
        {
            var archive = await downloader.DownloadAsync(kind, DataRoot("provider-" + kind), null, CancellationToken.None);
            Check(Path.GetFileName(archive) == ModuleDownloader.SourceFor(kind).FileName, "Selected provider used another package filename.");
        }
        Check(handler.Uris.SequenceEqual(ModuleCatalog.All.Select(item => ModuleDownloader.SourceFor(item.Kind).DownloadUri)), "Provider request order or URLs changed.");
    });

    await Test("official standalone DLL downloads retain bytes and reject a wrong or malformed module", async () =>
    {
        foreach (var (name, bytes, valid) in new[]
        {
            ("valid", eyeTrackVrDll, true), ("wrong-entry", ModuleDll("OtherModule"), false),
            ("web-page", Encoding.UTF8.GetBytes("<html>not a module</html>"), false),
            ("zip-instead", package, false), ("truncated", eyeTrackVrDll[..80], false)
        })
        {
            var data = DataRoot("standalone-" + name);
            var handler = new FakeHandler((_, _) => Task.FromResult(Response(bytes)));
            using var client = new HttpClient(handler);
            using var downloader = new ModuleDownloader(client);
            if (valid)
            {
                var file = await downloader.DownloadAsync(ModuleKind.EyeTrackVR, data);
                Check(Path.GetFileName(file) == "ETVRTrackingModule.dll" && File.ReadAllBytes(file).SequenceEqual(bytes),
                    "The official standalone DLL was renamed, transformed, or treated as a ZIP.");
            }
            else
            {
                await ExpectFailureAsync(() => downloader.DownloadAsync(ModuleKind.EyeTrackVR, data));
                AssertNoArtifacts(data);
            }
        }
    });

    if (args.Contains("--official-modules"))
        await Test("real official SRanipal, Beyond and ETVR packages download, identify, install and reload", async () =>
        {
            using var downloader = new ModuleDownloader();
            var data = DataRoot("official-modules");
            var store = new ModuleStore(data);
            foreach (var kind in new[] { ModuleKind.SRanipal, ModuleKind.Beyond, ModuleKind.EyeTrackVR })
            {
                var file = await downloader.DownloadAsync(kind, data);
                var release = store.Prepare(file, kind);
                Check(release.Kind == kind && release.ModuleId == ModuleCatalog.Get(kind).ModuleId
                    && release.DllFileName == ModuleCatalog.Get(kind).DllFileName, "The real package did not retain its official identity.");
                store.Register(release); store.Activate(release);
                Check(new ModuleStore(data).Current?.Id == release.Id, "Installed official module did not survive a store reload.");
                if (kind == ModuleKind.SRanipal)
                    foreach (var generation in new[] { "New", "Old" })
                        foreach (var dependency in new[] { "SRanipal.dll", "libHTC_License.dll", "nanomsg.dll", "SRWorks_Log.dll", "ViveSR_Client.dll" })
                            Check(File.Exists(Path.Combine(release.DirectoryPath, "ModuleLibs", generation, dependency)),
                                "The SRanipal ZIP lost a native dependency.");
                if (kind == ModuleKind.Beyond)
                    Check(Path.GetFileName(release.DirectoryPath) == "net7.0", "The nested Beyond module entry was not selected.");
                var current = store.Current;
                ExpectFailure(() => store.Prepare(file, kind == ModuleKind.EyeTrackVR ? ModuleKind.Beyond : ModuleKind.EyeTrackVR));
                Check(store.Current == current, "Rejecting the wrong official provider changed the active module.");
            }
            Check(new ModuleStore(data).Installed.Count == 3, "The three new providers did not remain separate module entries.");
        });

    await Test("missing Content-Length downloads stream correctly with unknown progress total", async () =>
    {
        var data = DataRoot("unknown-length");
        var progress = new CaptureProgress();
        var handler = new FakeHandler((_, _) => Task.FromResult(StreamResponse(new ForwardOnlyStream(package))));
        using var client = new HttpClient(handler);
        using var downloader = new ModuleDownloader(client);
        var archive = await downloader.DownloadAsync(ModuleKind.SteamLink, data, progress, CancellationToken.None);
        Check(File.ReadAllBytes(archive).SequenceEqual(package), "Unknown-length ZIP content changed.");
        Check(progress.Values.Count > 0 && progress.Values[^1].ReceivedBytes == package.Length
            && progress.Values.All(value => value.TotalBytes is null), "Unknown-length progress invented a total.");
    });

    await Test("GitHub release assets follow an allowed official HTTPS redirect", async () =>
    {
        var destination = new Uri("https://release-assets.githubusercontent.com/github-production-release-asset/fixture/module.zip");
        var handler = new FakeHandler((request, _) => Task.FromResult(request.RequestUri!.Host == "github.com"
            ? Redirect(destination) : Response(package)));
        using var client = new HttpClient(handler);
        using var downloader = new ModuleDownloader(client);
        var archive = await downloader.DownloadAsync(ModuleKind.SteamLink, DataRoot("allowed-redirect"), null, CancellationToken.None);
        Check(handler.Uris.Count == 2 && handler.Uris[^1] == destination && File.ReadAllBytes(archive).SequenceEqual(package), "Official GitHub asset redirect did not complete.");
    });

    await Test("off-host HTTPS redirects are rejected before sending another request", async () =>
    {
        var data = DataRoot("off-host-redirect");
        var handler = new FakeHandler((_, _) => Task.FromResult(Redirect(new Uri("https://untrusted.example/module.zip"))));
        using var client = new HttpClient(handler);
        using var downloader = new ModuleDownloader(client);
        await ExpectFailureAsync(() => downloader.DownloadAsync(ModuleKind.BabbleHeadsetRuntime, data, null, CancellationToken.None));
        Check(handler.Uris.Count == 1, "Downloader requested an untrusted redirect host.");
        AssertNoArtifacts(data);
    });

    await Test("insecure HTTP redirects are rejected even on an official host", async () =>
    {
        var data = DataRoot("insecure-redirect");
        var handler = new FakeHandler((_, _) => Task.FromResult(Redirect(new Uri("http://github.com/ykeara/LinkFT/module.zip"))));
        using var client = new HttpClient(handler);
        using var downloader = new ModuleDownloader(client);
        await ExpectFailureAsync(() => downloader.DownloadAsync(ModuleKind.SteamLink, data, null, CancellationToken.None));
        Check(handler.Uris.Count == 1, "Downloader followed an insecure redirect.");
        AssertNoArtifacts(data);
    });

    await Test("official redirect loops stop after at most six requests", async () =>
    {
        var data = DataRoot("redirect-loop");
        var requests = 0;
        var handler = new FakeHandler((_, _) =>
        {
            requests++;
            if (requests > 8) throw new InvalidOperationException("Test safety cutoff: redirect loop is unbounded.");
            return Task.FromResult(Redirect(new Uri("https://github.com/ykeara/LinkFT/module.zip?attempt=" + requests)));
        });
        using var client = new HttpClient(handler);
        using var downloader = new ModuleDownloader(client);
        await ExpectFailureAsync(() => downloader.DownloadAsync(ModuleKind.SteamLink, data, null, CancellationToken.None));
        Check(requests is > 1 and <= 6, "Redirect count was not bounded to the initial request plus five redirects.");
        AssertNoArtifacts(data);
    });

    await Test("HTTP failure leaves no partial file or owned download folder", async () =>
    {
        var data = DataRoot("http-failure");
        var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        { Content = new StringContent("Temporary upstream failure") }));
        using var client = new HttpClient(handler);
        using var downloader = new ModuleDownloader(client);
        await ExpectFailureAsync(() => downloader.DownloadAsync(ModuleKind.VirtualDesktop, data, null, CancellationToken.None));
        AssertNoArtifacts(data);
    });

    await Test("pre-cancelled downloads never issue an HTTP request", async () =>
    {
        var data = DataRoot("pre-cancelled");
        var handler = new FakeHandler((_, _) => Task.FromResult(Response(package)));
        using var client = new HttpClient(handler);
        using var downloader = new ModuleDownloader(client);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await ExpectCancellation(() => downloader.DownloadAsync(ModuleKind.BaballoniaDesktop, data, null, cancellation.Token));
        Check(handler.Uris.Count == 0, "A cancelled operation issued a request.");
        AssertNoArtifacts(data);
    });

    await Test("cancellation during streaming removes the partial archive", async () =>
    {
        var data = DataRoot("cancelled-stream");
        using var cancellation = new CancellationTokenSource();
        var stream = new CancelAfterReadStream(package, cancellation);
        var handler = new FakeHandler((_, _) => Task.FromResult(StreamResponse(stream)));
        using var client = new HttpClient(handler);
        using var downloader = new ModuleDownloader(client);
        await ExpectCancellation(() => downloader.DownloadAsync(ModuleKind.VirtualDesktop, data, null, cancellation.Token));
        Check(stream.BytesRead > 0, "Streaming cancellation was not exercised.");
        AssertNoArtifacts(data);
    });

    await Test("injected HTTP timeout cancels promptly and removes temporary files", async () =>
    {
        var data = DataRoot("timeout");
        var handler = new FakeHandler(async (_, cancellation) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
            throw new InvalidOperationException("The timeout did not cancel the handler.");
        });
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(100) };
        using var downloader = new ModuleDownloader(client);
        var timer = Stopwatch.StartNew();
        await ExpectFailureAsync(() => downloader.DownloadAsync(ModuleKind.SteamLink, data, null, CancellationToken.None));
        Check(timer.Elapsed < TimeSpan.FromSeconds(3), "Downloader ignored the caller's short HTTP timeout.");
        AssertNoArtifacts(data);
    });

    await Test("oversized Content-Length is rejected before reading the response body", async () =>
    {
        var data = DataRoot("oversized-header");
        var stream = new GeneratedStream(1024);
        var handler = new FakeHandler((_, _) =>
        {
            var response = StreamResponse(stream);
            response.Content.Headers.ContentLength = MaxArchiveBytes + 1;
            return Task.FromResult(response);
        });
        using var client = new HttpClient(handler);
        using var downloader = new ModuleDownloader(client);
        await ExpectFailureAsync(() => downloader.DownloadAsync(ModuleKind.BabbleHeadsetRuntime, data, null, CancellationToken.None));
        Check(stream.BytesRead == 0, "An oversized advertised response body was read.");
        AssertNoArtifacts(data);
    });

    await Test("streamed size limit stops an unbounded response without trusting headers", async () =>
    {
        var data = DataRoot("oversized-stream");
        var stream = new GeneratedStream(MaxArchiveBytes + 10 * 1024 * 1024);
        var handler = new FakeHandler((_, _) => Task.FromResult(StreamResponse(stream)));
        using var client = new HttpClient(handler);
        using var downloader = new ModuleDownloader(client);
        await ExpectFailureAsync(() => downloader.DownloadAsync(ModuleKind.BaballoniaDesktop, data, null, CancellationToken.None));
        Check(stream.BytesRead > MaxArchiveBytes && stream.BytesRead <= MaxArchiveBytes + 1024 * 1024, "The streamed byte limit did not stop the body promptly.");
        AssertNoArtifacts(data);
    });

    await Test("truncated declared content is rejected even when available bytes form a valid ZIP", async () =>
    {
        var data = DataRoot("truncated-content");
        var handler = new FakeHandler((_, _) =>
        {
            var response = Response(package);
            response.Content.Headers.ContentLength = package.Length + 100;
            return Task.FromResult(response);
        });
        using var client = new HttpClient(handler);
        using var downloader = new ModuleDownloader(client);
        await ExpectFailureAsync(() => downloader.DownloadAsync(ModuleKind.VirtualDesktop, data, null, CancellationToken.None));
        AssertNoArtifacts(data);
    });

    await Test("HTML, non-ZIP, empty responses and damaged ZIPs never become completed downloads", async () =>
    {
        foreach (var (name, bytes, mediaType) in new[]
        {
            ("html", Encoding.UTF8.GetBytes("<!doctype html><html>Download unavailable</html>"), "text/html"),
            ("non-zip", new byte[] { 1, 2, 3, 4, 5 }, "application/octet-stream"),
            ("empty-response", Array.Empty<byte>(), "application/zip"),
            ("empty-zip", Zip(), "application/zip"),
            ("truncated-zip", package[..^10], "application/zip")
        })
        {
            var data = DataRoot("invalid-" + name);
            var handler = new FakeHandler((_, _) =>
            {
                var response = Response(bytes);
                response.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
                return Task.FromResult(response);
            });
            using var client = new HttpClient(handler);
            using var downloader = new ModuleDownloader(client);
            await ExpectFailureAsync(() => downloader.DownloadAsync(ModuleKind.VirtualDesktop, data, null, CancellationToken.None));
            AssertNoArtifacts(data);
        }
    });

    await Test("ZIP entry and expanded-size limits reject hostile archive metadata", async () =>
    {
        var excessiveEntries = Zip(Enumerable.Range(0, 4097).Select(index => ("entry-" + index, Array.Empty<byte>())).ToArray());
        var expanded = Zip(("oversized.bin", new byte[] { 1 }));
        var directoryOffset = FindSignature(expanded, 0x02014b50);
        Check(directoryOffset >= 0, "ZIP fixture has no central directory.");
        BinaryPrimitives.WriteUInt32LittleEndian(expanded.AsSpan(directoryOffset + 24), 512U * 1024 * 1024 + 1);
        foreach (var (name, bytes) in new[] { ("entries", excessiveEntries), ("expanded", expanded) })
        {
            var data = DataRoot("zip-limit-" + name);
            var handler = new FakeHandler((_, _) => Task.FromResult(Response(bytes)));
            using var client = new HttpClient(handler);
            using var downloader = new ModuleDownloader(client);
            await ExpectFailureAsync(() => downloader.DownloadAsync(ModuleKind.SteamLink, data, null, CancellationToken.None));
            AssertNoArtifacts(data);
        }
    });

    await Test("a completed archive remains available when module identity validation rejects it", async () =>
    {
        var data = DataRoot("wrong-provider");
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            ModuleId = "2a8c8080-2a76-46af-bf76-1da7c0127ef8", DllFileName = "SteamLinkVRCFTModule.dll",
            ModuleName = "Steam Link", Version = "1.0.6"
        });
        var wrongPackage = Zip(("module.json", manifest), ("SteamLinkVRCFTModule.dll", new byte[] { 1, 2, 3 }));
        var handler = new FakeHandler((_, _) => Task.FromResult(Response(wrongPackage)));
        using var client = new HttpClient(handler);
        using var downloader = new ModuleDownloader(client);
        var archive = await downloader.DownloadAsync(ModuleKind.VirtualDesktop, data, null, CancellationToken.None);
        var store = new ModuleStore(data);
        ExpectFailure(() => store.Prepare(archive, ModuleKind.VirtualDesktop));
        Check(store.Installed.Count == 0 && store.Current is null, "Downloader installed a package or identity rejection modified the library.");
        Check(File.Exists(archive) && File.ReadAllBytes(archive).SequenceEqual(wrongPackage), "Identity validation deleted the downloaded original ZIP.");
    });
}
finally
{
    var absolute = Path.GetFullPath(root);
    if (absolute.StartsWith(workspace + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        Directory.Delete(absolute, recursive: true);
}

Console.WriteLine($"{passed} passed; {failed} failed.");
return failed == 0 ? 0 : 1;

async Task Test(string name, Func<Task> test)
{
    try { await test(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception error) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + error); }
}
string DataRoot(string name)
{
    var path = Path.Combine(root, name);
    Directory.CreateDirectory(path);
    return path;
}
static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
static bool ExpectedFailure(Exception error) => error is HttpRequestException or IOException or InvalidDataException
    or OperationCanceledException or ArgumentException or InvalidOperationException or UnauthorizedAccessException or TimeoutException;
static void ExpectFailure(Action action)
{
    try { action(); }
    catch (Exception error) when (ExpectedFailure(error)) { return; }
    throw new Exception("Expected failure did not occur.");
}
static async Task ExpectFailureAsync(Func<Task> action)
{
    try { await action(); }
    catch (Exception error) when (ExpectedFailure(error)) { return; }
    throw new Exception("Expected failure did not occur.");
}
static async Task ExpectCancellation(Func<Task> action)
{
    try { await action(); }
    catch (OperationCanceledException) { return; }
    throw new Exception("Expected cancellation did not occur.");
}
static void AssertNoArtifacts(string root)
{
    var downloads = Path.Combine(root, "downloads");
    Check(!Directory.Exists(downloads) || !Directory.EnumerateFileSystemEntries(downloads).Any(), "Failed download left partial files or an owned temporary folder.");
}
static HttpResponseMessage Response(byte[] bytes)
{
    var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
    response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = "untrusted-name.exe" };
    return response;
}
static HttpResponseMessage StreamResponse(Stream stream)
{
    var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
    response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
    return response;
}
static HttpResponseMessage Redirect(Uri destination)
{
    var response = new HttpResponseMessage(HttpStatusCode.Redirect);
    response.Headers.Location = destination;
    return response;
}
static byte[] Zip(params (string Name, byte[] Bytes)[] entries)
{
    using var buffer = new MemoryStream();
    using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        foreach (var (name, bytes) in entries)
            using (var output = archive.CreateEntry(name, CompressionLevel.NoCompression).Open()) output.Write(bytes);
    return buffer.ToArray();
}
static byte[] ModuleDll(string name)
{
    // Protocol fixture: metadata only, never loaded or executed by these tests.
    var metadata = new MetadataBuilder();
    metadata.AddModule(0, metadata.GetOrAddString(name + ".dll"), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
    metadata.AddAssembly(metadata.GetOrAddString(name), new Version(1, 0, 0, 0), default, default, default, AssemblyHashAlgorithm.None);
    var sdk = metadata.AddAssemblyReference(metadata.GetOrAddString("VRCFaceTracking.SDK"), new Version(1, 0, 0, 0), default, default, default, default);
    var tracking = metadata.AddTypeReference(sdk, metadata.GetOrAddString("VRCFaceTracking"), metadata.GetOrAddString("ExtTrackingModule"));
    metadata.AddTypeDefinition(TypeAttributes.NotPublic, default, metadata.GetOrAddString("<Module>"), default,
        MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
    metadata.AddTypeDefinition(TypeAttributes.Public | TypeAttributes.Class, metadata.GetOrAddString("Fixture"),
        metadata.GetOrAddString("TrackingModule"), tracking, MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
    var image = new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
        new MetadataRootBuilder(metadata), new BlobBuilder());
    var output = new BlobBuilder(); image.Serialize(output);
    return output.ToArray();
}
static int FindSignature(byte[] bytes, uint signature)
{
    for (var index = 0; index <= bytes.Length - 4; index++)
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index, 4)) == signature) return index;
    return -1;
}

sealed class CaptureProgress : IProgress<ModuleDownloadProgress>
{
    public List<ModuleDownloadProgress> Values { get; } = [];
    public void Report(ModuleDownloadProgress value) => Values.Add(value);
}

sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    public List<Uri> Uris { get; } = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Uris.Add(request.RequestUri!);
        return send(request, cancellationToken);
    }
}

class ForwardOnlyStream(byte[] bytes) : Stream
{
    private int offset;
    public long BytesRead { get; protected set; }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int start, int count)
    {
        var available = Math.Min(count, bytes.Length - offset);
        bytes.AsSpan(offset, available).CopyTo(buffer.AsSpan(start, available));
        offset += available;
        BytesRead += available;
        return available;
    }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var available = Math.Min(buffer.Length, bytes.Length - offset);
        bytes.AsMemory(offset, available).CopyTo(buffer);
        offset += available;
        BytesRead += available;
        return ValueTask.FromResult(available);
    }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

sealed class CancelAfterReadStream(byte[] bytes, CancellationTokenSource cancellation) : ForwardOnlyStream(bytes)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = base.ReadAsync(buffer, cancellationToken);
        cancellation.Cancel();
        return read;
    }
}

sealed class GeneratedStream(long length) : Stream
{
    public long BytesRead { get; private set; }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    private int Fill(Span<byte> buffer)
    {
        var available = (int)Math.Min(buffer.Length, length - BytesRead);
        buffer[..available].Clear();
        if (BytesRead == 0 && available >= 4) "PK\u0003\u0004"u8.CopyTo(buffer);
        BytesRead += available;
        return available;
    }
    public override int Read(byte[] buffer, int offset, int count) => Fill(buffer.AsSpan(offset, count));
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Fill(buffer.Span));
    }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
