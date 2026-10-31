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

string Required(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? Path.GetFullPath(args[index + 1]) : throw new ArgumentException("Missing " + name);
}
var worker = Required("--worker");
var etvr = Required("--etvr-dll");
var beyond = Required("--beyond-zip");
var sr = Required("--sranipal-zip");
var dotnet = args.Contains("--dotnet") ? Required("--dotnet") : null;
var root = Path.Combine(Environment.CurrentDirectory, "work", "official-runtime-tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var passed = 0;
var failed = 0;

await Test("official ETVR DLL initializes and carries independent eyes from loopback OSC to avatar OSC", async () =>
{
    using var output = Socket();
    var data = Path.Combine(root, "etvr");
    var release = new ModuleStore(data).Prepare(etvr, ModuleKind.EyeTrackVR);
    var inputPort = FreePort();
    File.WriteAllText(Path.Combine(release.DirectoryPath, "ETVRModuleConfig.json"), JsonSerializer.Serialize(new
    {
        ListeningAddress = "127.0.0.1", PortNumber = inputPort,
        ShouldEmulateEyeWiden = false, ShouldEmulateEyeSquint = false, ShouldEmulateEyebrows = false,
        WidenThresholdV1 = new[] { .6, 1 }, WidenThresholdV2 = new[] { .6, 1.05 },
        SqueezeThresholdV1 = new[] { .07, .5 }, SqueezeThresholdV2 = new[] { .07, -1 },
        EyebrowThresholdRising = .8, EyebrowThresholdLowering = .15, OutputMultiplier = 1
    }));
    using var probe = Start(release, data, Port(output));
    await probe.Wait("ready");
    Check(probe.Last("ready").GetProperty("eye").GetBoolean() && !probe.Last("ready").GetProperty("face").GetBoolean(), "ETVR did not claim eye-only tracking.");
    Check(probe.Last("ready").GetProperty("inputPort").GetInt32() == inputPort, "ETVR configured input port was not reflected by the worker.");
    using var input = new UdpClient();
    var clock = Stopwatch.StartNew();
    while (clock.Elapsed < TimeSpan.FromSeconds(1))
    {
        foreach (var (name, value) in new[] { ("EyeLidLeft", .25f), ("EyeLidRight", .85f), ("EyeLeftX", -.35f), ("EyeLeftY", .2f), ("EyeRightX", .4f), ("EyeRightY", -.15f), ("PupilDilation", 4.2f) })
            await input.SendAsync(FloatMessage("/avatar/parameters/v2/" + name, value), new IPEndPoint(IPAddress.Loopback, inputPort));
        await Task.Delay(10);
    }
    await Until(() => probe.Status(status => Math.Abs(status.GetProperty("eyeLeft").GetDouble() - .25) < .025
        && Math.Abs(status.GetProperty("eyeRight").GetDouble() - .85) < .025), "ETVR eye packet did not reach unified tracking.");
    var values = await ReadFloats(output, TimeSpan.FromMilliseconds(1400));
    Check(values.Any(pair => pair.Key.EndsWith("/v2/EyeLeftX") && pair.Value < -.1f), "ETVR left gaze did not reach avatar OSC.");
    Check(values.Any(pair => pair.Key.EndsWith("/v2/EyeRightX") && pair.Value > .1f), "ETVR right gaze did not reach avatar OSC.");
    Check(values.Any(pair => pair.Key.EndsWith("/v2/EyeLidLeft") && Math.Abs(pair.Value - .25f * .75f) < .03), "ETVR blink did not reach avatar OSC.");
    await probe.Stop();
});

await Test("official Beyond DLL waits offline and reads synthetic gaze and blink shared memory", async () =>
{
    const string name = "VRCFTMemmapData";
    using var output = Socket();
    var data = Path.Combine(root, "beyond");
    var release = new ModuleStore(data).Prepare(beyond, ModuleKind.Beyond);
    using var probe = Start(release, data, Port(output));
    await probe.Wait("waiting");
    Check(output.Available == 0, "Beyond sent neutral tracking while its runtime was unavailable.");
    // CreateNew deliberately fails instead of opening or modifying real headset software's mapping.
    using var memory = MemoryMappedFile.CreateNew(name, 128);
    using var view = memory.CreateViewAccessor();
    view.Write(0, -.3f); view.Write(4, .2f); view.Write(8, 1f);
    view.Write(12, .4f); view.Write(16, -.2f); view.Write(20, 1f);
    view.Write(36, 1f); view.Write(40, DateTime.UtcNow.Ticks); view.Write(48, 1);
    view.Write(52, .25f); view.Write(56, .65f);
    await probe.Wait("ready");
    Check(probe.Last("ready").GetProperty("eye").GetBoolean() && !probe.Last("ready").GetProperty("face").GetBoolean(), "Beyond did not claim eye-only tracking.");
    await Until(() => probe.Status(status => Math.Abs(status.GetProperty("eyeLeft").GetDouble() - .75) < .001
        && Math.Abs(status.GetProperty("eyeRight").GetDouble() - .35) < .001), "Beyond shared memory did not reach unified blink tracking.");
    var values = await ReadFloats(output, TimeSpan.FromMilliseconds(700));
    Check(values.Any(pair => pair.Key.EndsWith("/v2/EyeLeftX") && pair.Value < -.1f)
        && values.Any(pair => pair.Key.EndsWith("/v2/EyeRightX") && pair.Value > .1f), "Beyond gaze did not reach avatar OSC.");
    await probe.Stop();
});

await Test("official SRanipal DLL loads and waits for its required runtime without sending neutral tracking", async () =>
{
    var running = Process.GetProcessesByName("sr_runtime");
    try { Check(running.Length == 0, "Run the isolated SRanipal offline test after closing SRanipal; the test will not touch a real runtime."); }
    finally { foreach (var process in running) process.Dispose(); }
    using var output = Socket();
    var data = Path.Combine(root, "sr");
    var release = new ModuleStore(data).Prepare(sr, ModuleKind.SRanipal);
    using var probe = Start(release, data, Port(output));
    await probe.Wait("waiting");
    await Task.Delay(300);
    Check(!probe.Messages.Any(message => message.GetProperty("type").GetString() == "ready") && output.Available == 0,
        "An offline SRanipal module sent neutral tracking or incorrectly reported ready.");
    await probe.Stop();
});

Console.WriteLine($"{passed} passed; {failed} failed. Isolated fixture data: {root}");
return failed == 0 ? 0 : 1;

async Task Test(string name, Func<Task> action)
{
    try { await action(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception error) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + error); }
}
WorkerProbe Start(ModuleRelease release, string data, int outputPort)
{
    new AppSettings { AutoStart = false, OutputPort = outputPort, ReceivePort = FreePort() }.Save(data);
    return new WorkerProbe(worker, dotnet, release, data);
}
static UdpClient Socket()
{
    var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    socket.Client.ReceiveBufferSize = 1024 * 1024;
    return socket;
}
static int Port(UdpClient socket) => ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
static int FreePort() { using var socket = Socket(); return Port(socket); }
static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static async Task Until(Func<bool> condition, string message)
{
    var timer = Stopwatch.StartNew();
    while (!condition()) { if (timer.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException(message); await Task.Delay(20); }
}
static byte[] FloatMessage(string address, float value)
{
    var bytes = new byte[((Encoding.UTF8.GetByteCount(address) + 4) / 4) * 4 + 8];
    Encoding.UTF8.GetBytes(address).CopyTo(bytes, 0);
    var offset = bytes.Length - 8; bytes[offset] = (byte)','; bytes[offset + 1] = (byte)'f';
    BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(offset + 4), BitConverter.SingleToInt32Bits(value));
    return bytes;
}
static async Task<Dictionary<string, float>> ReadFloats(UdpClient output, TimeSpan duration)
{
    var values = new Dictionary<string, float>();
    using var stop = new CancellationTokenSource(duration);
    while (!stop.IsCancellationRequested)
    {
        try { Parse((await output.ReceiveAsync(stop.Token)).Buffer, values); }
        catch (OperationCanceledException) { break; }
    }
    return values;
}
static void Parse(ReadOnlySpan<byte> bytes, Dictionary<string, float> values)
{
    if (bytes.Length < 8) return;
    if (bytes[..8].SequenceEqual("#bundle\0"u8))
    {
        var index = 16;
        while (index + 4 <= bytes.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(bytes[index..]); index += 4;
            if (length <= 0 || length > bytes.Length - index) return;
            Parse(bytes.Slice(index, length), values); index += length;
        }
        return;
    }
    var end = bytes.IndexOf((byte)0);
    if (end < 1) return;
    var address = Encoding.UTF8.GetString(bytes[..end]); var offset = (end + 4) / 4 * 4;
    if (offset + 8 <= bytes.Length && bytes[offset] == ',' && bytes[offset + 1] == 'f')
        values[address] = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(bytes[(offset + 4)..]));
}

