English | [简体中文](README.zh-CN.md)

# Side Island

A compact, iOS-inspired desktop island for notifications, music, focus and everyday tools.

**v1.0.0** · **38 tools on Windows** · **macOS and Ubuntu preview**

Dock it to any screen edge, keep useful widgets visible while collapsed, then expand for the full toolbox. Drag to reposition, resize the expanded panel, and switch between English/Chinese and light/dark themes. The app uses C#; no Python, Electron or Node.js runtime is required.

[Download v1.0.0](https://github.com/yishengpoxiao/SideIsland/releases/latest) · [Build from source](docs/BUILDING.md) · [Architecture](docs/ARCHITECTURE.md)

Development work after v1.0.0 adds portable data recovery, workspaces, log watch and other fixes. See [Unreleased improvements](docs/UNRELEASED.md); the published v1.0.0 downloads remain unchanged.

## Choose your platform

| Platform | Status | Requirements | Release asset |
| --- | --- | --- | --- |
| Windows | Mature implementation; regression and real desktop/player testing | Windows 10 2004 / build 19041 or later, or Windows 11; .NET Framework 4.8; x64 desktop is the tested environment | `SideIsland-v1.0.0-win-x64.zip` |
| Apple Silicon Mac | **Preview: not tested on a target Mac** | macOS 15 or later; ARM64 | `SideIsland-v1.0.0-osx-arm64.zip` |
| Ubuntu | **Preview: not tested on a target Ubuntu desktop** | Ubuntu 22.04 or later; x64 or ARM64; X11 or an available XWayland session | `SideIsland-v1.0.0-linux-x64.zip` / `SideIsland-v1.0.0-linux-arm64.zip` |

The preview has **21 tool entries**, rather than full parity with the Windows toolbox. Build, packaging and headless test results do not establish native macOS/Ubuntu compatibility. Mac packages are **not Developer ID signed or notarized**. See the [porting status](cross-platform/docs/PORTING-STATUS.md) before choosing a preview.

## Get started

### Windows

1. Download the Windows ZIP from [Releases](https://github.com/yishengpoxiao/SideIsland/releases/latest) and extract the entire archive to a permanent folder.
2. Run `CodexIsland.exe`. Click the island to expand; drag the collapsed body to move it. Use the tray or right-click menu to find or exit the app.
3. Open settings to choose widgets, docking, hover delay, language and appearance. `Ctrl + Alt + Space` toggles the island; `Ctrl + K` searches tools while it has focus.

Most tools work without a Codex account. Quota monitoring requires an installed, signed-in Codex desktop app or CLI; select the executable and data directory in settings if discovery fails.

**Optional Windows notification access**

The portable EXE alone cannot read other apps' system notifications. Close the app and run `install-notifications.cmd` from the complete extracted package. Local development registration requires Windows Developer Mode; an already trusted, signed MSIX is the alternative. The installer does not enable Developer Mode or add certificates itself.

Launch **Side Island** from the Start menu or `启动通知版.cmd`, choose **Connect system notifications** in Messages, then grant access in Windows yourself. Keep the installation folder in place after registration. Only notifications actually delivered through Windows are available; an app's private popup is not a system notification. [Installation details](packaging/README.md)

### macOS preview

Extract the preview and move the complete `Side Island.app` to your application folder. The unsigned, unnotarized preview may be blocked by macOS; this project does not provide Gatekeeper bypass commands. Target-Mac launch, signing and notarization remain outstanding.

The island prioritizes a real display notch and places controls in the visible wings or below it. The physical camera cutout cannot display pixels. Apple Music access is explicitly connected and may need Automation permission; visible notification reading needs a separate, manual Accessibility grant. [Mac notch details](cross-platform/docs/MAC-NOTCH.md)

### Ubuntu preview

Extract the matching architecture package, open the `SideIsland` folder and run `./side-island`. Preview archives include execution permissions and the .NET runtime; the desktop's native libraries are still required.

Optional integrations use `playerctl` for MPRIS media, `pactl` for PulseAudio/PipeWire volume, `ss` for ports, `dbus-monitor` for permitted notification monitoring, and `systemd-inhibit` for keep-awake. Missing tools are reported and are not installed automatically. Native Wayland positioning, topmost behavior and input transparency are not guaranteed by this preview. [Preview guide](cross-platform/README.md)

## What you can do

| Area | Windows features |
| --- | --- |
| Stay informed | Custom collapsed widgets, Codex quota, CPU/memory/battery, notification previews, per-app blocking and priority, task activity |
| Music and audio | Playback controls, available artwork and metadata, local LRC lyrics, optional online lyric matching, system and per-app volume |
| Focus and routines | Custom focus timers, stopwatch, reminders, notes/to-dos, workspace launch, scene layouts and automatic scene rules |
| Developer tools | Port owner/PID/path lookup, live log tracking, file/process watching, service-ready reminders, text diff, templates and checksums |
| Images and files | Screenshot pinning and annotation, local Windows OCR, image resize/compression/conversion, batch rename, file shelf and LAN file sharing |
| Everyday utilities | QR codes, clipboard history, calculator, units, dates/time zones, text tools, random/password tools and color picking |

Tool availability, limits and detailed interactions are covered in the [Windows guide](docs/windows-guide.zh-CN.md). Its older development version labels are retained as historical context; the public release is **v1.0.0**.

Real Windows playback/control and artwork testing has covered QQ Music, NetEase Cloud Music and Apple Music. Cider was not installed and is **not claimed as tested**. Metadata, progress, controls and lyrics depend on what the player provides; unavailable progress is not fabricated.

## Platform differences

| Capability | Windows | macOS / Ubuntu preview |
| --- | --- | --- |
| Tool coverage | 38 modules | 21 entries; partial migration |
| Notifications | Windows notification listener with registered identity and user consent | Mac: recognizable, visible Accessibility cards only. Linux: future standard Notify calls allowed by the session bus. Optional authenticated localhost feed |
| Media | Windows system media sessions, subject to player support | Mac: Apple Music only. Linux: MPRIS via playerctl. No promise of equivalent artwork or synchronized lyrics |
| Audio | System and per-app mixer | System output volume; no equivalent cross-platform per-app mixer |
| Screenshots | Region capture, pinning, annotation and OCR | System capture/portal and pinning; native permissions and desktop behavior unverified |
| Clipboard | Opt-in text/image history, search, favorites and app exclusions | Ordinary clipboard tools; no universal background history guarantee |
| Docking | Four edges, multi-monitor positions and fullscreen rules | Mac notch-first placement; X11/XWayland positioning. Fullscreen Spaces and native Wayland behavior need target testing |

See [notification scope](cross-platform/docs/NOTIFICATIONS.md), [screenshot scope](cross-platform/docs/SCREEN-PIN.md) and [batch-file safeguards](cross-platform/docs/BATCH-FILES.md) for preview boundaries.

## Local data and connections

Preferences, notes and other saved tool data live on your computer. Windows notification history is transient unless you explicitly turn a notification into a saved task. The preview's notification history is off by default and can be explicitly saved as local plaintext. Clipboard recording is opt-in; saved notes and favorites are not encrypted.

Codex quota checks communicate through the local, signed-in Codex service. Optional LRCLIB lyric lookup sends track title, artist, album and duration, and is off by default. LAN file sharing starts only when you start a share. Review tool settings before enabling a data source.

## Build and repository

Windows:

```powershell
.\build.ps1
.\build.ps1 -Test
```

Preview, with PowerShell 7 and the .NET SDK specified by `cross-platform/global.json`:

```powershell
pwsh ./cross-platform/test.ps1 -Rounds 2
pwsh ./cross-platform/build.ps1 -Runtime osx-arm64
pwsh ./cross-platform/build.ps1 -Runtime linux-x64
```

[BUILDING.md](docs/BUILDING.md) explains prerequisites, Linux ARM64, packaging, native UI tests and validation limits.

- `src/`, `tests/`: Windows application and regression suites.
- `cross-platform/`: shared models, Avalonia desktop, adapters, tests and preview packaging.
- `packaging/`: optional Windows notification identity and MSIX tooling.
- `docs/`, `tools/`, `assets/`: documentation, development helpers and product assets.

Release binaries, ZIPs and checksums belong in **GitHub Release assets**, not the source repository. Build outputs, caches, local settings and raw diagnostic artifacts are not source deliverables.

## Notices

See [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) and the [preview dependency notices](cross-platform/licenses/) for bundled components. A license for the project itself has not yet been selected; third-party licenses do not license the rest of this project.
