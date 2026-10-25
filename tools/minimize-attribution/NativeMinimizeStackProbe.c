#define WIN32_LEAN_AND_MEAN
#ifndef _WIN32_WINNT
#define _WIN32_WINNT 0x0602
#endif
#include <windows.h>
#include <stdint.h>

/* Diagnostic DLL for a single calling UI thread only. It never injects into
   another process, generates input, blocks a hook or changes a window. */
#define API __declspec(dllexport)
#define RECORD_CAPACITY 256
#define TARGET_CAPACITY 8
#define FRAME_CAPACITY 48

typedef struct ProbeRecord {
    uint32_t Size, Version, EventKind, HookCode, ProcessId, ThreadId;
    uint64_t Qpc, Hwnd;
    int64_t Command;
    uint32_t Ismex, InputAvailable, DeviceType, OriginId, FrameCount, Role;
    uint64_t Frames[FRAME_CAPACITY];
} ProbeRecord;

typedef struct ProbeStatus {
    uint32_t Size, Version, ProcessId, ThreadId, Installed, Captured;
    uint32_t Dropped, UnregisteredHookSeen, RegistrationRejected, Drained;
    uint32_t LastError, ReentrantDrops;
    uint64_t QpcFrequency;
    uint32_t RecordSize, TargetCount;
} ProbeStatus;

typedef struct ProbeTarget { HWND Hwnd; uint32_t Role; } ProbeTarget;
static HMODULE Module;
static HHOOK Hook;
static DWORD OwnerProcess, OwnerThread;
static ProbeTarget Targets[TARGET_CAPACITY];
static ProbeRecord Records[RECORD_CAPACITY];
static uint32_t TargetCount, RecordCount, DrainedCount, Dropped, Unregistered;
static uint32_t RegistrationRejected, LastError, ReentrantDrops;
static BOOL Capturing;
static uint64_t Frequency;

static BOOL OnOwnerThread(void) {
    return OwnerThread != 0 && GetCurrentThreadId() == OwnerThread &&
        GetCurrentProcessId() == OwnerProcess;
}
static BOOL TargetIsOwn(HWND hwnd) {
    DWORD process = 0;
    DWORD thread = GetWindowThreadProcessId(hwnd, &process);
    return IsWindow(hwnd) && process == OwnerProcess && thread == OwnerThread;
}
static uint32_t FindRole(HWND hwnd) {
    uint32_t i;
    for (i = 0; i < TargetCount; ++i) if (Targets[i].Hwnd == hwnd) return Targets[i].Role;
    return 0;
}
static void Capture(ProbeRecord *record, uint32_t kind, uint32_t code, HWND hwnd, int64_t command, uint32_t role) {
    LARGE_INTEGER now;
    INPUT_MESSAGE_SOURCE input = { 0 };
    USHORT count;
    void *frames[FRAME_CAPACITY] = { 0 };
    uint32_t i;
    ZeroMemory(record, sizeof(*record));
    QueryPerformanceCounter(&now);
    record->Size = sizeof(*record); record->Version = 1;
    record->EventKind = kind; record->HookCode = code;
    record->ProcessId = GetCurrentProcessId(); record->ThreadId = GetCurrentThreadId();
    record->Qpc = (uint64_t)now.QuadPart;
    record->Hwnd = (uint64_t)(uintptr_t)hwnd; record->Command = command; record->Role = role;
    record->Ismex = InSendMessageEx(NULL);
    if (GetCurrentInputMessageSource(&input)) {
        record->InputAvailable = 1;
        record->DeviceType = (uint32_t)input.deviceType;
        record->OriginId = (uint32_t)input.originId;
    }
    /* Capture before any reverse P/Invoke transition. No managed callback. */
    count = CaptureStackBackTrace(0, FRAME_CAPACITY, frames, NULL);
    record->FrameCount = count;
    for (i = 0; i < count; ++i) record->Frames[i] = (uint64_t)(uintptr_t)frames[i];
}
static LRESULT CALLBACK ObserveCbt(int code, WPARAM wParam, LPARAM lParam) {
    if (code == HCBT_MINMAX && OnOwnerThread()) {
        HWND hwnd = (HWND)wParam;
        uint32_t role = FindRole(hwnd);
        if (!role || !TargetIsOwn(hwnd)) ++Unregistered;
        else if (Capturing) ++ReentrantDrops;
        else if (RecordCount >= RECORD_CAPACITY) ++Dropped;
        else {
            Capturing = TRUE;
            Capture(&Records[RecordCount++], 1, (uint32_t)code, hwnd, (int64_t)lParam, role);
            Capturing = FALSE;
        }
    }
    /* All paths preserve the native chain and its return value. */
    return CallNextHookEx(Hook, code, wParam, lParam);
}

