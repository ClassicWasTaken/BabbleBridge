// Copyright (c) 2026 Frame Bridge contributors.
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.Diagnostics;
using BabbleFrameBridge.App;

int passed = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); passed++; }
short[] Pcm(byte[] wave) => Enumerable.Range(0, (wave.Length - 44) / 2)
    .Select(i => BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(44 + i * 2))).ToArray();
var timer = Stopwatch.StartNew();
var examples = new Dictionary<KittyVocalization, byte[]>();
foreach (var kind in Enum.GetValues<KittyVocalization>())
{
    var wave = KittyVoiceSynthesizer.CreateWave(kind, 42, .18);
    examples[kind] = wave;
    Check(wave.AsSpan(0, 4).SequenceEqual("RIFF"u8) && wave.AsSpan(8, 8).SequenceEqual("WAVEfmt "u8), "The voice is not an ordinary WAV.");
    Check(BinaryPrimitives.ReadInt32LittleEndian(wave.AsSpan(4)) == wave.Length - 8, "RIFF size mismatch.");
    Check(BinaryPrimitives.ReadInt32LittleEndian(wave.AsSpan(40)) == wave.Length - 44, "PCM length mismatch.");
    Check(BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(20)) == 1 && BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(22)) == 1
        && BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(34)) == 16, "Voice must remain mono16-bit PCM.");
    var samples = Pcm(wave);
    var seconds = samples.Length / (double)KittyVoiceSynthesizer.SampleRate;
    Check(seconds is > .4 and < 2.1, "An ambient voice has an unbounded or unexpectedly long duration.");
    Check(samples.Max(s => Math.Abs((int)s)) <= Math.Ceiling(short.MaxValue * .18 * .78), "Voice exceeds requested quiet gain.");
    var rms = Math.Sqrt(samples.Average(s => Math.Pow(s / (double)short.MaxValue, 2)));
    Check(rms is > .008 and < .13, "Voice is silent or excessively loud.");
    Check(samples[0] == 0 && samples[^1] == 0, "Voice has a discontinuous edge.");
    var early = samples.Take(120).Max(s => Math.Abs((int)s));
    var ending = samples.TakeLast(120).Max(s => Math.Abs((int)s));
    Check(early < samples.Max(s => Math.Abs((int)s)) * .25 && ending < samples.Max(s => Math.Abs((int)s)) * .1,
        "Voice has no soft onset or release.");
    Check(wave.SequenceEqual(KittyVoiceSynthesizer.CreateWave(kind, 42, .18)), "Seeded voice synthesis is not deterministic.");
    Check(!wave.SequenceEqual(KittyVoiceSynthesizer.CreateWave(kind, 43, .18)), "Different calls have no pitch/duration variation.");
    Check(Pcm(KittyVoiceSynthesizer.CreateWave(kind, 42, double.NaN)).All(s => s == 0), "Invalid volume was not muted.");
}
Check(examples.Values.Select(w => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(w))).Distinct().Count() == 4,
    "Meow, chirp, yawn and purr must be distinct voices.");

var calls = 0; var stops = 0; var clock = 0d;
using (var voice = new KittyVoice(_ => { calls++; return true; }, () => stops++, () => clock))
{
    Check(voice.Play(KittyVocalization.Meow), "An enabled voice was rejected.");
    await voice.PendingPlayback.WaitAsync(TimeSpan.FromSeconds(5));
    Check(calls == 1, "The injected audio output was not used.");
    Check(!voice.Play(KittyVocalization.Chirp), "Voices overlapped or ignored the global cooldown.");
    clock = 5;
    Check(voice.Play(KittyVocalization.Chirp), "The voice never recovered from its cooldown.");
    await voice.PendingPlayback.WaitAsync(TimeSpan.FromSeconds(5));
    Check(calls == 2, "The second distinct voice did not reach output.");
    voice.Enabled = false;
    Check(stops >= 1 && !voice.Play(KittyVocalization.Meow), "Muting did not stop or suppress playback.");
    voice.Enabled = true; voice.Volume = 0; clock = 10;
    Check(!voice.Play(KittyVocalization.Meow), "Zero volume still scheduled a voice.");
    voice.Volume = double.PositiveInfinity;
    Check(voice.Volume == 0, "Invalid volume did not remain muted.");
    Check(!voice.Play((KittyVocalization)999), "An unknown voice was accepted.");
}

foreach (var operation in new[] { "stop", "mute", "dispose" })
{
    using var entered = new ManualResetEventSlim();
    using var finish = new ManualResetEventSlim();
    var lateCalls = 0;
    using var voice = new KittyVoice(_ => { lateCalls++; return true; }, () => { }, () => 0,
        (kind, seed, gain, mood) => { entered.Set(); finish.Wait(TimeSpan.FromSeconds(5)); return examples[kind]; });
    Check(voice.Play(KittyVocalization.Meow) && entered.Wait(TimeSpan.FromSeconds(5)), "The blocked synthesis did not start.");
    if (operation == "stop") voice.Stop();
    else if (operation == "mute") { voice.Enabled = false; voice.Enabled = true; }
    else voice.Dispose();
    finish.Set();
    await voice.PendingPlayback.WaitAsync(TimeSpan.FromSeconds(5));
    Check(lateCalls == 0, "A late synthesis played after " + operation + ".");
}

using (var unavailable = new KittyVoice(_ => throw new InvalidOperationException("Injected unavailable audio device"), () => { }))
{
    Check(unavailable.Play(KittyVocalization.Yawn), "The test voice was not scheduled.");
    await unavailable.PendingPlayback.WaitAsync(TimeSpan.FromSeconds(5));
    Check(unavailable.PendingPlayback.IsCompletedSuccessfully, "A missing audio device faulted the companion.");
}
var export = Array.IndexOf(args, "--samples");
if (export >= 0 && export + 1 < args.Length)
{
    var directory = Path.GetFullPath(args[export + 1]);
    Directory.CreateDirectory(directory);
    foreach (var pair in examples) File.WriteAllBytes(Path.Combine(directory, pair.Key + ".wav"), pair.Value);
    Console.WriteLine("Original silent-test sample files: " + directory);
}
Console.WriteLine($"PASS {passed} voice checks; original local synthesis, gain/fades/cooldowns/cancellation. {timer.ElapsedMilliseconds} ms; no speakers or native playback used.");
