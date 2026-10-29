// BridgeInternal.h
//
// Internal declarations shared by the bridge DLL translation units.
// Evidence for every vendor item lives in docs/inovance-autoshop-native-bridge.md
// (S2) and docs/inovance-autoshop-re-notes.md; nothing here is documented API.

#pragma once

#include "BridgeApi.h"

#define WIN32_LEAN_AND_MEAN
#include <windows.h>

namespace AutoShopInjectBridge {

// ---------------------------------------------------------------------------
// Bridge-side configuration (pending-<pid>.json or built-in defaults).
// ---------------------------------------------------------------------------

struct BridgeConfig
{
    char pipeName[128];
    bool allowWrite;      // element-write pipeline reachable (loopback-guarded)
    bool allowForce;      // default false -> force answers Unsupported (gate closed)
    bool allowDownload;   // default false -> download answers Unsupported (gate closed)
    bool hasCompileEntry; // F1: true by default with the verified chain below
    unsigned long compileEntryRva;      // 0x15A2EC0 compile-all entry (no-arg __thiscall on cmpMgr)
    unsigned long compileRootRva;       // 0x1581A70 root-manager singleton getter (__cdecl, no args)
    unsigned long compileMemberRva;     // 0x1581CA0 compile-all manager accessor (__thiscall, = *(root+0x34))
    unsigned long compileProjectGateRva; // 0x161B620 current-project gate (__thiscall, returns non-0 when a project is open)
    unsigned long compileGateOwnerRva;  // 0x1581C20 accessor for the gate owner object (__thiscall, = *(root+0x24))
};

// Loads the pending configuration file for the current PID (written by the
// injector before ResumeThread) and deletes it after reading. When the file is
// absent the struct keeps the built-in defaults. Never fails.
void LoadPendingConfig(BridgeConfig* config);

// Applies a JSON object with the documented keys onto defaults; shared by the
// pending-file reader and (for the harness) Bridge_SetPipeName paths.
void ParseConfigJson(const char* json, BridgeConfig* config);

// ---------------------------------------------------------------------------
// Minimal JSON field extraction (requests are small and trusted-shape; the C#
// side is the parsing authority). Same approach as S2's BridgePipeServer.
// ---------------------------------------------------------------------------

bool JsonExtractString(const char* json, const char* field, char* out, size_t outSize);
bool JsonExtractInt(const char* json, const char* field, int* out);
bool JsonExtractBool(const char* json, const char* field, bool* out);

// ---------------------------------------------------------------------------
// Vendor communication stack access (in-process, runtime resolution).
// ---------------------------------------------------------------------------

class VendorComm
{
public:
    // Returns true when CommunicateEthernet.dll is loaded in this process
    // (i.e. we really are inside an AutoShop-like host).
    static bool HasVendorModule();

    // True after SetTargetLoopback succeeded in this bridge session. Every
    // vendor comm-stack call is refused until this flag is set, so the bridge
    // can never contact the vendor default target 192.168.1.1.
    static bool IsLoopbackTargetSet();

    // Validates ip as a loopback dotted quad (127.0.0.0/8) and stores it into
    // the vendor connection configuration (CCommunicateEth::SetCommunicatConfig,
    // HWND=nullptr verified safe by S2). Any non-loopback address is REFUSED.
    static long SetTargetLoopback(const char* ip, int port);

    // Read-only statics with proven signatures (S2 shim, static-import ABI).
    // Each performs the loopback-target precondition and returns the vendor rc.
    static long GetExecMode(int* execMode);
    static long GetGcmState(int* gcmState);
    static long GetPlcLable(void* lableBuffer, int bufferBytes);

