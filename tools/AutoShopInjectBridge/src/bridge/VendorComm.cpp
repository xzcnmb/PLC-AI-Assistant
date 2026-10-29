// VendorComm.cpp
//
// In-process access to the AutoShop communication stack for the inject bridge.
//
// Evidence base (docs/inovance-autoshop-native-bridge.md, S2, all
// reverse-engineered, no vendor endorsement, AutoShop 4.12.0.2 x86):
//
//   ?GetExecMode@CCommunicateEth@@SAJPAW4EnumPlcRunModel@@HW4EnumExecuteMode@@W4EnumCommandLevel@@@Z
//   ?GetGCMState@CCommunicateEth@@SAJPAW4EnumGCMState@@PAVCWnd@@W4EnumExecuteMode@@W4EnumCommandLevel@@@Z
//   ?GetPlcLable@CCommunicateEth@@SAJPAU_PLC_LABLE@@W4EnumCommunicaterType@@W4EnumExecuteMode@@W4EnumCommandLevel@@@Z
//   ?ReadElemValue@CCommunicateEth@@SAJPAVCSoftAddrMap@@PAVCWnd@@W4EnumExecuteMode@@W4EnumCommandLevel@@@Z
//   ?SetCommunicatConfig@CCommunicateEth@@SAXPAUtagEthernetInfo@@PAUHWND__@@@Z
//   ?IsEthOnLine@CCommunicateEth@@SAHXZ
//   ??0CSoftAddrMap@@QAE@XZ                                     (InterfaceMgr.dll)
//
// Static member functions mangle as __cdecl on x86 ("SA"), so they are invoked
// through plain __cdecl function pointers resolved with GetProcAddress. The
// CSoftAddrMap constructor is __thiscall ("QA").
//
// tagEthernetInfo (20 bytes, InterfaceMgr.dll ctor RVA 0x1EC0): +0x00 uint16
// port, +0x04..+0x08 four IP bytes; the rest is zero-filled generously.
//
// F1/F2 updates (2026-10-28):
// - read_elements fills the map through the REAL primitives (tagSoftAddr
//   5-arg ctor 0x1C10 + CMap::SetAt 0x12AB0, both used by AutoShop itself),
//   calls ReadElemValue(mode=0, level=0) and reads the tag+0x20 value slot
//   after a bounded settle window (docs/inovance-autoshop-native-bridge.md §15);
//
// G1 update (2026-10-29, docs/inovance-autoshop-monitor-values.md §2): the
// value-delivery slot on the ReadElemValue path is the tag's CString at
// +0x0C, NOT the tag+0x20 double. Evidence (CommunicateEthernet 4.12.0.2,
// capstone, this subagent):
//   - ReadElemValue (0x37B0) -> 0x2C command (vtable 0x387AC, code 0x55
//     stored @+0x24) -> RunCommThread -> Execute at 0xDDE0: reads comm type
//     via vtable+0x3c, proceeds only when type==4 (ethernet), then 0xDE20.
//   - 0xDE20 iterates the map (GetNextAssoc) and picks the FIRST entry whose
//     tag+0x10 == 1 (bit). Word entries (type 4) are skipped — the map path
//     never requests a word element. It builds the a3c1 request (bytes
//     0xA3 0xC1 written at CE+0xDF05..07, matching the 21-byte frame F2
//     captured on the wire), transacts it (0x95C0: send 0x9820, recv 0x9860
//     with a 5s x recv-timeout loop) and on rc==1 parses the reply (0x9BC0)
//     and assigns a static value STRING into tag+0x0C (CString::operator=
//     through mfc90u ordinal 811, strings at CE+0x4606C/0x46070).
//   - The UI's own value getter g_GetMonElemValue (InterfaceMgr 0x3CDE0)
//     also reads tag+0x0C (the +0x28 flag gates the "1" display), so the
//     delivered value for monitor-map reads is the +0x0C string.
//   - Word reads use ReadWatchElemValue (0x36C0 -> 0x28 command, vtable
//     0x388B4, Execute 0xE250 -> 0xE2F0): frames a3c2/a3c3 carry counts of
//     the CMonitElemBuffer element arrays (buffer vtable slots +0xc/+0x18/
//     +0x14 read via GetPublicMgr's active map at vtable+0x80). The base
//     buffer's vtable slots are stubs (0x5080/0x70e7a), so a base object
//     answers counts=0 and the command exits with rc=2 before any frame.
// Hence ReadElementsEx below snapshots every tag+0x0C pointer before the
// call, and after the settle window reports delivered=1 only when a CString
// pointer actually changed (a responding backend end-to-end). The double
// slot is still mirrored for continuity but is never a delivery signal.
// - compile invokes the F1-verified compile-all chain (root 0x1581A70 ->
//   cmpMgr 0x1581CA0 -> entry 0x15A2EC0, with the project gate 0x161B620
//   checked first; docs/inovance-autoshop-write-signatures.md §5);
// - the eth-channel element-write exports (WriteElemValue/Unforce) remain
//   NEVER invoked: F1 proved they are stubs returning 0xF390=62352 with no
//   function body (CommunicateEthernet.dll 4.12.0.2), and the Force body
//   terminates the process in a way user SEH cannot catch.

#include "BridgeInternal.h"

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>

namespace AutoShopInjectBridge {
namespace {

// ---------------------------------------------------------------------------
// Vendor module + export resolution
// ---------------------------------------------------------------------------

const char* const kCommDllMarker      = "CommunicateEthernet.dll";
const char* const kInterfaceDllMarker = "InterfaceMgr.dll";

HMODULE FindVendorModule(const char* moduleName)
{
    // Inside AutoShop both DLLs are already loaded; GetModuleHandle is enough.
    // The fallback LoadLibrary keeps the harness able to run with an explicit
    // DLL directory (SetDllDirectory by the harness); it never downloads or
    // writes anything.
    HMODULE mod = ::GetModuleHandleA(moduleName);
    if (mod != nullptr)
    {
        return mod;
    }
    return ::LoadLibraryA(moduleName);
}

// In-memory PE export-table scan (x86). The image is loader-mapped, so any RVA
// resolves to base + RVA. This proves the write exports exist in the loaded
// vendor build without invoking them.
bool ScanExportByPrefix(HMODULE mod, const char* prefix,
                        char* outName, size_t outNameBytes,
                        unsigned long* outRva, unsigned long* outAddress)
{
    if (mod == nullptr || prefix == nullptr)
    {
        return false;
    }
    const BYTE* base = reinterpret_cast<const BYTE*>(mod);
    const IMAGE_DOS_HEADER* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE)
    {
        return false;
    }
    const IMAGE_NT_HEADERS32* nt =
        reinterpret_cast<const IMAGE_NT_HEADERS32*>(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE)
    {
        return false;
    }
    DWORD exportRva = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXPORT].VirtualAddress;
    if (exportRva == 0)
    {
        return false;
    }
    const IMAGE_EXPORT_DIRECTORY* exports =
        reinterpret_cast<const IMAGE_EXPORT_DIRECTORY*>(base + exportRva);
    const DWORD* names = reinterpret_cast<const DWORD*>(base + exports->AddressOfNames);
    const WORD* nameOrdinals = reinterpret_cast<const WORD*>(base + exports->AddressOfNameOrdinals);
    const DWORD* functions = reinterpret_cast<const DWORD*>(base + exports->AddressOfFunctions);

    for (DWORD i = 0; i < exports->NumberOfNames; ++i)
    {
        const char* name = reinterpret_cast<const char*>(base + names[i]);
        if (std::strstr(name, prefix) == name) // exact name start, not a substring hit
        {
            if (outName != nullptr && outNameBytes > 0)
            {
                snprintf(outName, outNameBytes, "%s", name);
            }
            if (outRva != nullptr)
            {
                *outRva = functions[nameOrdinals[i]];
            }
            if (outAddress != nullptr)
            {
                *outAddress = functions[nameOrdinals[i]] + static_cast<DWORD>(reinterpret_cast<uintptr_t>(base));
            }
            return true;
        }
    }
    return false;
}

