# Official module runtime checks

These optional Windows tests use the authors' compiled releases, a built worker, isolated profiles, and loopback output. Download the official SRanipal 1.5 ZIP, Beyond v1.1.0 `Build.zip`, and ETVR 0.0.5 DLL first.

```powershell
dotnet run --project tests/BabbleFrameBridge.ModuleRuntime.Tests -c Release -- `
  --worker "dist/BabbleFrameBridge/runtime/BabbleFrameBridge.Worker.exe" `
  --etvr-dll "path/to/ETVRTrackingModule.dll" `
  --beyond-zip "path/to/Build.zip" `
  --sranipal-zip "path/to/SRanipalTrackingModule.zip"
```

For a framework-dependent worker DLL, pass `--worker path/to/BabbleFrameBridge.Worker.dll --dotnet path/to/dotnet.exe`.

The ETVR check sends synthetic gaze and blink packets through the official DLL and verifies outgoing avatar OSC. The Beyond check verifies offline waiting, then supplies synthetic shared memory and verifies gaze and blink output. The SRanipal check verifies module loading, offline waiting without output, and clean shutdown. Actual headset tracking, calibration, and SRanipal native initialization require the relevant hardware and are **not verified** by these tests.

Close SRanipal and Beyond Eyetracking before running. The tests will not overwrite an existing Beyond memory mapping or use a live SRanipal process. Test profiles remain under `work/official-runtime-tests` for inspection; personal app settings are untouched.
