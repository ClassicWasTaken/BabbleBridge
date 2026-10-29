// Babble Bridge modification notice (2026-10-29): synchronized frame delivery,
// steady 90 Hz scheduling, and full resends for VRChat tracking timeouts.
// Upstream: Project-Babble/VRCFaceTracking b4b39da5c9fe048c960aa3b9e56742834df6ff36.
// Copyright 2024 benaclejames. Apache-2.0; see vendor/LICENSE and NOTICE.txt.
using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using VRCFaceTracking.Core.Contracts;
using VRCFaceTracking.Core.OSC;
namespace VRCFaceTracking.Core.Services;
public class ParameterSenderService : BackgroundService
{
    // Mapping, caches and serialization share this short lock. UDP sends follow it.
    public static object FrameLock { get; } = new();
    public const int TargetRateHz = 90;
    private static readonly Queue<OscMessage> SendQueue = new();
    private readonly OscSendService _sendService;
    private volatile bool legacyOutputEnabled = true;
    private static int resendGeneration;
    private long framesProcessed;
    public long FramesProcessed => Interlocked.Read(ref framesProcessed);
    public static int ResendGeneration => Volatile.Read(ref resendGeneration);
    public static void RequestFullResend() => Interlocked.Increment(ref resendGeneration);
    public bool LegacyOutputEnabled
    {
        get => legacyOutputEnabled;
        set
        {
            lock (FrameLock)
            {
                if (value && !legacyOutputEnabled) RequestFullResend();
                legacyOutputEnabled = value;
                if (!value) SendQueue.Clear();
            }
        }
    }
    public static bool AllParametersRelevantStatic { get; set; }
    public bool AllParametersRelevant
    {
        get => AllParametersRelevantStatic;
        set
        {
            lock (FrameLock)
            {
                if (AllParametersRelevantStatic == value) return;
                AllParametersRelevantStatic = value;
                SendQueue.Clear();
                RequestFullResend();
                foreach (var parameter in UnifiedTracking.AllParameters)
                    parameter.ResetParam(Array.Empty<IParameterDefinition>());
            }
        }
    }
    public ParameterSenderService(OscSendService sendService) => _sendService = sendService;
    public static void Enqueue(OscMessage message) { lock (FrameLock) SendQueue.Enqueue(message); }
    public static void Clear() { lock (FrameLock) SendQueue.Clear(); }
    protected override Task ExecuteAsync(CancellationToken cancellationToken) => Task.Factory.StartNew(
        () => SendFrames(cancellationToken), cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    private void SendFrames(CancellationToken cancellationToken)
    {
        using var clock = new TrackingFrameClock(TargetRateHz);
        var nextHeartbeat = Stopwatch.GetTimestamp();
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                Task? transmission = null;
                lock (FrameLock)
                {
                    // VRChat retires eye endpoints after ten seconds without a message.
                    var now = Stopwatch.GetTimestamp();
                    if (legacyOutputEnabled && now >= nextHeartbeat)
                    {
                        RequestFullResend();
                        nextHeartbeat = now + Stopwatch.Frequency;
                    }
                    UnifiedTracking.UpdateData(cancellationToken).GetAwaiter().GetResult();
                    Interlocked.Increment(ref framesProcessed);
                    if (!legacyOutputEnabled) SendQueue.Clear();
                    else if (SendQueue.Count > 0)
                    {
                        var messages = SendQueue.ToArray();
                        SendQueue.Clear();
                        // Send snapshots packet bytes before returning its network task.
                        transmission = _sendService.Send(messages, cancellationToken);
                    }
                }
                transmission?.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception e) { RequestFullResend(); SentrySdk.CaptureException(e); }
            if (!clock.WaitForNextFrame(cancellationToken)) break;
        }
    }
}