// ---------------------------------------------------------------------------
// Function pointer types for the proven read-only statics.
// Enum values are unknown (reverse-engineered signatures only); zero is passed,
// exactly as the S2 shim does.
// ---------------------------------------------------------------------------

typedef long (__cdecl* FnGetExecMode)(unsigned* mode, int index, unsigned execMode, unsigned level);
typedef long (__cdecl* FnGetGcmState)(unsigned* state, void* window, unsigned execMode, unsigned level);
typedef long (__cdecl* FnGetPlcLable)(void* lable, unsigned type, unsigned execMode, unsigned level);
typedef long (__cdecl* FnReadElemValue)(void* elements, void* window, unsigned execMode, unsigned level);
typedef void (__cdecl* FnSetCommunicatConfig)(void* info, void* window);
typedef int  (__cdecl* FnIsEthOnLine)();
typedef void (__thiscall* FnCSoftAddrMapCtor)(void* self);
typedef void (__thiscall* FnCSoftAddrMapDtor)(void* self);

// F2 mapping primitives (InterfaceMgr.dll, all REAL implementations — the
// base CSoftAddrMap::AddElem 0x5080 is a stub, these are not):
typedef void  (__thiscall* FnTagCtor5)(void* self, const wchar_t* addr1, int enumResult,
                                       const wchar_t* addr2, int dataType, int field18);
typedef void  (__thiscall* FnTagDtor)(void* self);
typedef void  (__thiscall* FnMapSetAt)(void* self, const wchar_t* key, void** value);
typedef int   (__thiscall* FnMapLookup)(void* self, const wchar_t* key, void** value);
typedef int   (__thiscall* FnMapGetCount)(void* self);
typedef void  (__thiscall* FnMapRemoveAll)(void* self);

// F1 compile-all chain (AutoShop.exe, RELOCS_STRIPPED ImageBase 0x400000):
typedef void* (__cdecl*   FnGetRootManager)();
typedef void* (__thiscall* FnGetMember)(void* self);
typedef int   (__thiscall* FnProjectGate)(void* self);
typedef void  (__thiscall* FnCompileAll)(void* self);

// G1 (docs/inovance-autoshop-monitor-values.md):
//   CCommunicateEth::ReadWatchElemValue (CommunicateEthernet export RVA 0x36C0).
//   NOTE: the third argument of the vendor Read* statics is a CWnd* used as
//   [wnd+0x20] (m_hWnd) — NULL is proven safe (S2 §10.2); a real frame CWnd*
//   is resolved via mfc90u!AfxGetMainWnd for the wnd-variant (F3 passed a raw
//   HWND and faulted; the vendor dereferences it, SEH-isolated there).
typedef long (__cdecl* FnReadWatchElemValue)(void* buffer, void* window,
                                             unsigned execMode, unsigned level);
//   CMonitElemBuffer ctor/dtor (InterfaceMgr RVAs 0x4FAF0 / 0x4FB00, __thiscall).
typedef void (__thiscall* FnBufferCtor)(void* self);
typedef void (__thiscall* FnBufferDtor)(void* self);
//   CInterfaceMgr::GetPublicMgr (InterfaceMgr RVA 0x9250, static __cdecl).
typedef void* (__cdecl* FnGetPublicMgr)();
//   CPublicMgr active-map accessor = vtable slot +0x80 (thiscall, 0 stack args;
//   the F1 write dialog resolves the active map exactly this way).
typedef void* (__thiscall* FnActiveMapAccessor)(void* self);
//   mfc90u!AfxGetMainWnd / AfxGetApp + CWinApp::GetMainWnd (frame CWnd* for
//   the wnd-variant; F3's crash came from passing a raw HWND as if CWnd*).
typedef void* (__cdecl* FnAfxGetMainWnd)();
typedef void* (__thiscall* FnGetAppMainWnd)(void* self);
//   CCommunicateEth::SimpleCommand(const CString&, mode, level) — real body at
//   CommunicateEthernet 0x3400 (F1: the UI download/monitor sequence issues it
//   as the "PLC ready wait" with cmd 0 then 0x7B polling). The online-probe
//   variant calls it with the empty string against the stored loopback config.
typedef long (__cdecl* FnSimpleCommand)(const void* commandString,
                                        unsigned execMode, unsigned level);

HMODULE g_commModule = nullptr;
HMODULE g_interfaceModule = nullptr;
FnGetExecMode         g_getExecMode = nullptr;
FnGetGcmState         g_getGcmState = nullptr;
FnGetPlcLable         g_getPlcLable = nullptr;
FnReadElemValue       g_readElemValue = nullptr;
FnSetCommunicatConfig g_setCommunicatConfig = nullptr;
FnIsEthOnLine         g_isEthOnLine = nullptr;
FnCSoftAddrMapCtor    g_softAddrMapCtor = nullptr;
FnCSoftAddrMapDtor    g_softAddrMapDtor = nullptr;
FnTagCtor5            g_tagCtor5 = nullptr;
FnTagDtor             g_tagDtor = nullptr;
FnMapSetAt            g_mapSetAt = nullptr;
FnMapLookup           g_mapLookup = nullptr;
FnMapGetCount         g_mapGetCount = nullptr;
FnMapRemoveAll        g_mapRemoveAll = nullptr;
bool g_resolved = false;
volatile LONG g_loopbackTargetSet = 0;

// Step markers for the compile chain (diagnostics: the SEH boundary reports the
// last completed step when a vendor fault is caught). 0=none 1=exe-base 2=gate
// 3=root 4=cmpMgr 5=compileAll submitted.
volatile LONG g_compileStep = 0;
// Diagnostics: the runtime addresses resolved by the last compile attempt.
volatile LONG g_lastRootPtr = 0;
volatile LONG g_lastOwnerPtr = 0;

// Exact mangled names (F2 harness tools/AutoShopNativeBridge/src/f2/
// F2RealReadHarness.cpp resolved all of them on this build).
const char* const kTagCtor5Name =
    "??0tagSoftAddr@@QAE@PB_WW4EnumForceQueryResult@@0HH@Z";
const char* const kTagDtorName =
    "??1tagSoftAddr@@UAE@XZ";
const char* const kMapSetAtName =
    "?SetAt@?$CMap@V?$CStringT@_WV?$StrTraitMFC_DLL@_WV?$ChTraitsCRT@_W@ATL@@@@@ATL@@PB_WPAUtagSoftAddr@@AAPAU3@@@QAEXPB_WAAPAUtagSoftAddr@@@Z";
const char* const kMapLookupName =
    "?Lookup@?$CMap@V?$CStringT@_WV?$StrTraitMFC_DLL@_WV?$ChTraitsCRT@_W@ATL@@@@@ATL@@PB_WPAUtagSoftAddr@@AAPAU3@@@QBEHPB_WAAPAUtagSoftAddr@@@Z";
const char* const kMapGetCountName =
    "?GetCount@?$CMap@V?$CStringT@_WV?$StrTraitMFC_DLL@_WV?$ChTraitsCRT@_W@ATL@@@@@ATL@@PB_WPAUtagSoftAddr@@AAPAU3@@@QBEHXZ";
const char* const kMapRemoveAllName =
    "?RemoveAll@?$CMap@V?$CStringT@_WV?$StrTraitMFC_DLL@_WV?$ChTraitsCRT@_W@ATL@@@@@ATL@@PB_WPAUtagSoftAddr@@AAPAU3@@@QAEXXZ";
const char* const kMapDtorName =
    "??1CSoftAddrMap@@UAE@XZ";

// G1 names (all proven in docs/inovance-autoshop-monitor-values.md §2/§4):
const char* const kReadWatchName =
    "?ReadWatchElemValue@CCommunicateEth@@SAJPAVCMonitElemBuffer@@PAVCWnd@@W4EnumExecuteMode@@W4EnumCommandLevel@@@Z";
