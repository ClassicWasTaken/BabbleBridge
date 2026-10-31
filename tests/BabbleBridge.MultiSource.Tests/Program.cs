// Copyright (c) 2026 Face Tracking Bridge contributors.
// SPDX-License-Identifier: Apache-2.0
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using BabbleFrameBridge.Shared;

string Required(string name) { var index = Array.IndexOf(args, name); return index >= 0 && index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) : throw new ArgumentException("Missing " + name); }
var worker = Required("--worker"); var dotnet = args.Contains("--dotnet") ? Required("--dotnet") : null;
var root = Path.Combine(Environment.CurrentDirectory, "work", "multi-source-tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var passed = 0; var failed = 0;

await Test("separate producers merge independent eyes, brows and mouth through one steady output stream", async () =>
{
    using var output = new OscCapture();
    var profile = Profile("merge", output.Port);
    var eye = Fixture(profile, "eye", new() { ["left"] = .16f, ["right"] = .82f, ["x"] = -.3f, ["rx"] = .4f, ["wide"] = .32f, ["brow"] = .18f, ["jaw"] = .99f });
    var face = Fixture(profile, "face", new() { ["left"] = .99f, ["right"] = .99f, ["x"] = .99f, ["wide"] = .99f, ["brow"] = .99f, ["jaw"] = .72f });
    using var probe = Start(profile, eye, face);
    await probe.WaitReady();
    await Until(() => probe.Status(status => status.GetProperty("eyeAvailable").GetBoolean() && status.GetProperty("faceAvailable").GetBoolean()
        && Near(status.GetProperty("eyeLeft").GetSingle(), .16f) && Near(status.GetProperty("jawOpen").GetSingle(), .72f)), "Merged diagnostics did not retain role ownership.");
    await Until(() => output.Has("/v2/EyeLeftX", -.3f) && output.Has("/v2/JawOpen", .72f)
        && output.Has("/v2/BrowInnerUpLeft", .18f), "The merged eye/brow/mouth OSC values were contaminated by the other source.");
    await Until(() => probe.Status(status => status.GetProperty("trackingRate").GetDouble() is >= 55 and <= 110), "The merged sender did not maintain its elapsed-time cadence.");
    var status = probe.LatestStatus(); var eyePid = status.GetProperty("eyeWorkerPid").GetInt32(); var facePid = status.GetProperty("faceWorkerPid").GetInt32();
    Check(eyePid > 0 && facePid > 0 && eyePid != facePid && eyePid != probe.Id, "Inputs did not run in isolated child processes.");
    Check(output.SourcePorts.Count == 1, "More than one OSC sender reached the avatar.");
    await probe.Stop();
    Check(!Alive(eyePid) && !Alive(facePid) && File.Exists(Path.Combine(Path.GetDirectoryName(eye)!, "fixture-torn-down.txt"))
        && File.Exists(Path.Combine(Path.GetDirectoryName(face)!, "fixture-torn-down.txt")), "Stopping the coordinator left inputs or skipped normal teardown.");
});

await Test("a waiting eye source cannot suppress live mouth tracking or claim unavailable eyes", async () =>
{
    using var output = new OscCapture(); var profile = Profile("partial", output.Port);
    var eye = Fixture(profile, "eye", new()); var face = Fixture(profile, "face", new() { ["jaw"] = .66f });
    using var probe = Start(profile, eye, face, "Beyond");
    await probe.WaitReady();
    await Until(() => probe.Status(status => !status.GetProperty("eyeAvailable").GetBoolean() && status.GetProperty("faceAvailable").GetBoolean()
        && Near(status.GetProperty("eyeLeft").GetSingle(), 1) && Near(status.GetProperty("jawOpen").GetSingle(), .66f)), "The live mouth was suppressed by an offline eye provider.");
    await Until(() => output.Has("/v2/JawOpen", .66f), "A waiting eye module blocked outgoing mouth OSC.");
    await probe.Stop();
});

await Test("two offline sources remain selectable without claiming channels or sending neutral tracking", async () =>
{
    using var output = new OscCapture(); var profile = Profile("both-offline", output.Port);
    using var probe = Start(profile, Fixture(profile, "eye", new()), Fixture(profile, "face", new()), "Beyond", "Beyond");
    await probe.WaitReady();
    await Until(() => probe.Status(status => !status.GetProperty("eyeAvailable").GetBoolean() && !status.GetProperty("faceAvailable").GetBoolean()
        && status.GetProperty("eyeSourceState").GetString() == "Waiting" && status.GetProperty("faceSourceState").GetString() == "Waiting"),
        "The offline pair did not retain independent hardware-wait states.");
    Check(output.SourcePorts.Count == 0, "An offline pair sent neutral avatar tracking before a source became available.");
    await probe.Stop();
});

await Test("stale inputs go neutral while the other source stays live and a crashed child recovers independently", async () =>
{
    using var output = new OscCapture(); var profile = Profile("recovery", output.Port);
    var eye = Fixture(profile, "eye", new() { ["left"] = .2f }); var face = Fixture(profile, "face", new() { ["jaw"] = .7f });
    using var probe = Start(profile, eye, face); await probe.WaitReady();
    await Until(() => probe.Status(status => status.GetProperty("eyeAvailable").GetBoolean() && status.GetProperty("faceAvailable").GetBoolean()), "Inputs never became live.");
    var before = probe.LatestStatus(); var facePid = before.GetProperty("faceWorkerPid").GetInt32();
    File.WriteAllText(Path.Combine(Path.GetDirectoryName(eye)!, "fixture-pause.flag"), "pause");
    await Until(() => probe.StatusAfter(before, status => !status.GetProperty("eyeAvailable").GetBoolean() && status.GetProperty("faceAvailable").GetBoolean()
        && Near(status.GetProperty("eyeLeft").GetSingle(), 1) && Near(status.GetProperty("jawOpen").GetSingle(), .7f)), "Stale eyes were held or erased the live face.");
    File.Delete(Path.Combine(Path.GetDirectoryName(eye)!, "fixture-pause.flag"));
    var crashedAt = probe.MessageCount;
    using (var child = Process.GetProcessById(facePid)) child.Kill();
    await Until(() => probe.Messages.Skip(crashedAt).Any(status => Type(status) == "status"
        && status.GetProperty("faceWorkerPid").GetInt32() is var pid && pid > 0 && pid != facePid
        && status.GetProperty("faceAvailable").GetBoolean() && Near(status.GetProperty("jawOpen").GetSingle(), .7f)), "The crashed face input did not restart independently.");
    await probe.Stop();
});

await Test("identical module selections keep the existing single-module path", async () =>
{
    using var output = new OscCapture(); var profile = Profile("same", output.Port); var module = Fixture(profile, "one", new());
    using var probe = Start(profile, module, module); await probe.WaitReady();
    Check(probe.Messages.Where(message => Type(message) == "ready").All(message => !message.TryGetProperty("combined", out var combined) || !combined.GetBoolean()),
        "Identical module selection unnecessarily created combined workers.");
    await Until(() => output.Has("/v2/JawOpen", .6f), "The existing single-module output path changed.");
    await probe.Stop();
});

await Test("malformed child snapshots remain isolated and do not break normal coordinator shutdown", async () =>
{
    using var output = new OscCapture(); var profile = Profile("malformed", output.Port);
    var eye = Fixture(profile, "eye", new()); var face = Fixture(profile, "face", new() { ["jaw"] = .63f });
    File.WriteAllText(Path.Combine(Path.GetDirectoryName(eye)!, "fixture-mode.txt"), "malformed-snapshot");
    using var probe = Start(profile, eye, face); await probe.WaitReady();
    await Until(() => probe.Status(status => status.GetProperty("eyeSourceState").GetString() == "Failed"
        && !status.GetProperty("eyeAvailable").GetBoolean() && status.GetProperty("faceAvailable").GetBoolean()),
        "A malformed snapshot killed its supervisor instead of isolating the failed input.");
    await Until(() => output.Has("/v2/JawOpen", .63f), "A malformed eye source suppressed the valid face source.");
    await probe.Stop();
});

await Test("two invalid SDK modules fail before ready and cannot leave orphan workers", async () =>
{
    using var output = new OscCapture(); var profile = Profile("bad-pair", output.Port);
    var eye = Fixture(profile, "eye", new()); var face = Fixture(profile, "face", new());
    File.WriteAllText(Path.Combine(Path.GetDirectoryName(eye)!, "fixture-mode.txt"), "unsupported-sdk");
    File.WriteAllText(Path.Combine(Path.GetDirectoryName(face)!, "fixture-mode.txt"), "unsupported-sdk");
    using var probe = Start(profile, eye, face);
    await Until(() => probe.Messages.Any(message => Type(message) == "fatal"), "Both invalid input modules did not fail startup.");
    Check(!probe.Messages.Any(message => Type(message) == "ready") && output.SourcePorts.Count == 0,
        "An invalid pair announced ready or emitted tracking before validation.");
    await probe.WaitExit();
    foreach (var file in Directory.GetFiles(profile, "fixture-initialized.txt", SearchOption.AllDirectories))
        Check(!Alive(int.Parse(File.ReadAllText(file))), "A failed source remained alive after coordinator startup failed.");
});

await Test("inputs that fail before their first valid frame cannot commit a combined startup", async () =>
{
    using var output = new OscCapture(); var profile = Profile("bad-first-frame", output.Port);
    var eye = Fixture(profile, "eye", new()); var face = Fixture(profile, "face", new());
    File.WriteAllText(Path.Combine(Path.GetDirectoryName(eye)!, "fixture-mode.txt"), "malformed-snapshot");
    File.WriteAllText(Path.Combine(Path.GetDirectoryName(face)!, "fixture-mode.txt"), "malformed-snapshot");
    using var probe = Start(profile, eye, face);
    await Until(() => probe.Messages.Any(message => Type(message) == "fatal"), "Inputs with no valid frames did not fail startup.");
    Check(!probe.Messages.Any(message => Type(message) == "ready") && output.SourcePorts.Count == 0,
        "A pair announced ready before either module completed a valid first frame.");
    await probe.WaitExit();
});

await Test("Babble's overlapping desktop listeners are rejected before either module starts", async () =>
{
    using var output = new OscCapture(); var profile = Profile("ports", output.Port);
    var eye = Fixture(profile, "eye", new()); var face = Fixture(profile, "face", new());
    using var probe = Start(profile, eye, face, "BabbleHeadsetRuntime", "BaballoniaDesktop");
    await Until(() => probe.Messages.Any(message => Type(message) == "fatal"), "Overlapping input listeners were not rejected.");
    Check(probe.Messages.Any(message => Type(message) == "fatal" && message.GetProperty("message").GetString()!.Contains("both need UDP 8888")), "Port collision did not explain the conflicting listener.");
    Check(!Directory.GetFiles(profile, "fixture-initialized.txt", SearchOption.AllDirectories).Any(), "A conflicting pair launched input modules before preflight finished.");
    await probe.WaitExit();
});

await Test("abrupt coordinator termination closes its input-worker lifetime job", async () =>
{
    using var output = new OscCapture(); var profile = Profile("orphan", output.Port);
    using var probe = Start(profile, Fixture(profile, "eye", new()), Fixture(profile, "face", new()));
    await Until(() => probe.Status(status => status.GetProperty("eyeAvailable").GetBoolean() && status.GetProperty("faceAvailable").GetBoolean()), "Orphan test never started both inputs.");
    var status = probe.LatestStatus(); var eyePid = status.GetProperty("eyeWorkerPid").GetInt32(); var facePid = status.GetProperty("faceWorkerPid").GetInt32();
    probe.KillCoordinator();
    await Until(() => !Alive(eyePid) && !Alive(facePid), "Terminating the coordinator orphaned an input worker.");
});

await Test("input-only workers publish bounded snapshots without opening avatar feedback or output sockets", async () =>
{
    using var output = new OscCapture(); var profile = Profile("input-only", output.Port); var module = Fixture(profile, "input", new());
    using var probe = new Probe(worker, dotnet, ["--module", module, "--kind", "Custom", "--input-only", "--data", profile, "--no-discovery"]);
    await probe.WaitReady();
    using var feedback = new UdpClient(new IPEndPoint(IPAddress.Loopback, AppSettings.Load(profile).ReceivePort));
    var before = probe.Messages.Count(message => Type(message) == "input");
    await Task.Delay(1100);
    var snapshots = probe.Messages.Where(message => Type(message) == "input").ToArray();
    Check(snapshots.Length - before is > 35 and < 110, "Snapshot delivery exceeded its90Hz bound or did not progress.");
    Check(output.SourcePorts.Count == 0, "An input-only worker sent avatar OSC.");
    foreach (var snapshot in snapshots)
    {
        var value = snapshot.Deserialize<ModuleChannelSnapshot>()!;
        Check(snapshot.GetRawText().Length < ModuleChannelSnapshot.MaximumLineLength && value.IsValid(value.Shapes.Length), "The input worker published an invalid or oversized snapshot.");
    }
    await probe.Stop();
});

if (args.Contains("--etvr-dll") && args.Contains("--baballonia-zip"))
await Test("official EyeTrackVR and Baballonia modules merge their real input protocols", async () =>
{
    using var output = new OscCapture(); var profile = Profile("official-etvr-babble", output.Port);
    var store = new ModuleStore(profile);
    var eyes = store.Prepare(Required("--etvr-dll"), ModuleKind.EyeTrackVR);
    var face = store.Prepare(Required("--baballonia-zip"), ModuleKind.BaballoniaDesktop);
    var eyePort = FreePort(); var facePort = FreePort();
    File.WriteAllText(Path.Combine(eyes.DirectoryPath, "ETVRModuleConfig.json"), JsonSerializer.Serialize(new
    {
        ListeningAddress = "127.0.0.1", PortNumber = eyePort, ShouldEmulateEyeWiden = false, ShouldEmulateEyeSquint = false, ShouldEmulateEyebrows = false,
        WidenThresholdV1 = new[] { .6, 1 }, WidenThresholdV2 = new[] { .6, 1.05 }, SqueezeThresholdV1 = new[] { .07, .5 },
        SqueezeThresholdV2 = new[] { .07, -1 }, EyebrowThresholdRising = .8, EyebrowThresholdLowering = .15, OutputMultiplier = 1
    }));
    using var probe = Start(profile, Path.Combine(eyes.DirectoryPath, eyes.DllFileName), Path.Combine(face.DirectoryPath, face.DllFileName),
        "EyeTrackVR", "BaballoniaDesktop", ["--face-input-port", facePort.ToString()]);
    await probe.WaitReady();
    await Until(() => probe.Status(status => status.GetProperty("eyeAvailable").GetBoolean() && status.GetProperty("faceAvailable").GetBoolean()), "The official pair did not initialize.");
    using var input = new UdpClient();
    var clock = Stopwatch.StartNew();
    while (clock.Elapsed < TimeSpan.FromSeconds(1))
    {
        foreach (var (name, value) in new[] { ("EyeLidLeft", .22f), ("EyeLidRight", .88f), ("EyeLeftX", -.38f), ("EyeRightX", .42f) })
            await input.SendAsync(FloatMessage("/avatar/parameters/v2/" + name, value), new IPEndPoint(IPAddress.Loopback, eyePort));
        await input.SendAsync(FloatMessage("/jawOpen", .68f), new IPEndPoint(IPAddress.Loopback, facePort));
        await input.SendAsync(FloatMessage("/EyeLidLeft", .99f), new IPEndPoint(IPAddress.Loopback, facePort));
        await Task.Delay(10);
    }
    await Until(() => probe.Status(status => Near(status.GetProperty("eyeLeft").GetSingle(), .22f) && Near(status.GetProperty("jawOpen").GetSingle(), .68f)), "Official input data was not merged independently.");
    await Until(() => output.Has("/v2/EyeLeftX", -.38f) && output.Has("/v2/JawOpen", .68f), "The official pair did not reach the single avatar OSC stream.");
    Check(output.SourcePorts.Count == 1, "The official module pair created multiple avatar OSC streams.");
    await probe.Stop();
});

if (args.Contains("--beyond-zip"))
await Test("official Beyond validity and timestamp stop neutralize eyes while another module keeps the mouth live", async () =>
{
    using var memory = MemoryMappedFile.CreateNew("VRCFTMemmapData", 128);
    using var view = memory.CreateViewAccessor();
    view.Write(0, -.3f); view.Write(8, 1f); view.Write(12, .4f); view.Write(20, 1f); view.Write(48, 1); view.Write(52, .2f); view.Write(56, .6f);
    using var writerStop = new CancellationTokenSource();
    var writer = Task.Run(async () => { try { while (!writerStop.IsCancellationRequested) { view.Write(40, DateTime.UtcNow.Ticks); await Task.Delay(25, writerStop.Token); } } catch (OperationCanceledException) { } });
    using var output = new OscCapture(); var profile = Profile("official-beyond", output.Port);
    var module = new ModuleStore(profile).Prepare(Required("--beyond-zip"), ModuleKind.Beyond);
    using var probe = Start(profile, Path.Combine(module.DirectoryPath, module.DllFileName), Fixture(profile, "face", new() { ["jaw"] = .74f }), "Beyond");
    await probe.WaitReady();
    await Until(() => probe.Status(status => status.GetProperty("eyeAvailable").GetBoolean() && status.GetProperty("faceAvailable").GetBoolean()
        && Near(status.GetProperty("eyeLeft").GetSingle(), .8f)), "The real Beyond module did not feed the combined eye channel.");
    var before = probe.LatestStatus(); view.Write(48, 0);
    await Until(() => probe.StatusAfter(before, status => !status.GetProperty("eyeAvailable").GetBoolean()
        && status.GetProperty("faceAvailable").GetBoolean() && Near(status.GetProperty("eyeLeft").GetSingle(), 1)
        && Near(status.GetProperty("jawOpen").GetSingle(), .74f)), "Beyond IsValid=false held its eyes or suppressed the live mouth.");
    before = probe.LatestStatus(); view.Write(48, 1);
    await Until(() => probe.StatusAfter(before, status => status.GetProperty("eyeAvailable").GetBoolean()), "Beyond eyes did not recover from an invalid frame.");
    writerStop.Cancel(); await writer;
    before = probe.LatestStatus();
    await Until(() => probe.StatusAfter(before, status => !status.GetProperty("eyeAvailable").GetBoolean()
        && status.GetProperty("faceAvailable").GetBoolean()), "A stopped Beyond timestamp left stale eyes active.");
    await probe.Stop();
});

Console.WriteLine($"{passed} passed; {failed} failed. Profiles: {root}");
return failed == 0 ? 0 : 1;

string Profile(string name, int port) { var path = Path.Combine(root, name); Directory.CreateDirectory(path); new AppSettings { AutoStart = false, OutputPort = port, ReceivePort = FreePort() }.Save(path); return path; }
string Fixture(string profile, string name, Dictionary<string, float> values)
{
    var directory = Path.Combine(profile, name); Directory.CreateDirectory(directory);
    foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures"), "*.dll")) File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
    File.WriteAllText(Path.Combine(directory, "fixture-values.json"), JsonSerializer.Serialize(values));
    return Path.Combine(directory, "BabbleBridge.CustomModule.Fixture.dll");
}
Probe Start(string profile, string eye, string face, string eyeKind = "Custom", string faceKind = "Custom", string[]? extra = null) => new(worker, dotnet,
    ["--module", eye, "--kind", eyeKind, "--module-name", "Eye fixture", "--face-module", face, "--face-kind", faceKind, "--face-name", "Face fixture", "--data", profile, "--all-parameters", "--no-discovery", .. extra ?? []]);
async Task Test(string name, Func<Task> action) { try { await action(); passed++; Console.WriteLine("PASS " + name); } catch (Exception error) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + error); } }
static string Type(JsonElement message) => message.TryGetProperty("type", out var type) ? type.GetString() ?? "" : "";
static bool Near(float value, float target) => Math.Abs(value - target) < .025;
static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static int FreePort() { using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); return ((IPEndPoint)socket.Client.LocalEndPoint!).Port; }
static bool Alive(int pid) { if (pid <= 0) return false; try { using var process = Process.GetProcessById(pid); return !process.HasExited; } catch (ArgumentException) { return false; } }
static byte[] FloatMessage(string address, float value)
{
    var data = new byte[(Encoding.UTF8.GetByteCount(address) + 4) / 4 * 4 + 8]; Encoding.UTF8.GetBytes(address).CopyTo(data, 0);
    var offset = data.Length - 8; data[offset] = (byte)','; data[offset + 1] = (byte)'f';
    BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(offset + 4), BitConverter.SingleToInt32Bits(value)); return data;
}
static async Task Until(Func<bool> condition, string message) { var clock = Stopwatch.StartNew(); while (!condition()) { if (clock.Elapsed > TimeSpan.FromSeconds(12)) throw new TimeoutException(message); await Task.Delay(20); } }