API uint32_t __cdecl ProbeAbiSize(void) { return sizeof(ProbeRecord); }
API uint32_t __cdecl ProbeSelfCapture(ProbeRecord *record, uint32_t size) {
    if (!record || size != sizeof(*record)) return ERROR_INVALID_PARAMETER;
    Capture(record, 3, 0, NULL, 0, 0);
    return ERROR_SUCCESS;
}
API uint32_t __cdecl ProbeInstall(void) {
    LARGE_INTEGER frequency;
    if (Hook || OwnerThread) return ERROR_ALREADY_EXISTS;
    OwnerProcess = GetCurrentProcessId(); OwnerThread = GetCurrentThreadId();
    QueryPerformanceFrequency(&frequency); Frequency = (uint64_t)frequency.QuadPart;
    Hook = SetWindowsHookExW(WH_CBT, ObserveCbt, Module, OwnerThread);
    if (!Hook) {
        LastError = GetLastError(); OwnerThread = 0;
        return LastError ? LastError : ERROR_GEN_FAILURE;
    }
    return ERROR_SUCCESS;
}
API uint32_t __cdecl ProbeRegister(HWND hwnd, uint32_t role) {
    uint32_t i;
    if (!OnOwnerThread() || !Hook || !role || !TargetIsOwn(hwnd)) {
        ++RegistrationRejected; return ERROR_INVALID_PARAMETER;
    }
    for (i = 0; i < TargetCount; ++i) if (Targets[i].Hwnd == hwnd) {
        Targets[i].Role = role; return ERROR_SUCCESS;
    }
    if (TargetCount >= TARGET_CAPACITY) { ++RegistrationRejected; return ERROR_INSUFFICIENT_BUFFER; }
    Targets[TargetCount].Hwnd = hwnd; Targets[TargetCount].Role = role; ++TargetCount;
    return ERROR_SUCCESS;
}
API uint32_t __cdecl ProbeCopyRecords(ProbeRecord *destination, uint32_t capacity, uint32_t *copied) {
    uint32_t available, count;
    if (!OnOwnerThread() || !destination || !copied) return ERROR_INVALID_PARAMETER;
    available = RecordCount - DrainedCount;
    count = available < capacity ? available : capacity;
    CopyMemory(destination, &Records[DrainedCount], count * sizeof(ProbeRecord));
    DrainedCount += count; *copied = count;
    return ERROR_SUCCESS;
}
API uint32_t __cdecl ProbeGetStatus(ProbeStatus *status, uint32_t size) {
    if (!status || size != sizeof(*status)) return ERROR_INVALID_PARAMETER;
    ZeroMemory(status, sizeof(*status));
    status->Size = sizeof(*status); status->Version = 1;
    status->ProcessId = OwnerProcess; status->ThreadId = OwnerThread;
    status->Installed = Hook ? 1 : 0; status->Captured = RecordCount;
    status->Dropped = Dropped; status->UnregisteredHookSeen = Unregistered;
    status->RegistrationRejected = RegistrationRejected; status->Drained = DrainedCount;
    status->LastError = LastError; status->ReentrantDrops = ReentrantDrops;
    status->QpcFrequency = Frequency; status->RecordSize = sizeof(ProbeRecord); status->TargetCount = TargetCount;
    return ERROR_SUCCESS;
}
API uint32_t __cdecl ProbeStop(void) {
    if (!OnOwnerThread()) return ERROR_INVALID_THREAD_ID;
    if (Hook && !UnhookWindowsHookEx(Hook)) { LastError = GetLastError(); return LastError; }
    Hook = NULL;
    return ERROR_SUCCESS;
}
BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved) {
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH) { Module = instance; DisableThreadLibraryCalls(instance); }
    return TRUE;
}
