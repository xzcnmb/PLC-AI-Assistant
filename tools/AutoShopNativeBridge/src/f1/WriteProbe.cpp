// WriteProbe.cpp
//
// F1 write-surface verification harness for the Inovance communication stack
// (CommunicateEthernet.dll!CCommunicateEth, x86, static facade).
//
// What it does (all LocalSimulator only):
//   [A] environment: vendor DLL load + map-primitive resolution
//   [B] read-only closed loop: SetCommunicatConfig(127.0.0.1:12939) ->
//       IsEthOnLine -> GetExecMode / GetGCMState   (S2-proven baseline)
//   [C] minimal write closed loop: vendor CSoftAddrMap ctor + operator[] +
//       tagSoftAddr ctor + WriteElemValue(map, NULL, EnumExecuteMode(1),
//       EnumCommandLevel(0)) -> Modbus FC3 readback on 127.0.0.1:502
//   [D] Force: frame-construction + call receipt only (runtime force
//       semantics need a RUN-state PLC; the simulator is STOPped)
//   [E] Unforce: same call-receipt level
//   [F] Download is NEVER called (per task red line; D1 file-transfer layer
//       owns the download workflow)
//
// Safety:
//   - The target IP is HARD-checked to be 127.0.0.0/8 (or "localhost") before
//     any comm-stack call; anything else aborts before SetCommunicatConfig.
//   - tagSoftAddr values are placed ONLY on the loopback simulator.
//   - No registry writes, no install-directory writes; the only file output
//     is the report under %TEMP%\plcmcp-handoff\f1-write-probe\.
//
// All signatures/enums are reverse-engineered from AutoShop V4.12.0.2 x86
// (see docs/inovance-autoshop-write-signatures.md). No vendor endorsement;
// version-locked 4.12.0.2 / x86.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <winsock2.h>
#include <ws2tcpip.h>
#include <cstdio>
#include <cstring>
#include <cstdlib>
#include <cstdarg>

// ---------------------------------------------------------------------------
// Vendor write surface (statically imported via the generated import lib).
// Signatures restored from the mangled export names; parameter ORDER confirmed
// by the Communicater.dll forwarding functions (see the signatures doc).
// ---------------------------------------------------------------------------

// EnumExecuteMode: UI call sites consistently pass 1 (T1c: WriteMemory/
// ReadMemory/SetGCMState/SetPassword/CheckPassword/SimpleCommand all push 1).
// EnumCommandLevel: UI call sites consistently pass 0.
enum EnumExecuteMode { EnumExecuteMode_Normal = 1 };
enum EnumCommandLevel { EnumCommandLevel_Default = 0 };
enum EnumPlcRunModel { EnumPlcRunModel_Unknown = 0 };
enum EnumGCMState { EnumGCMState_Unknown = 0 };

class CSoftAddrMap;
class CWnd;
struct tagEthernetInfo;

class CCommunicateEth
{
public:
    // ?WriteElemValue@CCommunicateEth@@SAJPAVCSoftAddrMap@@PAVCWnd@@0W4EnumExecuteMode@@W4EnumCommandLevel@@@Z
    // Export RVA 0x37A0 in 4.12.0.2 = `mov eax,0xF390; ret` stub (returns
    // 62352 = the vendor's "failure/unavailable" code; never touches args).
    // Declared + linked here to verify that behavior at runtime.
    static long WriteElemValue(CSoftAddrMap* elements, CWnd* window,
                               CSoftAddrMap* elements2,
                               EnumExecuteMode execMode, EnumCommandLevel level);

    // ?Force@CCommunicateEth@@SAJPAVCSoftAddrMap@@PAVCWnd@@0W4EnumExecuteMode@@W4EnumCommandLevel@@H@Z
    // Export RVA 0x3200 = real body (allocates a 0x34-byte command object).
    static long Force(CSoftAddrMap* elements, CWnd* window,
                      CSoftAddrMap* elements2,
                      EnumExecuteMode execMode, EnumCommandLevel level, int flag);

    // ?Unforce@CCommunicateEth@@SAJPAVCSoftAddrMap@@PAVCWnd@@0W4EnumExecuteMode@@W4EnumCommandLevel@@@Z
    // Export RVA 0x37A0 = same stub as WriteElemValue.
    static long Unforce(CSoftAddrMap* elements, CWnd* window,
                        CSoftAddrMap* elements2,
                        EnumExecuteMode execMode, EnumCommandLevel level);

