# Third-party copyrights and licenses

The original Face Tracking Bridge code and original headset artwork are
Copyright (c) 2026 Frame Bridge contributors, licensed under [Apache 2.0](LICENSE).
Third-party works retain their own copyrights and terms. This file maps those
works to the full notices preserved in the repository and distribution.

| Component | Copyright / license | Preserved notices |
| --- | --- | --- |
| VRCFaceTracking Core, SDK, and the native OSC binary from its pinned distribution | Copyright 2024 benaclejames; Apache-2.0 | [Upstream license](licenses/VRCFaceTracking-LICENSE.txt), [source license](vendor/LICENSE), [provenance](licenses/LICENSE-SOURCES.txt) |
| IBM Plex Sans, unmodified | Copyright (c) 2017 IBM Corp.; Reserved Font Name “Plex”; SIL OFL-1.1 | [Font copyright and full OFL](src/BabbleFrameBridge.App/Assets/Brand/IBM-Plex-OFL.txt), [asset sources](src/BabbleFrameBridge.App/Assets/Brand/SOURCE.txt) |
| CommunityToolkit.Mvvm 8.4.0 | .NET Foundation and Contributors; MIT | [NuGet license and third-party notices](licenses/NuGet/) |
| Gameloop.Vdf 0.6.2 | Copyright (c) 2016 Shravan Rajinikanth; MIT | [Full license](licenses/NuGet/Gameloop.Vdf-0.6.2-LICENSE-MIT.txt) |
| Newtonsoft.Json 13.0.4 | Copyright (c) 2007 James Newton-King in the license; original package metadata also states 2008; MIT | [Full license](licenses/NuGet/Newtonsoft.Json-13.0.4-LICENSE.md), [original metadata](licenses/DEPENDENCY-METADATA.json) |
| Sentry 6.0.0 | Copyright (c) 2018 Sentry; MIT | [Full license](licenses/NuGet/Sentry-6.0.0-LICENSE-MIT.txt) |
| Microsoft.Extensions.* 10.0.1 and System.Diagnostics.EventLog 10.0.1 | Microsoft Corporation in package metadata; .NET Foundation and Contributors in the applicable MIT text | [Full MIT text](licenses/NuGet/Microsoft-dotnet-10.0.1-LICENSE-MIT.txt), [individual package notices](licenses/NuGet/) |
| Microsoft.NETCore.Platforms 1.1.0 and NETStandard.Library 1.6.1 metapackages | Microsoft Corporation; exact packaged Microsoft .NET Library terms | [Packaged license and notices](licenses/NuGet/), [original metadata](licenses/DEPENDENCY-METADATA.json) |
| Self-contained Microsoft.NETCore.App and Microsoft.WindowsDesktop.App runtimes | .NET Foundation and Contributors and the authors identified in runtime notices; MIT and component-specific terms | [Exact restored runtime licenses and notices](licenses/DotNet/) |
| Rust standard library embedded in the upstream native OSC binary | Rust contributors identified in COPYRIGHT; MIT/Apache-2.0 and included component notices | [Full Rust license and copyright files](licenses/Rust/) |

[DEPENDENCY-METADATA.json](licenses/DEPENDENCY-METADATA.json) records the original
metadata for 34 resolved NuGet libraries and two runtime packages.
[LICENSE-SOURCES.txt](licenses/LICENSE-SOURCES.txt) explains provenance and scope;
[LICENSE-SOURCES.json](licenses/LICENSE-SOURCES.json) records the source URLs and
hashes of collected texts. Preserve the full license and copyright texts, not
only this summary or a license URL.

The native `fti_osc.dll` is retained unchanged from the Apache-licensed
VRCFaceTracking revision documented in [NOTICE.txt](NOTICE.txt). Its exact
historical native source build has not been independently reproduced. The
separate SimpleRustOSC repository is a provenance clue, not an additional
license grant. The upstream binary's recorded SHA-256 is
`668628bcaf181617839663fe3624f42eb7d36ae3d16e3434ace40a9545aa982e`.

Microsoft's Visual C++ Redistributable is a separate prerequisite when needed.
This project does not copy or ship `vcruntime140.dll` from Windows. Obtain it
from [Microsoft's official distribution](https://learn.microsoft.com/cpp/windows/latest-supported-vc-redist)
and follow its applicable terms. Build tools and the installer compiler are
not included in the app package.

Baballonia, [Virtual Desktop](https://github.com/guygodin/VirtualDesktop.VRCFaceTracking)
and [Ykeara LinkFT](https://github.com/ykeara/LinkFT) module packages, the headset
runtime, VRChat, and headset/streaming software are not distributed here. They
remain governed by their own licenses. The app's Apache license does not
relicense those separately obtained modules or their dependencies. Official
Project Babble logo assets and the previous website-animation port were removed
from this version; no permission to use another project's trademarks is implied.

When redistributing a build, include `LICENSE`, `NOTICE.txt`, this file,
`licenses/`, and `Assets/Brand/IBM-Plex-OFL.txt`. Paths in the table are source-tree
paths; the font license is copied under `Assets/Brand/` in the installed app.

This project is independent and is not sponsored or endorsed by Project Babble,
VRCFaceTracking, Valve, or VRChat. This compliance record is not a legal opinion
or a warranty of non-infringement.