const char* const kBufferCtorName =
    "??0CMonitElemBuffer@@QAE@XZ";
const char* const kBufferDtorName =
    "??1CMonitElemBuffer@@UAE@XZ";
const char* const kGetPublicMgrName =
    "?GetPublicMgr@CInterfaceMgr@@SAPAVCPublicMgr@@XZ";
const char* const kSimpleCommandName =
    "?SimpleCommand@CCommunicateEth@@SAJABV?$CStringT@_WV?$StrTraitMFC_DLL@_WV?$ChTraitsCRT@_W@ATL@@@@@ATL@@W4EnumExecuteMode@@W4EnumCommandLevel@@@Z";

FnReadWatchElemValue  g_readWatchElemValue = nullptr;
FnBufferCtor          g_bufferCtor = nullptr;
FnBufferDtor          g_bufferDtor = nullptr;
FnGetPublicMgr        g_getPublicMgr = nullptr;
FnSimpleCommand       g_simpleCommand = nullptr;

void ResolveInterfacePrimitives()
{
    if (g_interfaceModule == nullptr || g_tagCtor5 != nullptr)
    {
        return;
    }
    g_tagCtor5 = reinterpret_cast<FnTagCtor5>(::GetProcAddress(g_interfaceModule, kTagCtor5Name));
    g_tagDtor = reinterpret_cast<FnTagDtor>(::GetProcAddress(g_interfaceModule, kTagDtorName));
    g_mapSetAt = reinterpret_cast<FnMapSetAt>(::GetProcAddress(g_interfaceModule, kMapSetAtName));
    g_mapLookup = reinterpret_cast<FnMapLookup>(::GetProcAddress(g_interfaceModule, kMapLookupName));
    g_mapGetCount = reinterpret_cast<FnMapGetCount>(::GetProcAddress(g_interfaceModule, kMapGetCountName));
    g_mapRemoveAll = reinterpret_cast<FnMapRemoveAll>(::GetProcAddress(g_interfaceModule, kMapRemoveAllName));
    g_softAddrMapDtor = reinterpret_cast<FnCSoftAddrMapDtor>(::GetProcAddress(g_interfaceModule, kMapDtorName));
    g_bufferCtor = reinterpret_cast<FnBufferCtor>(::GetProcAddress(g_interfaceModule, kBufferCtorName));
    g_bufferDtor = reinterpret_cast<FnBufferDtor>(::GetProcAddress(g_interfaceModule, kBufferDtorName));
    g_getPublicMgr = reinterpret_cast<FnGetPublicMgr>(::GetProcAddress(g_interfaceModule, kGetPublicMgrName));
}

bool ResolveAll()
{
    if (g_resolved)
    {
        return g_commModule != nullptr;
    }
    g_resolved = true;

    g_commModule = FindVendorModule(kCommDllMarker);
    if (g_commModule == nullptr)
    {
        return false;
    }
    g_interfaceModule = FindVendorModule(kInterfaceDllMarker);

    g_getExecMode = reinterpret_cast<FnGetExecMode>(::GetProcAddress(
        g_commModule,
        "?GetExecMode@CCommunicateEth@@SAJPAW4EnumPlcRunModel@@HW4EnumExecuteMode@@W4EnumCommandLevel@@@Z"));
    g_getGcmState = reinterpret_cast<FnGetGcmState>(::GetProcAddress(
        g_commModule,
        "?GetGCMState@CCommunicateEth@@SAJPAW4EnumGCMState@@PAVCWnd@@W4EnumExecuteMode@@W4EnumCommandLevel@@@Z"));
    g_getPlcLable = reinterpret_cast<FnGetPlcLable>(::GetProcAddress(
        g_commModule,
        "?GetPlcLable@CCommunicateEth@@SAJPAU_PLC_LABLE@@W4EnumCommunicaterType@@W4EnumExecuteMode@@W4EnumCommandLevel@@@Z"));
    g_readElemValue = reinterpret_cast<FnReadElemValue>(::GetProcAddress(
        g_commModule,
        "?ReadElemValue@CCommunicateEth@@SAJPAVCSoftAddrMap@@PAVCWnd@@W4EnumExecuteMode@@W4EnumCommandLevel@@@Z"));
    g_setCommunicatConfig = reinterpret_cast<FnSetCommunicatConfig>(::GetProcAddress(
        g_commModule,
        "?SetCommunicatConfig@CCommunicateEth@@SAXPAUtagEthernetInfo@@PAUHWND__@@@Z"));
    g_isEthOnLine = reinterpret_cast<FnIsEthOnLine>(::GetProcAddress(
        g_commModule, "?IsEthOnLine@CCommunicateEth@@SAHXZ"));
    g_readWatchElemValue = reinterpret_cast<FnReadWatchElemValue>(::GetProcAddress(
        g_commModule, kReadWatchName));
    g_simpleCommand = reinterpret_cast<FnSimpleCommand>(::GetProcAddress(
        g_commModule, kSimpleCommandName));

    if (g_interfaceModule != nullptr)
    {
        g_softAddrMapCtor = reinterpret_cast<FnCSoftAddrMapCtor>(::GetProcAddress(
            g_interfaceModule, "??0CSoftAddrMap@@QAE@XZ"));
        ResolveInterfacePrimitives();
    }
    return g_getExecMode != nullptr || g_readElemValue != nullptr;
}

bool PreconditionsForCommCalls()
{
    if (!ResolveAll())
    {
        return false;
    }
    // Loopback-first discipline: refuse to drive the vendor comm stack until
    // this session has stored a loopback connection target.
    return g_loopbackTargetSet != 0;
}

// ---------------------------------------------------------------------------
// SEH-isolated compile-chain calls (no C++ objects inside: MSVC C2712).
// Each returns null/false and the exception code when the vendor call faults,
// so a fault is attributable to a step from the structured error response.
// ---------------------------------------------------------------------------

void* SafeCallRoot(const BYTE* base, unsigned long rootRva, unsigned long* outCode)
{
    *outCode = 0;
    __try
    {
        return reinterpret_cast<FnGetRootManager>(
            reinterpret_cast<uintptr_t>(base) + rootRva)();
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        *outCode = ::GetExceptionCode();
        return nullptr;
    }
}

void* SafeCallAccessor(const BYTE* base, unsigned long accRva, void* obj, unsigned long* outCode)
{
    *outCode = 0;
    __try
    {
        return reinterpret_cast<FnGetMember>(
            reinterpret_cast<uintptr_t>(base) + accRva)(obj);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        *outCode = ::GetExceptionCode();
        return nullptr;
    }
}

int SafeCallGate(const BYTE* base, unsigned long gateRva, void* obj, unsigned long* outCode)
{
    *outCode = 0;
    __try
    {
        return reinterpret_cast<FnProjectGate>(
            reinterpret_cast<uintptr_t>(base) + gateRva)(obj);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        *outCode = ::GetExceptionCode();
        return -1;
    }
}

// Finds the main MFC frame window of this process (E3 §6.1 evidence: class
// prefix "Afx:00400000", title carries the project directory). The vendor
// async read/write commands deliver results to the UI callback window (p2 of
// ReadElemValue/WriteMemory; F1 §4.1 "wnd+0x20 = m_hWnd") — passing a real
// frame HWND instead of NULL is the value-delivery experiment (F3 smoke).
struct FindFrameContext
{
    DWORD pid;
    HWND frame;
};

