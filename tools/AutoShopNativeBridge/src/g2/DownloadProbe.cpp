// DownloadProbe.cpp
//
// G2 download-closure harness for the Inovance communication stack
// (CommunicateEthernet.dll!CCommunicateEth, x86, static facade).
//
// Purpose (docs/inovance-autoshop-download-closedloop.md):
//   [A] vendor DLL resolution (runtime GetProcAddress by exact mangled name)
//   [B] loopback baseline: SetCommunicatConfig(127.0.0.1:<port>) -> IsEthOnLine
//       -> GetGCMState/GetExecMode receipts
//   [C] Download(path, cmd, NULL, index, 1, 0) matrix over candidate
//       EnumCommunicaterCmd values, then CompleteDownloadCmd(cmd, NULL, 1, 0),
//       then EnableUpload(index, 1, 0) — all hard-gated to 127.0.0.0/8
//   [D] receipts + timing; daemon-side effects are observed externally
//       (frame capture, file digests, Modbus reads) by the runner scripts.
//
// Safety (red lines):
//   - Target IP is HARD-checked to 127.0.0.0/8 BEFORE SetCommunicatConfig.
//     Anything else aborts before any vendor call. Zero physical PLC.
//   - Read-only vendor DLLs (never modified); output only under
//     %TEMP%\plcmcp-handoff\g2-download-probe\.
//
// All signatures/enums reverse-engineered from AutoShop V4.12.0.2 x86;
// see docs/inovance-autoshop-write-signatures.md (F1) and
// docs/inovance-autoshop-download-protocol.md (D1). No vendor endorsement.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <winsock2.h>
#include <ws2tcpip.h>
#include <cstdio>
#include <cstring>
#include <cstdlib>
#include <cstdarg>

// ---------------------------------------------------------------------------
// resolved vendor entry points (exact mangled names, F1 signatures)
// ---------------------------------------------------------------------------
typedef void  (__cdecl *FnSetCommunicatConfig)(void* ethInfo, HWND hwnd);
typedef int   (__cdecl *FnIsEthOnLine)();
typedef long  (__cdecl *FnGetGCMState)(int* state, void* wnd, int mode, int level);
typedef long  (__cdecl *FnGetExecMode)(int* mode, int index, int mode_, int level);
typedef long  (__cdecl *FnDownload)(const wchar_t* path, int cmd, void* wnd,
                                    int index, int execMode, int level);
typedef long  (__cdecl *FnCompleteDownloadCmd)(int cmd, void* wnd, int execMode, int level);
typedef long  (__cdecl *FnEnableUpload)(int index, int execMode, int level);
typedef long  (__cdecl *FnDownloadPlcSoft)(const wchar_t* path, void* wnd, int execMode, int level);
typedef long  (__cdecl *FnSwitchUpgradeMode)(int* out, int execMode, int level);

struct Vendor
{
    HMODULE ce = nullptr;
    FnSetCommunicatConfig setConfig = nullptr;
    FnIsEthOnLine isEthOnLine = nullptr;
    FnGetGCMState getGcmState = nullptr;
    FnGetExecMode getExecMode = nullptr;
    FnDownload download = nullptr;
    FnCompleteDownloadCmd completeDownloadCmd = nullptr;
    FnEnableUpload enableUpload = nullptr;
    FnDownloadPlcSoft downloadPlcSoft = nullptr;
};

static FILE* g_report = nullptr;

static void say(const char* fmt, ...)
{
    va_list ap; va_start(ap, fmt);
    vprintf(fmt, ap);
    if (g_report) { vfprintf(g_report, fmt, ap); fflush(g_report); }
    va_end(ap);
}

static void* proc_or_die(HMODULE m, const char* name, bool optional = false)
{
    void* p = (void*)GetProcAddress(m, name);
    if (!p && !optional) { say("FATAL: GetProcAddress(%s)\n", name); exit(2); }
    return p;
}