sealed class Probe : IDisposable
{
    private readonly Process process;
    public readonly ConcurrentQueue<JsonElement> Messages = new();
    public int Id => process.Id;
    public int MessageCount => Messages.Count;
    public Probe(string worker, string? dotnet, string[] arguments)
    {
        var start = new ProcessStartInfo(dotnet ?? worker) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (dotnet is not null) start.ArgumentList.Add(worker); foreach (var argument in arguments) start.ArgumentList.Add(argument);
        process = Process.Start(start)!;
        process.OutputDataReceived += (_, args) => { if (args.Data is null) return; try { Messages.Enqueue(JsonDocument.Parse(args.Data).RootElement.Clone()); } catch (JsonException) { } };
        process.ErrorDataReceived += (_, args) => { if (args.Data is not null) Console.Error.WriteLine(args.Data); };
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
    }
    public bool Status(Func<JsonElement, bool> predicate) => Messages.Any(message => Kind(message) == "status" && predicate(message));
    public JsonElement LatestStatus() => Messages.Last(message => Kind(message) == "status");
    public bool StatusAfter(JsonElement previous, Func<JsonElement, bool> predicate)
    { var messages = Messages.ToArray(); var found = Array.FindLastIndex(messages, message => message.GetRawText() == previous.GetRawText()); return messages.Skip(found + 1).Any(message => Kind(message) == "status" && predicate(message)); }
    public async Task WaitReady()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        while (!Messages.Any(message => Kind(message) == "ready"))
        { if (process.HasExited || Messages.Any(message => Kind(message) == "fatal")) throw new InvalidOperationException(string.Join("\n", Messages)); await Task.Delay(20, timeout.Token); }
    }
    public async Task Stop() { await process.StandardInput.WriteLineAsync("stop"); await process.StandardInput.FlushAsync(); await WaitExit(); if (process.ExitCode != 0) throw new InvalidOperationException("Worker failed: " + string.Join("\n", Messages)); }
    public async Task WaitExit() => await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
    public void KillCoordinator() => process.Kill();
    public void Dispose() { try { if (!process.HasExited) process.Kill(true); } finally { process.Dispose(); } }
    private static string Kind(JsonElement message) => message.TryGetProperty("type", out var type) ? type.GetString() ?? "" : "";
}