BOOL CALLBACK EnumFrameProc(HWND hwnd, LPARAM lp)
{
    FindFrameContext* ctx = reinterpret_cast<FindFrameContext*>(lp);
    DWORD pid = 0;
    ::GetWindowThreadProcessId(hwnd, &pid);
    if (pid != ctx->pid || !::IsWindowVisible(hwnd))
    {
        return TRUE;
    }
    wchar_t cls[64] = {0};
    if (::GetClassNameW(hwnd, cls, 64) == 0 ||
        wcsncmp(cls, L"Afx:00400000", 12) != 0)
    {
        return TRUE;
    }
    wchar_t title[256] = {0};
    int len = ::GetWindowTextW(hwnd, title, 256);
    if (len > 0)
    {
        ctx->frame = hwnd;
        return FALSE;
    }
    return TRUE;
}

HWND FindMainAfxFrame()
{
    FindFrameContext ctx;
    ctx.pid = ::GetCurrentProcessId();
    ctx.frame = nullptr;
    ::EnumWindows(EnumFrameProc, reinterpret_cast<LPARAM>(&ctx));
    return ctx.frame;
}

bool SafeCallCompile(const BYTE* base, unsigned long entryRva, void* cmpMgr, unsigned long* outCode)
{
    *outCode = 0;
    __try
    {
        reinterpret_cast<FnCompileAll>(
            reinterpret_cast<uintptr_t>(base) + entryRva)(cmpMgr);
        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        *outCode = ::GetExceptionCode();
        return false;
    }
}

// ---------------------------------------------------------------------------
// G1 value-slot probes (docs/inovance-autoshop-monitor-values.md §2).
// The delivered value of BOTH vendor read commands lands in the tagSoftAddr
// CString at +0x0C (bit path: CE 0xDE20 tail assigns static "1"/"0" strings;
// word path: CE 0xA610 assigns formatted value strings). MFC90 CStringT is a
// single wchar_t* member. These probes read the slot through SEH so a broken
// pointer is attributable and never takes the host down.
// ---------------------------------------------------------------------------

void* SnapshotTagCString(void* tag)
{
    return tag == nullptr ? nullptr : *reinterpret_cast<void**>(
        reinterpret_cast<unsigned char*>(tag) + 0x0C);
}

double SnapshotTagDouble(void* tag)
{
    double value = 0.0;
    if (tag != nullptr)
    {
        ::memcpy(&value, reinterpret_cast<unsigned char*>(tag) + 0x20, sizeof(value));
    }
    return value;
}

// Reads the wchar_t* stored at tag+0x0C into out (UTF-8). Returns false when
// the slot is null or the read faults.
bool SafeReadTagCString(void* tag, char* out, int outBytes, unsigned long* outCode)
{
    if (outCode != nullptr)
    {
        *outCode = 0;
    }
    if (out == nullptr || outBytes <= 0)
    {
        return false;
    }
    out[0] = '\0';
    __try
    {
        const wchar_t* data = *reinterpret_cast<const wchar_t**>(
            reinterpret_cast<unsigned char*>(tag) + 0x0C);
        if (data == nullptr)
        {
            return false;
        }
        wchar_t wide[40];
        int i = 0;
        for (; i < 39; ++i)
        {
            wchar_t ch = data[i];
            wide[i] = ch;
            if (ch == L'\0')
            {
                break;
            }
        }
        wide[i] = L'\0';
        int bytes = ::WideCharToMultiByte(CP_UTF8, 0, wide, -1, out, outBytes, nullptr, nullptr);
        if (bytes <= 0)
        {
            out[0] = '\0';
            return false;
        }
        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        if (outCode != nullptr)
        {
            *outCode = ::GetExceptionCode();
        }
        out[0] = '\0';
        return false;
    }
}

// Resolves the UI active map: CInterfaceMgr::GetPublicMgr() -> object vtable
// slot +0x80 (the F1 §4.1 sequence of the write dialog). SEH-isolated.
void* SafeResolveActiveMap(FnGetPublicMgr getPublicMgr, unsigned long* outCode)
{
    if (outCode != nullptr)
    {
        *outCode = 0;
    }
    __try
    {
        void* publicMgr = getPublicMgr();
        if (publicMgr == nullptr)
        {
            return nullptr;
        }
        void** vtable = *reinterpret_cast<void***>(publicMgr);
        FnActiveMapAccessor accessor =
            reinterpret_cast<FnActiveMapAccessor>(vtable[0x80 / sizeof(void*)]);
        return accessor(publicMgr);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        if (outCode != nullptr)
        {
            *outCode = ::GetExceptionCode();
        }
        return nullptr;
    }
}

long SafeCallReadWatch(FnReadWatchElemValue readWatch, void* buffer, void* wnd,
                       unsigned mode, unsigned level, unsigned long* outCode)
{
    if (outCode != nullptr)
    {
        *outCode = 0;
    }
    __try
    {
        return readWatch(buffer, wnd, mode, level);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        if (outCode != nullptr)
        {
            *outCode = ::GetExceptionCode();
        }
        return -1;
    }
}

void SafeCallBufferCtor(FnBufferCtor ctor, void* self, unsigned long* outCode)
{
    if (outCode != nullptr)
    {
        *outCode = 0;
    }
    __try
    {
        ctor(self);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        if (outCode != nullptr)
        {
            *outCode = ::GetExceptionCode();
        }
    }
}

void SafeCallBufferDtor(FnBufferDtor dtor, void* self, unsigned long* outCode)
{
    if (outCode != nullptr)
    {
        *outCode = 0;
    }
    __try
    {
        dtor(self);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        if (outCode != nullptr)
        {
            *outCode = ::GetExceptionCode();
        }
    }
}

// CWnd*-shaped shim: the vendor read statics only ever dereference [wnd+0x20]
// (m_hWnd; S2 §10.2, F1 §2) and the completion notify IsWindow-checks it
// before PostMessageW(0x4CA, 0x49, rc). The F3 crash was the raw HWND passed
// AS the CWnd* (handle+0x20 dereferenced). This shim provides a readable
// CWnd-layout object pointing at the real frame window; it is heap-owned by
// the bridge, never freed while a vendor command may still reference it.
struct CWndShim
{
    unsigned char reserved[0x20];
    void* hWnd;
};

CWndShim* g_wndShim = nullptr;      // kept alive for the bridge session
volatile LONG g_wndShimFaulted = 0; // 1 = a vendor call through the shim faulted

// Static MFC90 CStringT<wchar_t> image for the empty command string (the
// online probe). Header = { nRefCnt = -1 (locked static), nLength = 0,
// nAllocLength = 0 } immediately before the characters; the vendor code only
// reads the string. Falls back to a hand-built locked-static image if mfc90u
// does not export the shared empty buffer.
struct StaticCStringEmpty
{
    int nRefCnt;
    unsigned nLength;
    unsigned nAllocLength;
    wchar_t chars[2];
};
StaticCStringEmpty g_emptyCmd = { -1, 0, 0, { L'\0', L'\0' } };

long SafeCallSimpleCommand(FnSimpleCommand fn, const void* cmdStr,
                           unsigned mode, unsigned level, unsigned long* outCode)
{
    if (outCode != nullptr)
    {
        *outCode = 0;
    }
    __try
    {
        return fn(cmdStr, mode, level);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        if (outCode != nullptr)
        {
            *outCode = ::GetExceptionCode();
        }
        return -1;
    }
}

void* GetMainFrameWndShim()
{
    HWND frame = FindMainAfxFrame();
    if (frame == nullptr)
    {
        return nullptr;
    }
    if (g_wndShim == nullptr)
    {
        g_wndShim = reinterpret_cast<CWndShim*>(::malloc(sizeof(CWndShim)));
        if (g_wndShim == nullptr)
        {
            return nullptr;
        }
        ::memset(g_wndShim, 0, sizeof(CWndShim));
    }
    g_wndShim->hWnd = frame;
    return g_wndShim;
}

} // namespace

// ---------------------------------------------------------------------------
// BridgeConfig / JSON helpers
// ---------------------------------------------------------------------------

