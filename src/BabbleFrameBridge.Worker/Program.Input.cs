// Copyright (c) 2026 Face Tracking Bridge contributors.
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using BabbleFrameBridge.Shared;
using VRCFaceTracking;
using VRCFaceTracking.Core.Params.Data;

namespace BabbleFrameBridge.Worker;

internal static partial class Program
{
    private static long _beyondTimestamp, _beyondTimestampChangedAt;
    private static void StartLifetimeWatchers(IReadOnlyDictionary<string, string?> options, CancellationTokenSource stop)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var input = await Console.In.ReadLineAsync(stop.Token);
                    if (input is null || input.Equals("stop", StringComparison.OrdinalIgnoreCase)) { stop.Cancel(); return; }
                }
            }
            catch (OperationCanceledException) { }
        });
        if (options.TryGetValue("--parent", out var parentText))
        {
            if (!int.TryParse(parentText, out var parent) || parent <= 0 || parent == Environment.ProcessId)
                throw new ArgumentException("The parent process ID is invalid.");
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        using var process = Process.GetProcessById(parent);
                        if (process.HasExited) { stop.Cancel(); return; }
                        await Task.Delay(500, stop.Token);
                    }
                }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException or OperationCanceledException)
                { stop.Cancel(); }
            });
        }
        Console.CancelKeyPress += (_, args) => { args.Cancel = true; stop.Cancel(); };
    }

    private static int RunInputOnly(ExtTrackingModule module, ModuleKind kind, string name, int inputPort,
        (bool eyeSuccess, bool expressionSuccess) claims, CancellationToken token)
    {
        Emit(new { type = "ready", source = kind.ToString(), eye = claims.eyeSuccess, face = claims.expressionSuccess,
            inputOnly = true, inputPort, message = name + " input initialized." });
        var interval = Stopwatch.Frequency / 90;
        var deadline = Stopwatch.GetTimestamp();
        long sequence = 0;
        while (!token.IsCancellationRequested)
        {
            module.Update();
            token.ThrowIfCancellationRequested();
            var now = Stopwatch.GetTimestamp();
            if (now < deadline)
            {
                if (token.WaitHandle.WaitOne(TimeSpan.FromSeconds((double)(deadline - now) / Stopwatch.Frequency))) break;
            }
            // Snapshot copies own their arrays. A slow input is never replayed in a burst.
            Emit(CaptureInput(module, kind, claims, ++sequence));
            deadline = Stopwatch.GetTimestamp() + interval;
            if (kind != ModuleKind.Custom && module.GetType().GetMethod("RestartNeeded",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, Type.EmptyTypes)?.Invoke(module, null) is true)
            {
                Emit(new { type = "restart", message = name + " changed its tracking mode." });
                return 0;
            }
        }
        return 0;
    }

    private static ModuleChannelSnapshot CaptureInput(ExtTrackingModule module, ModuleKind kind,
        (bool eyeSuccess, bool expressionSuccess) claims, long sequence)
    {
        var data = UnifiedTracking.Data;
        var babble = kind is ModuleKind.BabbleHeadsetRuntime or ModuleKind.BaballoniaDesktop;
        var inputs = babble ? GetHeadsetEyeInputs(module) : (NormalizedDilation: false, Pupil: false, Widen: claims.eyeSuccess);
        var link = kind == ModuleKind.BabbleHeadsetRuntime ? GetField(module, "babbleLink") : null;
        var connected = kind != ModuleKind.BabbleHeadsetRuntime || GetField(link, "_sid") is string
            && GetField(link, "_lastSeen") is DateTime seen && DateTime.UtcNow - seen < TimeSpan.FromSeconds(3.5);
        var receivingKnown = kind is ModuleKind.BabbleHeadsetRuntime or ModuleKind.VirtualDesktop or ModuleKind.Beyond;
        var receiving = kind switch
        {
            ModuleKind.BabbleHeadsetRuntime => connected && GetField(link, "_lastSeen") is DateTime received && DateTime.UtcNow - received < TimeSpan.FromSeconds(2.5),
            ModuleKind.VirtualDesktop => GetField(module, "_isTracking") is true,
            ModuleKind.Beyond => BeyondInputValid(module),
            _ => false
        };
        var range = inputs.NormalizedDilation ? (2f, 8f) : data.Eye.NormalizedPupilRange;
        static float Bound(float value, float min, float max, float fallback = 0) => float.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
        static ModuleEyeSnapshot Eye(UnifiedSingleEyeData eye) => new()
        {
            X = Bound(eye.Gaze.x, -1, 1), Y = Bound(eye.Gaze.y, -1, 1),
            Openness = Bound(eye.Openness, -1, 2, 1), Pupil = Bound(eye.PupilDiameter_MM, 0, 100)
        };
        return new ModuleChannelSnapshot
        {
            Sequence = sequence, Eye = claims.eyeSuccess, Face = claims.expressionSuccess,
            Connected = connected, ReceivingKnown = receivingKnown, Receiving = receiving,
            Left = Eye(data.Eye.Left), Right = Eye(data.Eye.Right),
            PupilMinimum = range?.Item1, PupilMaximum = range?.Item2,
            PupilAvailable = inputs.Pupil || data.Eye.Left.PupilDiameter_MM > 0 || data.Eye.Right.PupilDiameter_MM > 0,
            WidenAvailable = inputs.Widen,
            Shapes = data.Shapes.Select(shape => Bound(shape.Weight, 0, 1)).ToArray()
        };
    }

    private static bool BeyondInputValid(ExtTrackingModule module)
    {
        try
        {
            // The official Beyond structure places IsValid at byte48. Opening an
            // accessor is owned by that module; do not create or write user IPC.
            if (GetField(module, "_accessor") is not System.IO.MemoryMappedFiles.MemoryMappedViewAccessor accessor || accessor.ReadInt32(48) != 1) return false;
            var timestamp = accessor.ReadInt64(40);
            if (timestamp == 0) return true; // Older writers may supply only IsValid.
            var now = Stopwatch.GetTimestamp();
            if (_beyondTimestampChangedAt == 0 || timestamp != _beyondTimestamp)
            { _beyondTimestamp = timestamp; _beyondTimestampChangedAt = now; }
            // Observe progress without assuming the writer's epoch or clock units.
            return Stopwatch.GetElapsedTime(_beyondTimestampChangedAt).TotalSeconds < 2.5;
        }
        catch (Exception error) when (error is ObjectDisposedException or IOException) { return false; }
    }
}
