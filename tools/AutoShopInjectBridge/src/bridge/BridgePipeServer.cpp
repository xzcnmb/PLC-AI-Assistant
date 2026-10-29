// BridgePipeServer.cpp
//
// Operation surface of the inject bridge (whitelist; anything else answers
// "method not found" and is never attempted):
//
//   handshake        identity + capability list (no vendor calls)
//   capabilities     capability matrix honoring the bridge-side gates
//   set_target       {ip, port}: stores a LOOPBACK-ONLY connection target
//                    (CCommunicateEth::SetCommunicatConfig). Non-loopback
//                    addresses are refused before anything is stored.
//   read_status      GetExecMode / GetGCMState call-return evidence
//   read_elements    in-process element read (G1 value-path upgrade): the
//                    map is filled through the REAL vendor primitives
//                    (tagSoftAddr 5-arg ctor + CMap::SetAt), ReadElemValue is
//                    called once PER element (the T1 executor CE 0xDE20
//                    transacts only the first tag+0x10==1 entry per command),
//                    and BOTH value slots are polled in a bounded settle
//                    window: the tagSoftAddr CString at +0x0C — the DELIVERY
//                    slot per vendor disassembly (CE 0xDE20 tail / 0xA610:
//                    CString::operator= of the answered value; UI getter
//                    g_GetMonElemValue reads the same slot) — and the +0x20
//                    double (constructor-initialized mirror). Optional
//                    params: mode/level (enum values), settleMs, wnd=1
//                    (CWnd*-shaped main-frame shim instead of NULL),
//                    activeMap=1 (resolve+report the UI active map,
//                    read-only), watch=1 (attempt ReadWatchElemValue with a
//                    base CMonitElemBuffer: word-read surface receipt).
//                    The answer reports delivered=N — the number of CString
//                    slots that actually changed (daemon-answered reads);
//                    nothing is ever presented as a value that was not read.
//   write_elements   RESERVATION: C#-gate + allowWrite gate + loopback check
//                    + export resolution; the vendor export is resolved but
//                    NOT invoked (signature not reconstructed) -> the answer
//                    states resolved=true/invoked=false, never success
//   force            gate closed by default -> Unsupported; if opened, same
//                    reservation semantics as write_elements
//   download         gate closed by default -> Unsupported; if opened, honest
//                    G2 verdict: vendor Download export is non-transferring
//                    standalone; loopback closed loop lives on the D1 protocol layer
//   compile          F1-verified compile-all chain (OPEN by default): the
//                    project gate 0x161B620 is checked first (clear error when
//                    no project is open), then root 0x1581A70 -> member
//                    0x1581CA0 -> entry 0x15A2EC0 (async submit; artifacts
//                    appear under <projectDir>\Compile; no PLC action).
//                    Addresses are configurable and base-validated.
//   shutdown         stops the server
//
// Every request handler runs under SEH (HandleLineSafe): a vendor fault
// becomes a structured error response instead of a crashed AutoShop.

#include "BridgePipeServer.h"

#include <cstdio>
#include <cstring>
#include <string>