void ParseConfigJson(const char* json, BridgeConfig* config)
{
    if (config == nullptr)
    {
        return;
    }
    if (json != nullptr)
    {
        char name[128];
        if (JsonExtractString(json, "pipeName", name, sizeof(name)) && name[0] != '\0')
        {
            snprintf(config->pipeName, sizeof(config->pipeName), "%s", name);
        }
        bool flag = false;
        if (JsonExtractBool(json, "allowWrite", &flag))
        {
            config->allowWrite = flag;
        }
        if (JsonExtractBool(json, "allowForce", &flag))
        {
            config->allowForce = flag;
        }
        if (JsonExtractBool(json, "allowDownload", &flag))
        {
            config->allowDownload = flag;
        }
        int rva = 0;
        if (JsonExtractBool(json, "compileEnabled", &flag))
        {
            // Explicit enable/disable beats the entry-RVA presence heuristic;
            // "compile-rva none" on the injector writes compileEnabled=false.
            config->hasCompileEntry = flag;
        }
        if (JsonExtractInt(json, "compileEntryRva", &rva) && rva > 0)
        {
            config->hasCompileEntry = true;
            config->compileEntryRva = static_cast<unsigned long>(rva);
        }
        if (JsonExtractInt(json, "compileRootRva", &rva) && rva > 0)
        {
            config->compileRootRva = static_cast<unsigned long>(rva);
        }
        if (JsonExtractInt(json, "compileMemberRva", &rva) && rva > 0)
        {
            config->compileMemberRva = static_cast<unsigned long>(rva);
        }
        if (JsonExtractInt(json, "compileProjectGateRva", &rva) && rva > 0)
        {
            config->compileProjectGateRva = static_cast<unsigned long>(rva);
        }
        if (JsonExtractInt(json, "compileGateOwnerRva", &rva) && rva > 0)
        {
            config->compileGateOwnerRva = static_cast<unsigned long>(rva);
        }
    }
}

void LoadPendingConfig(BridgeConfig* config)
{
    if (config == nullptr)
    {
        return;
    }
    snprintf(config->pipeName, sizeof(config->pipeName), "%s", BRIDGE_DEFAULT_PIPE_NAME);
    config->allowWrite = BRIDGE_DEFAULT_ALLOW_WRITE != 0;
    config->allowForce = BRIDGE_DEFAULT_ALLOW_FORCE != 0;
    config->allowDownload = BRIDGE_DEFAULT_ALLOW_DOWNLOAD != 0;
    // F1 (2026-10-28): the compile gate is OPEN by default — the compile-all
    // chain (root 0x1581A70 -> member 0x1581CA0 -> entry 0x15A2EC0) is
    // signature-verified and runtime-verified x3 by E3; compile is offline
    // behaviour with no PLC action. The project gate 0x161B620 (with its
    // accessor 0x1581C20) is checked before every invocation.
    config->hasCompileEntry = true;
    config->compileEntryRva = 0x15A2EC0;
    config->compileRootRva = 0x1581A70;
    config->compileMemberRva = 0x1581CA0;
    config->compileProjectGateRva = 0x161B620;
    config->compileGateOwnerRva = 0x1581C20;

    char tempDir[MAX_PATH] = {0};
    if (::GetTempPathA(MAX_PATH, tempDir) == 0)
    {
        return;
    }
    char path[MAX_PATH];
    snprintf(path, sizeof(path),
             "%splcmcp-handoff\\inject-bridge\\pending-%lu.json",
             tempDir, static_cast<unsigned long>(::GetCurrentProcessId()));

    HANDLE file = ::CreateFileA(path, GENERIC_READ, FILE_SHARE_READ, nullptr,
                                OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE)
    {
        return; // no pending config: defaults stand
    }
    char buffer[2048] = {0};
    DWORD read = 0;
    ::ReadFile(file, buffer, sizeof(buffer) - 1, &read, nullptr);
    ::CloseHandle(file);
    ::DeleteFileA(path); // consumed exactly once, by the process it names

    ParseConfigJson(buffer, config);
}

bool JsonExtractString(const char* json, const char* field, char* out, size_t outSize)
{
    if (json == nullptr || field == nullptr || out == nullptr || outSize == 0)
    {
        return false;
    }
    char pattern[64];
    snprintf(pattern, sizeof(pattern), "\"%s\"", field);
    const char* p = std::strstr(json, pattern);
    if (p == nullptr)
    {
        return false;
    }
    p += std::strlen(pattern);
    while (*p == ' ' || *p == ':' || *p == '\t')
    {
        ++p;
    }
    if (*p != '"')
    {
        return false;
    }
    ++p;
    size_t n = 0;
    while (*p != '"' && *p != '\0' && n + 1 < outSize)
    {
        out[n++] = *p++;
    }
    out[n] = '\0';
    return true;
}

bool JsonExtractInt(const char* json, const char* field, int* out)
{
    if (json == nullptr || field == nullptr || out == nullptr)
    {
        return false;
    }
    char pattern[64];
    snprintf(pattern, sizeof(pattern), "\"%s\"", field);
    const char* p = std::strstr(json, pattern);
    if (p == nullptr)
    {
        return false;
    }
    p += std::strlen(pattern);
    while (*p == ' ' || *p == ':' || *p == '\t')
    {
        ++p;
    }
    bool negative = false;
    if (*p == '-')
    {
        negative = true;
        ++p;
    }
    if (*p < '0' || *p > '9')
    {
        return false;
    }
    long value = 0;
    while (*p >= '0' && *p <= '9')
    {
        value = value * 10 + (*p - '0');
        ++p;
    }
    *out = static_cast<int>(negative ? -value : value);
    return true;
}

bool JsonExtractBool(const char* json, const char* field, bool* out)
{
    if (json == nullptr || field == nullptr || out == nullptr)
    {
        return false;
    }
    char pattern[64];
    snprintf(pattern, sizeof(pattern), "\"%s\"", field);
    const char* p = std::strstr(json, pattern);
    if (p == nullptr)
    {
        return false;
    }
    p += std::strlen(pattern);
    while (*p == ' ' || *p == ':' || *p == '\t')
    {
        ++p;
    }
    if (std::strncmp(p, "true", 4) == 0)
    {
        *out = true;
        return true;
    }
    if (std::strncmp(p, "false", 5) == 0)
    {
        *out = false;
        return true;
    }
    return false;
}

// ---------------------------------------------------------------------------
// VendorComm
// ---------------------------------------------------------------------------

bool VendorComm::HasVendorModule()
{
    return ResolveAll();
}

bool VendorComm::IsLoopbackTargetSet()
{
    return g_loopbackTargetSet != 0;
}

bool VendorComm::IsLoopbackAddress(const char* ip)
{
    if (ip == nullptr)
    {
        return false;
    }
    // Loopback = 127.0.0.0/8 dotted quad. "localhost" is accepted and normalized
    // by the caller if needed; the vendor struct wants a numeric IP.
    int a = 0, b = 0, c = 0, d = 0;
    if (sscanf_s(ip, "%d.%d.%d.%d", &a, &b, &c, &d) != 4)
    {
        return false;
    }
    if (a < 0 || a > 255 || b < 0 || b > 255 || c < 0 || c > 255 || d < 0 || d > 255)
    {
        return false;
    }
    return a == 127;
}

long VendorComm::SetTargetLoopback(const char* ip, int port)
{
    if (!ResolveAll() || g_setCommunicatConfig == nullptr)
    {
        return -32001;
    }
    if (ip == nullptr || !IsLoopbackAddress(ip))
    {
        // Refuse BEFORE anything is stored: the bridge cannot be pointed at a
        // non-loopback target, full stop.
        return -32003;
    }
    if (port < 1 || port > 65535)
    {
        return -32602;
    }
    unsigned char info[64];
    ZeroMemory(info, sizeof(info));
    info[0] = static_cast<unsigned char>(port & 0xFF);
    info[1] = static_cast<unsigned char>((port >> 8) & 0xFF);
    int a = 0, b = 0, c = 0, d = 0;
    sscanf_s(ip, "%d.%d.%d.%d", &a, &b, &c, &d);
    info[4] = static_cast<unsigned char>(a);
    info[5] = static_cast<unsigned char>(b);
    info[6] = static_cast<unsigned char>(c);
    info[7] = static_cast<unsigned char>(d);

    // HWND=nullptr is verified safe in the vendor disassembly (S2, 10.2).
    g_setCommunicatConfig(reinterpret_cast<void*>(info), nullptr);
    ::InterlockedExchange(&g_loopbackTargetSet, 1);
    return 0;
}

