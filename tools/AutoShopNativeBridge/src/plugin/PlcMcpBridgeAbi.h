// PlcMcpBridgeAbi.h
//
// Reconstructed ABI declarations for the Inovance AutoShop plugin framework
// (AutoVarShared\Plugin.dll, x86, VS2008-era build) and for the CSoftAddrMap /
// tagSoftAddr element model (InterfaceMgr.dll).
//
// EVERY declaration in this file is reverse-engineered from disassembly evidence
// (dumpbin /disasm) collected on AutoShop V4.12.0.2. Evidence and confidence
// levels are documented in docs/inovance-autoshop-native-bridge.md. There is no
// vendor header, no vendor endorsement, and the layout is version-locked to
// 4.12.0.2 (x86).
//
// Cross-toolchain rules used here:
//   * The vendor DLLs were built with VS2008 (msvcr90/msvcp90). We build with
//     VS2022 v143 x86. No C++ object with a non-trivial layout (std::string,
//     CString, PluginInfo) is ever passed by value across the boundary; every
//     such object is hand-built in the VS2008 memory layout and passed by
//     pointer/reference. References are pointers on x86, so a
//     `const std::string&` parameter is ABI-safe when the pointee is a
//     hand-built VS2008 string image.
//   * vtables are declared manually (function-pointer tables) so the slot order
//     is deterministic and does not depend on the compiler's vtable emission.
//   * Namespaces mirror the vendor mangled names exactly: PluginInfo,
//     CPluginBase and PluginEnum are global; PluginManager is in
//     PluginInterface.

#pragma once

#include <cstdint>
#include <cstddef>
#include <string>

#include <windows.h>

namespace PlcMcpBridge
{

// ---------------------------------------------------------------------------
// VS2008 std::string image (28 bytes on x86).
//   +0x00 void*      proxy    (allocator proxy from _String_val; unused by
//                              read paths, may be nullptr in our images)
//   +0x04 char[16]   buf      (SSO buffer; holds the heap pointer when long)
//   +0x14 unsigned   mysize
//   +0x18 unsigned   myres    (SSO capacity 15; >= 16 means "heap")
// Evidence:
//   * Plugin.dll `??0PluginInfo@@QAE@W4PluginEnum@@V?$basic_string...I@Z`
//     (RVA 0x12D0) returns with `ret 24h` = 36 stack arg bytes =
//     4 (enum) + 28 (string by value) + 4 (version); the version argument is
//     read at entry+0x24, i.e. the string argument occupies entry+8..entry+0x23.
//   * Plugin.dll `DLibrary::LoadLibByPath` (RVA 0x1000):
//     `cmp [edi+14h],0` (empty check), `cmp [edi+18h],10h` / `lea esi,[edi+4]`
//     / `mov eax,[esi]` (heap pointer at +0x04) -- buffer at +0x04, size at
//     +0x14, capacity at +0x18, SSO when capacity < 16.
//   * msvcp90.dll (WinSxS, 9.0.30729.4974) string copy ctor at RVA 0x7286:
//     `mov dword ptr [esi+18h], 0Fh` -- the SSO capacity is 15.
//   * VS2015+ dropped both the proxy and the 4th field
//     (sizeof(std::string) == 24 on VS2022 x86), which is why the hand-built
//     image below is required.
// ---------------------------------------------------------------------------
struct V2008String
{
    void*         proxy;  // +0x00
    union
    {
        char  buf[16];   // +0x04 SSO buffer
        char* ptr;       // +0x04 heap pointer
    } data;
    unsigned int  mysize; // +0x14
    unsigned int  myres;  // +0x18
};

static_assert(sizeof(V2008String) == 28, "VS2008 std::string must be 28 bytes on x86");
static_assert(offsetof(V2008String, data) == 0x04, "buffer/heap pointer at +0x04");
static_assert(offsetof(V2008String, mysize) == 0x14, "mysize at +0x14");
static_assert(offsetof(V2008String, myres) == 0x18, "myres at +0x18");

// Builds a VS2008 string image for a NUL-terminated ASCII name.
// Names of at most 15 characters use the SSO image (no allocation).
// Longer names are copied into a process-heap buffer referenced through
// *heapOut; the caller MUST free that buffer with HeapFree after the vendor
// call returns (the vendor only reads a `const std::string&`; it never takes
// ownership of a caller-provided image). Strings that will be OWNED by the
// framework (e.g. a registered PluginInfo name or the sret string of
// GetPluginName) must use the SSO form, because the framework destroys them
// through the VS2008 string destructor, which would free a foreign pointer.
inline bool MakeV2008String(V2008String& out, const char* text, void** heapOut)
{
    if (heapOut != nullptr)
    {
        *heapOut = nullptr;
    }
    if (text == nullptr)
    {
        return false;
    }
    out.proxy = nullptr;
    unsigned int len = 0;
    while (text[len] != '\0')
    {
        ++len;
    }
    if (len < 16)
    {
        for (unsigned int i = 0; i < 16; ++i)
        {
            out.data.buf[i] = '\0';
        }
        for (unsigned int i = 0; i < len; ++i)
        {
            out.data.buf[i] = text[i];
        }
        out.mysize = len;
        out.myres = 15;  // VS2008 SSO reserve (< 16 selects the SSO path)
        return true;
    }

    if (heapOut == nullptr)
    {
        return false; // caller requires an SSO (framework-owned) image
    }
    char* buffer = static_cast<char*>(HeapAlloc(GetProcessHeap(), 0, len + 1));
    if (buffer == nullptr)
    {
        return false;
    }
    for (unsigned int i = 0; i < len; ++i)
    {
        buffer[i] = text[i];
    }
    buffer[len] = '\0';
    out.data.ptr = buffer;
    out.mysize = len;
    out.myres = len;  // >= 16 selects the heap path
    *heapOut = buffer;
    return true;
}

// SSO-only convenience overload (rejects names of 16+ characters).
inline bool MakeV2008String(V2008String& out, const char* text)
{
    return MakeV2008String(out, text, nullptr);
}

} // namespace PlcMcpBridge

