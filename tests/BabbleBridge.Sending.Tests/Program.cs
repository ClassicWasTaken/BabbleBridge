// Copyright (c) 2026 Babble Bridge contributors.
// SPDX-License-Identifier: Apache-2.0
// Uses random loopback ports only; never starts OSCQuery/mDNS or a real module.
using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using VRCFaceTracking;
using VRCFaceTracking.Core.Contracts;
using VRCFaceTracking.Core.mDNS;
using VRCFaceTracking.Core.OSC;
using VRCFaceTracking.Core.OSC.DataTypes;
using VRCFaceTracking.Core.OSC.Query.mDNS;
using VRCFaceTracking.Core.Params.Data;
using VRCFaceTracking.Core.Services;

var passed = 0;
var failed = 0;
_ = new UnifiedTrackingMutator(NullLogger<UnifiedTrackingMutator>.Instance, null!);

await Test("90 Hz delivery has bounded jitter and prompt cancellation", async () =>
{
    using var capture = new Capture();
    using var transport = new OscSendService(NullLogger<OscSendService>.Instance, capture.Target);
    using var sender = new ParameterSenderService(transport);
    var ticks = new List<long>();
    var frame = new OscMessage("/test/frame", typeof(int));
    var sequence = 0;
    UnifiedTracking.OnUnifiedDataUpdated = _ =>
    {
        ticks.Add(Stopwatch.GetTimestamp());
        frame.Value = ++sequence;
        ParameterSenderService.Enqueue(frame);
    };
    await sender.StartAsync(CancellationToken.None);
    await Task.Delay(2200);
    var stopped = Stopwatch.StartNew();
    await sender.StopAsync(new CancellationTokenSource(1000).Token);
    Check(stopped.ElapsedMilliseconds < 300, "Frame cancellation stalled.");
    var intervals = ticks.Zip(ticks.Skip(1), (a, b) => (b - a) * 1000d / Stopwatch.Frequency).Order().ToArray();
    var hz = (ticks.Count - 1) * (double)Stopwatch.Frequency / (ticks[^1] - ticks[0]);
    Console.WriteLine($"  Measured {hz:F1} Hz, p95 interval {intervals[(int)(intervals.Length * .95)]:F2} ms ({ticks.Count} frames)");
    Check(hz is > 80 and < 100, "Frame scheduler is not sustaining approximately 90 Hz.");
    Check(intervals[(int)(intervals.Length * .95)] < 25, "Most frames are excessively late.");
    Check(sender.FramesProcessed == ticks.Count, "Frame diagnostics do not count actual updates.");
    await capture.Finish();
    var values = capture.Messages.Select(m => m.Int).ToArray();
    Check(values.Length >= ticks.Count - 2 && values.Distinct().Count() == values.Length, "Frame delivery lost or duplicated local packets.");
});

await Test("unchanged gaze keeps sending heartbeat frames", async () =>
{
    using var capture = new Capture();
    using var transport = new OscSendService(NullLogger<OscSendService>.Instance, capture.Target);
    using var sender = new ParameterSenderService(transport);
    UnifiedTracking.OnUnifiedDataUpdated = _ => { };
    var parameter = new BaseParam<float>("SendingGaze", _ => .375f);
    parameter.ResetParam(new IParameterDefinition[] { new Definition("/tracking/eye/SendingGaze", typeof(float)) });
    try
    {
        await sender.StartAsync(CancellationToken.None);
        await Task.Delay(2300);
        await sender.StopAsync(CancellationToken.None);
        await capture.Finish();
        var gaze = capture.Messages.Where(m => m.Address == "/tracking/eye/SendingGaze").ToArray();
        Check(gaze.Length >= 3, "A still gaze stopped sending after its first value.");
        Check(gaze.All(m => m.Float == .375f), "Heartbeat changed gaze values.");
        Check((gaze[^1].Received - gaze[0].Received).TotalSeconds > 1.5, "Resends were only a startup burst.");
    }
    finally { parameter.ResetParam(Array.Empty<IParameterDefinition>()); }
});

await Test("packets snapshot values while earlier delivery is pending", async () =>
{
    using var capture = new Capture();
    using var transport = new OscSendService(NullLogger<OscSendService>.Instance, capture.Target);
    using var entered = new ManualResetEventSlim();
    using var release = new ManualResetEventSlim();
    var dispatches = 0;
    transport.OnMessagesDispatched = _ =>
    {
        if (Interlocked.Increment(ref dispatches) != 1) return;
        entered.Set();
        if (!release.Wait(2000)) throw new TimeoutException("Pending send fixture timed out.");
    };
    var first = new OscMessage("/test/first", typeof(int)) { Value = 1 };
    var firstSend = Task.Run(() => transport.Send(first, CancellationToken.None));
    Check(entered.Wait(1000), "Did not enter first delivery.");
    var mutable = new OscMessage("/test/snapshot", typeof(float)) { Value = .25f };
    try
    {
        var secondSend = transport.Send(new[] { mutable }, CancellationToken.None);
        mutable.Address = "/test/later";
        mutable.Value = .75f;
        release.Set();
        await Task.WhenAll(firstSend, secondSend);
        await capture.Finish();
        var snapshot = capture.Messages.Single(m => m.Address == "/test/snapshot");
        Check(snapshot.Float == .25f && !capture.Messages.Any(m => m.Address == "/test/later"), "A later update rewrote an in-flight frame.");
    }
    finally { release.Set(); }
});

