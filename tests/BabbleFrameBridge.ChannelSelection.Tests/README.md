# Channel selection regression checks

Run from the repository root with .NET 10:

```powershell
dotnet run --project tests/BabbleFrameBridge.ChannelSelection.Tests -c Release
```

This pure shared-code suite exercises persistent eye/face assignments, deduplicated shared modules, pair rollback, legacy single-module state, missing or invalid modules, safe removal, backup recovery, and failed atomic writes. It uses the test build's managed shared assembly as a staging fixture. It does not load module code, connect to tracking hardware, send OSC, or access the real application profile.
