// Copyright (c) 2026 Frame Bridge contributors.
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace BabbleFrameBridge.App;

public enum KittyVocalization { Meow, Chirp, Yawn, Purr }

/// <summary>
/// Quiet, original, locally synthesized kitten voices. Synthesis is off the UI
/// thread. Muting, hiding or disposing invalidates pending playback immediately.
/// No microphone, recordings, network, speech model or extra runtime is used.
/// </summary>
public sealed class KittyVoice : IDisposable
{
    private readonly object _gate = new();
    private readonly Func<byte[], bool> _play;
    private readonly Action _stop;
    private readonly Func<double> _clock;
    private readonly Func<KittyVocalization, int, double, double, byte[]> _synthesize;
    private readonly NativeWaveOutput? _native;
    private readonly Random _random = new();
    private bool _enabled = true, _disposed;
    private double _volume = .18, _nextAllowed;
    private int _generation;
    private Task _pending = Task.CompletedTask;

    public KittyVoice(Func<byte[], bool>? play = null, Action? stop = null, Func<double>? clock = null,
        Func<KittyVocalization, int, double, double, byte[]>? synthesize = null)
    {
        if (play is null)
        {
            _native = new NativeWaveOutput();
            _play = _native.Play;
            _stop = _native.Stop;
        }
        else
        {
            _play = play;
            _stop = stop ?? (() => { });
        }
        _clock = clock ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        _synthesize = synthesize ?? KittyVoiceSynthesizer.CreateWave;
    }

    public bool Enabled
    {
        get { lock (_gate) return _enabled && !_disposed; }
        set
        {
            lock (_gate)
            {
                if (_disposed || _enabled == value) return;
                _enabled = value;
                if (!value) StopCore();
            }
        }
    }

    /// <summary>Waveform gain only; never changes Windows or other applications' volume.</summary>
    public double Volume
    {
        get { lock (_gate) return _volume; }
        set
        {
            lock (_gate)
            {
                if (_disposed) return;
                _volume = double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
                if (_volume == 0) StopCore();
            }
        }
    }

    /// <summary>Completion of the latest synthesis/playback-start operation, not the sound's duration.</summary>
    public Task PendingPlayback { get { lock (_gate) return _pending; } }