// tagEthernetInfo: port LE16 at [0..2), ip octets at [4..8) (F1 WriteProbe,
// empirically ONLINE against the loopback simulator)

static bool is_loopback(const char* ip)
{
    return strncmp(ip, "127.", 4) == 0 || strcmp(ip, "localhost") == 0;
}

int main(int argc, char** argv)
{
    // args: <ip> <port> <payloadPathW> [cmdHex] [index] [phase]
    const char* ip = argc > 1 ? argv[1] : "127.0.0.1";
    int port = argc > 2 ? atoi(argv[2]) : 12939;
    // hard loopback gate BEFORE any vendor call (the red line)
    if (!is_loopback(ip)) { printf("REFUSED: non-loopback target %s\n", ip); return 9; }
    if ((ntohl(inet_addr(ip)) & 0xFF000000u) != 0x7F000000u) {
        printf("REFUSED: not 127.0.0.0/8\n"); return 9;
    }

    char tmp[MAX_PATH]; GetTempPathA(MAX_PATH, tmp);
    char dir[MAX_PATH]; _snprintf(dir, MAX_PATH, "%splcmcp-handoff\\g2-download-probe", tmp);
    CreateDirectoryA(dir, nullptr);
    char rpath[MAX_PATH]; _snprintf(rpath, MAX_PATH, "%s\\report-%s.txt", dir,
        argc > 6 ? argv[6] : "run");
    g_report = fopen(rpath, "a");

    say("=== G2 DownloadProbe %s ===\n", "2026-10-29");
    say("[target] %s:%d (loopback verified)\n", ip, port);

    Vendor v;
    char dllpath[MAX_PATH];
    // vendor DLLs copied into the run dir by the build script; load from there
    char run[MAX_PATH];
    const char* env = getenv("PLCMCP_G2_RUN");
    if (env && *env) { strncpy_s(run, MAX_PATH, env, _TRUNCATE); }
    else _snprintf(run, MAX_PATH, "%sAutoShopNativeBridge-g2-run", tmp);
    _snprintf(dllpath, MAX_PATH, "%s\\CommunicateEthernet.dll", run);
    v.ce = LoadLibraryA(dllpath);
    if (!v.ce) { say("FATAL: LoadLibrary(%s) err=%lu\n", dllpath, GetLastError()); return 2; }

    v.setConfig = (FnSetCommunicatConfig)proc_or_die(v.ce,
        "?SetCommunicatConfig@CCommunicateEth@@SAXPAUtagEthernetInfo@@PAUHWND__@@@Z");
    v.isEthOnLine = (FnIsEthOnLine)proc_or_die(v.ce, "?IsEthOnLine@CCommunicateEth@@SAHXZ");
    v.getGcmState = (FnGetGCMState)proc_or_die(v.ce,
        "?GetGCMState@CCommunicateEth@@SAJPAW4EnumGCMState@@PAVCWnd@@W4EnumExecuteMode@@W4EnumCommandLevel@@@Z");
    v.getExecMode = (FnGetExecMode)proc_or_die(v.ce,
        "?GetExecMode@CCommunicateEth@@SAJPAW4EnumPlcRunModel@@HW4EnumExecuteMode@@W4EnumCommandLevel@@@Z");
    v.download = (FnDownload)proc_or_die(v.ce,
        "?Download@CCommunicateEth@@SAJPB_WW4EnumCommunicaterCmd@@PAVCWnd@@HW4EnumExecuteMode@@W4EnumCommandLevel@@@Z");
    v.completeDownloadCmd = (FnCompleteDownloadCmd)proc_or_die(v.ce,
        "?CompleteDownloadCmd@CCommunicateEth@@SAJW4EnumCommunicaterCmd@@PAVCWnd@@W4EnumExecuteMode@@W4EnumCommandLevel@@@Z");
    v.enableUpload = (FnEnableUpload)proc_or_die(v.ce,
        "?EnableUpload@CCommunicateEth@@SAJHW4EnumExecuteMode@@W4EnumCommandLevel@@@Z");
    say("[A] vendor exports resolved (Download/CompleteDownloadCmd/EnableUpload)\n");

    unsigned char info[64]; memset(info, 0, sizeof(info));
    info[0] = (unsigned char)(port & 0xFF);
    info[1] = (unsigned char)((port >> 8) & 0xFF);
    unsigned ipb[4] = {0,0,0,0};
    sscanf(ip, "%u.%u.%u.%u", &ipb[0], &ipb[1], &ipb[2], &ipb[3]);
    info[4] = (unsigned char)ipb[0]; info[5] = (unsigned char)ipb[1];
    info[6] = (unsigned char)ipb[2]; info[7] = (unsigned char)ipb[3];
    v.setConfig((void*)info, nullptr);
    int online = v.isEthOnLine();
    say("[B] SetCommunicatConfig(%s:%d); IsEthOnLine=%d\n", ip, port, online);

    int st = -1; long rc = v.getGcmState(&st, nullptr, 0, 0);
    say("[B] GetGCMState(0,0) rc=%ld state=%d\n", rc, st);

    // --- Download matrix ----------------------------------------------------
    wchar_t pathW[1024];
    if (argc > 3) {
        int n = MultiByteToWideChar(CP_UTF8, 0, argv[3], -1, pathW, 1020);
        if (n <= 0) pathW[0] = 0;
    } else pathW[0] = 0;
    int cmd = argc > 4 ? (int)strtol(argv[4], nullptr, 0) : 0;
    int idx = argc > 5 ? atoi(argv[5]) : 0;

    // mode/level: args 7/8 (default 1/0 = the UI combination; F1 observed
    // rc=17 rejection for (1,0) in the standalone harness, rc=0 acceptance
    // for (0,0) on GetExecMode/GetGCMState)
    const char* envMode = getenv("G2_MODE");
    const char* envLevel = getenv("G2_LEVEL");
    int emode = envMode ? atoi(envMode) : 1;
    int elevel = envLevel ? atoi(envLevel) : 0;

    if (pathW[0]) {
        say("[C] Download(path=\"%S\", cmd=0x%X, wnd=NULL, index=%d, mode=%d, level=%d)\n", pathW, cmd, idx, emode, elevel);
        long drc = v.download(pathW, cmd, nullptr, idx, emode, elevel);
        say("[C] Download rc=%ld (0x%lX)  [0=accepted, 0xF390=62352=failure/unavailable, 17=0x11=rejection(1,0)]\n",
            drc, (unsigned long)drc);
        Sleep(4000); // allow async comm thread to touch the wire
        say("[C] completion: CompleteDownloadCmd(cmd=0x%X, NULL, %d, %d)\n", cmd, emode, elevel);
        long crc = v.completeDownloadCmd(cmd, nullptr, emode, elevel);
        say("[C] CompleteDownloadCmd rc=%ld\n", crc);
        Sleep(3000);
    }

    say("[D] EnableUpload(index=%d, %d, %d)\n", idx, emode, elevel);
    long erc = v.enableUpload(idx, emode, elevel);
    say("[D] EnableUpload rc=%ld\n", erc);

    if (argc > 7 && strcmp(argv[7], "plcsoft_only") == 0) {
        say("[E] DownloadPlcSoft(path, NULL, %d, %d)\n", emode, elevel);
        long prc = v.downloadPlcSoft(pathW, nullptr, emode, elevel);
        say("[E] DownloadPlcSoft rc=%ld\n", prc);
        Sleep(4000);
    }

    const char* hold = getenv("G2_HOLD");
    if (hold && *hold) {
        int ms = atoi(hold);
        say("[H] holding %d ms for async comm thread...\n", ms);
        Sleep(ms);
    }

    say("=== end ===\n");
    if (g_report) fclose(g_report);
    return 0;
}
