# Build, test and package

[English overview](../README.md) · [中文说明](../README.zh-CN.md) · [Architecture](ARCHITECTURE.md)

Run the commands below from the repository root. The public release version is **v1.0.0**, sourced from the root `VERSION` file. Windows and the platform previews have separate build pipelines.

## Windows prerequisites

- Windows 10 build 19041 or later, or Windows 11; the existing native test environment is x64.
- .NET Framework 4.8 and the Windows-provided framework C# compiler/WPF assemblies.
- Windows PowerShell 5.1 or PowerShell 7 for ordinary builds; optional Appx registration should use Windows PowerShell 5.1.
- An unlocked interactive desktop for WPF input, native menus, transparency and screen-coordinate tests. Do not interact with the test windows while an automated UI run owns them.

No Visual Studio solution, Python, Node.js or Electron runtime is required for the Windows app. The ordinary Windows build does not download dependencies. MSIX creation additionally requires Windows SDK packaging tools; those tools are not needed for a portable EXE build.

## Windows build and tests

```powershell
.\build.ps1
.\build.ps1 -Test
```

The first command produces `dist/CodexIsland.exe`, its configuration file and third-party notices. `-Test` also compiles `.runtime/tests/NativeTests.exe` and runs the registered suite coordinator. WPF-heavy groups execute in fresh STA child processes; a failure, native exit or timeout fails the run rather than triggering automatic retries.

To compile tests without launching them, then select a registered suite:

```powershell
.\build.ps1 -CompileTests
.\.runtime\tests\NativeTests.exe --suite SupportTests
```

Suite names are listed in `tests/NativeTests.cs`. The historical baseline contains 87 suites. This describes available coverage and prior validation history, not a claim that every release-preparation step reran all 87. Keep the original output for the actual run and distinguish fixture, own-window and live-player tests.

Native menu/click checks require an available desktop. A process returning success while a lock-screen overlay covered the target is not sufficient evidence of a real click. An interrupted or obstructed run must be recorded as such.

### Diagnostic regression runner

For a complete instrumented Windows run, the repository provides `tools/run-feature-regression.ps1` and its verifier. Prepare the test executable, owned-process crash collector and notification-app staging first:

```powershell
.\build.ps1 -CompileTests
.\tools\native-diagnostics\build-wer-collector.ps1
.\packaging\stage-package.ps1
.\tools\run-feature-regression.ps1 -RunName release-check-001 -Full
.\tools\verify-feature-regression.ps1 -RunDirectory .\artifacts\features-1.13\release-check-001
```

Use a fresh run name. This runner checks the input desktop, uses the public probe sources in `tools/common/`, stops the staged app for isolation and requests a registered-app restart afterward. It is intended for a configured Windows test machine; package registration, user access and the interactive desktop are separate prerequisites. Old development labels in evidence-directory names are retained for script compatibility.

Raw logs and any dumps remain local under `artifacts/`. Review and sanitize a concise summary before sharing it; do not commit user data, private screenshots, credentials, memory dumps or machine-specific paths. The [diagnostic tools guide](../tools/native-diagnostics/README.md) explains the collectors.

### Optional real-player checks

`tools/test-live-media.ps1` is separate from isolated tests. It can inspect a real Windows media session, and explicit command options can change playback. Online-lyrics mode sends track metadata to LRCLIB. Select a known session and obtain the appropriate user's authorization before using either capability; parser fixtures are not a substitute for real-player evidence.

## Windows notification identity and packaging

Run the portable EXE without notification registration if you only need independent tools. Reading other apps' system notifications requires an application identity, the notification capability and the user's own Windows consent.

```powershell
.\install-notifications.ps1 -CheckOnly
.\install-notifications.ps1
```

The second command needs Developer Mode for loose development registration, or a signed MSIX already trusted by that account via `-SignedPackagePath`. It does not enable Developer Mode or add a signing certificate. Close the app before registration, keep its directory in place afterward, and launch the registered app from Start or `启动通知版.cmd`.

For a portable release, build before packaging:

```powershell
.\build.ps1
.\build-release.ps1
```

The Windows asset is `release/SideIsland-v1.0.0-win-x64.zip`. The full package includes the executable, English/Chinese READMEs, notices and optional notification-registration scripts. Publish the archive and release checksums as GitHub Release attachments; do not commit generated release folders or binaries.