    // Read-only baseline (S2-proven):
    static void SetCommunicatConfig(tagEthernetInfo* info, HWND window);
    static int  IsEthOnLine();
    static long GetExecMode(EnumPlcRunModel* mode, int index, EnumExecuteMode execMode,
                            EnumCommandLevel level);
    static long GetGCMState(EnumGCMState* state, CWnd* window, EnumExecuteMode execMode,
                            EnumCommandLevel level);
};

// ---------------------------------------------------------------------------
// InterfaceMgr.dll map primitives, resolved at runtime by exact mangled name
// (avoids a second import lib and any mangling drift).
//
// Evidence (docs/inovance-autoshop-write-signatures.md, T1d):
//   CSoftAddrMap ctor (RVA 0x53100, real): vtable + empty CMap header
//   CMap<CStringT<wchar>, wchar_t const*, tagSoftAddr*, tagSoftAddr&>:
//     operator[](const wchar_t*) -> tagSoftAddr&   (RVA 0x12A40, real)
//     SetAt(const wchar_t*, tagSoftAddr*&)         (RVA 0x12AB0, real)
//     GetCount() const                             (RVA 0x26130, real)
//   tagSoftAddr default ctor (RVA 0x1AB0, real) sets vtable 0x100B3484,
//     CString at +0x08/+0x0C/+0x14, int +0x10, double +0x20, int +0x18/+0x1C
//     => 72-byte object (0x48 malloc in the AutoShop write dialog path)
//   tagSoftAddr 5-arg ctor (RVA 0x1C10): (addr, EnumForceQueryResult, addr2,
//     int, int) -> addr into the +0x08 CString.
// ---------------------------------------------------------------------------

typedef void  (__thiscall *FnCSoftAddrMapCtor)(void* self);
typedef void**(__thiscall *FnCMapOperatorIndex)(void* self, const wchar_t* key);
typedef void  (__thiscall *FnCMapSetAt)(void* self, const wchar_t* key, void** value);
typedef int   (__thiscall *FnCMapGetCount)(const void* self);
typedef void  (__thiscall *FnTagSoftAddrCtor)(void* self);
typedef void  (__thiscall *FnTagSoftAddrCtor5)(void* self, const wchar_t* addr,
                                               int queryResult,
                                               const wchar_t* addr2,
                                               int p4, int p5);

struct MapPrimitives
{
    HMODULE interfaceMgr;
    FnCSoftAddrMapCtor softAddrMapCtor;
    FnCMapOperatorIndex mapOperatorIndex;
    FnCMapSetAt mapSetAt;
    FnCMapGetCount mapGetCount;
    FnTagSoftAddrCtor tagSoftAddrCtor;
    FnTagSoftAddrCtor5 tagSoftAddrCtor5;
};

static bool ResolveMapPrimitives(MapPrimitives* out)
{
    ZeroMemory(out, sizeof(*out));
    out->interfaceMgr = LoadLibraryA("InterfaceMgr.dll");
    if (out->interfaceMgr == nullptr)
    {
        return false;
    }
    struct Entry { const char* name; void** slot; } table[] = {
        { "??0CSoftAddrMap@@QAE@XZ", reinterpret_cast<void**>(&out->softAddrMapCtor) },
        { "??A?$CMap@V?$CStringT@_WV?$StrTraitMFC_DLL@_WV?$ChTraitsCRT@_W@ATL@@@@@ATL@@PB_WPAUtagSoftAddr@@AAPAU3@@@QAEAAPAUtagSoftAddr@@PB_W@Z",
          reinterpret_cast<void**>(&out->mapOperatorIndex) },
        { "?SetAt@?$CMap@V?$CStringT@_WV?$StrTraitMFC_DLL@_WV?$ChTraitsCRT@_W@ATL@@@@@ATL@@PB_WPAUtagSoftAddr@@AAPAU3@@@QAEXPB_WAAPAUtagSoftAddr@@@Z",
          reinterpret_cast<void**>(&out->mapSetAt) },
        { "?GetCount@?$CMap@V?$CStringT@_WV?$StrTraitMFC_DLL@_WV?$ChTraitsCRT@_W@ATL@@@@@ATL@@PB_WPAUtagSoftAddr@@AAPAU3@@@QBEHXZ",
          reinterpret_cast<void**>(&out->mapGetCount) },
        { "??0tagSoftAddr@@QAE@XZ", reinterpret_cast<void**>(&out->tagSoftAddrCtor) },
        { "??0tagSoftAddr@@QAE@PB_WW4EnumForceQueryResult@@0HH@Z",
          reinterpret_cast<void**>(&out->tagSoftAddrCtor5) },
    };
    int resolved = 0;
    for (const Entry& e : table)
    {
        *e.slot = reinterpret_cast<void*>(GetProcAddress(out->interfaceMgr, e.name));
        if (*e.slot != nullptr)
        {
            ++resolved;
        }
    }
    return resolved == 6 && out->softAddrMapCtor != nullptr &&
           (out->mapOperatorIndex != nullptr || out->mapSetAt != nullptr) &&
           out->tagSoftAddrCtor5 != nullptr;
}

