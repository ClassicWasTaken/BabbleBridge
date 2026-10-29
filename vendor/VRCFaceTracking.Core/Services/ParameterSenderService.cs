// Frame Bridge modification notice (2026-10-29): pause legacy output in native mode and request full resends.
// Upstream: Project-Babble/VRCFaceTracking b4b39da5c9fe048c960aa3b9e56742834df6ff36.
// Copyright 2024 benaclejames. Apache-2.0; see vendor/LICENSE and repository NOTICE.txt.
using Microsoft.Extensions.Hosting;
using VRCFaceTracking.Core.Contracts;
using VRCFaceTracking.Core.OSC;

namespace VRCFaceTracking.Core.Services;

public class ParameterSenderService : BackgroundService
{
    // We probably don't need a queue since we use osc message bundles, but for now, we're keeping it as
    // we might want to allow a way for the user to specify bundle or single message sends in the future
    private static readonly Queue<OscMessage> SendQueue = new();
 
    private readonly OscSendService _sendService;
    private volatile bool legacyOutputEnabled = true;
    private static int resendGeneration;
    public static int ResendGeneration => Volatile.Read(ref resendGeneration);
    public static void RequestFullResend() => Interlocked.Increment(ref resendGeneration);

    // Native SDK face frames include gaze and independent eyelids. Suppress the older
    // eye endpoints and avatar parameters while that complete stream is selected.
    public bool LegacyOutputEnabled
    {
        get => legacyOutputEnabled;
        set
        {
            if (value && !legacyOutputEnabled) RequestFullResend();
            legacyOutputEnabled = value;
        }
    }

    public static bool AllParametersRelevantStatic
    {
        get; set;
    }
    public bool AllParametersRelevant
    {
        get => AllParametersRelevantStatic;
        set
        {
            if (AllParametersRelevantStatic == value) return;
            AllParametersRelevantStatic = value;
            SendQueue.Clear();
            foreach (var parameter in UnifiedTracking.AllParameters)
            {
                parameter.ResetParam(Array.Empty<IParameterDefinition>());
            }
        }
    }
    
    public ParameterSenderService(OscSendService sendService)
    {
        _sendService = sendService;
    }

    public static void Enqueue(OscMessage message) => SendQueue.Enqueue(message);
    public static void Clear() => SendQueue.Clear();
    
    protected async override Task ExecuteAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(10, cancellationToken);

                await UnifiedTracking.UpdateData(cancellationToken);

                if (!LegacyOutputEnabled)
                {
                    SendQueue.Clear();
                    continue;
                }

                // Send all messages in OSCParams.SendQueue
                if (SendQueue.Count <= 0)
                {
                    continue;
                }

                await _sendService.Send(SendQueue.ToArray(), cancellationToken);

                SendQueue.Clear();
            }
            catch (Exception e)
            {
                SentrySdk.CaptureException(e, scope =>
                {
                    var i = 0;
                    foreach (var msg in SendQueue)
                    {
                        scope.SetExtra($"Address {i}", msg.Address);
                        scope.SetExtra($"Values {i}", msg._meta.ValueLength);
                        scope.SetExtra($"Value 0 {i}", msg.Value);
                        i++;
                    }
                });
            }
        }
    }
}
