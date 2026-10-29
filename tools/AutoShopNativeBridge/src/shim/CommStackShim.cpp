// CommStackShim.cpp
//
// M3 x86 shim over the Inovance communication stack
// (CommunicateEthernet.dll!CCommunicateEth, x86, static facade).
//
// The shim statically imports the read-only statics below and re-exports a
// flat, undecorated C API that the in-process plugin (and therefore the M4
// named pipe) can call:
//
//   PlcMcpComm_GetExecMode / GetGcmState / GetPlcLable / ReadElements /
//   ReadSeriesWord / Version / Shutdown
//
// WRITE / FORCE / DOWNLOAD entry points of CCommunicateEth are deliberately
// NOT imported. This shim is read-only by construction.
//
// All types below are reverse-engineered (see docs/inovance-autoshop-native-bridge.md);
// enum VALUES are unknown and are passed as 0 unless noted. There is no vendor
// header and no vendor endorsement; version-locked to AutoShop 4.12.0.2 x86.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstdio>
#include <cstring>
#include <string>

// ATL CStringT with the vendor's exact trait template, so that our
// declarations mangle identically to the vendor exports:
//   V?$CStringT@_WV?$StrTraitMFC_DLL@_WV?$ChTraitsCRT@_W@ATL@@@@@ATL@@
// In the VC9-era MFC bridge, StrTraitMFC_DLL lives in the GLOBAL namespace
// (only its ChTraitsCRT argument is ATL::). VS2022's ATL no longer ships it,
// so we declare a same-named global template: only the mangled name matters
// across the boundary, because the parameter is passed as a reference.
#include <atlstr.h>
#include <atlcoll.h>

template <typename T, typename Traits>
class StrTraitMFC_DLL : public Traits
{
public:
    static IAtlStringMgr* GetDefaultManager() throw()
    {
        return ATL::CAtlStringMgr::GetInstance();
    }

    static HINSTANCE FindStringResourceInstance(_In_ UINT nID) throw()
    {
        return ATL::AtlFindStringResourceInstance(nID);
    }
};

// ---------------------------------------------------------------------------
// Vendor type declarations (mangling-compatible).
// ---------------------------------------------------------------------------

class CSoftAddrMap;
class CWnd;
class CWordArray;
struct _PLC_LABLE;
struct tagEthernetInfo;

// Enum VALUES are unknown (reverse-engineered signatures only).
enum EnumPlcRunModel { EnumPlcRunModel_Unknown = 0 };
enum EnumGCMState { EnumGCMState_Unknown = 0 };
enum EnumExecuteMode { EnumExecuteMode_Unknown = 0 };
enum EnumCommandLevel { EnumCommandLevel_Unknown = 0 };
enum EnumCommunicaterType { EnumCommunicaterType_Unknown = 0 };

typedef ATL::CStringT<wchar_t, StrTraitMFC_DLL<wchar_t, ATL::ChTraitsCRT<wchar_t>>> VendorWideString;

class CCommunicateEth
{
public:
    // ?GetExecMode@CCommunicateEth@@SAJPAW4EnumPlcRunModel@@HW4EnumExecuteMode@@W4EnumCommandLevel@@@Z
    static long GetExecMode(EnumPlcRunModel* mode, int index,
                            EnumExecuteMode execMode, EnumCommandLevel level);

    // ?GetGCMState@CCommunicateEth@@SAJPAW4EnumGCMState@@PAVCWnd@@W4EnumExecuteMode@@W4EnumCommandLevel@@@Z
    static long GetGCMState(EnumGCMState* state, CWnd* window,
                            EnumExecuteMode execMode, EnumCommandLevel level);

    // ?GetPlcLable@CCommunicateEth@@SAJPAU_PLC_LABLE@@W4EnumCommunicaterType@@W4EnumExecuteMode@@W4EnumCommandLevel@@@Z
    static long GetPlcLable(_PLC_LABLE* lable, EnumCommunicaterType type,
                            EnumExecuteMode execMode, EnumCommandLevel level);

    // ?ReadElemValue@CCommunicateEth@@SAJPAVCSoftAddrMap@@PAVCWnd@@W4EnumExecuteMode@@W4EnumCommandLevel@@@Z
    static long ReadElemValue(CSoftAddrMap* elements, CWnd* window,
                              EnumExecuteMode execMode, EnumCommandLevel level);

