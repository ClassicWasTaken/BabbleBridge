# Contributing

Bug reports and pull requests are welcome. For a bug report, include the app
version, module name/version, Windows version, and steps to reproduce it.
Remove avatar IDs, IP addresses, usernames, and other personal data from logs
and screenshots. Use [private reporting](SECURITY.md) for security problems.

Fork the repository, create a branch, and open a pull request against `main`.
Explain the problem, the resulting behavior, and how you checked the change.
Include before/after screenshots for interface changes. Hardware-specific
changes should identify which headset and module were actually tested.

## Build and check

Use Windows x64, PowerShell, the .NET 10 SDK, and Python 3.11 or newer.
From the repository root:

```powershell
.\build.ps1
dotnet run --project tests/BabbleFrameBridge.Tests -c Release -- --settings-only
dotnet run --project tests/BabbleFrameBridge.Download.Tests -c Release
dotnet run --project tests/EyeData.Tests -c Release
dotnet run --project tests/BabbleBridge.Sending.Tests -c Release
dotnet run --project tests/BabbleFrameBridge.NativeFace.Tests -c Release
dotnet run --project tests/BabbleFrameBridge.Appearance.Tests -c Release -- --publish-dir dist/BabbleFrameBridge
python tests/check_distribution_licenses.py --publish-dir dist/BabbleFrameBridge
```

See [BUILD.txt](BUILD.txt) for real-module integration tests and installer
checks. Obtain module ZIPs separately; keep modules, local profiles, logs,
recordings, signing files, and credentials out of commits and build artifacts.
Tests should use temporary profiles and synthetic tracking data.

## Licenses and assets

Original contributions are submitted under this repository's
[Apache 2.0 license](LICENSE). Preserve existing copyright notices and the
licenses of third-party code, fonts, and dependencies. Document the origin and
license of added dependencies or assets in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
Use original visual assets or assets with suitable redistribution rights.
Separate tracking modules retain their own terms and are not bundled here.

Changes to vendored VRCFaceTracking source should retain upstream attribution
and carry a clear modification notice. Keep CI builds safe for forked pull
requests: read-only permissions, hosted runners, and no publishing credentials.
