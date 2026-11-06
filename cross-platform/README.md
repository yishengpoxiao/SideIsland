# Side Island for macOS and Ubuntu

English | [简体中文](README.zh-CN.md)

**First release: v1.0.0. macOS and Linux support remains a preview.** This release number does not mean these platforms have passed native hardware validation. The independent Avalonia 12.1.4 / .NET 10 project targets Apple Silicon Macs and Ubuntu x64/arm64, reuses shared C# models, and supplies separate desktop adapters. Its scripts do not replace the Windows WPF application's installation or packages.

## What is included

The compact interface provides 21 entries: focus, music, Codex usage, ports, notes, clipboard, text comparison, text tools, QR transfer, calculator, random tools, checksums, screenshot pins, batch files, batch images, notifications, task watch, keep awake, system volume, task activities and settings. English/Chinese switching and light/dark themes are available. This is a port in progress, with a smaller scope than the Windows edition; see [porting status](docs/PORTING-STATUS.md).

- **Notifications:** app exclusions, priority, repeat merging, search, unread management and optional local history. Mac reads only accessible visible notification cards after explicit Accessibility opt-in. Ubuntu monitors new session D-Bus `Notify` calls when permitted. A token-protected local HTTP input is off by default. It does not replace the system notification service or promise hidden message history. [Details](docs/NOTIFICATIONS.md).
- **Screenshot pins:** system area selection on Mac; the Screenshot portal on Ubuntu, with installed GNOME Screenshot/Spectacle fallbacks only when the interface is unavailable. Drag, zoom, adjust opacity, close or save a new PNG. Cancellation and permission denial do not launch another picker. [Details](docs/SCREEN-PIN.md).
- **Batch files/images:** preview names and conflicts before renaming; retain an operation journal and support same-session undo when file identity/content still matches. Resize multiple images, change JPEG quality and export PNG/JPEG to a new output location. [Details](docs/BATCH-FILES.md).

Implemented adapters and managed tests do not establish that native permissions, capture, notifications or filesystem calls have passed on the destination system.

## Mac: notch first

The package requires macOS 15.0 or newer within the [.NET 10 support policy](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md). The table updated 2026-09-28 lists macOS 15, 26 and 27; Avalonia's lower deployment minimum does not extend runtime support. Target-device validation remains outstanding.

On a display with a real camera cutout, public AppKit safe-area and auxiliary-area APIs place a black island around the notch. Controls occupy its two wings; expanded content starts below it. The physical camera area has no drawable pixels. Without a usable notch, the window falls back to an ordinary top/selected edge.

The native bridge uses AppKit screen points directly to avoid applying Retina scaling twice. Multi-monitor arrangements, menu-bar behavior, Spaces, fullscreen, sleep/wake and display reconnection still require a real Mac. [Native bridge notes](docs/MAC-NOTCH.md).

The app's `Info.plist` sets `NSPrefersDisplaySafeAreaCompatibilityMode=false` for notch-aware layout. The required physical-device layout audit is outstanding; this per-app setting does not change global display settings or prove compatibility.

The first Mac media adapter connects to **Apple Music only**, after an explicit connection action, using Apple Events that may require user consent. Other players, synchronized lyrics, universal artwork access and per-app audio mixing need separate adapters. Volume controls the system output.

## Ubuntu

The target is Ubuntu 22.04 or newer on x64/arm64; native library compatibility must be checked on each target. Edge docking currently follows the X11/XWayland model. A Wayland session needs working XWayland; native Wayland positioning, stacking and transparent input remain subject to compositor protocols.

Music uses MPRIS players through `playerctl`; audio uses PulseAudio-compatible `pactl`, including PipeWire configurations providing that interface. Missing commands are reported rather than installed or replaced with fake state. Port details depend on system commands, procfs and process permissions.

Clipboard actions are explicit desktop reads/writes. They are not a universal Wayland background history service. Full work scenarios, screenshot annotation and the Windows media/lyrics feature set are not yet equivalent.

## Build and package

Use PowerShell and the .NET 10 SDK pinned by `global.json`; a runtime alone is insufficient. Scripts prefer the project-local `.runtime/cross-platform-sdk/10.0.402` SDK or accept `-DotnetPath`. They keep CLI/NuGet state under the repository's `.runtime` directory and do not register a system SDK.

From this directory:

```powershell
pwsh ./build.ps1 -Runtime osx-arm64
pwsh ./build.ps1 -Runtime linux-x64
pwsh ./build.ps1 -Runtime linux-arm64
```

`-SkipPackage` publishes only to `out/<rid>`. `-Runtime win-x64` builds the portable interface for Windows verification as `SideIsland-Preview-v1.0.0-win-x64.zip`; this distinct filename preserves the WPF product's release. Packaging can also be run after a matching completed publication:

```powershell
pwsh ./package.ps1 -Runtime osx-arm64 -PublishDirectory ./out/osx-arm64
```

The repository's `release/` directory receives:

- `SideIsland-v1.0.0-osx-arm64.zip`, containing a self-contained `Side Island.app` bundle.
- `SideIsland-v1.0.0-linux-x64.zip` or `SideIsland-v1.0.0-linux-arm64.zip`, containing `SideIsland/` and its `side-island` launcher.
- A `.sha256` and `.manifest.json` beside each ZIP, with payload hashes, Unix modes, runtime, release version and validation scope. Both README languages are included.

Packaging refuses to overwrite an existing ZIP, checksum or manifest. Review and archive an earlier local output explicitly, or change `VERSION` and rebuild for a new release. Already published release assets should remain unchanged.

ZIPs declare a Unix creator OS. Directories/native programs/libraries use 0755; regular data files use 0644. After extraction on Ubuntu, run `./side-island` inside `SideIsland/`. To use the optional desktop entry, first place the complete directory in your chosen app location and expose its launcher on your user PATH. Self-contained .NET packages still depend on basic desktop native libraries.

**The Mac package has not been Developer ID signed, notarized or validated on a physical Mac.** Cross-publication and ZIP checks do not establish Gatekeeper approval or a successful native launch. Normal signing/notarization and target validation remain outstanding. No Gatekeeper-disabling or quarantine-bypass scripts are provided. Package manifests retain `PlatformStatus=preview` and `TargetRuntimeExecuted=false`.

## Validation and version maintenance

```powershell
pwsh ./test.ps1 -Rounds 2
```

The runner retains restore/build logs, each full test output, rendered images, source hashes and assembly hashes. It exercises actual headless Avalonia layouts in English/Chinese, light/dark, 320/480/760-point widths, 360/720-point heights and 100%/125%/150%/200% simulated DPI. Headless rendering and injected adapter tests do not replace a real window manager, Retina display, native capture/permissions or audio-service validation. Read the preserved results in `artifacts/cross-platform/`; Windows WPF regression counts do not transfer to this port.

`build.ps1` records immutable source inputs and publication hashes. `package.ps1` checks native apphost architecture, full payload copying, decompressed ZIP hashes and execution modes. Failed operations are not recorded as passed.

The repository root `VERSION` file is the single release-version source shared with Windows. `Directory.Build.props` reads it for project metadata; the UI reads its own assembly version, and build/package/test scripts read the same root file. The packager rejects mismatching publish markers or assemblies instead of relabeling old binaries. Keep static manifest templates and documentation examples aligned when changing versions. The v1.0.0 release tag and the macOS/Linux preview status describe different things.
