// BridgeApi.h
//
// Public contract of the AutoShop inject bridge DLL (E2, phase three).
//
// The bridge is a plain x86 DLL that an in-house injector loads into a
// *stand-alone, self-started* AutoShop instance (CREATE_SUSPENDED ->
// CreateRemoteThread(LoadLibraryA) -> remote Bridge_Init -> ResumeThread).
// Inside the AutoShop process it serves a JSON-line named pipe and answers
// operation requests by calling the vendor communication stack that is already
// loaded in that process (CommunicateEthernet.dll / InterfaceMgr.dll,
// reverse-engineered signatures, see docs/inovance-autoshop-native-bridge.md).
//
// All interfaces are reverse-engineered; there is NO vendor endorsement.
// Version-locked to AutoShop 4.12.0.2 (x86). Honesty rules (docs/contracts.md):
// unimplemented = Unsupported, never faked.
//
// Exported flat C API (5 functions, undecorated cdecl on x86):
//
//   DWORD Bridge_Init(void* reserved)
//       Starts the pipe server thread. Configuration is read from
//       %TEMP%\plcmcp-handoff\inject-bridge\pending-<pid>.json (written by the
//       injector BEFORE ResumeThread; the file is consumed and deleted) or
//       falls back to the built-in defaults below. Returns 0 on success.
//       Designed to be invoked remotely by the injector (CreateRemoteThread on
//       base + export RVA); it is also usable in-process by the test harness.
//
//   DWORD Bridge_SetPipeName(const char* name)
//       Manual (harness) mode only: overrides the pipe name BEFORE the server
//       starts. Refused once the server thread is running. The injector path
//       never calls this; it configures the name through the pending file.
//
//   DWORD Bridge_Pump(int timeoutMs)
//       Manual (harness) mode only: performs one accept + handle cycle with
//       the given timeout. Returns 1 = handled a request, 0 = idle/timeout,
//       <0 = error (-2 when the threaded server from Bridge_Init is running).
//
//   void Bridge_Shutdown(void)
//       Stops the server thread, releases vendor module handles. Idempotent.
//
//   const char* Bridge_Version(void)
//       Static version/identity string.
//
// Wire protocol (one JSON object per line, UTF-8, LF terminated), mirrored by
// src/PlcMcp.Engineering/Workers/AutoShop/InjectBridge/InjectBridgeProtocol.cs.

#pragma once

#define BRIDGE_WORKER_NAME        "PlcMcpAutoShopInjectBridge"
#define BRIDGE_WORKER_VERSION     "1.1.0"
#define BRIDGE_PROTOCOL_VERSION   "1.0"
#define BRIDGE_DEFAULT_PIPE_NAME  "PlcMcpAutoShopInjectBridge"
#define BRIDGE_VERSION_STRING \
    "PlcMcp AutoShopInjectBridge " BRIDGE_WORKER_VERSION \
    " (x86, in-process, compile gate open (F1), in-process element read, " \
    "loopback-gated write reservation, AutoShop 4.12.0.2)"

// Default bridge-side configuration. The C# gate (InjectBridgeGateOptions) is
// the primary control; the DLL-side flags below are the second layer:
// - the compile gate is OPEN by default (F1, 2026-10-28: the compile-all chain
//   root 0x1581A70 -> member 0x1581CA0 -> entry 0x15A2EC0 is signature-verified
//   and was runtime-verified x3 by E3; compile = offline behaviour, no PLC
//   action, see BridgePipeServer.cpp HandleCompile);
// - the element-write pipeline is compiled in and reachable (loopback hard-
//   checked per call; vendor invocation is deterministically closed - the eth
//   channel WriteElemValue/Unforce exports are stubs returning 0xF390);
// - force and download are DISABLED at DLL level by default: they answer
//   Unsupported directly ("gate closed") and never touch the vendor exports.
#define BRIDGE_DEFAULT_ALLOW_WRITE     1
#define BRIDGE_DEFAULT_ALLOW_FORCE     0
#define BRIDGE_DEFAULT_ALLOW_DOWNLOAD  0

// Structured error codes (mirrored by InjectBridgeProtocol.ErrorCodes).
#define BRIDGE_ERR_MISSING_METHOD       (-32600)
#define BRIDGE_ERR_METHOD_NOT_FOUND     (-32601)
#define BRIDGE_ERR_INVALID_PARAMS       (-32602)
#define BRIDGE_ERR_VENDOR_MODULEAbsent  (-32001)  // vendor module/export unavailable
#define BRIDGE_ERR_VENDOR_CALL_FAILED   (-32002)  // vendor call returned an error
#define BRIDGE_ERR_LOOPBACK_REQUIRED    (-32003)  // non-loopback target refused / no loopback target set
#define BRIDGE_ERR_SEH                  (-32004)  // bridge exception (SEH), host kept alive
#define BRIDGE_ERR_GATE_CLOSED          (-32005)  // DLL-side gate closed (force/download default)
#define BRIDGE_ERR_UNSUPPORTED          (-32006)  // honest Unsupported (compile slot, download payload...)

// Loopback-only discipline: every vendor comm-stack call issued by this bridge
// goes to an explicitly configured loopback target (set_target with 127.x.x.x).
// The vendor default connection target (192.168.1.1:12939) is therefore never
// contacted by this bridge.
