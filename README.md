# Steam Frame Babble Bridge

A Windows desktop bridge between **Babble Frame Runtime on Steam Frame** and **VRChat**. It hosts a separately supplied Baballonia tracking module using a compatible VRCFaceTracking Core and SDK. The Windows shortcut remains **Frame Bridge** for existing installations.

This is an independent application. It is not an official release of Project Babble, VRCFaceTracking, Valve, or VRChat, and no affiliation or endorsement is implied. The bridge uses its own original artwork and independently implemented particle animation; compatible project names identify interoperability.

## Features

- Automatic headset discovery through BFR Link.
- Legacy VRCFaceTracking avatar parameters and VRChat's native SDK beta face-tracking input, selected from the avatar's advertised capability.
- A light or dark interface that follows Windows, including changes while the app is open.
- Built-in particle motion and transitions that respect Windows' reduced-motion preference.
- Manual module ZIP imports, startup validation, and restoration of the previous working module.
- Per-user installation and updates that preserve settings and imported modules.

**No Baballonia module ZIP or DLL is included in the application, installer, or repository.** You must obtain and import your own compatible module.

## Getting started

You need Windows 10 version 2004 or later (x64), the [Microsoft Visual C++ v14 Redistributable (x64)](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist), Steam Frame running [Babble Frame Runtime](https://github.com/Project-Babble/Babble-Frame-Runtime), VRChat with OSC enabled, and a compatible [Baballonia module ZIP](https://github.com/Project-Babble/Baballonia) obtained separately. Install the Visual C++ runtime from Microsoft; it is not redistributed by this repository.

1. Run a packaged `FrameBridge-Setup-<version>.exe` installer, or extract the entire Windows x64 portable archive. The packaged app includes its .NET runtime.
2. Open Frame Bridge and choose **Add module ZIP**, or drop your module ZIP onto the window.
3. Start Babble Frame Runtime on the headset. The headset and PC must be able to reach each other on the network.
4. Enable OSC in VRChat and select a compatible face-tracking avatar. Frame Bridge discovers the local VRChat client and its avatar capabilities automatically.

Keep other VRCFaceTracking/Baballonia bridges closed while Frame Bridge is running, because they can use the same listening ports. The defaults are BFR Link UDP `6767`, module OSC UDP `8888`, VRChat output `127.0.0.1:9000`, and avatar-change input `127.0.0.1:9001`.

The overview shows headset connection, VRChat output, and the active module. Expression mapping continues in the worker; there is no expression-monitor panel or application animation toggle.

## Updates and data

To update the **app**, run a newer installer. To update the **module**, choose **Import module ZIP** in the app. A new module is activated after initialization succeeds; **Restore previous** returns to the saved module. An incompatible module API can require an app update.

Settings, activity logs, and imported module versions are stored in `%LOCALAPPDATA%\BabbleFrameBridge`. App upgrades preserve that folder, and normal uninstall leaves it available for reinstalling. App updates are distributed as newer setup files; no online automatic-update service is configured.

See [START-HERE.txt](START-HERE.txt) for setup and troubleshooting. Imported module DLLs execute code on the computer; the worker process provides restart/unload isolation, not an operating-system security sandbox.

## Build from source

Build on Windows x64 with the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). NuGet restore requires an internet connection.

```powershell
.\build.ps1
```

The self-contained .NET app is published to `dist\BabbleFrameBridge`. Keep its runtime, assets, `LICENSE`, `NOTICE.txt`, `THIRD_PARTY_NOTICES.md`, and `licenses/` together when distributing it. The separate Microsoft Visual C++ prerequisite is not installed by the build or installer.

An optional installer can be built with Inno Setup 7.1.1 or a compatible newer compiler:

```powershell
.\installer\build-installer.ps1
```

See [BUILD.txt](BUILD.txt) and [installer/README.txt](installer/README.txt) for build, versioning, and installer details. The installer helper rejects payloads containing a Baballonia module archive/DLL, obsolete official artwork, or an app-local `vcruntime140.dll` copied from Windows. It preserves the .NET runtime's separate `vcruntime140_cor3.dll`.

## Tests

The following checks do not need a module, headset, VRChat session, or account credentials:

```powershell
dotnet run --project tests/EyeData.Tests -c Release
dotnet run --project tests/BabbleFrameBridge.NativeFace.Tests -c Release
dotnet run --project tests/BabbleFrameBridge.Appearance.Tests -c Release -- --publish-dir dist/BabbleFrameBridge
```

The appearance harness opens isolated offscreen windows and temporary data profiles. It does not change Windows personalization settings.

Package and real-module integration tests require a ZIP you supply explicitly:

```powershell
$moduleZip = 'C:\Path\VRCFaceTracking.Baballonia.zip'
dotnet run --project tests/BabbleFrameBridge.Tests -c Release -- $moduleZip
python tests/integration_test.py --worker dist/BabbleFrameBridge/runtime/BabbleFrameBridge.Worker.exe --module-zip $moduleZip
python tests/native_integration_test.py --worker dist/BabbleFrameBridge/runtime/BabbleFrameBridge.Worker.exe --module-zip $moduleZip
```

Python 3.11+ is recommended. Integration tests use temporary module/config copies and isolated loopback ports; they do not inject synthetic expressions into a live VRChat session. Do not commit module fixtures, local profiles, logs, or recordings.

The GitHub Actions workflow builds the portable application and runs the module-free tests on Windows. Its downloadable build artifact retains the distribution notices. It does not publish releases or use a private module ZIP.

## Compatibility

This host is specifically for Babble Frame Runtime and the compatible Baballonia module family. It is not a general replacement for every VRCFaceTracking module. Native SDK beta support follows VRChat's documented [`/tracking/face/v1` protocol](https://vrc-beta-docs.netlify.app/avatars/face-tracking/osc/); an avatar must advertise the corresponding support. Eye widening and actual eyebrow movement are distinct inputs, and unsupported source channels are not synthesized.

OSC output counters confirm sends, not that an avatar applied every blendshape. Visual results depend on the avatar's own setup.

## Copyright, licenses, and attribution

The original app code and original bridge artwork are licensed under Apache 2.0.

See [LICENSE](LICENSE), [NOTICE.txt](NOTICE.txt), [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md), and [licenses/](licenses/) for the applicable copyright notices, source provenance, and component-specific terms. Third-party code, fonts, and native libraries retain their respective licenses and copyrights; the repository's main license does not replace those terms. IBM Plex Sans fonts retain their SIL Open Font License notices.

The separately obtained Baballonia module and headset runtime have their own licenses and are not distributed here. Preserve the required license and notice files when redistributing this application. Product names are used to identify interoperability; trademark rights and endorsement are not granted by a software license.

The SDK is pinned in `global.json` so the packaged runtime matches the recorded notices. When updating SDK, runtime, or package versions, refresh the license inventory and run `python tests/check_distribution_licenses.py --publish-dir dist/BabbleFrameBridge`.
