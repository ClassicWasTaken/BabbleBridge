// Copyright (c) 2026 Frame Bridge contributors.
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using VRCFaceTracking;
using VRCFaceTracking.Core.Contracts;
using VRCFaceTracking.Core.Params.Data;
using VRCFaceTracking.Core.Params.Expressions;

namespace BabbleFrameBridge.Worker;

/// <summary>
/// Sends VRChat's native face tracking protocol without changing the pinned module SDK or
/// the native OSC library ABI. Enabled only for an avatar advertising full face tracking.
/// Protocol: https://vrc-beta-docs.netlify.app/avatars/face-tracking/osc/
/// </summary>
internal sealed class NativeFaceSender(IOscTarget target, ILogger<NativeFaceSender> logger) : IDisposable
{
    internal const int PayloadLength = 102;
    internal const int PayloadOffset = 28;
    internal const int PacketLength = 132;

    private readonly object sendLock = new();
    private readonly byte[] packet = new byte[PacketLength];
    private Socket? socket;
    private IPEndPoint? endpoint;
    private string? destination;
    private bool started, disposed;
    private volatile bool enabled;
    private long framesSent;
    private DateTime lastErrorAt = DateTime.MinValue;

    public bool Enabled { get => enabled; set => enabled = value; }
    public long FramesSent => Interlocked.Read(ref framesSent);

    // The wire protocol fixes this order independently of the module's enum ordinals.
    private static readonly UnifiedExpressions[] ShapeOrder =
    [
        UnifiedExpressions.EyeSquintRight, UnifiedExpressions.EyeSquintLeft,
        UnifiedExpressions.EyeWideRight, UnifiedExpressions.EyeWideLeft,
        UnifiedExpressions.BrowPinchRight, UnifiedExpressions.BrowPinchLeft,
        UnifiedExpressions.BrowLowererRight, UnifiedExpressions.BrowLowererLeft,
        UnifiedExpressions.BrowInnerUpRight, UnifiedExpressions.BrowInnerUpLeft,
        UnifiedExpressions.BrowOuterUpRight, UnifiedExpressions.BrowOuterUpLeft,
        UnifiedExpressions.NasalDilationRight, UnifiedExpressions.NasalDilationLeft,
        UnifiedExpressions.NasalConstrictRight, UnifiedExpressions.NasalConstrictLeft,
        UnifiedExpressions.CheekSquintRight, UnifiedExpressions.CheekSquintLeft,
        UnifiedExpressions.CheekPuffRight, UnifiedExpressions.CheekPuffLeft,
        UnifiedExpressions.CheekSuckRight, UnifiedExpressions.CheekSuckLeft,
        UnifiedExpressions.JawOpen, UnifiedExpressions.JawRight, UnifiedExpressions.JawLeft,
        UnifiedExpressions.JawForward, UnifiedExpressions.JawBackward, UnifiedExpressions.JawClench,
        UnifiedExpressions.JawMandibleRaise, UnifiedExpressions.MouthClosed,
        UnifiedExpressions.LipSuckUpperRight, UnifiedExpressions.LipSuckUpperLeft,
        UnifiedExpressions.LipSuckLowerRight, UnifiedExpressions.LipSuckLowerLeft,
        UnifiedExpressions.LipSuckCornerRight, UnifiedExpressions.LipSuckCornerLeft,
        UnifiedExpressions.LipFunnelUpperRight, UnifiedExpressions.LipFunnelUpperLeft,
        UnifiedExpressions.LipFunnelLowerRight, UnifiedExpressions.LipFunnelLowerLeft,
        UnifiedExpressions.LipPuckerUpperRight, UnifiedExpressions.LipPuckerUpperLeft,
        UnifiedExpressions.LipPuckerLowerRight, UnifiedExpressions.LipPuckerLowerLeft,
        UnifiedExpressions.MouthUpperUpRight, UnifiedExpressions.MouthUpperUpLeft,
        UnifiedExpressions.MouthUpperDeepenRight, UnifiedExpressions.MouthUpperDeepenLeft,
        UnifiedExpressions.NoseSneerRight, UnifiedExpressions.NoseSneerLeft,
        UnifiedExpressions.MouthLowerDownRight, UnifiedExpressions.MouthLowerDownLeft,
        UnifiedExpressions.MouthUpperRight, UnifiedExpressions.MouthUpperLeft,
        UnifiedExpressions.MouthLowerRight, UnifiedExpressions.MouthLowerLeft,
        UnifiedExpressions.MouthCornerPullRight, UnifiedExpressions.MouthCornerPullLeft,
        UnifiedExpressions.MouthCornerSlantRight, UnifiedExpressions.MouthCornerSlantLeft,
        UnifiedExpressions.MouthFrownRight, UnifiedExpressions.MouthFrownLeft,
        UnifiedExpressions.MouthStretchRight, UnifiedExpressions.MouthStretchLeft,
        UnifiedExpressions.MouthDimpleRight, UnifiedExpressions.MouthDimpleLeft,
        UnifiedExpressions.MouthRaiserUpper, UnifiedExpressions.MouthRaiserLower,
        UnifiedExpressions.MouthPressRight, UnifiedExpressions.MouthPressLeft,
        UnifiedExpressions.MouthTightenerRight, UnifiedExpressions.MouthTightenerLeft,
        UnifiedExpressions.TongueOut, UnifiedExpressions.TongueUp, UnifiedExpressions.TongueDown,
        UnifiedExpressions.TongueRight, UnifiedExpressions.TongueLeft, UnifiedExpressions.TongueRoll,
        UnifiedExpressions.TongueBendDown, UnifiedExpressions.TongueCurlUp,
        UnifiedExpressions.TongueSquish, UnifiedExpressions.TongueFlat,
        UnifiedExpressions.TongueTwistRight, UnifiedExpressions.TongueTwistLeft,
        UnifiedExpressions.SoftPalateClose, UnifiedExpressions.ThroatSwallow,
        UnifiedExpressions.NeckFlexRight, UnifiedExpressions.NeckFlexLeft
    ];

