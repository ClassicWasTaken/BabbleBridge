// Copyright (c) 2026 Frame Bridge contributors.
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Text;
using BabbleFrameBridge.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using VRCFaceTracking;
using VRCFaceTracking.Core.Contracts;
using VRCFaceTracking.Core.Params.Data;
using VRCFaceTracking.Core.Params.Expressions;
using VRCFaceTracking.Core.Types;

var passed = 0;
var failed = 0;

Test("OSC address, blob tag, big-endian length, full payload, and zero padding", () =>
{
    var bytes = Encode(NewData());
    Check(bytes.Length == 132, "Expected 132-byte OSC packet.");
    Check(Encoding.ASCII.GetString(bytes.AsSpan(0, 17)) == "/tracking/face/v1", "Wrong OSC address.");
    Check(bytes.AsSpan(17, 3).ToArray().All(v => v == 0), "Address not null-terminated/aligned.");
    Check(bytes.AsSpan(20, 4).SequenceEqual(new byte[] { 44, 98, 0, 0 }), "Wrong blob type tag/alignment.");
    Check(BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(24, 4)) == 102, "Blob length must be exactly 102.");
    Check(Payload(bytes).ToArray().All(v => v == 0), "Neutral face must not activate a tracking key.");
    Check(bytes[130] == 0 && bytes[131] == 0, "Blob padding is not zero.");
});

Test("separate eye gaze directions and independent eyelids use documented wire slots", () =>
{
    var data = NewData();
    data.Eye.Right.Gaze = new Vector2(1, -1);
    data.Eye.Left.Gaze = new Vector2(-1, 1);
    data.Eye.Right.Openness = 0;
    data.Eye.Left.Openness = .25f;
    var payload = Payload(Encode(data));
    Check(payload[..10].SequenceEqual(new byte[] { 255, 0, 0, 255, 255, 0, 255, 0, 255, 191 }), "Eye order, side, direction, or closed amount changed.");
    Check(payload[10..].ToArray().All(v => v == 0), "Gaze/blink contaminated another expression.");
});

Test("widen, actual brow shapes, jaw, and last neck shape retain independent protocol indices", () =>
{
    var data = NewData();
    data.Shapes[(int)UnifiedExpressions.EyeWideRight].Weight = 1;
    data.Shapes[(int)UnifiedExpressions.EyeWideLeft].Weight = .5f;
    data.Shapes[(int)UnifiedExpressions.BrowInnerUpRight].Weight = 1;
    data.Shapes[(int)UnifiedExpressions.BrowOuterUpLeft].Weight = .25f;
    data.Shapes[(int)UnifiedExpressions.JawOpen].Weight = 1;
    data.Shapes[(int)UnifiedExpressions.NeckFlexLeft].Weight = 1;
    var payload = Payload(Encode(data));
    var expected = new byte[102];
    expected[16] = 255;
    expected[17] = 127;
    expected[22] = 255;
    expected[25] = 63;
    expected[36] = 255;
    expected[101] = 255;
    Check(payload.SequenceEqual(expected), "A wire index changed or an unrelated key was activated.");
});

Test("pupil dilation and constriction are sent for each eye and remain mutually exclusive", () =>
{
    var data = NewData();
    data.Eye.Right.PupilDiameter_MM = 8;
    data.Eye.Left.PupilDiameter_MM = 2;
    var payload = Payload(Encode(data));
    Check(payload[10] == 255 && payload[11] == 0 && payload[12] == 0 && payload[13] == 255, "Dilation/constriction is missing or swapped.");
    data.Eye.Right.PupilDiameter_MM = 3.5f;
    data.Eye.Left.PupilDiameter_MM = 6.5f;
    payload = Payload(Encode(data));
    Check(payload[10] == 0 && payload[11] == 127 && payload[12] == 127 && payload[13] == 0, "Pupil halves must scale independently from neutral.");
    data.Eye.Right.PupilDiameter_MM = data.Eye.Left.PupilDiameter_MM = 5;
    Check(Payload(Encode(data))[10..14].ToArray().All(v => v == 0), "Neutral pupil must activate neither opposing shape.");
});

Test("missing and invalid measurements stay neutral; strengths cannot overflow bytes", () =>
{
    var data = NewData();
    data.Eye.Left.PupilDiameter_MM = float.NaN;
    data.Eye.Right.PupilDiameter_MM = 0;
    data.Eye.Right.Gaze = new Vector2(float.NaN, float.PositiveInfinity);
    data.Eye.Left.Gaze = new Vector2(-3, 3);
    data.Eye.Left.Openness = float.NaN;
    data.Shapes[(int)UnifiedExpressions.JawOpen].Weight = 9;
    data.Shapes[(int)UnifiedExpressions.EyeWideLeft].Weight = -9;
    data.Shapes[(int)UnifiedExpressions.BrowInnerUpLeft].Weight = float.PositiveInfinity;
    var payload = Payload(Encode(data));
    Check(payload[..4].ToArray().All(v => v == 0), "Invalid gaze must be zero.");
    Check(payload[4] == 255 && payload[6] == 255 && payload[9] == 0, "Gaze was not clamped or invalid blink leaked.");
    Check(payload[10..14].ToArray().All(v => v == 0), "Missing pupils must not create full dilation/constriction.");
    Check(payload[36] == 255 && payload[17] == 0 && payload[23] == 0, "Shape strengths are not finite and clamped.");
});

