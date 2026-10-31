# Simultaneous module runtime checks

```powershell
dotnet run --project tests/BabbleBridge.MultiSource.Tests -c Release -- `
  --worker "dist/BabbleFrameBridge/runtime/BabbleFrameBridge.Worker.exe"
```

For a framework-dependent worker, add `--dotnet path/to/dotnet.exe` and pass the worker DLL instead.

The default tests use deterministic SDK fixtures in separate processes. They cover eye/brow versus lower-face ownership, one OSC stream, sender cadence, partial availability, stale neutralization, isolated crash recovery, startup rejection, input port conflicts, bounded input-only snapshots, normal teardown, and abrupt coordinator death without orphaned inputs.

Optional official-protocol checks:

```powershell
dotnet run --project tests/BabbleBridge.MultiSource.Tests -c Release -- `
  --worker "dist/BabbleFrameBridge/runtime/BabbleFrameBridge.Worker.exe" `
  --etvr-dll "path/to/ETVRTrackingModule.dll" `
  --baballonia-zip "path/to/Baballonia-Module-3.2.0.zip" `
  --beyond-zip "path/to/Build.zip"
```

Those checks drive the official ETVR and Baballonia modules with synthetic loopback OSC, and the official Beyond module with synthetic shared memory. They verify merged avatar OSC, validity filtering, and a stopped Beyond timestamp. They do not verify actual headset calibration or hardware tracking.

Close Beyond Eyetracking before running: the tests deliberately refuse to overwrite an existing `VRCFTMemmapData` mapping. Profiles are isolated under `work/multi-source-tests`; personal app settings are untouched.