// ---------------------------------------------------------------------------
// Vendor framework types (global namespace / PluginInterface, exactly as the
// vendor's mangled export names encode them).
// ---------------------------------------------------------------------------

enum PluginEnum
{
    PluginEnum_Bridge = 0x00000064 // our own value; vendor values unknown
};

class CPluginBase;

struct PluginInfo
{
    PlcMcpBridge::V2008String name;
    int                       enumValue; // PluginEnum
    unsigned                  version;
};

static_assert(sizeof(PluginInfo) == 36, "PluginInfo must be 36 bytes");

namespace PluginInterface
{

// ---------------------------------------------------------------------------
// PluginManager (opaque singleton, accessed through Plugin.dll exports).
// Only the methods used by this bridge are declared. Declarations match the
// vendor mangled export names exactly so that linking against Plugin.lib (the
// import library shipped with the product) resolves them:
//   ?GetPluginMgr@PluginManager@PluginInterface@@SAPAV12@XZ
//   ?RegisterInterface@PluginManager@PluginInterface@@QAEXABUPluginInfo@@@Z
//   ?LoadLibiary@PluginManager@PluginInterface@@QAE_NABV?$basic_string@...@@Z
//   ?GetPluginByEnum@PluginManager@PluginInterface@@QAEPAVCPluginBase@@W4PluginEnum@@@Z
//   ?IsLibraryLoaded@PluginManager@PluginInterface@@QBE_NABV?$basic_string@...@@Z
//   ?UnloadAllLibrary@PluginManager@PluginInterface@@QAEXXZ
// ---------------------------------------------------------------------------
class PluginManager
{
public:
    static PluginManager* GetPluginMgr();

    void RegisterInterface(const ::PluginInfo& info);
    bool LoadLibiary(const std::string& path); // vendor spelling
    ::CPluginBase* GetPluginByEnum(::PluginEnum pluginEnum);
    bool IsLibraryLoaded(const std::string& name);
    void UnloadAllLibrary();
};

} // namespace PluginInterface

