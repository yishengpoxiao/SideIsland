# NativeMinimizeStackProbe

This diagnostic has a **native C WH_CBT callback**, installed only on the calling
UI thread. Its fixed ring records HCBT_MINMAX attempts only for explicitly
registered live HWNDs owned by the same process and thread. It calls
CaptureStackBackTrace before any transition into managed code, and always calls
the next hook without changing its return value. It does not generate input,
change styles, restore windows, or install a global hook.

The bridge `CodexIsland.NativeMinimizeStackProbe` exposes public reflection-friendly
methods:

```csharp
var probe = NativeMinimizeStackProbe.Start(freshProjectOutputDirectory);
probe.Register(hwnd, "settings"); // on SourceInitialized, before Show
probe.Flush();                  // on installing UI thread, outside the hook
probe.Dispose();                // drains, unhooks, writes final summary
```

Compile `NativeMinimizeStackProbe.cs` into the diagnostic x64 process, and copy
`NativeMinimizeStackProbe.dll` next to its EXE. Each process permits one probe
session. Ordinary observation does **not** load NativeMinimizeStackControl.dll.
Native records are capped at 256; lost/reentrant records are explicit. Flushing
is deliberately outside the hook, so callers should flush promptly after a
critical native state message and in `finally`, before process termination.

`NativeStackCalibration.exe --self-check` loads the DLL and checks ABI/native
export stack capture without UI or hooks. This is not a minimization test.

`NativeStackCalibration.exe --output <fresh directory within project>` is an
explicit **positive control**. It creates its own WPF window and loads the
separate NativeMinimizeStackControl.dll, which calls ShowWindow(SW_MINIMIZE).
The control passes only if that known native caller module is present in the
observed CBT stack. The control compilation disables sibling-call optimization
and has a call after ShowWindow, preserving the source frame. A controlled result
must never be attributed to the historical failure.

The native callback records an **attempt**, which a later hook can cancel. Actual
WM_SIZE/placement state and ETW must corroborate a real minimization. Current
input source and ISMEX are receiver-thread context, not sender identity. Module
presence alone proves no cause; a captured native caller chain and correlated
first state change are required.

Build: `build-native-stack-probe.ps1 -OutputDirectory <fresh project directory>
-SelfCheck`. The compiler is a verified official portable Zig 0.17.0 archive in
`.runtime/native-probe-zig-deps`, with SHA256
`b5663f69581dcf391293fbf16c06cb80d81d806545ce618b4d0bab7f0eb8c428` from the
[official download index](https://ziglang.org/download/index.json). It changes no
system PATH, installation or registry setting. The initial sandboxed build's
atomic-renaming failure is retained in native-minimize-stack-probe-v1; v2 was
compiled with project-only cache access outside that restriction.