await Test("concurrent avatar rebinding cannot mix parameter address and value", async () =>
{
    using var capture = new Capture();
    using var transport = new OscSendService(NullLogger<OscSendService>.Instance, capture.Target);
    using var sender = new ParameterSenderService(transport);
    UnifiedTracking.OnUnifiedDataUpdated = _ => { };
    var generation = 0;
    var parameter = new BaseParam<float>("SendingValue", _ => generation);
    parameter.ResetParam(new IParameterDefinition[] { new Definition("/avatar/parameters/0/SendingValue", typeof(float)) });
    try
    {
        await sender.StartAsync(CancellationToken.None);
        await Task.Run(async () =>
        {
            for (var i = 1; i <= 180; i++)
            {
                lock (ParameterSenderService.FrameLock)
                {
                    ParameterSenderService.Clear();
                    generation = i;
                    parameter.ResetParam(new IParameterDefinition[] { new Definition($"/avatar/parameters/{i}/SendingValue", typeof(float)) });
                    ParameterSenderService.RequestFullResend();
                }
                await Task.Delay(2);
            }
        });
        await sender.StopAsync(CancellationToken.None);
        await capture.Finish();
        Check(capture.Messages.Count > 15, "Rebinding prevented frame delivery.");
        foreach (var message in capture.Messages)
            Check(float.Parse(message.Address.Split('/')[3]) == message.Float, "Avatar address and value came from different frames.");
    }
    finally { parameter.ResetParam(Array.Empty<IParameterDefinition>()); }
});

await Test("destination host changes take effect without changing the port", async () =>
{
    using var original = new Capture();
    using var changed = new Capture(IPAddress.Parse("127.0.0.2"), original.Target.OutPort);
    using var transport = new OscSendService(NullLogger<OscSendService>.Instance, original.Target);
    var message = new OscMessage("/test/target", typeof(int)) { Value = 1 };
    await transport.Send(message, CancellationToken.None);
    original.Target.DestinationAddress = "127.0.0.2";
    message.Value = 2;
    await transport.Send(message, CancellationToken.None);
    await original.Finish();
    await changed.Finish();
    Check(original.Messages.Count == 1 && original.Messages[0].Int == 1, "Old host kept receiving after the destination changed.");
    Check(changed.Messages.Count == 1 && changed.Messages[0].Int == 2, "New host did not receive tracking.");
});

await Test("binary parameters detach old avatar routes on rebind", async () =>
{
    using var capture = new Capture();
    using var transport = new OscSendService(NullLogger<OscSendService>.Instance, capture.Target);
    using var sender = new ParameterSenderService(transport);
    UnifiedTracking.OnUnifiedDataUpdated = _ => { };
    var parameter = new BinaryBaseParameter("SendingBinary", _ => .7f);
    static IParameterDefinition[] Definitions(string prefix) => new IParameterDefinition[] {
        new Definition($"/avatar/parameters/{prefix}/SendingBinary1", typeof(bool)),
        new Definition($"/avatar/parameters/{prefix}/SendingBinary2", typeof(bool)) };
    parameter.ResetParam(Definitions("old"));
    try
    {
        await sender.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        lock (ParameterSenderService.FrameLock)
        {
            ParameterSenderService.Clear();
            parameter.ResetParam(Definitions("new"));
            ParameterSenderService.RequestFullResend();
        }
        var rebound = DateTime.UtcNow;
        await Task.Delay(1200);
        await sender.StopAsync(CancellationToken.None);
        await capture.Finish();
        Check(capture.Messages.Any(m => m.Address.Contains("/old/")), "Old mapping fixture did not send.");
        Check(capture.Messages.Any(m => m.Address.Contains("/new/")), "New binary mapping did not send.");
        Check(!capture.Messages.Any(m => m.Received > rebound.AddMilliseconds(50) && m.Address.Contains("/old/")), "An obsolete avatar's binary parameters are still subscribed.");
    }
    finally { parameter.ResetParam(Array.Empty<IParameterDefinition>()); }
});

await Test("large batches split into complete bundles without missing messages", async () =>
{
    using var capture = new Capture();
    using var transport = new OscSendService(NullLogger<OscSendService>.Instance, capture.Target);
    var messages = Enumerable.Range(0, 250).Select(i => new OscMessage($"/test/bundle/{i}", typeof(int)) { Value = i }).ToArray();
    var dispatched = 0;
    transport.OnMessagesDispatched = count => dispatched += count;
    await transport.Send(messages, CancellationToken.None);
    await capture.Finish();
    Check(dispatched == messages.Length, "Dispatched count differs from the actual batch.");
    Check(capture.Messages.Count == messages.Length && capture.Messages.Select(m => m.Int).Distinct().Count() == messages.Length, "Split bundles dropped or duplicated messages.");
});

