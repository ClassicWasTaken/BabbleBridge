# Face Tracking Bridge

<h2 align="center">
  <a href="https://github.com/ClassicWasTaken/FaceTrackingBridge/releases/latest/download/FaceTracking-Bridge-Setup.exe">⬇ Download for Windows (.exe)</a>
</h2>

<p align="center">
  <a href="https://babble.diy/">Visit the Project Babble website ↗</a>
</p>

A Windows app that bridges eye and face tracking to VRChat through compatible **VRCFaceTracking SDK** modules. Supports Babble Headset Runtime, Baballonia desktop, Virtual Desktop, and Steam Link, with Jerry’s templates and VRChat’s native face-tracking SDK beta.

![Face Tracking Bridge running on Windows](docs/images/app-running.png)

## Get started

1. Install the app and download a **VRCFaceTracking module** from Modules, or import a module DLL or ZIP.
2. Select your module and start its **headset runtime or desktop tracking app**.
3. Enable **OSC** in VRChat and select a face-tracking avatar.

Downloads come from the module authors; modules are never bundled. Your selected module is remembered, with its own connection settings. Remove imported modules with the trash button. Jerry’s templates are supported.

Using Baballonia Expressions Alpha? Download **Baballonia** in Modules, or choose **Update for Expressions Alpha** on an older imported module.

In **Settings**, choose whether to start with SteamVR, close when it exits, or **start minimized to system tray**. Clicking **X** keeps tracking running in the tray; double-click the tray icon to reopen, or right-click it to **Quit**.

Click the **kitty face in the bottom-left corner** to meet Relay, an optional holographic robot cat with springy articulated movement. Its local recurrent AI follows play and rest routines, remembers recent context, and chooses what to pay attention to. Relay can knead, stalk, pounce, groom, nap, meow, chirp and yawn. Mute **Kitty sounds** in Settings; the companion pauses in the tray. [Behavior and research notes](docs/RELAY-BEHAVIOR.md).

Other compatible VRCFaceTracking SDK modules work too. `module.json` is optional; use a ZIP if a module needs supporting files.

Only import modules from publishers you trust: they run code on your PC under their own licenses.

Windows 10/11 x64. [Setup & requirements](START-HERE.txt) · [Build from source](BUILD.txt)

Original code: [Apache 2.0](LICENSE). [Copyrights & third-party licenses](THIRD_PARTY_NOTICES.md).

[Pull requests welcome](CONTRIBUTING.md) · [Report a security issue privately](SECURITY.md)

Independent project; not endorsed by Project Babble, VRCFaceTracking, Valve, or VRChat.
