# V4b native window-procedure brackets

V4b is a separate build. It does not change the frozen v3 CBT DLL or an already
running diagnostic. Use `NativeMinimizeStackProbeV4b.cs` instead of the v3 bridge,
and copy `NativeMinimizeStackProbeV4b.dll` next to the diagnostic EXE. The managed
class and Start/Register/Flush/Dispose contract remain the same.

Only the installing UI thread may register its own live HWNDs. Registration adds
one `SetWindowSubclass` instance with a DLL-function-address subclass ID. No other
thread or process is subclassed. The native callback calls `DefSubclassProc`
exactly once and returns its unmodified LRESULT; it does not block a command,
restore a window or change styles.

The callback records real native entry and return for WM_SYSCOMMAND, native button
messages, actual minimize/restore WM_SIZE, and WM_NCDESTROY. A successful normal
return is recorded only **after** DefSubclassProc returns. Each callback has a
unique monotonically increasing CallId, its actual parent CallId, and nesting
depth across all registered HWND messages. Filtered parent messages may have no
saved row; this is not an invitation to invent a parent. NCDESTROY removes the
own subclass and clears the target; Stop removes any remaining own registrations
and the CBT hook. Cleanup and active-depth counters are explicit.

CBT records retain the direct ShowWindow/placement observation path. Native stack
capture happens before entering managed WPF code. The status ring is bounded at
256 records; all drops and callback/cleanup failures are visible. Flush must be
called outside the native callback on its owner UI thread, especially from a
managed critical-message observer before the original body continues and in
finally. A process killed before flushing may leave records only in memory;
initial summary fields must not be treated as live counters in that case.

V4b suppresses W/L for non-window-state system commands, since SC_KEYMENU can
carry an accelerator character. Their real entry/return brackets remain, with
ParametersSuppressed=true. Only Minimize/Restore/Maximize/Close/Move/Size preserve
their numeric state parameters; no key, text or window title is stored by this
probe. The earlier V4 build lacks this parameter suppression and should be kept
as a frozen calibration artifact rather than used for ordinary input observation.

Compile-only/non-UI ABI check:

```powershell
tools/minimize-attribution/build-native-stack-probe-v4b.ps1 -SelfCheck
```

Explicit UI positive control, for the root agent to run when no other fixture is
active:

```powershell
.runtime/native-minimize-stack-probe-v4b/NativeStackCalibrationV4b.exe --output <fresh-project-directory>
```

It controls only its own small WPF window. The four phases are direct native
ShowWindow, own SendMessage SC_MINIMIZE, own PostMessage SC_MINIMIZE, and an outer
SC_MINIMIZE with a nested own SC_RESTORE. Validation requires real WM_SIZE1,
matching entry/return identity and QPC, nested native parent/depth, no synthesized
returns and complete cleanup. These are positive controls, not a reproduction of
the historical failure. ABI smoke checks install no hook or window and cannot
replace those UI controls.

An unfinished entry means the body has not been observed returning. It does not
by itself prove a crash, a sender, or a minimization. Pair the native bracket with
the exact own-HWND queue/ETW source and actual state change; neither the previous
GetMessage nor the presence of USER32/a vendor module alone identifies a caller.
