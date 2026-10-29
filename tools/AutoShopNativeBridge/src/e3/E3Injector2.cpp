// E3Injector2.cpp — like E3Injector but snapshots the project tree at intervals and
// records the main-window title at trigger time.
#include <windows.h>
#include <stdio.h>
#include <string.h>
#pragma comment(lib, "user32.lib")

static const wchar_t* EXE = L"D:\\Inovance Control\\AutoShop\\AutoShop.exe";
static const wchar_t* DLL = L"E3CompileBridge.dll";

static void logline(const char* fmt, ...) {
    char path[MAX_PATH];
    GetTempPathA(MAX_PATH, path);
    lstrcatA(path, "plcmcp-handoff\\e3-open-compile\\injector2_log.txt");
    HANDLE h = CreateFileA(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
                           NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE) return;
    SetFilePointer(h, 0, NULL, FILE_END);
    SYSTEMTIME st; GetLocalTime(&st);
    char buf[512];
    int n = _snprintf(buf, sizeof(buf), "%02d:%02d:%02d | ", st.wHour, st.wMinute, st.wSecond);
    va_list args; va_start(args, fmt);
    n += _vsnprintf(buf + n, sizeof(buf) - n - 2, fmt, args);
    va_end(args);
    buf[n++] = '\n';
    DWORD w; WriteFile(h, buf, n, &w, NULL);
    CloseHandle(h);
}

struct EnumCtx { DWORD pid; wchar_t titles[8][256]; int n; };

static BOOL CALLBACK enum_cb(HWND hwnd, LPARAM lp) {
    EnumCtx* ctx = (EnumCtx*)lp;
    DWORD pid = 0;
    GetWindowThreadProcessId(hwnd, &pid);
    if (pid != ctx->pid) return TRUE;
    if (!IsWindowVisible(hwnd)) return TRUE;
    wchar_t cls[64];
    GetClassNameW(hwnd, cls, 64);
    if (wcsstr(cls, L"Afx:00400000") != cls) return TRUE;
    wchar_t title[256];
    GetWindowTextW(hwnd, title, 256);
    if (ctx->n < 8) {
        lstrcpynW(ctx->titles[ctx->n], title, 256);
        ctx->n++;
    }
    return TRUE;
}

static void snap_titles(DWORD pid, const char* tag) {
    EnumCtx ctx = { pid };
    EnumWindows(enum_cb, (LPARAM)&ctx);
    for (int i = 0; i < ctx.n; i++) {
        char t[512];
        WideCharToMultiByte(CP_UTF8, 0, ctx.titles[i], -1, t, 512, NULL, NULL);
        logline("title[%s] #%d: %s", tag, i, t);
    }
}

static BOOL inject(DWORD pid, const wchar_t* dllPath) {
    HANDLE p = OpenProcess(PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION |
                           PROCESS_VM_OPERATION | PROCESS_VM_WRITE | PROCESS_VM_READ, FALSE, pid);
    if (!p) { logline("OpenProcess failed gle=%lu", GetLastError()); return FALSE; }
    size_t sz = (lstrlenW(dllPath) + 1) * sizeof(wchar_t);
    void* remote = VirtualAllocEx(p, NULL, sz, MEM_COMMIT, PAGE_READWRITE);
    if (!remote) { CloseHandle(p); return FALSE; }
    WriteProcessMemory(p, remote, dllPath, sz, NULL);
    HANDLE t = CreateRemoteThread(p, NULL, 0,
        (LPTHREAD_START_ROUTINE)GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "LoadLibraryW"),
        remote, 0, NULL);
    if (!t) { CloseHandle(p); return FALSE; }
    WaitForSingleObject(t, 15000);
    CloseHandle(t);
    VirtualFreeEx(p, remote, 0, MEM_RELEASE);
    CloseHandle(p);
    return TRUE;
}

int wmain(int argc, wchar_t** argv) {
    if (argc < 2) return 2;
    wchar_t cmdline[1024];
    _snwprintf(cmdline, 1024, L"\"%s\" \"%s\"", EXE, argv[1]);
    wchar_t cwd[MAX_PATH];
    GetCurrentDirectoryW(MAX_PATH, cwd);
    logline("=== injector2 run ===");
    STARTUPINFOW si = { sizeof(si) };
    PROCESS_INFORMATION pi;
    if (!CreateProcessW(NULL, cmdline, NULL, NULL, FALSE, CREATE_NEW_PROCESS_GROUP, NULL, cwd, &si, &pi)) {
        logline("CreateProcess failed gle=%lu", GetLastError());
        return 1;
    }
    DWORD pid = pi.dwProcessId;
    logline("AutoShop pid=%lu", pid);
    CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
    Sleep(20000);
    snap_titles(pid, "before-inject");
    wchar_t dllPath[MAX_PATH];
    _snwprintf(dllPath, MAX_PATH, L"%s\\%s", cwd, DLL);
    if (!inject(pid, dllPath)) { logline("inject failed"); return 1; }
    logline("injected");
    Sleep(3000);
    HANDLE ev = CreateEventA(NULL, TRUE, FALSE, "E3CompileGo");
    if (ev) { SetEvent(ev); logline("compile signal sent"); }
    for (int k = 0; k < 5; k++) {
        Sleep(8000);
        snap_titles(pid, "after-trigger");
    }
    wchar_t syscmd[128];
    _snwprintf(syscmd, 128, L"taskkill /F /T /PID %lu", pid);
    _wsystem(syscmd);
    logline("=== done ===");
    return 0;
}