sealed class OscCapture : IDisposable
{
    private readonly UdpClient socket = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly CancellationTokenSource stop = new();
    private readonly ConcurrentDictionary<string, float> values = new();
    public readonly ConcurrentDictionary<int, byte> SourcePorts = new();
    public int Port => ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    public bool Has(string suffix, float value) => values.Any(pair => pair.Key.EndsWith(suffix) && Math.Abs(pair.Value - value) < .03);
    public OscCapture() { socket.Client.ReceiveBufferSize = 1024 * 1024; _ = Read(); }
    private async Task Read()
    { try { while (!stop.IsCancellationRequested) { var packet = await socket.ReceiveAsync(stop.Token); SourcePorts.TryAdd(packet.RemoteEndPoint.Port, 0); Parse(packet.Buffer); } } catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException) { } }
    private void Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 8) return;
        if (bytes[..8].SequenceEqual("#bundle\0"u8))
        { var offset = 16; while (offset + 4 <= bytes.Length) { var count = BinaryPrimitives.ReadInt32BigEndian(bytes[offset..]); offset += 4; if (count <= 0 || count > bytes.Length - offset) return; Parse(bytes.Slice(offset, count)); offset += count; } return; }
        var end = bytes.IndexOf((byte)0); if (end <= 0) return; var address = Encoding.UTF8.GetString(bytes[..end]); var tag = (end + 4) / 4 * 4;
        if (tag + 8 <= bytes.Length && bytes[tag] == ',' && bytes[tag + 1] == 'f') values[address] = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(bytes[(tag + 4)..]));
    }
    public void Dispose() { stop.Cancel(); socket.Dispose(); stop.Dispose(); }
}