    // In-process element read (G1 upgrade of the F2 pipeline,
    // docs/inovance-autoshop-monitor-values.md).
    //
    // T1 static RE of the vendor executor (CommunicateEthernet.dll 4.12.0.2):
    // - CCommunicateEth::ReadElemValue (RVA 0x37B0) builds a 0x2C command
    //   (vtable RVA 0x387AC, code 0x55 @+0x24, map @+0x1C, m_hWnd @+0x20) and
    //   dispatches it via RunCommThread; the executor (RVA 0xDDE0 -> 0xDE20)
    //   iterates the passed CSoftAddrMap with GetNextAssoc and builds a wire
    //   request ONLY for entries whose tag+0x10 type == 1 (bit). Word entries
    //   are skipped outright: ReadElemValue never requests a word element.
    // - The response parser writes the value as a STRING into the tag's
    //   CString at tag+0x0C (CE+0xDE20 tail: CString::operator= with the
    //   static strings at CE+0x4606C / CE+0x46070 selected by the reply bit).
    //   The tag+0x20 double is constructor-initialized ONLY and is not a
    //   value-delivery slot on this path (F2 §15.5 assumption corrected).
    // - UI value getter g_GetMonElemValue (InterfaceMgr 0x3CDE0) also reads
    //   tag+0x0C, confirming the string slot is the delivered value.
    //
    // Therefore ReadElementsEx reads BOTH slots after the settle window: the
    // +0x20 double (kept for continuity) and the +0x0C CString (the T1
    // delivery slot). A CString whose pointer value changed against the
    // pre-call snapshot marks delivered=1 (frame answered end-to-end).
    //
    // Variants (all loopback-only, SEH-isolated per step, docs/inovance-
    // autoshop-monitor-values.md §4):
    // - mode/level: the F1 UI call sites use mode=1/level=0; the accepted
    //   combo in receipts was mode=0/level=0 (F2). Both are testable.
    // - useMainFrameWnd: pass a CWnd*-shaped 0x24-byte heap shim whose +0x20
    //   holds the real main-frame HWND (the vendor only ever reads [wnd+0x20]
    //   = m_hWnd for PostMessageW(0x4CA,0x49) completion notification; e2b0).
    //   F3's fault came from passing the raw HWND ITSELF as the CWnd* — the
    //   vendor then dereferenced handle+0x20. The shim is deterministic and
    //   avoids MFC TLS state issues (AutoShop.exe links MFC statically, class
    //   prefix "Afx:00400000", while CommunicateEthernet.dll uses mfc90u.dll,
    //   so mfc90u!AfxGetMainWnd would read the WRONG module state here).
    //   T1 verdict: the value write-back is INLINE on the worker
    //   (CString::operator= into tag+0x0C), the window is only notified —
    //   so wnd=NULL remains the honest baseline and the shim a bounded
    //   experiment (SEH-isolated).
    // - useActiveMap: resolve CInterfaceMgr::GetPublicMgr + vtable+0x80 and
    //   REPORT the active map pointer/count (diagnostic only — no CMap::Remove
    //   export exists, so entries must not be inserted into the UI map).
    // - useWatchPath: construct a base CMonitElemBuffer via the vendor ctor
    //   (InterfaceMgr 0x4FAF0) and call CCommunicateEth::ReadWatchElemValue
    //   (CommunicateEthernet 0x36C0, command code path a3c2/a3c3 words); the
    //   base buffer's vtable is stubbed, so this documents the word-read
    //   surface honestly (rc recorded, values live inside the buffer object).
    struct ReadElementsRequest
    {
        const char* addressesCsv;
        int mode;             // -1 -> 0
        int level;            // -1 -> 0
        int settleMs;         // <=0 -> 6000 (cap 20000)
        bool useMainFrameWnd; // pass AfxGetMainWnd() CWnd* instead of NULL
        bool useActiveMap;    // resolve + report the UI active map (no insert)
        bool useWatchPath;    // additionally try ReadWatchElemValue
        // T2 "session prerequisite" probe: call CCommunicateEth::SimpleCommand
        // (real body, CommunicateEthernet 0x3400; the F1 UI download/monitor
        // sequence sends cmd 0 then polls 0x7B) with an empty command against
        // the stored LOOPBACK config first, hoping it materializes the comm
        // worker session the async commands need. Recorded in diagnostics as
        // online=rc=N. SEH-isolated; loopback target precondition stands.
        bool tryOnlineProbe;
    };
    struct ReadElementsOutcome
    {
        int count;            // map entries built
        int delivered;        // 1 = at least one tag+0x0C CString changed
        long readRc;          // ReadElemValue return code
        long watchRc;         // ReadWatchElemValue return code (if attempted)
        long onlineRc;        // SimpleCommand online-probe return (-1 not attempted)
        unsigned long activeMapAddress;   // 0 when unresolved
        int activeMapCount;               // -1 when unresolved
        char onlineBefore[32];            // IsEthOnLine diagnostic
        char values[2048];    // "addr=%.6g;..." double-slot mirror
        char strings[1024];   // "addr='...';..." +0x0C delivery slot
        char diagnostics[512];// variant resolution notes (one per line ;)
    };
    static long ReadElements(const ReadElementsRequest& request, ReadElementsOutcome* out);