// ---------------------------------------------------------------------------
// Reporting
// ---------------------------------------------------------------------------

static FILE* g_report = nullptr;

static void Report(const char* format, ...)
{
    va_list args;
    va_start(args, format);
    vprintf(format, args);
    va_end(args);
    printf("\n");
    if (g_report != nullptr)
    {
        va_list args2;
        va_start(args2, format);
        vfprintf(g_report, format, args2);
        va_end(args2);
        fprintf(g_report, "\n");
        fflush(g_report);
    }
}

// ---------------------------------------------------------------------------
// Loopback hard check (red line: never send anything to a non-loopback target)
// ---------------------------------------------------------------------------

static bool ParseLoopbackTarget(const char* spec, char* ip, int ipBytes, int* port)
{
    if (spec == nullptr || ip == nullptr || port == nullptr)
    {
        return false;
    }
    char buffer[128];
    strncpy_s(buffer, sizeof(buffer), spec, _TRUNCATE);
    char* colon = strrchr(buffer, ':');
    if (colon == nullptr)
    {
        return false;
    }
    *colon = '\0';
    *port = atoi(colon + 1);
    if (*port < 1 || *port > 65535)
    {
        return false;
    }
    if (_stricmp(buffer, "localhost") == 0)
    {
        strncpy_s(ip, ipBytes, "127.0.0.1", _TRUNCATE);
        return true;
    }
    int a = 0, b = 0, c = 0, d = 0;
    if (sscanf_s(buffer, "%d.%d.%d.%d", &a, &b, &c, &d) != 4)
    {
        return false;
    }
    if (a != 127)   // HARD: 127.0.0.0/8 only
    {
        return false;
    }
    if (b < 0 || b > 255 || c < 0 || c > 255 || d < 0 || d > 255)
    {
        return false;
    }
    snprintf(ip, ipBytes, "%d.%d.%d.%d", a, b, c, d);
    return true;
}

// ---------------------------------------------------------------------------
// Minimal Modbus TCP FC3 read (loopback only, self-contained evidence)
// ---------------------------------------------------------------------------

static bool ModbusReadHolding(const char* ip, int port, int startAddr, int count,
                              unsigned short* outRegs)
{
    WSADATA wsa;
    if (WSAStartup(MAKEWORD(2, 2), &wsa) != 0)
    {
        return false;
    }
    SOCKET s = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (s == INVALID_SOCKET)
    {
        WSACleanup();
        return false;
    }
    sockaddr_in addr;
    ZeroMemory(&addr, sizeof(addr));
    addr.sin_family = AF_INET;
    addr.sin_port = htons(static_cast<u_short>(port));
    if (inet_pton(AF_INET, ip, &addr.sin_addr) != 1)
    {
        closesocket(s);
        WSACleanup();
        return false;
    }
    if (connect(s, reinterpret_cast<sockaddr*>(&addr), sizeof(addr)) != 0)
    {
        closesocket(s);
        WSACleanup();
        return false;
    }
    unsigned char request[] = {
        0x00, 0x01,       // transaction id
        0x00, 0x00,       // protocol id
        0x00, 0x06,       // length
        0x01,             // unit id (daemon is unit-agnostic)
        0x03,             // function code: read holding registers
        static_cast<unsigned char>((startAddr >> 8) & 0xFF),
        static_cast<unsigned char>(startAddr & 0xFF),
        static_cast<unsigned char>((count >> 8) & 0xFF),
        static_cast<unsigned char>(count & 0xFF),
    };
    if (send(s, reinterpret_cast<const char*>(request), sizeof(request), 0) != sizeof(request))
    {
        closesocket(s);
        WSACleanup();
        return false;
    }
    unsigned char response[260];
    int got = 0;
    int want = 9 + count * 2;
    while (got < want)
    {
        int n = recv(s, reinterpret_cast<char*>(response) + got, sizeof(response) - got, 0);
        if (n <= 0)
        {
            break;
        }
        got += n;
    }
    closesocket(s);
    WSACleanup();
    if (got < want || response[7] != 0x03 || response[8] != count * 2)
    {
        return false;
    }
    for (int i = 0; i < count; ++i)
    {
        outRegs[i] = static_cast<unsigned short>((response[9 + i * 2] << 8) | response[10 + i * 2]);
    }
    return true;
}

