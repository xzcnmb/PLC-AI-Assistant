// E3CompileBridge.cpp — minimal in-process bridge for E3 (AutoShop compile trigger).
// Injected into a SELF-LAUNCHED AutoShop.exe instance (x86) by E3Injector.exe.
// On DllMain(PROCESS_ATTACH) it spawns a thread that:
//   1. waits for the named event "E3CompileGo"
//   2. calls the 全部编译 entry exactly like the 0x80E1 WM_COMMAND handler does:
//        root = 0x1581A70();  cmpMgr = 0x1581CA0(root);  0x15A2EC0(cmpMgr);
//   3. writes a marker log to %TEMP%\plcmcp-handoff\e3-open-compile\bridge_log.txt
// Read-only w.r.t. the installation; no UI interaction; no PLC.
// Addresses are ImageBase-relative for AutoShop.exe 4.12.0.2 (ImageBase 0x400000,
// RELOCS_STRIPPED => fixed VAs).
#include <windows.h>
#include <stdio.h>

static void logline(const char* fmt, ...) {
    char path[MAX_PATH];
    GetTempPathA(MAX_PATH, path);
    lstrcatA(path, "plcmcp-handoff\\e3-open-compile\\bridge_log.txt");
    HANDLE h = CreateFileA(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
                           NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE) return;
    SetFilePointer(h, 0, NULL, FILE_END);
    SYSTEMTIME st; GetLocalTime(&st);
    char buf[512];
    int n = _snprintf(buf, sizeof(buf), "%04d-%02d-%02d %02d:%02d:%02d.%03d | pid=%lu | ",
                      st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond,
                      st.wMilliseconds, GetCurrentProcessId());
    va_list args; va_start(args, fmt);
    n += _vsnprintf(buf + n, sizeof(buf) - n - 2, fmt, args);
    va_end(args);
    buf[n++] = '\n';
    DWORD w; WriteFile(h, buf, n, &w, NULL);
    CloseHandle(h);
}

typedef void* (*PFN_GETROOT)(void);
typedef void* (__thiscall *PFN_GETMEMBER)(void*);   // 0x1581CA0 = __thiscall: ecx=root, returns [root+0x34]
typedef void  (__thiscall *PFN_COMPILEALL)(void*);

// fixed VAs (AutoShop.exe 4.12.0.2, ImageBase 0x400000)
static const DWORD VA_GETROOT    = 0x1581A70;  // root manager singleton (cached at .data 0x2438E3C)
static const DWORD VA_GET_CMPMGR = 0x1581CA0;  // root->field_0x34 = compile-all manager
static const DWORD VA_COMPILEALL = 0x15A2EC0;  // 全部编译 entry (what 0x80E1 calls)

static DWORD WINAPI worker(LPVOID) {
    logline("bridge attached, waiting for E3CompileGo");
    HANDLE ev = CreateEventA(NULL, TRUE, FALSE, "E3CompileGo");
    if (!ev) { logline("CreateEvent failed gle=%lu", GetLastError()); return 1; }
    if (WaitForSingleObject(ev, 120000) != WAIT_OBJECT_0) {
        logline("timeout waiting for E3CompileGo");
        return 1;
    }
    logline("E3CompileGo received, calling compile-all chain");
    HMODULE exe = GetModuleHandleW(L"AutoShop.exe");
    if (!exe) { logline("no AutoShop.exe module"); return 1; }
    DWORD base = (DWORD)exe;
    logline("AutoShop.exe base=0x%08X (expected 0x00400000)", base);
    if (base != 0x00400000) { logline("unexpected base, aborting"); return 1; }

    PFN_GETROOT getRoot = (PFN_GETROOT)(base + (VA_GETROOT - 0x400000));
    PFN_GETMEMBER getCmp = (PFN_GETMEMBER)(base + (VA_GET_CMPMGR - 0x400000));
    PFN_COMPILEALL compileAll = (PFN_COMPILEALL)(base + (VA_COMPILEALL - 0x400000));

    void* root = getRoot();
    logline("root manager = 0x%08X", (DWORD)(size_t)root);
    if (!root) { logline("root manager NULL, aborting"); return 1; }
    void* cmpMgr = getCmp(root);
    logline("compile-all manager (root+0x34) = 0x%08X", (DWORD)(size_t)cmpMgr);
    if (!cmpMgr) { logline("compile manager NULL, aborting"); return 1; }
    __try {
        compileAll(cmpMgr);
        logline("compileAll returned");
    } __except(EXCEPTION_EXECUTE_HANDLER) {
        logline("compileAll raised exception 0x%08X", GetExceptionCode());
    }
    logline("worker done");
    return 0;
}

static DWORD WINAPI thread_stub(LPVOID p) {
    return worker(p);
}

BOOL APIENTRY DllMain(HMODULE h, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(h);
        CreateThread(NULL, 0, thread_stub, NULL, 0, NULL);
    }
    return TRUE;
}