Test("reusing the packet clears unsupported keys and alignment bytes", () =>
{
    var data = NewData();
    var packet = Enumerable.Repeat((byte)255, NativeFaceSender.PacketLength).ToArray();
    data.Shapes[(int)UnifiedExpressions.JawOpen].Weight = 1;
    NativeFaceSender.WritePacket(data, packet);
    data.Shapes = [];
    NativeFaceSender.WritePacket(data, packet);
    Check(Payload(packet).ToArray().All(v => v == 0) && packet[130] == 0 && packet[131] == 0, "Previous frame left stale tracking keys.");
    var rejected = false;
    try { NativeFaceSender.WritePacket(data, new byte[131]); }
    catch (ArgumentException) { rejected = true; }
    Check(rejected, "Short destination buffer was accepted.");
});

Test("native sends require opt-in, repeat unchanged frames, follow target changes, and stop on disposal", () =>
{
    using var first = Receiver();
    using var second = Receiver();
    var target = new TestTarget { OutPort = ((IPEndPoint)first.Client.LocalEndPoint!).Port };
    using var sender = new NativeFaceSender(target, NullLogger<NativeFaceSender>.Instance);
    var data = NewData();
    data.Shapes[(int)UnifiedExpressions.EyeWideLeft].Weight = 1;
    sender.Start();
    sender.Start();
    UnifiedTracking.OnUnifiedDataUpdated.Invoke(data);
    Check(first.Available == 0 && sender.FramesSent == 0, "Disabled native route emitted data.");
    sender.Enabled = true;
    UnifiedTracking.OnUnifiedDataUpdated.Invoke(data);
    Check(Receive(first).SequenceEqual(Encode(data)) && sender.FramesSent == 1, "Native packet or frame count is wrong.");
    UnifiedTracking.OnUnifiedDataUpdated.Invoke(data);
    Check(Receive(first).SequenceEqual(Encode(data)) && sender.FramesSent == 2, "Unchanged face was not sent as a complete frame.");
    target.OutPort = ((IPEndPoint)second.Client.LocalEndPoint!).Port;
    UnifiedTracking.OnUnifiedDataUpdated.Invoke(data);
    Check(Receive(second).SequenceEqual(Encode(data)) && first.Available == 0 && sender.FramesSent == 3, "Native route did not follow the OSCQuery target.");
    sender.Enabled = false;
    UnifiedTracking.OnUnifiedDataUpdated.Invoke(data);
    sender.Enabled = true;
    sender.Dispose();
    UnifiedTracking.OnUnifiedDataUpdated.Invoke(data);
    Check(second.Available == 0 && sender.FramesSent == 3, "Disabled/disposed sender still emitted packets.");
});

Test("concurrent source updates cannot mix frames in the shared packet buffer", () =>
{
    using var receiver = Receiver();
    var target = new TestTarget { OutPort = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port };
    using var sender = new NativeFaceSender(target, NullLogger<NativeFaceSender>.Instance) { Enabled = true };
    sender.Start();
    var frames = Enumerable.Range(0, 32).Select(i =>
    {
        var data = NewData();
        data.Shapes[(int)UnifiedExpressions.JawOpen].Weight = i / 31f;
        data.Shapes[(int)UnifiedExpressions.EyeWideLeft].Weight = 1 - i / 31f;
        data.Eye.Right.PupilDiameter_MM = 2 + 6 * i / 31f;
        return data;
    }).ToArray();
    var expected = frames.Select(data => Convert.ToHexString(Encode(data))).ToHashSet();
    Parallel.ForEach(frames, data => UnifiedTracking.OnUnifiedDataUpdated.Invoke(data));
    var received = Enumerable.Range(0, frames.Length).Select(_ => Convert.ToHexString(Receive(receiver))).ToHashSet();
    Check(sender.FramesSent == frames.Length && received.SetEquals(expected), "Concurrent events mixed, lost, or duplicated frame contents.");
});

Test("managed avatar reader accepts zero-data OSC boolean tags and one string", () =>
{
    const string capability = "/avatar/avatarfullfacetracking";
    var yes = AvatarOscReader.Read(OscPacket(capability, ",T")).ToArray();
    var no = AvatarOscReader.Read(OscPacket(capability, ",F")).ToArray();
    var avatar = AvatarOscReader.Read(OscPacket("/avatar/change", ",s", OscString("avtr_protocol_test"))).ToArray();
    Check(yes.Length == 1 && yes[0].Address == capability && yes[0].Value is true, "OSC T tag was not read as true.");
    Check(no.Length == 1 && no[0].Address == capability && no[0].Value is false, "OSC F tag was not read as false.");
    Check(avatar.Length == 1 && avatar[0].Address == "/avatar/change" && avatar[0].Value is string id && id == "avtr_protocol_test", "Avatar string was not read.");
});