await Test("mDNS resolves reordered records by service and host name", () =>
{
    var packet = DiscoveryPacket();
    Check(MdnsClientResolver.TryResolve(packet, IPAddress.Loopback, Array.Empty<IPAddress>(), out var endpoint), "Reordered DNS response was ignored.");
    Check(endpoint.Equals(new IPEndPoint(IPAddress.Loopback, 45678)), "Unrelated DNS record supplied the OSCQuery endpoint.");
    return Task.CompletedTask;
});

await Test("mDNS rejects other PCs and remote addresses advertised by a local peer", () =>
{
    var packet = DiscoveryPacket();
    Check(!MdnsClientResolver.TryResolve(packet, IPAddress.Parse("192.0.2.99"), new[] { IPAddress.Parse("192.0.2.2") }, out _), "Another PC was accepted as this game's discovery peer.");
    foreach (var record in packet.answers.Concat(packet.additionals))
        if (record.Data is ARecord a) a.Address = IPAddress.Parse("192.0.2.99");
    Check(!MdnsClientResolver.TryResolve(packet, IPAddress.Loopback, Array.Empty<IPAddress>(), out _), "A remote advertised host escaped same-PC discovery scope.");
    return Task.CompletedTask;
});

Console.WriteLine($"{passed} sending tests passed; {failed} failed.");
return failed == 0 ? 0 : 1;

async Task Test(string name, Func<Task> test)
{
    var previous = UnifiedTracking.OnUnifiedDataUpdated;
    ParameterSenderService.Clear();
    try { await test(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + ": " + e); }
    finally { UnifiedTracking.OnUnifiedDataUpdated = previous; ParameterSenderService.Clear(); }
}
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static DnsPacket DiscoveryPacket()
{
    var service = new List<string> { "VRChat-Client-test", "_oscjson", "_tcp", "local" };
    var host = new List<string> { "game", "local" };
    return new DnsPacket
    {
        QUERYRESPONSE = true,
        answers = new[] {
            new DnsResource(new ARecord { Address = IPAddress.Loopback }, new List<string> { "unrelated", "local" }),
            new DnsResource(new SRVRecord { Port = 45678, Target = host }, service),
            new DnsResource(new PTRRecord { DomainLabels = service }, new List<string> { "_oscjson", "_tcp", "local" }) },
        additionals = new[] { new DnsResource(new ARecord { Address = IPAddress.Loopback }, host) }
    };
}

sealed record Definition(string Address, Type Type) : IParameterDefinition { public string Name => Address; }
sealed class Target : IOscTarget
{
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    public bool IsConnected { get; set; }
    public int InPort { get; set; }
    public int OutPort { get; set; }
    public string DestinationAddress { get; set; } = "127.0.0.1";
}
sealed record Message(string Address, float? Float, int? Int, DateTime Received);
sealed class Capture : IDisposable
{
    private readonly UdpClient udp;
    private readonly CancellationTokenSource stop = new();
    private readonly Task receive;
    public Target Target { get; }
    public List<Message> Messages { get; } = new();
    public Capture(IPAddress? address = null, int port = 0)
    {
        udp = new UdpClient(new IPEndPoint(address ?? IPAddress.Loopback, port));
        udp.Client.ReceiveBufferSize = 1 << 20;
        Target = new Target { DestinationAddress = (address ?? IPAddress.Loopback).ToString(), OutPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port };
        receive = Receive();
    }
    private async Task Receive()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var packet = await udp.ReceiveAsync(stop.Token);
                Read(packet.Buffer);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
    public async Task Finish() { await Task.Delay(50); stop.Cancel(); await receive; }
    private void Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith("#bundle\0"u8))
        {
            var offset = 16;
            while (offset < bytes.Length)
            {
                var length = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(offset, 4));
                offset += 4;
                Read(bytes.Slice(offset, length));
                offset += length;
            }
            return;
        }
        var position = 0;
        var address = ReadString(bytes, ref position);
        var tags = ReadString(bytes, ref position);
        if (tags is ",T" or ",F")
        {
            if (position != bytes.Length) throw new Exception("Malformed boolean OSC size.");
            Messages.Add(new Message(address, null, tags == ",T" ? 1 : 0, DateTime.UtcNow));
            return;
        }
        if (tags is not ",f" and not ",i") throw new Exception("Malformed fixture OSC type tag: " + tags);
        var value = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(position, 4));
        if (position + 4 != bytes.Length) throw new Exception("Malformed fixture OSC size.");
        Messages.Add(new Message(address, tags == ",f" ? BitConverter.Int32BitsToSingle(value) : null, tags == ",i" ? value : null, DateTime.UtcNow));
    }
    private static string ReadString(ReadOnlySpan<byte> bytes, ref int position)
    {
        var end = bytes[position..].IndexOf((byte)0) + position;
        if (end < position) throw new Exception("Missing OSC terminator.");
        var value = Encoding.UTF8.GetString(bytes[position..end]);
        position = (end + 4) & ~3;
        return value;
    }
    public void Dispose() { stop.Cancel(); udp.Dispose(); stop.Dispose(); }
}
