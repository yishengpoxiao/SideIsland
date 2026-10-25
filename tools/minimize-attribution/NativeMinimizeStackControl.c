#define WIN32_LEAN_AND_MEAN
#include <windows.h>

/* Explicit positive control only. Not loaded by ordinary observation. */
__declspec(dllexport) DWORD __cdecl ControlledMinimize(HWND hwnd, DWORD expectedProcess, DWORD expectedThread) {
    DWORD process = 0;
    DWORD thread = GetWindowThreadProcessId(hwnd, &process);
    if (!IsWindow(hwnd) || process != expectedProcess || thread != expectedThread ||
        process != GetCurrentProcessId() || thread != GetCurrentThreadId()) return ERROR_INVALID_PARAMETER;
    ShowWindow(hwnd, SW_MINIMIZE);
    /* Preserve this native caller frame instead of tail-calling ShowWindow. */
    return IsIconic(hwnd) ? ERROR_SUCCESS : ERROR_GEN_FAILURE;
}