    /// <summary>Returns whether a voice was accepted. Cooldowns prevent chatter and overlapping sounds.</summary>
    public bool Play(KittyVocalization voice, double mood = 0)
    {
        lock (_gate)
        {
            var now = _clock();
            if (_disposed || !_enabled || _volume <= 0 || !Enum.IsDefined(voice)
                || !double.IsFinite(now) || now < _nextAllowed) return false;
            var generation = ++_generation;
            var seed = _random.Next();
            var volume = _volume;
            // The longest possible sample finishes before this cooldown does.
            _nextAllowed = now + (voice switch {
                KittyVocalization.Chirp => 2.5, KittyVocalization.Meow => 4.5,
                KittyVocalization.Yawn => 7, _ => 8 });
            _pending = Task.Run(() =>
            {
                try
                {
                    var wave = _synthesize(voice, seed, volume, mood);
                    lock (_gate)
                    {
                        if (_disposed || !_enabled || generation != _generation || _volume <= 0) return;
                        _play(wave);
                    }
                }
                catch (Exception error) when (error is IOException or InvalidOperationException
                    or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
                {
                    // An unavailable audio device must not disturb tracking or the UI.
                }
            });
            return true;
        }
    }

    public void Stop() { lock (_gate) StopCore(); }

    private void StopCore()
    {
        _generation++;
        try { _stop(); }
        catch (Exception error) when (error is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _enabled = false;
            StopCore();
            _native?.Dispose();
        }
    }

    /// <summary>
    /// Owns a pinned WAV for PlaySoundW's asynchronous memory lifetime. Playback
    /// is limited to this process. No fallback system sound is ever requested.
    /// API: https://learn.microsoft.com/previous-versions/dd743680(v=vs.85)
    /// </summary>
    private sealed class NativeWaveOutput : IDisposable
    {
        private static readonly object NativeGate = new();
        private static GCHandle _buffer;
        private static NativeWaveOutput? _owner;
        private bool _disposed;

        public bool Play(byte[] wave)
        {
            lock (NativeGate)
            {
                if (_disposed || !OperatingSystem.IsWindows()) return false;
                StopPrevious();
                _buffer = GCHandle.Alloc(wave, GCHandleType.Pinned);
                _owner = this;
                try
                {
                    if (PlaySound(_buffer.AddrOfPinnedObject(), 0, 0x0001 | 0x0002 | 0x0004)) return true;
                }
                catch { ReleaseBuffer(); throw; }
                ReleaseBuffer();
                return false;
            }
        }

        public void Stop()
        {
            lock (NativeGate) if (ReferenceEquals(_owner, this)) StopPrevious();
        }

        private static void StopPrevious()
        {
            if (_owner is null) return;
            try { PlaySound(0, 0, 0); }
            finally { ReleaseBuffer(); }
        }

        private static void ReleaseBuffer()
        {
            if (_buffer.IsAllocated) _buffer.Free();
            _owner = null;
        }

        public void Dispose() { lock (NativeGate) { Stop(); _disposed = true; } }

        [DllImport("winmm.dll", EntryPoint = "PlaySoundW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PlaySound(nint sound, nint module, uint flags);
    }
}

/// <summary>
/// Original additive/formant synthesis: changing kitten pitch, vowel-shaped
/// harmonics, breath and trill envelopes. These are stylized robot-cat voices,
/// not sampled animal recordings or speech generated from a third-party model.
/// </summary>
public static class KittyVoiceSynthesizer
{
    public const int SampleRate = 24000;
    private const double Tau = Math.PI * 2;

    public static byte[] CreateWave(KittyVocalization voice, int seed = 0, double volume = .18, double mood = 0)
    {
        if (!Enum.IsDefined(voice)) throw new ArgumentOutOfRangeException(nameof(voice));
        volume = double.IsFinite(volume) ? Math.Clamp(volume, 0, 1) : 0;
        mood = double.IsFinite(mood) ? Math.Clamp(mood, -1, 1) : 0;
        var random = new Random(seed);
        var variation = random.NextDouble();
        var duration = voice switch {
            KittyVocalization.Meow => .88 + variation * .23,
            KittyVocalization.Chirp => .46 + variation * .14,
            KittyVocalization.Yawn => 1.35 + variation * .28,
            _ => 1.65 + variation * .25 };
        var samples = new double[(int)(duration * SampleRate)];
        var pitchScale = .94 + variation * .12;
        var phase = 0d; var breathLow = 0d; var peak = 0d;
        for (var i = 0; i < samples.Length; i++)
        {
            var t = i / (double)SampleRate;
            var u = t / duration;
            double fundamental, first, second, third, voiced, breath, envelope;
            switch (voice)
            {
                case KittyVocalization.Meow:
                    fundamental = Curve(u, 520, 865 + mood * 60, 670, 390);
                    first = Curve(u, 1150, 1250, 830, 570);
                    second = Curve(u, 2550, 2700, 1950, 1450);
                    third = 3700 - u * 850;
                    voiced = .93; breath = .025;
                    envelope = Math.Pow(Math.Sin(Math.PI * u), .70) * (.88 + .12 * Math.Sin(u * Math.PI * 1.2));
                    break;
                case KittyVocalization.Chirp:
                    fundamental = 680 + 420 * Math.Sin(Math.PI * Math.Pow(u, .65)) + 65 * Math.Sin(Tau * 30 * t);
                    first = 1400 + 260 * Math.Sin(Math.PI * u);
                    second = 2800 - u * 550; third = 4000;
                    voiced = .88; breath = .015;
                    var pulse = .68 + .32 * Math.Sin(Tau * (13 + variation * 5) * t);
                    envelope = Math.Pow(Math.Sin(Math.PI * u), .5) * pulse;
                    break;
                case KittyVocalization.Yawn:
                    fundamental = 240 + 155 * Math.Sin(Math.PI * u) - 95 * u;
                    first = 550 + 550 * Math.Sin(Math.PI * u);
                    second = 1100 + 700 * Math.Sin(Math.PI * u); third = 2800;
                    voiced = .52 * Math.Pow(Math.Sin(Math.PI * u), .5);
                    breath = .13 + .17 * u;
                    envelope = Math.Pow(Math.Sin(Math.PI * u), 1.1);
                    break;
                default:
                    fundamental = 92 + 6 * Math.Sin(Tau * .8 * t);
                    first = 340; second = 760; third = 1300;
                    voiced = .8; breath = .035;
                    envelope = Math.Pow(Math.Sin(Math.PI * u), .45) * (.22 + .78 * Math.Pow(.5 + .5 * Math.Sin(Tau * 26 * t), 1.5));
                    break;
            }
            fundamental *= pitchScale * (1 + .006 * Math.Sin(Tau * (5.2 + variation) * t));
            phase += fundamental / SampleRate;
            phase -= Math.Floor(phase);
            var signal = 0d; var total = 0d;
            for (var harmonic = 1; harmonic <= 18; harmonic++)
            {
                var frequency = fundamental * harmonic;
                if (frequency > SampleRate * .43) break;
                var weight = .10 / Math.Pow(harmonic, 1.35)
                    + Formant(frequency, first, first * .29) / Math.Pow(harmonic, .72)
                    + .48 * Formant(frequency, second, second * .19) / Math.Pow(harmonic, .62)
                    + .16 * Formant(frequency, third, third * .17) / Math.Pow(harmonic, .55);
                signal += weight * Math.Sin(Tau * harmonic * phase + harmonic * .13);
                total += weight;
            }
            var noise = random.NextDouble() * 2 - 1;
            breathLow += .18 * (noise - breathLow);
            var air = breathLow * .75 + (noise - breathLow) * .12;
            var edge = Smooth(Math.Min(t / .025, (duration - t) / .08));
            // Soft compression retains a little glottal texture without hard clipping.
            var value = Math.Tanh((signal / Math.Max(.1, total) * voiced + air * breath) * 1.35) * envelope * edge;
            samples[i] = value;
            peak = Math.Max(peak, Math.Abs(value));
        }
        var gain = peak > .000001 ? volume * .78 / peak : 0;
        var wave = new byte[44 + samples.Length * 2];
        WriteHeader(wave, samples.Length);
        for (var i = 0; i < samples.Length; i++)
        {
            var value = i == 0 || i == samples.Length - 1 ? 0 : (int)Math.Round(samples[i] * gain * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(44 + i * 2), (short)Math.Clamp(value, short.MinValue, short.MaxValue));
        }
        return wave;
    }

    private static double Formant(double frequency, double center, double width)
    {
        var difference = (frequency - center) / width;
        return Math.Exp(-.5 * difference * difference);
    }

    private static double Smooth(double t) { t = Math.Clamp(t, 0, 1); return t * t * (3 - 2 * t); }
    private static double Curve(double t, double a, double b, double c, double d) => t < .2
        ? a + (b - a) * Smooth(t / .2) : t < .67 ? b + (c - b) * Smooth((t - .2) / .47) : c + (d - c) * Smooth((t - .67) / .33);

    private static void WriteHeader(Span<byte> wave, int sampleCount)
    {
        "RIFF"u8.CopyTo(wave); BinaryPrimitives.WriteInt32LittleEndian(wave[4..], wave.Length - 8);
        "WAVEfmt "u8.CopyTo(wave[8..]); BinaryPrimitives.WriteInt32LittleEndian(wave[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(wave[20..], 1); BinaryPrimitives.WriteInt16LittleEndian(wave[22..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(wave[24..], SampleRate); BinaryPrimitives.WriteInt32LittleEndian(wave[28..], SampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(wave[32..], 2); BinaryPrimitives.WriteInt16LittleEndian(wave[34..], 16);
        "data"u8.CopyTo(wave[36..]); BinaryPrimitives.WriteInt32LittleEndian(wave[40..], sampleCount * 2);
    }
}
