# Native layout diagnostics

These utilities are separate from the app and normal test runner. They diagnose
only this repository's own `.runtime/.../NativeTests.exe`; they do not register a
global debugger, attach to an unrelated process, or change Windows policy.

Build the x64 .NET Framework launcher:

```powershell
.\tools\native-diagnostics\build.ps1
.\.runtime\native-diagnostics\NativeCrashProbe.exe --self-test
```

Set a new evidence folder and the file-backed test trace, then launch one suite:

```powershell
$evidencePath = Join-Path (Get-Location).Path 'artifacts\native-layout-rootcause\capture-new'
$env:CODEX_ISLAND_NATIVE_TRACE_PATH = Join-Path $evidencePath 'trace.bin'
$env:CODEX_ISLAND_NATIVE_TRACE_QUIET = '1'
.\.runtime\native-diagnostics\NativeCrashProbe.exe .runtime\tests\NativeTests.exe ToolPageLayoutTests $evidencePath 240
```

An optional fifth argument such as `content.enter` performs a normal launch first
and late-attaches only after that committed mmap marker. The launcher verifies
the trace PID, its owned process handle and the executable path before attaching.
A missing marker or child exit before attachment is an inconclusive diagnostic
attempt, not evidence of a repair.

At `ucrtbase_clr0400!abort` the tool restores its one-byte breakpoint and RIP,
captures the original caller context, then resumes the real abort. Fatal exits
remain failures. It also records at most 32 first-chance C++ stacks and guarded EH
metadata without swallowing those exceptions or treating caught exceptions as
fatal. Evidence includes the original stdout/stderr, module addresses, RSP words,
native stacks, a full dump, and read-only input-desktop observations. Native walks
stop at non-executable PCs; managed frames should be read with the dump analyzer.
Live `StackWalk64` output is provisional when a module's unwind metadata is not
available. Use the offline native analyzer's local PE exception-directory data
to validate those native chains before attributing a failure to a caller.

For a normal launch with no debugger, build the separate WER collector:

```powershell
.\tools\native-diagnostics\build-wer-collector.ps1
.\.runtime\native-diagnostics\WerOwnDumpCollector.exe --self-test
.\.runtime\native-diagnostics\WerOwnDumpCollector.exe .runtime\raw-native-layout\NativeTests.exe ToolPageLayoutTests artifacts\native-layout-rootcause\wer-new 240
```

Use the actual owned test executable path and a fresh evidence folder. The
collector preserves the original process launch flags (`CREATE_NO_WINDOW` and
standard handles only), never attaches a debugger, and never changes WER or
Windows settings. It excludes existing WER dumps and checks each new dump's
MiscInfo PID and creation time before retaining or copying its contents. It
keeps a shared read/delete handle until the own-process dump has been copied,
then records byte count, SHA256, structural completeness and whether the writer
was confirmed closed. It does not alter the child's real exit code or retry a
failure into a pass.

To observe one uninterrupted default full-regression coordinator, without
passing it any arguments:

```powershell
.\.runtime\native-diagnostics\WerOwnDumpCollector.exe .runtime\tests\NativeTests.exe --full-regression artifacts\native-layout-rootcause\full-regression-new 1800
```

This opt-in mode also tracks suite descendants through Toolhelp parent links,
retained process handles, creation-time bounds and an exact executable-path
match. It records discovered child PIDs, creation times and real exits in
`owned-processes.json`, and accepts WER dump contents only for those verified
identities. Its 20 ms polling can miss short-lived normal children; coordinator
stdout is authoritative for the full suite result count. A faulting child that
remains alive for WER reporting is tracked without debugger attachment. There
is no automatic retry. The original single-suite mode keeps its existing
launch flags, timing and default timeout.

The non-UI controlled abort run in
`artifacts/native-layout-rootcause/controlled-wer-collector` confirms this path
can capture a real system WER minidump before WER deletes the temporary file.
Its `validation.json` explicitly marks the result as controlled tool validation,
not a product reproduction or a fix. The saved minidump also recovers the
fixture's managed Main/PInvoke stack with the offline analyzer.

The additional offline readers and the non-UI pointer-set proof build separately
with `build-proof-tools.ps1`; this does not rebuild either capture launcher.
The PE decoder uses the recorded official NuGet Iced 1.21.0 package, pinned at
SHA256 `D31613617E3DDF91239BBC94A8C587849B4236E8F8A2A2BA59EA2397A1487C7A`.
Its source URL and MIT license are retained in the diagnostic runtime folder.

`NativePtrMultisetProof.exe <fresh-project-output-directory>` calls the installed
framework's exact PDB-identified `Add`, `Remove` and `Clear` functions in its own
process, using only its own zero-initialized collection storage and aligned
comparison values. It refuses any framework image whose SHA256 differs from the
investigated image. Its legal API sequence reproduces a stale sorted flag and a
failed removal while the target remains present. The optional
`--clearflag-control` clears that flag only in the helper's own collection after
the linear swap; the native removal then succeeds. Neither mode changes the
framework DLL. These deterministic algorithm proofs and the original crash dump
are distinct pieces of evidence; their per-step states and hashes are preserved
in `native-ptrmultiset-proof` and `native-ptrmultiset-flag-control` evidence folders.

For offline managed analysis, fetch the pinned Microsoft ClrMD dependency from
[NuGet](https://www.nuget.org/packages/Microsoft.Diagnostics.Runtime/1.1.35504),
then build and read a local dump:

```powershell
.\tools\native-diagnostics\get-clrmd.ps1
.\tools\native-diagnostics\build.ps1
.\.runtime\native-diagnostics\AnalyzeManagedDump.exe <dump-path> <managed-stack-path>
```

The dependency is used only by the diagnostic reader; it is not an app/runtime
dependency. Its package SHA256 is checked before extraction. The analyzer uses
the matching local .NET Framework DAC and a bounded CONTEXT adapter for newer
Windows XSTATE tails. It never patches the original dump or uploads its contents.

The SDK's minidump structures use
[`pshpack4.h`](https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/minidumpapiset.h),
including a 16-byte Win64 `MINIDUMP_EXCEPTION_INFORMATION`. The launcher's self
test validates that ABI along with the debug-event and stack-walk structures.
Any controlled tool-validation abort is explicitly separate from a product
reproduction, and its source, executable and hash are retained in its evidence
folder.