    // Compile-all chain (F1, docs/inovance-autoshop-write-signatures.md §5 +
    // docs/inovance-autoshop-open-and-compile.md §5/§6; runtime-verified x3 by
    // E3 and x3 by the injected bridge smoke):
    //   root = 0x1581A70()                                  __cdecl singleton getter
    //   cmpMgr = 0x1581CA0(root)                            __thiscall, = *(root+0x34)
    //   0x161B620(*(root+0x24)) -> non-0 = project open     __thiscall project gate
    //       (auto-disassembled call site at 0x15A3243: this is *(root+0x24)
    //       via accessor 0x1581C20; 0 stack args; 0 -> msgbox 62220 exit)
    //   0x15A2EC0(cmpMgr)                                   __thiscall, 0 stack args,
    //                                                       async submit, returns fast
    // The addresses are VAs of AutoShop.exe (RELOCS_STRIPPED, ImageBase
    // 0x400000, no ASLR); the exe base is re-verified in-process and every
    // address is bounds-checked against the PE SizeOfImage before any call.
    // Returns 0 when compile-all was submitted; a negative bridge error code
    // when a precondition failed (gate closed / base mismatch / gate=0).
    struct CompileTargets
    {
        unsigned long rootRva;
        unsigned long memberRva;
        unsigned long projectGateRva;
        unsigned long gateOwnerRva;
        unsigned long entryRva;
    };
    struct CompileCallResult
    {
        bool submitted;
        int projectGateResult;      // raw gate return (only meaningful when gate was checked)
        unsigned long rootAddress;  // resolved runtime addresses (evidence)
        unsigned long cmpMgrAddress;
        char reason[320];           // failure reason (empty on success)
    };
    static long InvokeCompile(const CompileTargets& targets, CompileCallResult* out);

    // Diagnostics: last completed compile-chain step (0=none 1=exe-base 2=gate
    // 3=root 4=cmpMgr 5=compileAll submitted) and the runtime pointers resolved
    // by the last attempt. Reported by the SEH boundary so a vendor fault is
    // attributable to a step.
    static int LastCompileStep();
    static unsigned long LastRootPtr();
    static unsigned long LastOwnerPtr();

    // Write-surface export resolution WITHOUT invocation. Scans the vendor
    // export table for the exact name prefix and reports the resolved name,
    // RVA and absolute address. The vendor function is never called by this
    // bridge in the delivered state: the trailing parameter semantics of the
    // write statics are not reconstructed (re-notes: WriteElemValue/Unforce/
    // ProgramValidate share an incremental-link thunk RVA), and calling with
    // guessed arguments would endanger the host. Returns false when the export
    // is absent.
    static bool ResolveWriteExport(const char* namePrefix,
                                   char* outName, size_t outNameBytes,
                                   unsigned long* outRva,
                                   unsigned long* outAddress);

    // Releases cached vendor module handles (Bridge_Shutdown).
    static void Unload();

    // "127.0.0.1" style loopback check shared with the request handlers.
    static bool IsLoopbackAddress(const char* ip);
};

// Runs a request handler under SEH so a vendor-side fault becomes a structured
// error response instead of a crashed host. handler writes the response body.
typedef void (*FnRequestHandler)(const char* json, const char* id, char* outResponse, size_t outResponseBytes);
bool HandleLineSafe(const char* line, char* outResponse, size_t outResponseBytes);

} // namespace AutoShopInjectBridge