long VendorComm::GetExecMode(int* execMode)
{
    if (execMode != nullptr)
    {
        *execMode = -1;
    }
    if (!PreconditionsForCommCalls() || g_getExecMode == nullptr)
    {
        return -32001;
    }
    unsigned mode = 0;
    long rc = g_getExecMode(&mode, 0, 0, 0);
    if (execMode != nullptr)
    {
        *execMode = static_cast<int>(mode);
    }
    return rc;
}

long VendorComm::GetGcmState(int* gcmState)
{
    if (gcmState != nullptr)
    {
        *gcmState = -1;
    }
    if (!PreconditionsForCommCalls() || g_getGcmState == nullptr)
    {
        return -32001;
    }
    unsigned state = 0;
    long rc = g_getGcmState(&state, nullptr, 0, 0);
    if (gcmState != nullptr)
    {
        *gcmState = static_cast<int>(state);
    }
    return rc;
}

long VendorComm::GetPlcLable(void* lableBuffer, int bufferBytes)
{
    if (lableBuffer == nullptr || bufferBytes < 64)
    {
        return -32602;
    }
    ZeroMemory(lableBuffer, bufferBytes);
    if (!PreconditionsForCommCalls() || g_getPlcLable == nullptr)
    {
        return -32001;
    }
    return g_getPlcLable(lableBuffer, 0, 0, 0);
}