Test("managed avatar reader preserves mixed nested bundle order and ignores other addresses", () =>
{
    var bytes = OscBundle(
        OscPacket("/avatar/parameters/Unrelated", ",T"),
        OscPacket("/avatar/change", ",s", OscString("avtr_protocol_test")),
        OscBundle(OscPacket("/avatar/avatarfullfacetracking", ",T"),
            OscBundle(OscPacket("/avatar/avatarfullfacetracking", ",F"))));
    var values = AvatarOscReader.Read(bytes).ToArray();
    Check(values.Length == 3, "Nested bundle messages were lost or unrelated address leaked.");
    Check(values[0].Value is string id && id == "avtr_protocol_test" && values[1].Value is true && values[2].Value is false,
        "Bundle processing changed the arrival order or argument types.");
});

Test("managed avatar reader rejects malformed tags, padding, lengths, UTF-8, and truncated packets", () =>
{
    var valid = OscPacket("/avatar/avatarfullfacetracking", ",T");
    for (var i = 0; i < valid.Length; i++)
        Check(!AvatarOscReader.Read(valid[..i]).Any(), "A truncated message was accepted.");
    var malformed = new List<byte[]>
    {
        OscPacket("/avatar/avatarfullfacetracking", ",TF"),
        OscPacket("/avatar/avatarfullfacetracking", ",T", new byte[4]),
        OscPacket("/avatar/avatarfullfacetracking", ",i", new byte[4]),
        OscPacket("/avatar/change", ",ss", OscString("one"), OscString("two")),
        OscPacket("/avatar/change", ",s", new byte[] { 255, 255, 255, 255 }),
        OscPacket("/avatar/change", ",s", new byte[] { 192, 175, 0, 0 }),
        new byte[65536],
    };
    var invalidPadding = valid.ToArray();
    invalidPadding[^1] = 1;
    malformed.Add(invalidPadding);
    foreach (var length in new[] { 0, -1, int.MaxValue, 3, valid.Length + 4 })
    {
        var bundle = OscBundle(valid);
        BinaryPrimitives.WriteInt32BigEndian(bundle.AsSpan(16, 4), length);
        malformed.Add(bundle);
    }
    foreach (var packet in malformed)
        Check(!AvatarOscReader.Read(packet).Any(), "Malformed message was accepted.");
});

Test("managed avatar reader bounds nesting and safely handles arbitrary datagrams", () =>
{
    var deep = OscPacket("/avatar/avatarfullfacetracking", ",T");
    for (var i = 0; i < 12; i++) deep = OscBundle(deep);
    Check(!AvatarOscReader.Read(deep).Any(), "Excessive nesting was accepted.");
    var random = new Random(24591);
    for (var i = 0; i < 500; i++)
    {
        var packet = new byte[random.Next(0, 1024)];
        random.NextBytes(packet);
        Check(!AvatarOscReader.Read(packet).Any(), "Random bytes created a routing message.");
    }
});

Console.WriteLine($"Native face protocol: {passed} passed, {failed} failed.");
return failed == 0 ? 0 : 1;

void Test(string name, Action test)
{
    try { test(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception e) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + e.Message); }
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static UnifiedTrackingData NewData()
{
    var data = new UnifiedTrackingData();
    data.Eye.NormalizedPupilRange = (2f, 8f);
    return data;
}

static byte[] Encode(UnifiedTrackingData data)
{
    var bytes = new byte[NativeFaceSender.PacketLength];
    NativeFaceSender.WritePacket(data, bytes);
    return bytes;
}

static ReadOnlySpan<byte> Payload(byte[] bytes) => bytes.AsSpan(28, 102);

static UdpClient Receiver()
{
    var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    receiver.Client.ReceiveTimeout = 2000;
    return receiver;
}

static byte[] Receive(UdpClient receiver)
{
    IPEndPoint? remote = null;
    return receiver.Receive(ref remote);
}

static byte[] OscString(string value)
{
    var bytes = Encoding.UTF8.GetBytes(value);
    var result = new byte[(bytes.Length + 4) & ~3];
    bytes.CopyTo(result, 0);
    return result;
}

static byte[] OscPacket(string address, string tags, params byte[][] arguments) =>
    new[] { OscString(address), OscString(tags) }.Concat(arguments).SelectMany(bytes => bytes).ToArray();

static byte[] OscBundle(params byte[][] elements)
{
    using var stream = new MemoryStream();
    stream.Write("#bundle\0"u8);
    stream.Write(new byte[8]);
    foreach (var element in elements)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, element.Length);
        stream.Write(length);
        stream.Write(element);
    }
    return stream.ToArray();
}

internal sealed class TestTarget : IOscTarget
{
    public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
    public string DestinationAddress { get; set; } = "127.0.0.1";
    public int OutPort { get; set; }
    public int InPort { get; set; }
    public bool IsConnected { get; set; }
}
