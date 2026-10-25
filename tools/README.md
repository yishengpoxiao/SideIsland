# Development and regression tools

Run tools from the repository root. Application source is under `src/`, shared
models under `src/Shared/`, and vendored QR code under
`src/ThirdParty/QrCodeGenerator/`. Build scripts enumerate application `.cs`
files recursively; Windows test sources remain under `tests/`.

| Path | Purpose |
| --- | --- |
| `run-feature-regression.ps1`, `verify-feature-regression.ps1` | One continuous Windows 87-suite run and evidence verification; no automatic retry |
| `common/` | Source-only input desktop and obscuration probes used by the runners |
| `native-diagnostics/` | Owned-process crash collection and offline native analysis |
| `native-diagnostics/fixtures/layout-loop/` | Hash-pinned original source fixtures for the historical diagnostic loop |
| `layout-diagnostics/` | Layout, glyph and transition probes |
| `feature-validation/` | Feature backends and demo gallery harnesses |
| `minimize-attribution/` | Opt-in source attribution experiments and calibration versions |
| `test-live-media.ps1`, `LiveMediaProbe.cs` | Explicit real-player checks; requires the user's player session |
| `audit-staged-source.ps1` | Check staged Git blobs for generated/private files and high-confidence secret patterns |

Historical diagnostic versions have distinct data contracts and hash-pinned
baselines. They are not alternate product entry points and are not required for
normal builds. Their readmes state prerequisites; some attribution experiments
need retained local baseline executables and cannot be rebuilt from current
test output alone. Do not silently substitute a fresh binary for a historical
baseline. They remain source-only here to preserve the investigation method.

All generated SDKs, binaries, caches, captures and complete verification records
belong in ignored `.runtime/`, `artifacts/`, `dist/`, `release/` or portable
`bin/`, `obj/`, `out/` directories. Keep existing historical logs locally.
Publishing a concise, reviewed validation summary is separate from publishing
raw desktop/media/notification logs or memory dumps.