long VendorComm::ReadElements(const ReadElementsRequest& request, ReadElementsOutcome* out)
{
    if (out != nullptr)
    {
        out->count = 0;
        out->delivered = 0;
        out->readRc = -1;
        out->watchRc = -1;
        out->onlineRc = -1;
        out->activeMapAddress = 0;
        out->activeMapCount = -1;
        out->onlineBefore[0] = '\0';
        out->values[0] = '\0';
        out->strings[0] = '\0';
        out->diagnostics[0] = '\0';
    }
    if (request.addressesCsv == nullptr || request.addressesCsv[0] == '\0')
    {
        return -32602;
    }
    if (!PreconditionsForCommCalls() || g_readElemValue == nullptr)
    {
        return -32001;
    }
    if (g_softAddrMapCtor == nullptr || g_mapSetAt == nullptr || g_tagCtor5 == nullptr ||
        g_mapLookup == nullptr || g_mapGetCount == nullptr || g_mapRemoveAll == nullptr)
    {
        return -32001; // InterfaceMgr.dll / non-stub mapping primitives unavailable
    }
    if (out == nullptr)
    {
        return -32602;
    }

    int mode = request.mode < 0 ? 0 : request.mode;
    int level = request.level < 0 ? 0 : request.level;
    int settleBudgetMs = request.settleMs <= 0 ? 6000 : (request.settleMs > 20000 ? 20000 : request.settleMs);
    std::string diag;
    char line[160];

    // --- diagnostics: online state before the call --------------------------
    if (g_isEthOnLine != nullptr)
    {
        snprintf(out->onlineBefore, sizeof(out->onlineBefore), "%d", g_isEthOnLine());
    }
    else
    {
        snprintf(out->onlineBefore, sizeof(out->onlineBefore), "n/a");
    }

    // --- variant: active-map resolution (REPORT ONLY, never inserted into) --
    // No CMap::Remove export exists, so inserting bridge-owned tags into the
    // UI's live map without a proven removal path could race UI iteration —
    // diagnostic evidence only.
    if (request.useActiveMap)
    {
        if (g_getPublicMgr == nullptr)
        {
            diag += "activeMap=unresolved(GetPublicMgr export missing);";
        }
        else
        {
            unsigned long code = 0;
            void* activeMap = SafeResolveActiveMap(g_getPublicMgr, &code);
            if (code != 0)
            {
                snprintf(line, sizeof(line), "activeMap=faulted(0x%08lx);", code);
                diag += line;
            }
            else if (activeMap == nullptr)
            {
                diag += "activeMap=null(no project open in this instance);";
            }
            else
            {
                out->activeMapAddress = static_cast<unsigned long>(reinterpret_cast<uintptr_t>(activeMap));
                if (g_mapGetCount != nullptr)
                {
                    out->activeMapCount = g_mapGetCount(activeMap);
                }
                snprintf(line, sizeof(line), "activeMap=0x%08lx count=%d;",
                         out->activeMapAddress, out->activeMapCount);
                diag += line;
            }
        }
    }

    // --- variant: online session probe (SimpleCommand against stored config) -
    if (request.tryOnlineProbe)
    {
        if (g_simpleCommand == nullptr)
        {
            diag += "online=unresolved(SimpleCommand export missing);";
        }
        else
        {
            unsigned long code = 0;
            long orc = SafeCallSimpleCommand(g_simpleCommand, &g_emptyCmd,
                                             static_cast<unsigned>(mode),
                                             static_cast<unsigned>(level), &code);
            if (code != 0)
            {
                snprintf(line, sizeof(line), "online=fault(0x%08lx);", code);
            }
            else
            {
                out->onlineRc = orc;
                snprintf(line, sizeof(line), "online=rc=%ld;", orc);
            }
            diag += line;
        }
    }

    // --- variant: window delivery (CWnd*-shaped shim, never a raw HWND) -----
    void* wnd = nullptr;
    if (request.useMainFrameWnd)
    {
        if (g_wndShimFaulted != 0)
        {
            diag += "wnd=skim(prev shim call faulted, using NULL);";
        }
        else
        {
            wnd = GetMainFrameWndShim();
            diag += wnd != nullptr ? "wnd=main-frame-shim;" : "wnd=null(no Afx frame found);";
        }
    }
    else
    {
        diag += "wnd=null(proven-safe baseline);";
    }

    // --- element fill (F2 primitives; per-element maps for the bit path) ----
    // T1 executor evidence: the ReadElemValue map path (CE 0xDE20) iterates
    // the map and transacts ONLY the first entry with tag+0x10==1 (bit), then
    // returns. A shared multi-element map would therefore read at most one
    // element per call. The bridge hence builds ONE map per element so every
    // requested address actually gets a transaction (word entries are still
    // skipped by design — the map path never requests type!=1).
    const int kMaxElements = 16;
    wchar_t keys[kMaxElements][32];
    int types[kMaxElements];
    int elements = 0;
    const char* p = request.addressesCsv;
    while (*p != '\0' && elements < kMaxElements)
    {
        while (*p == ' ' || *p == ',' || *p == ';')
        {
            ++p;
        }
        if (*p == '\0')
        {
            break;
        }
        char token[32] = {0};
        int n = 0;
        while (*p != '\0' && *p != ',' && *p != ';' && n + 1 < (int)sizeof(token))
        {
            token[n++] = *p++;
        }
        while (n > 0 && (token[n - 1] == ' ' || token[n - 1] == '\r' || token[n - 1] == '\t'))
        {
            token[--n] = '\0';
        }
        if (n == 0)
        {
            continue;
        }
        // Bit devices (M/X/Y/S) use type 1 — the ONLY type the map executor
        // transacts. Word devices (D/R/T/C...) keep type 4 (the honest F2/F3
        // shape); the map path skips them, word reads belong to the
        // ReadWatchElemValue/CMonitElemBuffer surface.
        int type = 4;
        char prefix = token[0] >= 'a' && token[0] <= 'z' ? (char)(token[0] - 'a' + 'A') : token[0];
        if (prefix == 'M' || prefix == 'X' || prefix == 'Y' || prefix == 'S')
        {
            type = 1;
        }
        MultiByteToWideChar(CP_UTF8, 0, token, -1, keys[elements],
                            (int)(sizeof(keys[elements]) / sizeof(wchar_t)) - 1);
        keys[elements][sizeof(keys[elements]) / sizeof(wchar_t) - 1] = L'\0';
        types[elements] = type;
        elements++;
    }
    if (elements == 0)
    {
        return -32602;
    }
    out->count = elements;

    // Shared settle budget across elements: each element's transaction has a
    // vendor-internal recv timeout anyway, so poll each map briefly and move
    // on; a delivered element frees its window share early.
    std::string values;
    std::string strings;
    int deliveredTotal = 0;
    long firstRc = 0;
    for (int i = 0; i < elements; ++i)
    {
        void* map = ::malloc(0x40);
        if (map == nullptr)
        {
            firstRc = -32004;
            break;
        }
        ::memset(map, 0, 0x40);
        g_softAddrMapCtor(map);
        void* tag = ::malloc(0x48); // tagSoftAddr = 72 bytes (F1/F2 layout)
        if (tag == nullptr)
        {
            ::free(map);
            firstRc = -32004;
            break;
        }
        ::memset(tag, 0, 0x48);
        g_tagCtor5(tag, keys[i], 0, L"", types[i], 0); // +0x08 addr, +0x10 type
        void* slot = tag;
        g_mapSetAt(map, keys[i], &slot);

        // Snapshot the delivery slots BEFORE the call.
        void* snapshotCString = SnapshotTagCString(tag);
        double snapshotDouble = SnapshotTagDouble(tag);

        long rc = g_readElemValue(map, wnd, static_cast<unsigned>(mode), static_cast<unsigned>(level));
        if (i == 0)
        {
            firstRc = rc;
        }
        out->readRc = firstRc;

        // Settle: poll tag+0x0C (the delivery slot) and +0x20 (double mirror).
        bool delivered = false;
        if (rc == 0)
        {
            int waited = 0;
            int perElementBudget = settleBudgetMs / (elements > 0 ? elements : 1);
            if (perElementBudget < 400)
            {
                perElementBudget = 400;
            }
            while (waited < perElementBudget)
            {
                ::Sleep(200);
                waited += 200;
                void* pv = nullptr;
                void* liveTag = tag;
                if (g_mapLookup(map, keys[i], &pv) && pv != nullptr)
                {
                    liveTag = pv; // the map may have replaced the entry
                }
                if (SnapshotTagCString(liveTag) != snapshotCString ||
                    SnapshotTagDouble(liveTag) != snapshotDouble)
                {
                    delivered = true;
                    tag = liveTag;
                    break;
                }
            }
        }

        // Extract the delivered string (CString at +0x0C) and the double.
        char narrow[32] = {0};
        ::WideCharToMultiByte(CP_UTF8, 0, keys[i], -1, narrow, (int)sizeof(narrow), nullptr, nullptr);
        double value = SnapshotTagDouble(tag);
        char item[80];
        snprintf(item, sizeof(item), delivered ? "%s=%.6g" : "%s=%.6g?", narrow, value);
        if (!values.empty())
        {
            values += ";";
        }
        values += item;

        char cstr[96] = {0};
        unsigned long code = 0;
        bool gotString = SafeReadTagCString(tag, cstr, static_cast<int>(sizeof(cstr)), &code);
        snprintf(item, sizeof(item), "%s=%s", narrow,
                 delivered ? (gotString && cstr[0] != '\0' ? cstr : "!") : "-");
        if (!strings.empty())
        {
            strings += ";";
        }
        strings += item;
        if (delivered)
        {
            deliveredTotal++;
        }

        // The map entry references the tag; free in the F2-proven order.
        g_mapRemoveAll(map);
        if (g_softAddrMapDtor != nullptr)
        {
            g_softAddrMapDtor(map);
        }
        ::free(map);
        if (g_tagDtor != nullptr)
        {
            g_tagDtor(tag);
        }
        ::free(tag);
    }
    out->delivered = deliveredTotal;
    snprintf(out->values, sizeof(out->values), "%s", values.c_str());
    snprintf(out->strings, sizeof(out->strings), "%s", strings.c_str());

    // --- variant: ReadWatchElemValue (word-read surface) --------------------
    if (request.useWatchPath)
    {
        if (g_readWatchElemValue == nullptr || g_bufferCtor == nullptr || g_bufferDtor == nullptr)
        {
            diag += "watch=unresolved(export/ctor missing);";
        }
        else
        {
            // Base CMonitElemBuffer: the T1 executor (CE 0xE2F0) pulls counts
            // from the buffer vtable; the BASE class answers 0 for all three
            // element classes, so no a3c2/a3c3 frame is built (this documents
            // the word-read surface honestly: receipt rc only, no values).
            void* buffer = ::malloc(0x100);
            if (buffer != nullptr)
            {
                ::memset(buffer, 0, 0x100);
                unsigned long ctorCode = 0;
                SafeCallBufferCtor(g_bufferCtor, buffer, &ctorCode);
                if (ctorCode != 0)
                {
                    snprintf(line, sizeof(line), "watch=ctor-fault(0x%08lx);", ctorCode);
                    diag += line;
                    ::free(buffer);
                }
                else
                {
                    unsigned long callCode = 0;
                    long wrc = SafeCallReadWatch(g_readWatchElemValue, buffer, wnd,
                                                 static_cast<unsigned>(mode),
                                                 static_cast<unsigned>(level), &callCode);
                    if (callCode != 0)
                    {
                        snprintf(line, sizeof(line), "watch=fault(0x%08lx);", callCode);
                        g_wndShimFaulted = 1;
                    }
                    else
                    {
                        out->watchRc = wrc;
                        snprintf(line, sizeof(line), "watch=rc=%ld;", wrc);
                    }
                    diag += line;
                    unsigned long dtorCode = 0;
                    SafeCallBufferDtor(g_bufferDtor, buffer, &dtorCode);
                    ::free(buffer);
                }
            }
        }
    }

    snprintf(out->diagnostics, sizeof(out->diagnostics), "%s", diag.c_str());
    return firstRc;
}

bool VendorComm::ResolveWriteExport(const char* namePrefix,
                                    char* outName, size_t outNameBytes,
                                    unsigned long* outRva,
                                    unsigned long* outAddress)
{
    if (!ResolveAll() || g_commModule == nullptr)
    {
        return false;
    }
    return ScanExportByPrefix(g_commModule, namePrefix, outName, outNameBytes, outRva, outAddress);
}