    // ?ReadSeriesWordElem@CCommunicateEth@@SAJABV?$CStringT@_WV?$StrTraitMFC_DLL@_WV?$ChTraitsCRT@_W@ATL@@@@@ATL@@ABGPAVCWordArray@@W4EnumExecuteMode@@W4EnumCommandLevel@@@Z
    // NOTE: the export table maps this name to RVA 0x3E90 which is a stub
    // (`xor eax,eax; ret`) shared with isDialConnect/SendATCmd/... -- in
    // 4.12.0.2 the method returns 0 without touching its arguments.
    static long ReadSeriesWordElem(const VendorWideString& address,
                                   const unsigned short& count,
                                   CWordArray* values,
                                   EnumExecuteMode execMode,
                                   EnumCommandLevel level);

    // ?SetCommunicatConfig@CCommunicateEth@@SAXPAUtagEthernetInfo@@PAUHWND__@@@Z
    // Stores the local connection target (IP/port) into the comm object; no
    // PLC traffic. HWND=nullptr verified safe in the vendor disassembly.
    static void SetCommunicatConfig(tagEthernetInfo* info, HWND window);

    // ?IsEthOnLine@CCommunicateEth@@SAHXZ
    // TCP liveness probe against the stored configuration (read-only).
    static int IsEthOnLine();

    // ?IsEthOnLine@CCommunicateEth@@SAHPAUtagEthernetInfo@@@Z
    static int IsEthOnLine(tagEthernetInfo* info);
};

// ---------------------------------------------------------------------------
// InterfaceMgr.dll element-model access (CSoftAddrMap base ctor is exported
// and real; AddElem/IsExist are stubs returning 0 in the base class).
// ---------------------------------------------------------------------------

typedef void (__thiscall* FnCSoftAddrMapCtor)(void* self);

static FnCSoftAddrMapCtor LoadCSoftAddrMapCtor()
{
    static FnCSoftAddrMapCtor cached = nullptr;
    static bool tried = false;
    if (!tried)
    {
        tried = true;
        HMODULE img = LoadLibraryA("InterfaceMgr.dll");
        if (img != nullptr)
        {
            cached = reinterpret_cast<FnCSoftAddrMapCtor>(
                GetProcAddress(img, "??0CSoftAddrMap@@QAE@XZ"));
        }
    }
    return cached;
}

// ---------------------------------------------------------------------------
// Flat C API (stable ABI for the pipe server and the test harness).
// ---------------------------------------------------------------------------

