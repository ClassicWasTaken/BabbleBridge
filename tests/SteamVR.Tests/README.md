SteamVR lifecycle tests use an injected fake registration API, temporary profiles, and synthetic process observations. They never initialize OpenVR, modify the user's SteamVR registration, or start/stop SteamVR.

Run `dotnet run --project tests/SteamVR.Tests/SteamVR.Tests.csproj`.

The checks cover registration/removal, manifest ownership, rollback, missing runtimes, executable paths with spaces, delayed shutdown, failed process reads, a server restart, and observer cancellation. A real headset session is still needed to verify SteamVR's external startup behavior end to end.

An explicit `--native-probe` argument runs a separate read-only ABI check against the installed runtime: it briefly connects as `VRApplication_Utility`, queries this app's installed/auto-launch flags, disconnects, and verifies that no SteamVR presentation processes appeared. Utility may briefly start a headless settings server; the probe waits for it to exit and the session observer deliberately ignores that server. The probe never calls the registration, removal, or flag-setting APIs. The normal test run does not load native code.