namespace PlcMcpBridge
{

using ::CPluginBase;
using ::PluginEnum;
using ::PluginInfo;
using ::PluginInterface::PluginManager;

// ---------------------------------------------------------------------------
// CPluginBase vtable contract (5 slots used by the framework load path).
//
// Slot order verified against PluginLogger.dll `??_7CLogPlugin@@6B@`
// (RVA 0x54B4):
//   +0x00 scalar deleting destructor  ~CPluginBase(int alsoDelete)
//   +0x04 complete object destructor  ~CPluginBase()
//   +0x08 unsigned GetPluginVersionEx() const
//   +0x0C std::string GetPluginName() const   (sret: ecx=this, [esp+4]=retbuf)
//   +0x10 PluginEnum GetPluginEnumEx() const
//
// Framework call sites (Plugin.dll):
//   * LoadLibiary failure path calls slot 0 with argument 1
//     (`mov eax,[edx]; push 1; mov ecx,esi; call eax`).
//   * IsCanLoadPlugin (RVA 0x3830) calls slot 4 (enum) then slot 2 (version);
//     requires the enum to already have a registered PluginInfo and
//     (version & 0xFFFF0000) == 0x00010000 && (version & 0xFF00) <= 0x100.
//   * IsValidateInterface (RVA 0x38C0) calls slot 4 then slot 2; the registered
//     PluginInfo version and the plugin version must share the same high 16
//     bits and (pluginVersion & 0xFF) >= (registeredVersion >> 8) & 0xFF.
// ---------------------------------------------------------------------------
struct CPluginBaseVtable
{
    void (*deletingDtor)(void* self, int alsoDelete);       // slot 0
    void (*completeDtor)(void* self);                       // slot 1
    unsigned (*getVersion)(const void* self);               // slot 2
    void (*getName)(const void* self, V2008String* retBuf); // slot 3 (sret)
    int (*getEnum)(const void* self);                       // slot 4
};

class CPluginBase
{
public:
    const CPluginBaseVtable* vt; // +0x00
    // The framework never reads CPluginBase data fields during the load path;
    // fields below are ours.
    int       reserved0;         // +0x04
    unsigned  version;           // +0x08
    int       enumValue;         // +0x0C
};

// ---------------------------------------------------------------------------
// InterfaceMgr.dll element model.
//
// CSoftAddrMap is a CMap<CStringT<wchar>, wchar_t const*, tagSoftAddr*,
// tagSoftAddr*> subclass (MFC map: address string -> element pointer).
//   +0x00 vtable
//   +0x04 m_pHashTable      (CAssoc**, NULL)
//   +0x08 m_nHashTableSize  (17)
//   +0x0C m_nCount          (0)
//   +0x10 m_pFreeList       (0)
//   +0x14 own field         (0, semantics unknown)
//   +0x18 own field         (10, semantics unknown)
// Evidence: InterfaceMgr.dll `??0CSoftAddrMap@@QAE@XZ` (RVA 0x53100) and the
// exported template instantiation
// `??0?$CMap@V?$CStringT@_WV?$StrTraitMFC_DLL@_WV?$ChTraitsCRT@_W@ATL@@@@@ATL@@PB_WPAUtagSoftAddr@@AAPAU3@@@QAE@H@Z`.
//
// tagSoftAddr (68-byte field range, polymorphic):
//   +0x00 vtable (??_7tagSoftAddr@@6B@ = 0x100B3484)
//   +0x04 padding / unused in ctor
//   +0x08 CStringT<wchar> #1   (ctor arg 1)
//   +0x0C CStringT<wchar> #2   (ctor arg 3)
//   +0x10 int                  (ctor arg 4)
//   +0x14 CStringT<wchar> #3   (default)
//   +0x18 int                  (ctor arg 5)
//   +0x1C EnumForceQueryResult (ctor arg 2)
//   +0x20 double               (0.0) -- numeric element value
//   +0x28 int (0) +0x2C int (0) +0x30 int (0)
//   +0x34/0x38/0x3C/0x40 CStringT<wchar> (empty)
// Evidence: InterfaceMgr.dll `??0tagSoftAddr@@QAE@PB_WW4EnumForceQueryResult@@0HH@Z`
// (RVA 0x1C10) and `??0tagSoftAddr@@QAE@XZ` (RVA 0x1AB0).
// ---------------------------------------------------------------------------

// ATL/MFC CStringT<wchar> with the vendor's exact trait template:
// CStringT<wchar, StrTraitMFC_DLL<wchar, ChTraitsCRT<wchar>>> (4 bytes: one
// CStringData* pointer).
struct MfcWideString
{
    void* data; // CStringData*
};

struct TagSoftAddr
{
    void*         vt;      // +0x00
    unsigned      pad04;   // +0x04
    MfcWideString str08;   // +0x08
    MfcWideString str0C;   // +0x0C
    int           int10;   // +0x10
    MfcWideString str14;   // +0x14
    int           int18;   // +0x18
    int           enum1C;  // +0x1C EnumForceQueryResult
    double        value20; // +0x20 numeric value
    int           int28;   // +0x28
    int           int2C;   // +0x2C
    int           int30;   // +0x30
    MfcWideString str34;   // +0x34
    MfcWideString str38;   // +0x38
    MfcWideString str3C;   // +0x3C
    MfcWideString str40;   // +0x40
};

// Fields span 0x00..0x43 (68 bytes); the double at +0x20 forces 8-byte
// alignment, so the vendor object size is 72 bytes with 4 bytes of tail
// padding (padding size inferred from alignment, not observed).
static_assert(sizeof(TagSoftAddr) == 72, "tagSoftAddr must be 72 bytes (aligned)");
static_assert(offsetof(TagSoftAddr, value20) == 0x20, "value double at +0x20");
static_assert(offsetof(TagSoftAddr, str40) == 0x40, "last CString at +0x40");

struct CSoftAddrMap
{
    void*    vt;             // +0x00 (??_7CSoftAddrMap@@6B@)
    void*    hashTable;      // +0x04 m_pHashTable
    unsigned hashTableSize;  // +0x08 (17)
    int      count;          // +0x0C m_nCount
    void*    freeList;       // +0x10 m_pFreeList
    int      own14;          // +0x14 (0; semantics unknown)
    int      own18;          // +0x18 (10; semantics unknown)
};

static_assert(sizeof(CSoftAddrMap) == 28, "CSoftAddrMap header must be 28 bytes");

// PluginEnum value used by this bridge. The vendor's real PluginEnum values
// are unknown (runtime-initialised globals); any value works inside our own
// test host, and the vendor service rejects unregistered enums anyway.
const int kBridgePluginEnum = PluginEnum_Bridge;

// Version accepted by Plugin.dll's IsCanLoadPlugin: high 16 bits must be 1,
// bits 8..15 must be <= 1. We use 0x00010000 (1.0.0).
const unsigned kBridgePluginVersion = 0x00010000u;

} // namespace PlcMcpBridge