extern "C" {

__declspec(dllexport) long PlcMcpComm_GetExecMode(int* execMode)
{
    if (execMode == nullptr)
    {
        return -1;
    }
    *execMode = -1;
    EnumPlcRunModel mode = EnumPlcRunModel_Unknown;
    long rc = CCommunicateEth::GetExecMode(&mode, 0, EnumExecuteMode_Unknown,
                                           EnumCommandLevel_Unknown);
    *execMode = static_cast<int>(mode);
    return rc;
}

__declspec(dllexport) long PlcMcpComm_GetGcmState(int* gcmState)
{
    if (gcmState == nullptr)
    {
        return -1;
    }
    *gcmState = -1;
    EnumGCMState state = EnumGCMState_Unknown;
    long rc = CCommunicateEth::GetGCMState(&state, nullptr, EnumExecuteMode_Unknown,
                                           EnumCommandLevel_Unknown);
    *gcmState = static_cast<int>(state);
    return rc;
}

__declspec(dllexport) long PlcMcpComm_GetPlcLable(void* lableBuffer, int bufferBytes,
                                                  int communicaterType)
{
    if (lableBuffer == nullptr || bufferBytes < 64)
    {
        return -1;
    }
    ZeroMemory(lableBuffer, bufferBytes);
    // _PLC_LABLE size is unknown; the generous caller buffer bounds any write.
    return CCommunicateEth::GetPlcLable(
        reinterpret_cast<_PLC_LABLE*>(lableBuffer),
        static_cast<EnumCommunicaterType>(communicaterType),
        EnumExecuteMode_Unknown, EnumCommandLevel_Unknown);
}

__declspec(dllexport) long PlcMcpComm_ReadElements(const char* addressesCsv,
                                                   int* outCount,
                                                   char* outValues,
                                                   int outValuesBytes)
{
    if (outCount != nullptr)
    {
        *outCount = 0;
    }
    if (outValues != nullptr && outValuesBytes > 0)
    {
        outValues[0] = '\0';
    }
    FnCSoftAddrMapCtor ctor = LoadCSoftAddrMapCtor();
    if (ctor == nullptr)
    {
        return -1001; // InterfaceMgr.dll / ctor symbol not resolvable
    }

    // The base CSoftAddrMap::AddElem is a stub (xor eax,eax; ret 8), so no
    // element can be inserted through the base class; the call below exercises
    // the CCommunicateEth::ReadElemValue calling convention with an empty map
    // and reports the vendor return code. It never writes to any PLC.
    unsigned char mapBuffer[256];
    ZeroMemory(mapBuffer, sizeof(mapBuffer));
    ctor(mapBuffer);

    long rc = CCommunicateEth::ReadElemValue(reinterpret_cast<CSoftAddrMap*>(mapBuffer),
                                             nullptr, EnumExecuteMode_Unknown,
                                             EnumCommandLevel_Unknown);
    if (rc != 0 && outValues != nullptr && outValuesBytes > 0)
    {
        snprintf(outValues, outValuesBytes, "rc=%ld; no PLC connection verified", rc);
    }
    return rc;
}

__declspec(dllexport) long PlcMcpComm_ReadSeriesWord(const wchar_t* address,
                                                     unsigned short count,
                                                     int* outCallResult)
{
    if (outCallResult != nullptr)
    {
        *outCallResult = -1;
    }
    if (address == nullptr)
    {
        return -1;
    }
    // In 4.12.0.2 the export resolves to a stub (see the declaration comment),
    // so the call is safe: it returns 0 without dereferencing its arguments.
    // CWordArray stays nullptr on purpose (its layout is unverified).
    VendorWideString wide(address);
    unsigned short countByRef = count;
    long rc = CCommunicateEth::ReadSeriesWordElem(wide, countByRef, nullptr,
                                                  EnumExecuteMode_Unknown,
                                                  EnumCommandLevel_Unknown);
    if (outCallResult != nullptr)
    {
        *outCallResult = static_cast<int>(rc);
    }
    return rc;
}

__declspec(dllexport) const char* PlcMcpComm_Version()
{
    return "PlcMcp CommStackShim 1.1.0 (reverse-engineered, read-only, x86, AutoShop 4.12.0.2)";
}

// ---------------------------------------------------------------------------
// Connection target configuration + liveness probe (flat API added for the
// simulator closed-loop experiments; still read-only, no PLC writes).
//
// tagEthernetInfo layout (evidence: docs/inovance-autoshop-native-bridge.md,
// InterfaceMgr.dll ctor disassembly):
//   +0x00 uint16 port (little-endian; vendor default 0x328B = 12939)
//   +0x04 .. +0x0A four IP bytes (vendor default 192,168,1,1)
//   remaining fields up to the 20-byte struct are unknown; the buffer below
//   is zero-filled and oversized (64 bytes) so the callee never reads past
//   initialized memory even if its layout assumptions differ slightly.
// ---------------------------------------------------------------------------

__declspec(dllexport) long PlcMcpComm_SetTarget(const char* ipCsv, int port)
{
    if (ipCsv == nullptr || port < 1 || port > 65535)
    {
        return -1;
    }
    unsigned char info[64];
    ZeroMemory(info, sizeof(info));
    info[0] = static_cast<unsigned char>(port & 0xFF);
    info[1] = static_cast<unsigned char>((port >> 8) & 0xFF);
    int a = 0, b = 0, c = 0, d = 0;
    if (sscanf_s(ipCsv, "%d.%d.%d.%d", &a, &b, &c, &d) != 4 ||
        a < 0 || a > 255 || b < 0 || b > 255 || c < 0 || c > 255 || d < 0 || d > 255)
    {
        return -2;
    }
    info[4] = static_cast<unsigned char>(a);
    info[5] = static_cast<unsigned char>(b);
    info[6] = static_cast<unsigned char>(c);
    info[7] = static_cast<unsigned char>(d);
    CCommunicateEth::SetCommunicatConfig(reinterpret_cast<tagEthernetInfo*>(info), nullptr);
    return 0;
}

// Uses the configuration stored by PlcMcpComm_SetTarget and probes it. Returns
// the vendor liveness result (0 = offline/not reachable, non-zero = online).
__declspec(dllexport) long PlcMcpComm_IsOnline()
{
    return CCommunicateEth::IsEthOnLine();
}

__declspec(dllexport) void PlcMcpComm_Shutdown()
{
    // Nothing to release: the shim keeps no vendor state.
}

} // extern "C"
