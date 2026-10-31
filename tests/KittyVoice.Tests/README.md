# Original kitty voices

Run `dotnet run --project tests/KittyVoice.Tests -c Release`.

The app synthesizes its own short meows, chirps, yawns and purrs with changing
pitch, vowel-shaped harmonic resonances, trill envelopes and quiet breath noise.
These are stylized robot-cat vocalizations, not recordings of real cats. The
code and generated sounds are original project work under Apache 2.0. There are
no audio downloads, microphone inputs, copied recordings, speech services or
third-party audio runtimes.

Synthesis runs off the UI thread. A global per-companion cooldown prevents
overlap and constant chatter. Muting, hiding or disposing invalidates queued
audio and stops native playback. Audio gain affects this waveform only and does
not change Windows or any other application's volume.

Normal tests inject a fake player and never initialize a native audio output or
play sound through the user's speakers. Checks cover PCM headers, duration,
gain, fades, reproducible variation, distinct voice shapes, mute, cooldown,
late-synthesis cancellation and audio-device failure. An optional
`--samples <directory>` writes original WAV examples without playing them.