long VendorComm::InvokeCompile(const CompileTargets& targets, CompileCallResult* out)
{
    if (out != nullptr)
    {
        out->submitted = false;
        out->projectGateResult = -1;
        out->rootAddress = 0;
        out->cmpMgrAddress = 0;
        out->reason[0] = '\0';
    }
    ::InterlockedExchange(&g_compileStep, 0);
    // AutoShop.exe is RELOCS_STRIPPED (ImageBase 0x400000, no ASLR): the
    // configured VAs are only valid when the exe really loaded at its
    // preferred base. Anything else means a different build/version.
    HMODULE exeBase = ::GetModuleHandleA(nullptr);
    if (exeBase == nullptr || reinterpret_cast<uintptr_t>(exeBase) != 0x00400000)
    {
        if (out != nullptr)
        {
            snprintf(out->reason, sizeof(out->reason),
                     "AutoShop.exe base mismatch (expected 0x00400000, got 0x%p): the compile "
                     "chain addresses are version-locked to AutoShop 4.12.0.2 x86 and this host "
                     "is not that image", reinterpret_cast<void*>(exeBase));
        }
        return -32006;
    }
    ::InterlockedExchange(&g_compileStep, 1);

    // Bounds-check every address against the exe PE SizeOfImage before any call.
    const BYTE* base = reinterpret_cast<const BYTE*>(exeBase);
    const IMAGE_DOS_HEADER* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE)
    {
        if (out != nullptr)
        {
            snprintf(out->reason, sizeof(out->reason), "the host image has no MZ signature");
        }
        return -32006;
    }
    const IMAGE_NT_HEADERS32* nt = reinterpret_cast<const IMAGE_NT_HEADERS32*>(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE)
    {
        if (out != nullptr)
        {
            snprintf(out->reason, sizeof(out->reason), "the host image has no PE signature");
        }
        return -32006;
    }
    DWORD imageSize = nt->OptionalHeader.SizeOfImage;
    // The configured values are ABSOLUTE VAs of AutoShop.exe (ImageBase
    // 0x400000; E3's proven arithmetic is base + (VA - 0x400000)). Convert to
    // RVAs first and bounds-check the RVA, then call base + rva.
    unsigned long vas[5] = { targets.rootRva, targets.memberRva, targets.projectGateRva,
                             targets.gateOwnerRva, targets.entryRva };
    unsigned long rvas[5] = { 0, 0, 0, 0, 0 };
    for (int i = 0; i < 5; ++i)
    {
        if (vas[i] < 0x00400000 + 0x1000 || vas[i] - 0x00400000 >= imageSize)
        {
            if (out != nullptr)
            {
                snprintf(out->reason, sizeof(out->reason),
                         "compile address 0x%08lx is outside the AutoShop.exe image (ImageBase 0x400000, SizeOfImage=0x%08lx)",
                         vas[i], imageSize);
            }
            return -32006;
        }
        rvas[i] = vas[i] - 0x00400000;
    }

    // Compile is OFFLINE behaviour (red line: safe, no PLC action): the chain
    // touches only the AutoShop UI object state, never the vendor comm stack —
    // so unlike read_elements it does NOT require a set_target loopback session.
    // F1/E3 invoked it without one. Each vendor call below is isolated in its
    // own SEH guard so a fault is attributable to a step with its code.
    // NOTE (F3, 2026-10-28): an earlier smoke blamed the explicit project-gate
    // call for a 0xC0000096 fault, but the real cause was an address-arithmetic
    // bug (base+VA instead of VA; fixed above). With correct addresses the
    // explicit gate call mirrors the vendor call site exactly.

    // Step 2: project gate (F1: auto-disassembled call site at 0x15A3243 —
    //   call 0x1581a70; mov ecx,eax; call 0x1581c20; mov ecx,eax; call 0x161b620;
    //   test eax,eax; jne  — i.e. int __thiscall f(void* this = *(root+0x24)
    //   via accessor 0x1581C20), 0 stack args, returns 0 when no project is
    //   open, which pops msgbox 62220 and exits the compile entry). The bridge
    //   checks it FIRST so the modal-dialog exit path is never taken on our
    //   threads.
    unsigned long rootPreCode = 0;
    void* rootForGate = SafeCallRoot(base, rvas[0], &rootPreCode);
    if (rootForGate == nullptr)
    {
        if (out != nullptr)
        {
            snprintf(out->reason, sizeof(out->reason),
                     "the root-manager getter (VA 0x%08lx) raised exception 0x%08lx; compile-all was NOT invoked",
                     vas[0], rootPreCode);
        }
        return -32004;
    }
    unsigned long ownerCode = 0;
    void* gateOwner = SafeCallAccessor(base, rvas[3], rootForGate, &ownerCode);
    if (gateOwner == nullptr)
    {
        if (out != nullptr)
        {
            snprintf(out->reason, sizeof(out->reason),
                     "the gate-owner accessor (VA 0x%08lx) raised exception 0x%08lx; compile-all was NOT invoked",
                     vas[3], ownerCode);
        }
        return -32004;
    }
    unsigned long gateCode = 0;
    int gateResult = SafeCallGate(base, rvas[2], gateOwner, &gateCode);
    ::InterlockedExchange(&g_lastOwnerPtr, static_cast<LONG>(reinterpret_cast<uintptr_t>(gateOwner)));
    ::InterlockedExchange(&g_compileStep, 2);
    if (gateCode != 0)
    {
        if (out != nullptr)
        {
            snprintf(out->reason, sizeof(out->reason),
                     "the project gate (VA 0x%08lx) raised exception 0x%08lx; compile-all was NOT invoked "
                     "(open a project with plc_autoshop_open_project first and retry)",
                     vas[2], gateCode);
        }
        return -32004;
    }
    if (out != nullptr)
    {
        out->projectGateResult = gateResult;
    }
    if (gateResult == 0)
    {
        if (out != nullptr)
        {
            snprintf(out->reason, sizeof(out->reason),
                     "project gate (0x161B620) returned 0: no project is open in this AutoShop "
                     "instance; compile-all was NOT invoked (open one with plc_autoshop_open_project first)");
        }
        return -32002;
    }

    // Step 3+4: the F1-verified chain (E3 x3 + smoke verified): root singleton
    // -> compile-all manager accessor -> no-arg __thiscall compile entry
    // (async submit, returns quickly; artifacts appear under <projectDir>\Compile).
    void* root = rootForGate;
    ::InterlockedExchange(&g_compileStep, 3);
    unsigned long cmpCode = 0;
    void* cmpMgr = SafeCallAccessor(base, rvas[1], root, &cmpCode);
    if (cmpMgr == nullptr)
    {
        if (out != nullptr)
        {
            snprintf(out->reason, sizeof(out->reason),
                     "the compile-all manager accessor (VA 0x%08lx) raised exception 0x%08lx; compile-all was NOT invoked",
                     vas[1], cmpCode);
        }
        return -32004;
    }
    ::InterlockedExchange(&g_compileStep, 4);

    // Step 5: no-arg __thiscall compile entry (async submit, returns quickly;
    // artifacts appear under <projectDir>\Compile within ~25-40s).
    unsigned long entryCode = 0;
    if (!SafeCallCompile(base, rvas[4], cmpMgr, &entryCode))
    {
        if (out != nullptr)
        {
            snprintf(out->reason, sizeof(out->reason),
                     "the compile-all entry (VA 0x%08lx) raised exception 0x%08lx; no compile was submitted",
                     vas[4], entryCode);
        }
        return -32004;
    }
    ::InterlockedExchange(&g_compileStep, 5);
    if (out != nullptr)
    {
        out->submitted = true;
        out->rootAddress = static_cast<unsigned long>(reinterpret_cast<uintptr_t>(root));
        out->cmpMgrAddress = static_cast<unsigned long>(reinterpret_cast<uintptr_t>(cmpMgr));
    }
    return 0;
}

int VendorComm::LastCompileStep()
{
    return static_cast<int>(::InterlockedCompareExchange(&g_compileStep, 0, 0));
}

unsigned long VendorComm::LastRootPtr()
{
    return static_cast<unsigned long>(::InterlockedCompareExchange(&g_lastRootPtr, 0, 0));
}

unsigned long VendorComm::LastOwnerPtr()
{
    return static_cast<unsigned long>(::InterlockedCompareExchange(&g_lastOwnerPtr, 0, 0));
}

void VendorComm::Unload()
{
    // Nothing is freed: vendor modules belong to the host process. Drop the
    // cached pointers only.
    g_getExecMode = nullptr;
    g_getGcmState = nullptr;
    g_getPlcLable = nullptr;
    g_readElemValue = nullptr;
    g_setCommunicatConfig = nullptr;
    g_isEthOnLine = nullptr;
    g_softAddrMapCtor = nullptr;
    g_readWatchElemValue = nullptr;
    g_simpleCommand = nullptr;
    g_bufferCtor = nullptr;
    g_bufferDtor = nullptr;
    g_getPublicMgr = nullptr;
    g_commModule = nullptr;
    g_interfaceModule = nullptr;
    g_resolved = false;
    ::InterlockedExchange(&g_loopbackTargetSet, 0);
}

} // namespace AutoShopInjectBridge
