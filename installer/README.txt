FRAME BRIDGE INSTALLER

The Windows installer installs Frame Bridge for the current user, creates its
Start menu shortcut, offers a desktop shortcut, and adds an uninstaller to
Windows Installed Apps. The application and its .NET runtime are included.
The tracking module is separate: users import their own Baballonia ZIP after
opening the app. Existing imported modules stay available through upgrades.
The setup wizard and uninstaller also follow Windows light/dark preferences.

Windows shortcuts and Installed Apps use the original headset icon stored at
Assets\Brand\FrameBridge-headset-v1.ico. A separate icon path lets upgrades
refresh the desktop and Start menu icon without resetting Windows' icon cache.

BUILD AN INSTALLER
Requirements: Windows x64, .NET 10 SDK, and Inno Setup Compiler. Inno Setup 7.1.1
is the tested compiler; obtain it from https://jrsoftware.org/isdl.php.

From the source root in PowerShell:
  .\build.ps1
  .\installer\build-installer.ps1

The first command publishes the app to dist\BabbleFrameBridge. The second reads
the app Version from its project, checks the published executable's version and
required payload files, and produces:
  dist\installer\FrameBridge-Setup-<version>.exe
  dist\installer\FrameBridge-Setup-<version>.exe.sha256

To use a compiler or payload in a different location:
  .\installer\build-installer.ps1 -Compiler 'C:\Tools\Inno Setup 7\ISCC.exe' -PayloadDirectory 'C:\Build\BabbleFrameBridge' -OutputDirectory 'C:\Build\Installers'

The helper searches PATH and the usual Inno Setup 7/6 installation directories.
It uses Inno Setup 7's long command-line switches, or legacy switches for version
6 when the installer script is compatible. Inno Setup 7 is recommended.
It never installs the compiled package automatically or deletes existing files.
It rejects payloads containing a module ZIP or Baballonia DLL. On upgrade, the
installer removes the obsolete baked-in Assets\Baballonia-module.zip from old
app folders; it preserves separately imported modules under AppData.

RELEASE A FUTURE UPDATE
1. Increase Version in src\BabbleFrameBridge.App\BabbleFrameBridge.App.csproj.
2. Publish the new app with build.ps1.
3. Run installer\build-installer.ps1 and distribute the resulting setup file.
4. Run that newer installer on a computer with Frame Bridge already installed.
   It upgrades the existing installation; uninstalling first is unnecessary.

Keep the production AppId in FrameBridge.iss unchanged across releases. Keep the
per-user and 64-bit installation modes consistent, and preserve the existing
installation directory. These values identify the same application to Windows
and Inno Setup, allowing one installed app entry to be updated over time.
The installer rejects an older release being installed over a newer release.

Settings, logs and imported module releases live separately in:
  %LOCALAPPDATA%\BabbleFrameBridge
The installer does not overwrite that folder. Updates preserve it, and normal
uninstall leaves it available for a later reinstall. A module ZIP update and an
app installer update are separate actions: module ZIPs still use Import module
ZIP inside the app.

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
