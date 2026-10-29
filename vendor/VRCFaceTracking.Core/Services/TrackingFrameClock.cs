// Copyright (c) 2026 Babble Bridge contributors.
// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace VRCFaceTracking.Core.Services;
/// <summary>Monotonic cadence without changing the system timer resolution.</summary>
internal sealed class TrackingFrameClock : IDisposable
{
    private sealed class TimerHandle : WaitHandle
    {
        public TimerHandle(SafeWaitHandle handle) => SafeWaitHandle = handle;
    }
    private readonly long interval;
    private readonly TimerHandle? timer;
    private long deadline;
    public TrackingFrameClock(int rateHz)
    {
        interval = Stopwatch.Frequency / rateHz;
        deadline = Stopwatch.GetTimestamp() + interval;
        if (!OperatingSystem.IsWindows()) return;
        // CREATE_WAITABLE_TIMER_HIGH_RESOLUTION: Windows 10 1803 and later.
        var handle = CreateWaitableTimerExW(IntPtr.Zero, null, 0x2, 0x00100002);
        if (handle.IsInvalid) { handle.Dispose(); handle = CreateWaitableTimerExW(IntPtr.Zero, null, 0, 0x00100002); }
        if (handle.IsInvalid) handle.Dispose(); else timer = new TimerHandle(handle);
    }
    public bool WaitForNextFrame(CancellationToken cancellationToken)
    {
        var now = Stopwatch.GetTimestamp();
        // Do not replay missed frames in a burst after a slow update or PC resume.
        if (now > deadline + interval) deadline = now;
        var remaining = deadline - now;
        if (remaining > 0)
        {
            if (timer != null)
            {
                var dueTime = -Math.Max(1, (long)(remaining * (10_000_000d / Stopwatch.Frequency)));
                if (SetWaitableTimerEx(timer.SafeWaitHandle, ref dueTime, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0))
                {
                    if (WaitHandle.WaitAny(new WaitHandle[] { timer, cancellationToken.WaitHandle }) == 1) return false;
                }
                else if (cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds((double)remaining / Stopwatch.Frequency))) return false;
            }
            else if (cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds((double)remaining / Stopwatch.Frequency))) return false;
        }
        deadline += interval;
        return !cancellationToken.IsCancellationRequested;
    }
    public void Dispose() => timer?.Dispose();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeWaitHandle CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimerEx(SafeWaitHandle timer, ref long dueTime, int period, IntPtr completionRoutine, IntPtr argument, IntPtr reasonContext, uint tolerableDelay);
}
