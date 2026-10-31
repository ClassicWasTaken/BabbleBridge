FACE TRACKING BRIDGE INSTALLER

The Windows installer installs Face Tracking Bridge for the current user, creates its
Start menu shortcut, offers a desktop shortcut, and adds an uninstaller to
Windows Installed Apps. The application and its .NET runtime are included.
Tracking modules are separate: users download official modules or import their
own compatible ZIPs from Modules. Imported modules stay available through upgrades.
The setup wizard and uninstaller also follow Windows light/dark preferences.

Windows shortcuts and Installed Apps use the original headset icon stored at
Assets\Brand\FaceTrackingBridge-headset-v2.ico. A separate icon path lets upgrades
refresh the desktop and Start menu icon without resetting Windows' icon cache.
Upgrades replace former app shortcuts only when their targets match
this installation. The previous installation path and application identity stay
stable so existing settings and imported modules continue to work.

BUILD AN INSTALLER
Requirements: Windows x64, .NET 10 SDK, and Inno Setup Compiler. Inno Setup 7.1.1
is the tested compiler; obtain it from https://jrsoftware.org/isdl.php.

From the source root in PowerShell:
  .\build.ps1
  .\installer\build-installer.ps1

The first command publishes the app to dist\BabbleFrameBridge. The second reads
the app Version from its project, checks the published executable's version and
required payload files, and produces:
  dist\installer\FaceTracking-Bridge-Setup-<version>.exe
  dist\installer\FaceTracking-Bridge-Setup-<version>.exe.sha256

To use a compiler or payload in a different location:
  .\installer\build-installer.ps1 -Compiler 'C:\Tools\Inno Setup 7\ISCC.exe' -PayloadDirectory 'C:\Build\BabbleFrameBridge' -OutputDirectory 'C:\Build\Installers'

The helper requires Inno Setup 7 or newer and searches PATH and the usual Inno
Setup 7 installation directories. It uses Inno Setup 7's long command-line
switches. Older compilers are rejected before compilation because they may lack
the installer styling and directives used here.
It never installs the compiled package automatically or deletes existing files.
It rejects payloads containing a module ZIP or external module DLL. On upgrade, the
installer removes the obsolete baked-in Assets\Baballonia-module.zip from old
app folders; it preserves separately imported modules under AppData.

RELEASE A FUTURE UPDATE
1. Increase Version in src\BabbleFrameBridge.App\BabbleFrameBridge.App.csproj.
2. Publish the new app with build.ps1.
3. Run installer\build-installer.ps1 and distribute the resulting setup file.
4. Run that newer installer on a computer with Face Tracking Bridge, FaceTracking Bridge, or Babble Bridge already installed.
   It upgrades the existing installation; uninstalling first is unnecessary.

Keep the production AppId in FrameBridge.iss unchanged across releases. Keep the
per-user and 64-bit installation modes consistent, and preserve the existing
installation directory. These values identify the same application to Windows
and Inno Setup, allowing one installed app entry to be updated over time.
Keep the BabbleBridgeReleaseGeneration registry marker at 3 in future installers.
The Face Tracking Bridge 1.0.x series accepts only generation-2 Babble Bridge
1.0.1/1.0.2 and FaceTracking Bridge 1.0.3 as known public numbering migrations,
including users who skip the reset's first 1.0.0 installer.
Babble Bridge 1.0.0 can reinstall normally with the same numeric version.
Retired private previews 1.0.1 through 1.0.5 without a generation marker can
still migrate to a public 1.0.x installer. Exact known older development builds
also require the absence of a marker. Every successful install records 3;
generation-3 and unknown installations receive normal downgrade protection.
New installations use %LOCALAPPDATA%\Programs\Face Tracking Bridge. Upgrades reuse
the existing installation directory automatically, including older app paths.
The installer rejects an older release being installed over a newer release.
Existing same-name desktop, Start menu and guide shortcuts are updated only when
their target belongs to this installation. Unrelated shortcuts are preserved.
Owned FaceTracking Bridge, Babble Bridge, Steam Frame Babble Bridge and Frame Bridge shortcuts migrate
after their new replacement exists. Foreign shortcuts with those names remain.
Setup and Uninstall refuse to proceed while this application's presence mutex
exists. Choose Quit from the app's system tray menu before updating or removing
it. Clicking the window's X hides the app and keeps tracking running.

STEAMVR LAUNCH REGISTRATION
Automatic SteamVR launch is optional and starts disabled. When it is enabled
inside the app, upgrades refresh this app's existing SteamVR registration after
the new executable has been installed. The app key stays stable across versions,
and the registration points to the actual installation directory.
Before uninstall removes the executable, it asks the app to disable automatic
SteamVR launch, unregister its own app key, and remove its owned manifest. Other
SteamVR applications and settings are left alone. Reinstalling requires opting
in again. These hidden maintenance commands use OpenVR's utility interface and
do not start a VR session. OpenVR may briefly start its background settings
service to save registration. If maintenance fails, Setup logs a warning and
continues so an unavailable runtime cannot prevent removal.

Settings, logs and imported module releases live separately in:
  %LOCALAPPDATA%\BabbleFrameBridge
Updates preserve that data, and normal uninstall leaves it available for a later
reinstall. Uninstall clears only the automatic SteamVR launch preference and its
owned registration; other preferences and imported modules are preserved. A
module ZIP update and an app installer update are separate actions: module ZIPs
still use Import module ZIP inside the app.

UPDATES AND SIGNING
Future app updates are delivered as newer setup files. No hosted automatic
update service is configured. The SHA-256 file records the setup file's checksum;
it verifies file integrity, not publisher identity.
This build is unsigned, so Windows may show an unknown publisher.

SOURCE FILES
  FrameBridge.iss       Installer definition and stable application identity
  build-installer.ps1   Version checks, compiler discovery and checksum output
  wizard assets        Original bridge artwork supplied with this source

Keep the installer definition, artwork and required notices with the source.
Keep LICENSE, NOTICE.txt, THIRD_PARTY_NOTICES.md, licenses/, and the IBM font
copyright/license file with each distribution. The setup does not include the
tracking module, official Babble logo assets, or a System32 copy of the Visual
C++ runtime. Install Microsoft's official x64 Redistributable separately when
required by the native OSC library. See the main notices for attribution.

The installer smoke test uses a random AppId, isolated paths and an explicitly
injected fake maintenance helper. It never registers or unregisters an app in
the user's SteamVR installation. Nonproduction AppIds disable these hooks by
default unless a test explicitly overrides them.