Creating an MSIX uses `packaging/build-package.ps1` and Windows SDK `MakeAppx`; signing additionally requires `signtool` and a suitable certificate already under your control. An unsigned MSIX is a build artifact, not a normally installable signed release. See [packaging/README.md](../packaging/README.md) for signing parameters and uninstalling the optional identity.

## Cross-platform preview prerequisites

- PowerShell 7 (`pwsh`).
- The **.NET SDK** selected by `cross-platform/global.json` (currently the 10.0.402 feature band with latest-patch roll-forward). A runtime alone cannot compile the project.
- NuGet access for the initial restore. The preview uses Avalonia 12.1.4 and .NET 10.
- Target native desktop libraries when actually running the published application.

The scripts use an explicit `-DotnetPath`, then a project-local `.runtime/cross-platform-sdk/10.0.402/` SDK when available, then `dotnet` on PATH. They do not install or register an SDK. CLI state and NuGet caches are kept under `.runtime/` and are not release contents.

The current preview targets Apple Silicon with macOS 15+ and Ubuntu 22.04+ on x64/ARM64. Use the intersection of the package's deployment target and the runtime/framework support policies; a successful cross-build does not establish a supported desktop. References: [.NET 10 supported OS versions](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md), [Avalonia supported platforms](https://docs.avaloniaui.net/docs/supported-platforms).

## Preview tests

```powershell
pwsh ./cross-platform/test.ps1 -Rounds 2
```

An explicit SDK path is also supported:

```powershell
pwsh ./cross-platform/test.ps1 -DotnetPath /path/to/dotnet -Rounds 2
```

The script restores/builds the test project, runs each complete round, and preserves restore/build output, per-round logs/images, assembly hashes and before/after source hashes under `artifacts/cross-platform/regression/`. A failed round or changing source inputs fails the run. The default is one round; `-Rounds` accepts one through five.

Coverage includes shared models, native-adapter parsers with explicit fixtures, synthetic loopback HTTP notifications, batch-operation safeguards, screenshot-service behavior and actual Avalonia headless rendering. The layout matrix varies language, theme, viewport and simulated DPI. These tests do not launch native Mac/Ubuntu desktop integrations or read personal notifications.

## Preview build and package

```powershell
pwsh ./cross-platform/build.ps1 -Runtime osx-arm64
pwsh ./cross-platform/build.ps1 -Runtime linux-x64
pwsh ./cross-platform/build.ps1 -Runtime linux-arm64
```

Each command restores and self-contained-publishes into `cross-platform/out/<rid>`, then packages the result. `-SkipPackage` stops after publishing. To package an existing publish directory:

```powershell
pwsh ./cross-platform/package.ps1 -Runtime osx-arm64 -PublishDirectory ./out/osx-arm64
```

`-PublishDirectory` is resolved relative to `cross-platform/` when it is not absolute. A `win-x64` runtime is available for preview-shell verification; it is **not** the mature WPF Windows release.

Expected user-facing assets:

| Target | Archive |
| --- | --- |
| Windows WPF | `SideIsland-v1.0.0-win-x64.zip` |
| Apple Silicon preview | `SideIsland-v1.0.0-osx-arm64.zip` |
| Ubuntu x64 preview | `SideIsland-v1.0.0-linux-x64.zip` |
| Ubuntu ARM64 preview | `SideIsland-v1.0.0-linux-arm64.zip` |

Preview packaging also emits SHA-256 and per-file manifest information. It checks apphost architecture, complete publish-file copies, extracted bytes and Unix archive permissions. macOS is wrapped as `Side Island.app`; Linux includes the `side-island` launcher. Self-contained publishing includes .NET, but does not remove native system-library requirements.

Mac signing/notarization and target-device execution are separate outstanding steps. The scripts do not disable Gatekeeper or modify trust settings. Ubuntu native permissions, session-bus policy, portal interaction, audio services and X11/XWayland positioning also need real desktop validation; native Wayland equivalence is not promised.

## Release evidence and repository hygiene

Keep source, build definitions, dependency attribution and sanitized summaries in Git. Keep generated `dist/`, `release/`, preview `out/`, `bin/obj`, `.runtime/`, registered-app staging, full diagnostics and private screenshots out of tracked source. Attach binaries and checksums to the [GitHub Release](https://github.com/yishengpoxiao/SideIsland/releases/latest).

Record exactly what ran: platform, desktop availability, commands, artifact hashes and outcomes. Prior Windows validation, a headless preview run, a static archive check and a real target-device run are distinct evidence. The project has not selected its own license; retain third-party notices without inventing a project-wide license grant.