// ---------------------------------------------------------------------------
// Phase helpers
// ---------------------------------------------------------------------------

// Builds the CSoftAddrMap via the vendor base ctor and inserts ONE tagSoftAddr
// ("D200", value 0x1234 at +0x20) via the exported CMap primitives - the same
// construction shape AutoShop's write dialog uses (map from the communicator +
// tagSoftAddr + SetAt), just with the base-class map.
// tagBuffer MUST outlive the map (the map stores the pointer; the vendor's
// real bodies iterate the map) - pass a static/heap buffer, not a stack local.
static bool BuildWriteMap(const MapPrimitives& prim, void* mapBuffer,
                          size_t mapBufferBytes, unsigned char* tagBuffer,
                          size_t tagBufferBytes, const wchar_t* address,
                          double value, int* insertedCount)
{
    if (mapBufferBytes < 256 || tagBufferBytes < 96)
    {
        return false;
    }
    ZeroMemory(mapBuffer, 256);
    prim.softAddrMapCtor(mapBuffer);

    ZeroMemory(tagBuffer, 96);
    // 5-arg ctor: (addr, EnumForceQueryResult=0, addr2="", int=0, int=0).
    // The vendor copies the address into the +0x08 CString itself.
    prim.tagSoftAddrCtor5(tagBuffer, address, 0, L"", 0, 0);
    *reinterpret_cast<double*>(tagBuffer + 0x20) = value;

    if (prim.mapSetAt != nullptr)
    {
        prim.mapSetAt(mapBuffer, address, reinterpret_cast<void**>(tagBuffer));
    }
    else if (prim.mapOperatorIndex != nullptr)
    {
        void** slot = prim.mapOperatorIndex(mapBuffer, address);
        if (slot == nullptr)
        {
            return false;
        }
        *slot = tagBuffer;
    }
    if (prim.mapGetCount != nullptr)
    {
        *insertedCount = prim.mapGetCount(mapBuffer);
    }
    return true;
}

// ---------------------------------------------------------------------------
// SEH-wrapped vendor calls (the Force/Unforce real bodies may dereference
// more tagSoftAddr structure than the base ctor initializes; E2's caution -
// "a guessed-signature call can crash the host" - is verified at runtime).
// ---------------------------------------------------------------------------

static DWORD g_sehCode = 0;

static DWORD SehFilter(unsigned long code)
{
    g_sehCode = code;
    return EXCEPTION_EXECUTE_HANDLER;
}

static long SafeCallWriteElemValue(void* map, void* map2, DWORD* exceptionCode)
{
    g_sehCode = 0;
    long rc = -999;
    __try
    {
        rc = CCommunicateEth::WriteElemValue(reinterpret_cast<CSoftAddrMap*>(map), nullptr,
                                             reinterpret_cast<CSoftAddrMap*>(map2),
                                             EnumExecuteMode_Normal,
                                             EnumCommandLevel_Default);
    }
    __except (SehFilter(GetExceptionCode()))
    {
        rc = -32004;
        *exceptionCode = g_sehCode;
        return rc;
    }
    *exceptionCode = 0;
    return rc;
}

static long SafeCallForce(void* map, void* map2, DWORD* exceptionCode)
{
    g_sehCode = 0;
    long rc = -999;
    __try
    {
        rc = CCommunicateEth::Force(reinterpret_cast<CSoftAddrMap*>(map), nullptr,
                                    reinterpret_cast<CSoftAddrMap*>(map2),
                                    EnumExecuteMode_Normal,
                                    EnumCommandLevel_Default, 1);
    }
    __except (SehFilter(GetExceptionCode()))
    {
        rc = -32004;
        *exceptionCode = g_sehCode;
        return rc;
    }
    *exceptionCode = 0;
    return rc;
}