sealed class WorkerProbe : IDisposable
{
    private readonly Process _process;
    public readonly ConcurrentQueue<JsonElement> Messages = new();
    private readonly ConcurrentQueue<string> _errors = new();
    public WorkerProbe(string worker, string? dotnet, ModuleRelease release, string data)
    {
        var start = new ProcessStartInfo(dotnet ?? worker) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (dotnet != null) start.ArgumentList.Add(worker);
        foreach (var argument in new[] { "--module", Path.Combine(release.DirectoryPath, release.DllFileName), "--kind", release.Kind.ToString(),
            "--data", data, "--no-discovery", "--all-parameters" }) start.ArgumentList.Add(argument);
        _process = Process.Start(start) ?? throw new InvalidOperationException("Worker did not start.");
        _process.OutputDataReceived += (_, line) => { if (line.Data is null) return; try { Messages.Enqueue(JsonDocument.Parse(line.Data).RootElement.Clone()); } catch (JsonException) { _errors.Enqueue(line.Data); } };
        _process.ErrorDataReceived += (_, line) => { if (line.Data != null) _errors.Enqueue(line.Data); };
        _process.BeginOutputReadLine(); _process.BeginErrorReadLine();
    }
    public JsonElement Last(string type) => Messages.Last(message => message.GetProperty("type").GetString() == type);
    public bool Status(Func<JsonElement, bool> predicate) => Messages.Any(message => message.GetProperty("type").GetString() == "status" && predicate(message));
    public async Task Wait(string type)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!Messages.Any(message => message.GetProperty("type").GetString() == type))
        {
            if (_process.HasExited || Messages.Any(message => message.GetProperty("type").GetString() == "fatal"))
                throw new InvalidOperationException("Worker failed: " + string.Join("\n", Messages) + string.Join("\n", _errors));
            await Task.Delay(20, timeout.Token);
        }
    }
    public async Task Stop()
    {
        await _process.StandardInput.WriteLineAsync("stop"); await _process.StandardInput.FlushAsync();
        await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        if (_process.ExitCode != 0) throw new InvalidOperationException("Worker shutdown failed: " + string.Join("\n", Messages));
    }
    public void Dispose() { try { if (!_process.HasExited) _process.Kill(true); } finally { _process.Dispose(); } }
}