namespace AutoShopInjectBridge {
namespace {

const wchar_t* kPipePrefix = L"\\\\.\\pipe\\";

HANDLE g_thread = nullptr;
HANDLE g_stopEvent = nullptr;
volatile LONG g_running = 0;
volatile LONG g_stopRequested = 0;

BridgeConfig g_config;
bool g_configInitialized = false;

// Manual mode (Pump/SetManualPipeName) runs without Bridge_Init; make sure the
// gate defaults (and any pending-<pid>.json) are applied exactly once.
void EnsureConfigInitialized()
{
    if (!g_configInitialized)
    {
        LoadPendingConfig(&g_config);
        g_configInitialized = true;
    }
}

// Manual-mode state.
HANDLE g_manualPipe = nullptr;
bool g_manualConnected = false;
HANDLE g_manualEvent = nullptr;
std::string g_manualPending;
std::string g_manualPipeNameUtf8;

// ---------------------------------------------------------------------------
// JSON response helpers
// ---------------------------------------------------------------------------

void AppendJsonString(std::string& out, const char* value)
{
    out += '"';
    for (const char* p = value ? value : ""; *p != '\0'; ++p)
    {
        if (*p == '"' || *p == '\\')
        {
            out += '\\';
        }
        if (static_cast<unsigned char>(*p) < 0x20)
        {
            char esc[8];
            snprintf(esc, sizeof(esc), "\\u%04x", *p);
            out += esc;
        }
        else
        {
            out += *p;
        }
    }
    out += '"';
}

void WriteError(std::string& response, const char* id, int code, const char* message)
{
    response = "{\"id\":\"";
    response += id ? id : "0";
    response += "\",\"error\":{\"code\":";
    char buf[32];
    snprintf(buf, sizeof(buf), "%d", code);
    response += buf;
    response += ",\"message\":";
    AppendJsonString(response, message);
    response += "}}";
}

void WriteResult(std::string& response, const char* id, const std::string& resultJson)
{
    response = "{\"id\":\"";
    response += id ? id : "0";
    response += "\",\"result\":";
    response += resultJson;
    response += "}";
}

// ---------------------------------------------------------------------------
// Handlers (called only through HandleLineSafe)
// ---------------------------------------------------------------------------

void HandleHandshake(const char*, const char* id, std::string& response)
{
    std::string result = "{\"workerName\":";
    AppendJsonString(result, BRIDGE_WORKER_NAME);
    char buf[256];
    result += ",\"workerVersion\":\"" BRIDGE_WORKER_VERSION "\",";
    result += "\"protocolVersion\":\"" BRIDGE_PROTOCOL_VERSION "\",";
    result += "\"vendor\":\"Inovance\",\"bitness\":\"x86\",";
    result += "\"runtimeEnvironment\":\"injected in-process (own started instance)\",";
    snprintf(buf, sizeof(buf), "\"pid\":%lu,", static_cast<unsigned long>(::GetCurrentProcessId()));
    result += buf;
    result += "\"capabilities\":[\"environment_inspection\",\"comm_stack_read_only_loopback_first\",\"element_write_reservation\"],";
    result += "\"disclaimer\":\"AutoShop interfaces were obtained by reverse engineering; no vendor endorsement; version-locked 4.12.0.2 x86; loopback-first discipline; no PLC action by default\"}";
    WriteResult(response, id, result);
}

void HandleCapabilities(const char*, const char* id, std::string& response)
{
    std::string result = "{";
    result += "\"comm_stack_read_only\":\"Experimental: in-process calls into CommunicateEthernet.dll (S2 signatures); loopback-first discipline; no PLC verified\",";
    result += "\"variable_read\":\"Experimental (in-process): per-element SetAt/tagCtor5 map fill + "
              "ReadElemValue + dual-slot settle poll (CString delivery slot tag+0x0C per vendor "
              "executor disassembly, +0x20 double mirror) with delivered= counter; optional "
              "mode/level enums, frame-CWnd shim, active-map report, ReadWatchElemValue word-"
              "surface receipt; loopback simulator only, not hardware-verified\",";
    result += "\"element_write\":";
    if (g_config.allowWrite)
    {
        result += "\"Experimental (reservation): C# gate + loopback check + export resolution implemented; vendor invocation blocked until per-method signature verification\"";
    }
    else
    {
        result += "\"Unsupported: bridge gate closed (allowWrite=false)\""; 
    }
    result += ",\"force\":";
    if (g_config.allowForce)
    {
        result += "\"Experimental (reservation): resolved-not-invoked until signature verification\"";
    }
    else
    {
        result += "\"Unsupported: bridge gate closed (allowForce=false, default)\"";
    }
    result += ",\"download\":";
    if (g_config.allowDownload)
    {
        result += "\"Unsupported (gate open, G2 verdict): vendor Download export enqueues but does not transfer standalone; loopback closed loop owns the D1 12939 protocol layer\"";
    }
    else
    {
        result += "\"Unsupported: bridge gate closed (allowDownload=false, default)\"";
    }
    result += ",\"compile\":";
    if (g_config.hasCompileEntry)
    {
        result += "\"Experimental (gate open): F1-verified in-process compile-all chain "
                  "(root 0x1581A70 -> cmpMgr 0x1581CA0 -> entry 0x15A2EC0; project gate 0x161B620 "
                  "checked first); E3 runtime-verified x3 + smoke verified; artifacts in "
                  "<projectDir>\\\\Compile; offline behaviour, no PLC action; version-locked 4.12.0.2\"";
    }
    else
    {
        result += "\"Unsupported: compile surface disabled (compileEnabled=false)\"";
    }
    result += ",\"run_stop\":\"Unsupported\"}";
    WriteResult(response, id, result);
}

void HandleSetTarget(const char* json, const char* id, std::string& response)
{
    char ip[64] = {0};
    int port = 0;
    if (!JsonExtractString(json, "ip", ip, sizeof(ip)) || !JsonExtractInt(json, "port", &port))
    {
        WriteError(response, id, BRIDGE_ERR_INVALID_PARAMS, "set_target requires 'ip' and 'port'");
        return;
    }
    if (!VendorComm::IsLoopbackAddress(ip))
    {
        // Hard refusal BEFORE any vendor interaction.
        WriteError(response, id, BRIDGE_ERR_LOOPBACK_REQUIRED,
                   "non-loopback target refused: the bridge only configures 127.0.0.0/8 addresses");
        return;
    }
    long rc = VendorComm::SetTargetLoopback(ip, port);
    if (rc != 0)
    {
        char message[192];
        snprintf(message, sizeof(message), "set_target refused (rc=%ld)", rc);
        WriteError(response, id, rc == -32001 ? BRIDGE_ERR_VENDOR_MODULEAbsent : BRIDGE_ERR_LOOPBACK_REQUIRED, message);
        return;
    }
    char buf[160];
    snprintf(buf, sizeof(buf), "{\"stored\":true,\"ip\":\"%s\",\"port\":%d,\"loopbackOnly\":true}", ip, port);
    WriteResult(response, id, std::string(buf));
}

void HandleReadStatus(const char*, const char* id, std::string& response)
{
    if (!VendorComm::HasVendorModule())
    {
        WriteError(response, id, BRIDGE_ERR_VENDOR_MODULEAbsent,
                   "CommunicateEthernet.dll is not loaded in this process (bridge must run inside the AutoShop process)");
        return;
    }
    if (!VendorComm::IsLoopbackTargetSet())
    {
        WriteError(response, id, BRIDGE_ERR_LOOPBACK_REQUIRED,
                   "no loopback target configured; call set_target with a 127.x.x.x address first (loopback-first discipline)");
        return;
    }
    int execMode = -1;
    int gcmState = -1;
    long rc1 = VendorComm::GetExecMode(&execMode);
    long rc2 = VendorComm::GetGcmState(&gcmState);
    char buf[256];
    snprintf(buf, sizeof(buf),
             "{\"execMode\":%d,\"execModeCallResult\":%ld,\"gcmState\":%d,\"gcmStateCallResult\":%ld,"
             "\"evidence\":\"call-return codes only; loopback target; no PLC verified\"}",
             execMode, rc1, gcmState, rc2);
    WriteResult(response, id, std::string(buf));
}

void HandleReadElements(const char* json, const char* id, std::string& response)
{
    if (!VendorComm::HasVendorModule())
    {
        WriteError(response, id, BRIDGE_ERR_VENDOR_MODULEAbsent,
                   "CommunicateEthernet.dll is not loaded in this process");
        return;
    }
    if (!VendorComm::IsLoopbackTargetSet())
    {
        WriteError(response, id, BRIDGE_ERR_LOOPBACK_REQUIRED,
                   "no loopback target configured; call set_target with a 127.x.x.x address first");
        return;
    }
    char addresses[512] = {0};
    if (!JsonExtractString(json, "addresses", addresses, sizeof(addresses)) || addresses[0] == '\0')
    {
        WriteError(response, id, BRIDGE_ERR_INVALID_PARAMS, "missing 'addresses' parameter");
        return;
    }

    // G1 read variants (all optional; defaults are the proven F2/F3 baseline):
    // mode/level enums, settleMs window, wnd=1 main-frame CWnd*-shim,
    // activeMap=1 resolve+report the UI map, watch=1 ReadWatchElemValue.
    VendorComm::ReadElementsRequest request;
    request.addressesCsv = addresses;
    int value = -1;
    request.mode = JsonExtractInt(json, "mode", &value) ? value : 0;
    value = -1;
    request.level = JsonExtractInt(json, "level", &value) ? value : 0;
    value = -1;
    request.settleMs = JsonExtractInt(json, "settleMs", &value) ? value : 6000;
    value = 0;
    JsonExtractInt(json, "wnd", &value);
    request.useMainFrameWnd = value != 0;
    value = 0;
    JsonExtractInt(json, "activeMap", &value);
    request.useActiveMap = value != 0;
    value = 0;
    JsonExtractInt(json, "watch", &value);
    request.useWatchPath = value != 0;
    value = 0;
    JsonExtractInt(json, "online", &value);
    request.tryOnlineProbe = value != 0;

    VendorComm::ReadElementsOutcome outcome;
    long rc = VendorComm::ReadElements(request, &outcome);
    if (rc != 0)
    {
        char message[256];
        snprintf(message, sizeof(message),
                 "in-process element read refused/failed (rc=%ld); see the bridge log for the reason", rc);
        WriteError(response, id, BRIDGE_ERR_VENDOR_CALL_FAILED, message);
        return;
    }

    std::string result = "{";
    char buf[128];
    snprintf(buf, sizeof(buf), "\"count\":%d,", outcome.count);
    result += buf;
    snprintf(buf, sizeof(buf), "\"delivered\":%d,", outcome.delivered);
    result += buf;
    result += "\"values\":";
    AppendJsonString(result, outcome.values);
    result += ",\"valueSlots\":";
    AppendJsonString(result, outcome.strings);
    result += ",\"watchRc\":";
    snprintf(buf, sizeof(buf), "%ld,", outcome.watchRc);
    result += buf;
    result += "\"onlineRc\":";
    snprintf(buf, sizeof(buf), "%ld,", outcome.onlineRc);
    result += buf;
    snprintf(buf, sizeof(buf), "\"activeMap\":\"0x%08lx\",\"activeMapCount\":%d,",
             outcome.activeMapAddress, outcome.activeMapCount);
    result += buf;
    result += "\"onlineBefore\":";
    AppendJsonString(result, outcome.onlineBefore);
    result += ",\"variant\":";
    AppendJsonString(result, outcome.diagnostics);
    // Dynamic verdict prefix so the delivered count flows verbatim through
    // every layer (the C# services and the MCP tool both echo the note).
    std::string note;
    if (outcome.delivered > 0)
    {
        note = "delivered=" + std::to_string(outcome.delivered) +
               " — VALUE WRITE-BACK CLOSED: the vendor write-back parser assigned "
               "tagSoftAddr+0x0C CStrings (daemon-answered reads); these values are PLC data "
               "as read by the vendor stack (loopback simulator).";
    }
    else
    {
        note = "delivered=0 — VALUE WRITE-BACK NOT OBSERVED: no tagSoftAddr+0x0C CString "
               "changed within the settle window, so these values remain CALL-LEVEL evidence, "
               "never PLC data.";
    }
    note += " In-process vendor read (G1 value-path): CSoftAddrMap filled via tagSoftAddr "
            "5-arg ctor + CMap::SetAt (non-stub primitives); ReadElemValue accepted (rc=0) "
            "per element; the settle window polls the DELIVERY slot tagSoftAddr+0x0C (the "
            "slot CE 0xDE20 tail / 0xA610 assign on an answered read; UI getter "
            "g_GetMonElemValue reads the same slot) and the +0x20 double (constructor-"
            "initialized mirror). Loopback target only; every variant SEH-isolated.";
    result += ",\"note\":";
    AppendJsonString(result, note.c_str());
    result += "}";
    WriteResult(response, id, result);
}

// Shared reservation pipeline for write_elements / force. Performs every gate
// and the loopback check, resolves the vendor export and reports
// resolved=true/invoked=false. The vendor function is NEVER invoked.
void HandleWriteReservation(const char* json, const char* id, std::string& response,
                            const char* method, const char* exportPrefix,
                            bool gateOpen, const char* gateHint)
{
    if (!gateOpen)
    {
        WriteError(response, id, BRIDGE_ERR_GATE_CLOSED, gateHint);
        return;
    }
    if (!VendorComm::HasVendorModule())
    {
        WriteError(response, id, BRIDGE_ERR_VENDOR_MODULEAbsent,
                   "CommunicateEthernet.dll is not loaded in this process");
        return;
    }
    // Optional explicit target parameter must be loopback when present.
    char target[64] = {0};
    if (JsonExtractString(json, "target", target, sizeof(target)) && target[0] != '\0')
    {
        if (!VendorComm::IsLoopbackAddress(target))
        {
            WriteError(response, id, BRIDGE_ERR_LOOPBACK_REQUIRED,
                       "non-loopback target refused: writes are only possible to 127.0.0.0/8");
            return;
        }
    }
    if (!VendorComm::IsLoopbackTargetSet())
    {
        WriteError(response, id, BRIDGE_ERR_LOOPBACK_REQUIRED,
                   "no loopback target configured; call set_target with a 127.x.x.x address first");
        return;
    }

    char exportName[256] = {0};
    unsigned long rva = 0;
    unsigned long address = 0;
    if (!VendorComm::ResolveWriteExport(exportPrefix, exportName, sizeof(exportName), &rva, &address))
    {
        WriteError(response, id, BRIDGE_ERR_VENDOR_MODULEAbsent,
                   "vendor export not found in the loaded CommunicateEthernet.dll export table");
        return;
    }

    std::string result = "{\"method\":";
    AppendJsonString(result, method);
    char buf[256];
    result += ",\"resolved\":true,\"invoked\":false,";
    result += "\"export\":";
    AppendJsonString(result, exportName);
    snprintf(buf, sizeof(buf), ",\"exportRva\":\"0x%08lx\",\"exportAddress\":\"0x%08lx\",", rva, address);
    result += buf;
    result += "\"reason\":\"vendor export resolved but not invoked: trailing parameter semantics of the "
              "write statics are not reconstructed (re-notes: WriteElemValue/Unforce/ProgramValidate share "
              "an incremental-link thunk RVA); invocation opens only after per-method signature "
              "verification; no value was written\"}";
    WriteResult(response, id, result);
}

void HandleWriteElements(const char* json, const char* id, std::string& response)
{
    HandleWriteReservation(json, id, response, "write_elements",
                           "?WriteElemValue@CCommunicateEth@@",
                           g_config.allowWrite,
                           "gate closed: allowWrite=false in the bridge config (the C# gate is closed by default too)");
}

void HandleForce(const char* json, const char* id, std::string& response)
{
    HandleWriteReservation(json, id, response, "force",
                           "?Force@CCommunicateEth@@",
                           g_config.allowForce,
                           "Unsupported: force is disabled by the bridge gate (allowForce=false, default); "
                           "opening requires an explicit operator decision plus S4 approval gating plus "
                           "per-method signature verification");
}

void HandleDownload(const char*, const char* id, std::string& response)
{
    if (!g_config.allowDownload)
    {
        WriteError(response, id, BRIDGE_ERR_GATE_CLOSED,
                   "Unsupported: download is disabled by the bridge gate (allowDownload=false, default); "
                   "opening requires an explicit operator decision plus S4 approval gating plus a wired "
                   "download payload workflow (D1 protocol layer)");
        return;
    }
    if (!VendorComm::HasVendorModule())
    {
        WriteError(response, id, BRIDGE_ERR_VENDOR_MODULEAbsent,
                   "CommunicateEthernet.dll is not loaded in this process");
        return;
    }
    if (!VendorComm::IsLoopbackTargetSet())
    {
        WriteError(response, id, BRIDGE_ERR_LOOPBACK_REQUIRED,
                   "no loopback target configured; call set_target with a 127.x.x.x address first");
        return;
    }
    // G2 empirical verdict (docs/inovance-autoshop-download-closedloop.md §3):
    // the vendor Download export IS a real body (0x3040, accepted rc=0 with
    // mode=0/level=0) but a STANDALONE invocation enqueues the command and
    // emits ZERO file-transfer frames (sentinel file on the daemon unchanged
    // after a 25 s hold) — the async comm worker needs the AutoShop UI session
    // prerequisites (F2 §15.3 pattern). The proven closed loop for the
    // loopback simulator is the D1 protocol layer instead:
    //   login -> type2 session "project\project.hcpp" -> type5 chunks -> 0x0F
    //   -> load trigger 0xFF000010 (simulator stub answers 0x82)
    //   -> RUN 0xFF000020=1 (acked; Modbus D100 0->4660, registers live).
    // This bridge therefore stays resolved-not-invoked for Download and never
    // calls the export, so a gated download can never pretend success.
    WriteError(response, id, BRIDGE_ERR_UNSUPPORTED,
               "Unsupported: the in-process vendor Download export was proven non-transferring "
               "standalone (G2 harness: rc=0 accepted, zero wire frames); the loopback closed loop "
               "runs on the D1 12939 protocol layer (file transfer + 0xFF000010 load + 0xFF000020 RUN)");
}

void HandleCompile(const char*, const char* id, std::string& response)
{
    if (!g_config.hasCompileEntry)
    {
        WriteError(response, id, BRIDGE_ERR_UNSUPPORTED,
                   "Unsupported: the compile surface is disabled (bridge config compileEnabled=false "
                   "or no compile entry point configured)");
        return;
    }
    VendorComm::CompileTargets targets;
    targets.rootRva = g_config.compileRootRva;
    targets.memberRva = g_config.compileMemberRva;
    targets.projectGateRva = g_config.compileProjectGateRva;
    targets.gateOwnerRva = g_config.compileGateOwnerRva;
    targets.entryRva = g_config.compileEntryRva;
    VendorComm::CompileCallResult result;
    long rc = VendorComm::InvokeCompile(targets, &result);
    if (rc != 0 || !result.submitted)
    {
        char message[512];
        snprintf(message, sizeof(message), "compile refused/failed (rc=%ld): %s", rc,
                 result.reason[0] != '\0' ? result.reason : "no detail");
        WriteError(response, id, rc != 0 ? rc : BRIDGE_ERR_VENDOR_CALL_FAILED, message);
        return;
    }
    char entry[64];
    snprintf(entry, sizeof(entry), "0x%08lx", g_config.compileEntryRva);
    char addresses[256];
    snprintf(addresses, sizeof(addresses),
             "root resolved to 0x%08lx, compile-all manager to 0x%08lx",
             result.rootAddress, result.cmpMgrAddress);
    std::string resultJson = "{\"compile\":\"submitted\",\"compileEntryRva\":";
    AppendJsonString(resultJson, entry);
    resultJson += ",\"note\":\"compile-all submitted asynchronously through the F1-verified in-process "
                  "chain (root 0x1581A70 -> cmpMgr 0x1581CA0 -> entry 0x15A2EC0, project gate 0x161B620 "
                  "checked first); ";
    resultJson += addresses;
    resultJson += "; artifacts appear under <projectDir>\\\\Compile; offline behaviour, "
                  "no PLC action\"}";
    WriteResult(response, id, resultJson);
}

void HandleLineInto(const char* line, char* outResponse, size_t outResponseBytes)
{
    char id[64] = "0";
    char method[64] = {0};
    JsonExtractString(line, "id", id, sizeof(id));
    if (!JsonExtractString(line, "method", method, sizeof(method)))
    {
        std::string response;
        WriteError(response, id, BRIDGE_ERR_MISSING_METHOD, "missing method");
        snprintf(outResponse, outResponseBytes, "%s", response.c_str());
        return;
    }

    std::string response;
    if (std::strcmp(method, "handshake") == 0)
    {
        HandleHandshake(line, id, response);
    }
    else if (std::strcmp(method, "capabilities") == 0)
    {
        HandleCapabilities(line, id, response);
    }
    else if (std::strcmp(method, "set_target") == 0)
    {
        HandleSetTarget(line, id, response);
    }
    else if (std::strcmp(method, "read_status") == 0)
    {
        HandleReadStatus(line, id, response);
    }
    else if (std::strcmp(method, "read_elements") == 0)
    {
        HandleReadElements(line, id, response);
    }
    else if (std::strcmp(method, "write_elements") == 0)
    {
        HandleWriteElements(line, id, response);
    }
    else if (std::strcmp(method, "force") == 0)
    {
        HandleForce(line, id, response);
    }
    else if (std::strcmp(method, "download") == 0)
    {
        HandleDownload(line, id, response);
    }
    else if (std::strcmp(method, "compile") == 0)
    {
        HandleCompile(line, id, response);
    }
    else if (std::strcmp(method, "shutdown") == 0)
    {
        std::string result = "{\"stopping\":true}";
        WriteResult(response, id, result);
        if (g_stopEvent != nullptr)
        {
            ::SetEvent(g_stopEvent);
        }
        ::InterlockedExchange(&g_stopRequested, 1);
    }
    else
    {
        WriteError(response, id, BRIDGE_ERR_METHOD_NOT_FOUND, "method not found");
    }

    snprintf(outResponse, outResponseBytes, "%s", response.c_str());
}

// SEH boundary. Must stay free of C++ objects with destructors (MSVC C2712):
// the handler delegates all string building to WriteSehError.
void WriteSehError(const char* line, char* outResponse, size_t outResponseBytes)
{
    char id[64] = "0";
    JsonExtractString(line, "id", id, sizeof(id));
    std::string response;
    char message[256];
    snprintf(message, sizeof(message),
             "bridge caught a structured exception while handling the request; the host process is kept alive "
             "(last compile-chain step: %d, root=0x%08lx, gateOwner=0x%08lx)",
             VendorComm::LastCompileStep(), VendorComm::LastRootPtr(), VendorComm::LastOwnerPtr());
    WriteError(response, id, BRIDGE_ERR_SEH, message);
    snprintf(outResponse, outResponseBytes, "%s", response.c_str());
}

void HandleLineSafeImpl(const char* line, char* outResponse, size_t outResponseBytes)
{
    __try
    {
        HandleLineInto(line, outResponse, outResponseBytes);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        WriteSehError(line, outResponse, outResponseBytes);
    }
}

// Returns false when the request asked the bridge to shut down.
bool HandleLineSafe(const char* line, char* outResponse, size_t outResponseBytes)
{
    HandleLineSafeImpl(line, outResponse, outResponseBytes);
    return ::InterlockedCompareExchange(&g_stopRequested, 0, 0) == 0;
}

// ---------------------------------------------------------------------------
// Pipe plumbing
// ---------------------------------------------------------------------------

HANDLE CreateBridgePipe(const char* pipeNameUtf8)
{
    wchar_t wideName[256];
    wchar_t pipeNameWide[240];
    MultiByteToWideChar(CP_UTF8, 0, pipeNameUtf8, -1, pipeNameWide,
                        static_cast<int>(sizeof(pipeNameWide) / sizeof(wchar_t)) - 1);
    pipeNameWide[sizeof(pipeNameWide) / sizeof(wchar_t) - 1] = L'\0';
    _snwprintf_s(wideName, sizeof(wideName) / sizeof(wchar_t), _TRUNCATE,
                 L"%s%s", kPipePrefix, pipeNameWide);
    wideName[255] = L'\0';

    // PIPE_REJECT_REMOTE_CLIENTS: the pipe only accepts local connections.
    // The default security descriptor limits access to the hosting user.
    // The pipe is created fully overlapped; blocking-style waits are done
    // through IoSync below (an OVERLAPPED on a non-overlapped pipe would be
    // undefined behavior).
    return ::CreateNamedPipeW(
        wideName,
        PIPE_ACCESS_DUPLEX | FILE_FLAG_FIRST_PIPE_INSTANCE | FILE_FLAG_OVERLAPPED,
        PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
        1, 65536, 65536, 0, nullptr);
}

// Overlapped read/write followed by a bounded synchronous wait. Returns true
// when the transfer completed; false on failure or timeout (clientOpen only
// meaningful for reads on the manual pipe).
bool IoSync(HANDLE pipe, void* buffer, DWORD bytes, bool write,
            DWORD* transferred, HANDLE event, DWORD timeoutMs)
{
    OVERLAPPED overlapped;
    ZeroMemory(&overlapped, sizeof(overlapped));
    overlapped.hEvent = event;
    ::ResetEvent(event);
    BOOL started = write
        ? ::WriteFile(pipe, buffer, bytes, nullptr, &overlapped)
        : ::ReadFile(pipe, buffer, bytes, nullptr, &overlapped);
    if (!started && ::GetLastError() != ERROR_IO_PENDING)
    {
        return false;
    }
    DWORD waited = ::WaitForSingleObject(event, timeoutMs);
    if (waited != WAIT_OBJECT_0)
    {
        ::CancelIo(pipe);
        // Drain the cancelled I/O result so the event/handle stay consistent.
        DWORD ignored = 0;
        ::GetOverlappedResult(pipe, &overlapped, &ignored, FALSE);
        return false;
    }
    return ::GetOverlappedResult(pipe, &overlapped, transferred, FALSE) != FALSE;
}

// Returns true when at least one line was handled; sets *clientOpen=false
// when the client disconnected. ERROR_PIPE_LISTENING (connect still pending)
// is reported as idle with the pipe kept listening.
bool ReadAvailableInto(std::string& pending, std::string& consumed, bool* clientOpen)
{
    *clientOpen = true;
    char buffer[4096];
    DWORD available = 0;
    if (!::PeekNamedPipe(g_manualPipe, nullptr, 0, nullptr, &available, nullptr))
    {
        DWORD err = ::GetLastError();
        if (err == ERROR_PIPE_LISTENING)
        {
            return false; // connect not acknowledged yet; keep listening
        }
        *clientOpen = false;
        return false;
    }
    if (available == 0)
    {
        return false;
    }
    DWORD toRead = available < sizeof(buffer) ? available : sizeof(buffer);
    DWORD read = 0;
    if (!IoSync(g_manualPipe, buffer, toRead, false, &read, g_manualEvent, 2000) || read == 0)
    {
        *clientOpen = false;
        return false;
    }
    pending.append(buffer, read);
    size_t newline;
    bool handledAny = false;
    while ((newline = pending.find('\n')) != std::string::npos)
    {
        std::string line = pending.substr(0, newline);
        pending.erase(0, newline + 1);
        while (!line.empty() && (line.back() == '\r' || line.back() == ' '))
        {
            line.pop_back();
        }
        if (line.empty())
        {
            continue;
        }
        char response[8192];
        HandleLineSafe(line.c_str(), response, sizeof(response));
        consumed += response;
        consumed += "\n";
        handledAny = true;
    }
    return handledAny;
}

DWORD WINAPI ServerThread(LPVOID)
{
    while (g_stopEvent != nullptr && ::WaitForSingleObject(g_stopEvent, 0) == WAIT_TIMEOUT)
    {
        HANDLE pipe = CreateBridgePipe(g_config.pipeName);
        if (pipe == INVALID_HANDLE_VALUE)
        {
            ::Sleep(200);
            continue;
        }
        BOOL connected = ::ConnectNamedPipe(pipe, nullptr) || ::GetLastError() == ERROR_PIPE_CONNECTED;
        if (connected)
        {
        HANDLE localEvent = ::CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (localEvent == nullptr)
        {
            ::DisconnectNamedPipe(pipe);
            ::CloseHandle(pipe);
            continue;
        }
        std::string pending;
        bool clientOpen = true;
        while (clientOpen && g_stopEvent != nullptr &&
               ::WaitForSingleObject(g_stopEvent, 0) == WAIT_TIMEOUT)
        {
            char buffer[4096];
            DWORD read = 0;
            if (!IoSync(pipe, buffer, sizeof(buffer), false, &read, localEvent, INFINITE) || read == 0)
            {
                break;
            }
            pending.append(buffer, read);
            size_t newline;
            while ((newline = pending.find('\n')) != std::string::npos)
            {
                std::string line = pending.substr(0, newline);
                pending.erase(0, newline + 1);
                while (!line.empty() && (line.back() == '\r' || line.back() == ' '))
                {
                    line.pop_back();
                }
                if (line.empty())
                {
                    continue;
                }
                char response[8192];
                HandleLineSafe(line.c_str(), response, sizeof(response));
                std::string out = response;
                out += "\n";
                DWORD written = 0;
                IoSync(pipe, &out[0], static_cast<DWORD>(out.size()), true,
                       &written, localEvent, INFINITE);
                ::FlushFileBuffers(pipe);
            }
        }
        ::CloseHandle(localEvent);
        }
        ::DisconnectNamedPipe(pipe);
        ::CloseHandle(pipe);
    }
    ::InterlockedExchange(&g_running, 0);
    return 0;
}

} // namespace

// ---------------------------------------------------------------------------
// BridgePipeServer
// ---------------------------------------------------------------------------

bool BridgePipeServer::Start(const BridgeConfig& config)
{
    if (g_running != 0)
    {
        return true;
    }
    g_config = config;
    if (g_stopEvent == nullptr)
    {
        g_stopEvent = ::CreateEventW(nullptr, TRUE, FALSE, nullptr);
    }
    else
    {
        ::ResetEvent(g_stopEvent);
    }
    if (g_stopEvent == nullptr)
    {
        return false;
    }
    ::InterlockedExchange(&g_stopRequested, 0);
    ::InterlockedExchange(&g_running, 1);
    g_thread = ::CreateThread(nullptr, 0, ServerThread, nullptr, 0, nullptr);
    if (g_thread == nullptr)
    {
        ::InterlockedExchange(&g_running, 0);
        return false;
    }
    return true;
}

bool BridgePipeServer::IsRunning()
{
    return g_running != 0;
}

bool BridgePipeServer::SetManualPipeName(const char* name)
{
    if (g_running != 0)
    {
        return false;
    }
    EnsureConfigInitialized();
    g_manualPipeNameUtf8 = name != nullptr && name[0] != '\0' ? name : BRIDGE_DEFAULT_PIPE_NAME;
    return true;
}

int BridgePipeServer::Pump(int timeoutMs)
{
    if (g_running != 0)
    {
        return -2; // threaded mode owns the pipe
    }
    EnsureConfigInitialized();
    if (g_manualPipeNameUtf8.empty())
    {
        g_manualPipeNameUtf8 = BRIDGE_DEFAULT_PIPE_NAME;
    }

    if (g_manualPipe == nullptr)
    {
        g_manualPipe = CreateBridgePipe(g_manualPipeNameUtf8.c_str());
        if (g_manualPipe == INVALID_HANDLE_VALUE || g_manualPipe == nullptr)
        {
            g_manualPipe = nullptr;
            return -1;
        }
        if (g_manualEvent == nullptr)
        {
            g_manualEvent = ::CreateEventW(nullptr, TRUE, FALSE, nullptr);
            if (g_manualEvent == nullptr)
            {
                ::CloseHandle(g_manualPipe);
                g_manualPipe = nullptr;
                return -1;
            }
        }
        g_manualConnected = false;
    }

    if (!g_manualConnected)
    {
        // (Re-)issue the connect so a client that connected after a previous
        // timeout is still acknowledged. The documented recoverable pattern is
        // ConnectNamedPipe -> timeout -> CancelIo -> ConnectNamedPipe again.
        OVERLAPPED overlapped;
        ZeroMemory(&overlapped, sizeof(overlapped));
        overlapped.hEvent = ::CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (overlapped.hEvent == nullptr)
        {
            return -1;
        }
        BOOL connected = ::ConnectNamedPipe(g_manualPipe, &overlapped);
        if (!connected)
        {
            DWORD err = ::GetLastError();
            if (err == ERROR_IO_PENDING)
            {
                DWORD waited = ::WaitForSingleObject(overlapped.hEvent, timeoutMs < 0 ? INFINITE : timeoutMs);
                if (waited != WAIT_OBJECT_0)
                {
                    ::CancelIo(g_manualPipe);
                    ::CloseHandle(overlapped.hEvent);
                    return 0; // keep the pipe listening for the next pump
                }
            }
            else if (err != ERROR_PIPE_CONNECTED)
            {
                ::CloseHandle(overlapped.hEvent);
                return -1;
            }
        }
        ::CloseHandle(overlapped.hEvent);
        g_manualConnected = true;
        return 0;
    }

    std::string consumed;
    bool clientOpen = true;
    bool handled = ReadAvailableInto(g_manualPending, consumed, &clientOpen);
    if (!clientOpen)
    {
        ::DisconnectNamedPipe(g_manualPipe);
        ::CloseHandle(g_manualPipe);
        g_manualPipe = nullptr;
        g_manualConnected = false;
        g_manualPending.clear();
        return 0;
    }
    if (!consumed.empty())
    {
        DWORD written = 0;
        IoSync(g_manualPipe, &consumed[0], static_cast<DWORD>(consumed.size()), true,
               &written, g_manualEvent, 2000);
        ::FlushFileBuffers(g_manualPipe);
    }
    if (::InterlockedCompareExchange(&g_stopRequested, 0, 0) != 0)
    {
        // shutdown was handled: tear the manual pipe down deterministically
        ::DisconnectNamedPipe(g_manualPipe);
        ::CloseHandle(g_manualPipe);
        g_manualPipe = nullptr;
        g_manualConnected = false;
        g_manualPending.clear();
    }
    return handled ? 1 : 0;
}

void BridgePipeServer::Stop()
{
    if (g_stopEvent != nullptr)
    {
        ::SetEvent(g_stopEvent);
    }
    if (g_thread != nullptr)
    {
        ::WaitForSingleObject(g_thread, 3000);
        ::CloseHandle(g_thread);
        g_thread = nullptr;
    }
    ::InterlockedExchange(&g_running, 0);
    if (g_manualPipe != nullptr)
    {
        ::DisconnectNamedPipe(g_manualPipe);
        ::CloseHandle(g_manualPipe);
        g_manualPipe = nullptr;
    }
    g_manualConnected = false;
    if (g_manualEvent != nullptr)
    {
        ::CloseHandle(g_manualEvent);
        g_manualEvent = nullptr;
    }
    g_manualPending.clear();
    g_manualPipeNameUtf8.clear();
    VendorComm::Unload();
}

} // namespace AutoShopInjectBridge