static long SafeCallUnforce(void* map, void* map2, DWORD* exceptionCode)
{
    g_sehCode = 0;
    long rc = -999;
    __try
    {
        rc = CCommunicateEth::Unforce(reinterpret_cast<CSoftAddrMap*>(map), nullptr,
                                      reinterpret_cast<CSoftAddrMap*>(map2),
                                      EnumExecuteMode_Normal,
                                      EnumCommandLevel_Default);
    }
    __except (SehFilter(GetExceptionCode()))
    {
        rc = -32004;
        *exceptionCode = g_sehCode;
        return rc;
    }
    *exceptionCode = 0;
    return rc;
}

int main(int argc, char** argv)
{
    // Report file: %TEMP%\plcmcp-handoff\f1-write-probe\report.txt
    char tempDir[MAX_PATH];
    if (GetEnvironmentVariableA("TEMP", tempDir, sizeof(tempDir)) > 0)
    {
        char reportPath[MAX_PATH];
        snprintf(reportPath, sizeof(reportPath),
                 "%s\\plcmcp-handoff\\f1-write-probe", tempDir);
        CreateDirectoryA(reportPath, nullptr);
        strncat_s(reportPath, sizeof(reportPath), "\\report.txt", _TRUNCATE);
        g_report = fopen(reportPath, "w");
    }

    const char* targetSpec = (argc > 2 && strcmp(argv[1], "--target") == 0)
                                 ? argv[2] : "127.0.0.1:12939";

    char ip[64];
    int port = 0;
    if (!ParseLoopbackTarget(targetSpec, ip, sizeof(ip), &port))
    {
        Report("[reject] target '%s' is not 127.0.0.0/8 - aborting before any "
               "comm-stack call (loopback hard check)", targetSpec);
        if (g_report != nullptr) { fclose(g_report); }
        return 2;
    }
    Report("[target] %s:%d (loopback verified)", ip, port);
    Report("PlcMcp WriteProbe 1.0.0 (F1 write-surface verification, x86, LocalSimulator)");

    // ---- [A] environment --------------------------------------------------
    MapPrimitives prim;
    if (!ResolveMapPrimitives(&prim))
    {
        Report("[A] FAIL: InterfaceMgr.dll map primitives unresolved");
        if (g_report != nullptr) { fclose(g_report); }
        return 3;
    }
    Report("[A] InterfaceMgr.dll map primitives resolved (ctor/[]/SetAt/GetCount/tagSoftAddr)");

    // ---- Modbus baseline (before any comm-stack call) ----------------------
    unsigned short before[3] = { 0xFFFF, 0xFFFF, 0xFFFF };
    bool haveBefore = ModbusReadHolding(ip, 502, 199, 3, before);
    Report("[modbus] FC3 baseline (protocol addr 199..201): %s  regs=[%u %u %u]",
           haveBefore ? "OK" : "FAIL", before[0], before[1], before[2]);

    // ---- [B] read-only closed loop ----------------------------------------
    unsigned char info[64];
    ZeroMemory(info, sizeof(info));
    info[0] = static_cast<unsigned char>(port & 0xFF);
    info[1] = static_cast<unsigned char>((port >> 8) & 0xFF);
    info[4] = 127;
    info[5] = 0;
    info[6] = 0;
    info[7] = 1;
    CCommunicateEth::SetCommunicatConfig(reinterpret_cast<tagEthernetInfo*>(info), nullptr);
    Report("[B] SetCommunicatConfig(127.0.0.1:%d, HWND=NULL) done", port);

    int online = CCommunicateEth::IsEthOnLine();
    Report("[B] IsEthOnLine -> %d (%s)", online,
           online != 0 ? "ONLINE (loopback simulator in ring)" : "OFFLINE");

    int execMode = -1;
    long rcExec = CCommunicateEth::GetExecMode(reinterpret_cast<EnumPlcRunModel*>(&execMode), 0,
                                               EnumExecuteMode_Normal,
                                               EnumCommandLevel_Default);
    Report("[B] GetExecMode(mode=1, level=0) -> rc=%ld mode=%d", rcExec, execMode);

    int gcmState = -1;
    long rcGcm = CCommunicateEth::GetGCMState(reinterpret_cast<EnumGCMState*>(&gcmState), nullptr,
                                              EnumExecuteMode_Normal,
                                              EnumCommandLevel_Default);
    Report("[B] GetGCMState(mode=1, level=0) -> rc=%ld state=%d (1=RUN/0=STOP per T1c)",
           rcGcm, gcmState);

    // S2-baseline comparison: (mode=0, level=0) was rc=0 in the S2 TestHost.
    int execMode0 = -1;
    long rcExec0 = CCommunicateEth::GetExecMode(reinterpret_cast<EnumPlcRunModel*>(&execMode0), 0,
                                                static_cast<EnumExecuteMode>(0),
                                                EnumCommandLevel_Default);
    Report("[B] GetExecMode(mode=0, level=0) -> rc=%ld mode=%d (S2 baseline comparison)",
           rcExec0, execMode0);

    // ---- [C] minimal write closed loop -------------------------------------
    static unsigned char mapBuffer[256];
    static unsigned char tagBuffer[128]; // outlives the map: the vendor's real
                                         // bodies (Force) iterate the map
    int count = -1;
    bool built = BuildWriteMap(prim, mapBuffer, sizeof(mapBuffer), tagBuffer,
                               sizeof(tagBuffer), L"D200",
                               4660.0 /* 0x1234 */, &count);
    Report("[C] write map built=%s insertedCount=%d (D200 -> 0x1234 @tagSoftAddr+0x20)",
           built ? "true" : "false", count);

    long rcWrite = -999;
    DWORD sehWrite = 0;
    if (built)
    {
        rcWrite = SafeCallWriteElemValue(mapBuffer, mapBuffer, &sehWrite);
    }
    Report("[C] WriteElemValue(map, NULL, map, mode=1, level=0) -> rc=%ld%s "
           "(0xF390=62352 = the export stub return; 0/1 = accepted)",
           rcWrite, sehWrite ? " [SEH]" : "");

    Sleep(2000); // async dispatch delay

    unsigned short after[3] = { 0xFFFF, 0xFFFF, 0xFFFF };
    bool haveAfter = ModbusReadHolding(ip, 502, 199, 3, after);
    Report("[modbus] FC3 after write (protocol addr 199..201): %s  regs=[%u %u %u]",
           haveAfter ? "OK" : "FAIL", after[0], after[1], after[2]);
    bool writeTookEffect = haveBefore && haveAfter &&
                           (before[0] != after[0] || before[1] != after[1] ||
                            before[2] != after[2]);
    Report("[C] verdict: write %s (regs 199..201 before=[%u %u %u] after=[%u %u %u])",
           writeTookEffect ? "TOOK EFFECT" : "NOT observed (negative or ignored; see daemon log)",
           before[0], before[1], before[2], after[0], after[1], after[2]);

    // ---- [D] Unforce (call receipt) ----------------------------------------
    long rcUnforce = -999;
    DWORD sehUnforce = 0;
    if (built)
    {
        rcUnforce = SafeCallUnforce(mapBuffer, mapBuffer, &sehUnforce);
    }
    Report("[D] Unforce(map, NULL, map, mode=1, level=0) -> rc=%ld%s (call receipt)",
           rcUnforce, sehUnforce ? " [SEH]" : "");

    // ---- [E] Force (frame + call receipt only; LAST: documented crash point) --
    // Force's export (RVA 0x3200) is a REAL body. Its internal command setup
    // reads structure the base-class map/tagSoftAddr does not provide; in the
    // reference run it terminated the process in a way user SEH could not
    // catch (vendor stack-cookie fast-fail pattern). --skip-force keeps the
    // run alive for the full report.
    bool skipForce = false;
    for (int i = 1; i < argc; ++i)
    {
        if (strcmp(argv[i], "--skip-force") == 0)
        {
            skipForce = true;
        }
    }
    long rcForce = -999;
    DWORD sehForce = 0;
    if (skipForce)
    {
        Report("[E] Force SKIPPED (--skip-force): call-receipt level only; "
               "runtime force semantics need a RUN-state PLC and the full "
               "element model (see the signatures doc)");
    }
    else
    {
        Report("[E] about to call Force (marker; a crash after this line is "
               "the documented Force negative result)");
        if (built)
        {
            rcForce = SafeCallForce(mapBuffer, mapBuffer, &sehForce);
        }
        Report("[E] Force(map, NULL, map, mode=1, level=0, flag=1) -> rc=%ld%s "
               "(call receipt; no effect expected - simulator STOPped)",
               rcForce, sehForce ? " [SEH caught vendor exception]" : "");
    }

    // ---- [F] Download: intentionally NOT called -----------------------------
    Report("[F] Download NOT called (red line: simulator load path is a stub; "
           "D1 file-transfer layer owns the download workflow)");

    Report("[done] PlcMcp WriteProbe finished; simulator/daemon must be cleaned up by the caller");
    if (g_report != nullptr)
    {
        fclose(g_report);
    }
    return 0;
}