    public void Start()
    {
        lock (sendLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (started) return;
            started = true;
            UnifiedTracking.OnUnifiedDataUpdated += OnDataUpdated;
        }
    }

    private void OnDataUpdated(UnifiedTrackingData data)
    {
        if (!enabled) return;
        lock (sendLock)
        {
            if (disposed || !enabled) return;
            try
            {
                // OSCQuery can change the VRChat port during a session. Read the same target
                // used by the legacy sender so both transports always follow it.
                if (endpoint == null || destination != target.DestinationAddress || endpoint.Port != target.OutPort)
                {
                    var address = IPAddress.Parse(target.DestinationAddress);
                    if (socket == null || socket.AddressFamily != address.AddressFamily)
                    {
                        socket?.Dispose();
                        socket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                    }
                    endpoint = new IPEndPoint(address, target.OutPort);
                    destination = target.DestinationAddress;
                }
                WritePacket(data, packet);
                if (socket!.SendTo(packet, endpoint) == PacketLength)
                    Interlocked.Increment(ref framesSent);
            }
            catch (Exception e) when (e is SocketException or FormatException or ArgumentException)
            {
                if (DateTime.UtcNow - lastErrorAt < TimeSpan.FromSeconds(5)) return;
                lastErrorAt = DateTime.UtcNow;
                logger.LogWarning("Could not send native face tracking: {Message}", e.Message);
            }
        }
    }

    internal static void WritePacket(UnifiedTrackingData data, Span<byte> output)
    {
        if (output.Length < PacketLength) throw new ArgumentException("OSC face packet buffer is too small.", nameof(output));
        output = output[..PacketLength];
        output.Clear();
        "/tracking/face/v1\0\0\0"u8.CopyTo(output);
        ",b\0\0"u8.CopyTo(output[20..]);
        BinaryPrimitives.WriteInt32BigEndian(output[24..28], PayloadLength);
        var payload = output.Slice(PayloadOffset, PayloadLength);

        var right = data.Eye.Right;
        var left = data.Eye.Left;
        payload[0] = Strength(right.Gaze.x);
        payload[1] = Strength(-right.Gaze.x);
        payload[2] = Strength(right.Gaze.y);
        payload[3] = Strength(-right.Gaze.y);
        payload[4] = Strength(-left.Gaze.x);
        payload[5] = Strength(left.Gaze.x);
        payload[6] = Strength(left.Gaze.y);
        payload[7] = Strength(-left.Gaze.y);
        payload[8] = Strength(1 - right.Openness);
        payload[9] = Strength(1 - left.Openness);

        // Dilation and constriction are opposing blendshapes. A neutral pupil activates
        // neither; missing measurements stay neutral through NormalizePupilDiameter.
        WritePupil(data.Eye.NormalizePupilDiameter(right.PupilDiameter_MM), payload, 10, 12);
        WritePupil(data.Eye.NormalizePupilDiameter(left.PupilDiameter_MM), payload, 11, 13);
        for (var i = 0; i < ShapeOrder.Length; i++)
        {
            var shape = (int)ShapeOrder[i];
            payload[14 + i] = shape < data.Shapes.Length ? Strength(data.Shapes[shape].Weight) : (byte)0;
        }
    }

    private static void WritePupil(float normalized, Span<byte> payload, int dilation, int constriction)
    {
        payload[dilation] = Strength((normalized - .5f) * 2);
        payload[constriction] = Strength((.5f - normalized) * 2);
    }

    private static byte Strength(float value) => float.IsFinite(value) ? (byte)(Math.Clamp(value, 0, 1) * 255) : (byte)0;

    public void Dispose()
    {
        lock (sendLock)
        {
            if (disposed) return;
            disposed = true;
            enabled = false;
            if (started)
                UnifiedTracking.OnUnifiedDataUpdated = (UnifiedTracking.OnUnifiedDataUpdated - OnDataUpdated) ?? (_ => { });
            socket?.Dispose();
        }
    }
}
