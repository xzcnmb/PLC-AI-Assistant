// PlcMcpBridgePlugin.cpp
//
// Minimal x86 plugin DLL for the Inovance AutoShop plugin framework
// (AutoVarShared\Plugin.dll). Purpose: prove the reverse-engineered plugin
// contract end-to-end and carry the M4 named-pipe IPC server inside the host
// process.
//
// Exports:
//   * CreatePlugin                      -- current framework contract
//     (Plugin.dll resolves this name with GetProcAddress after LoadLibrary).
//   * GetPlugInName/GetPlugInType/GetPlugInVersion/GetInterface/
//     QueryInterface/GetAuthor/GetCopyRight -- legacy 2010-era contract
//     (ACmd.dll/APipe.dll reference implementation; not used by Plugin.dll).
//
// Marker logging: every lifecycle event appends one line to
// %TEMP%\plcmcp-handoff\m1-marker.log (and a per-event file) so the host,
// load directory and timing are provable from disk.
//
// Build: see build.cmd (VS2022 v143, x86).

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstdio>
#include <cstdarg>
#include <cstring>
#include <string>

#include "PlcMcpBridgeAbi.h"
#include "BridgePipeServer.h"

// Link against the vendor import library shipped with the product
// (AutoVarShared\Plugin.lib, VS2008 COFF import library).
#pragma comment(lib, "Plugin.lib")

using namespace PlcMcpBridge;

namespace {

const char* kMarkerDir = "C:\\Users\\18735\\AppData\\Local\\Temp\\plcmcp-handoff";
// SSO-safe (<= 14 chars): the framework owns and destroys strings it receives
// through the VS2008 string destructor, so plugin-visible names must never be
// heap images.
const char* kPluginName = "PmNativeBridge";
const char* kPluginGuid = "6f2a9c34-1d45-4e7a-9b8c-2d6e5f7a1b3c"; // our own type GUID

HANDLE g_logMutex = nullptr;
bool   g_pipeServerStarted = false;

void MarkerLog(const char* event, const char* detail)
{
    if (g_logMutex == nullptr)
    {
        g_logMutex = CreateMutexW(nullptr, FALSE, L"Local\\PlcMcpBridgeMarkerLog");
    }
    DWORD wait = WaitForSingleObject(g_logMutex, 2000);
    char path[MAX_PATH];
    snprintf(path, sizeof(path), "%s\\m1-marker.log", kMarkerDir);
    FILE* f = nullptr;
    if (fopen_s(&f, path, "a") == 0 && f != nullptr)
    {
        SYSTEMTIME st;
        GetLocalTime(&st);
        char exePath[MAX_PATH] = {0};
        GetModuleFileNameA(nullptr, exePath, MAX_PATH);
        char dllPath[MAX_PATH] = {0};
        HMODULE self = nullptr;
        GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                               GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                           reinterpret_cast<LPCSTR>(&MarkerLog), &self);
        if (self != nullptr)
        {
            GetModuleFileNameA(self, dllPath, MAX_PATH);
        }
        fprintf(f,
                "%04u-%02u-%02u %02u:%02u:%02u.%03u | pid=%lu | event=%s | dll=%s | host=%s | %s\n",
                st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond, st.wMilliseconds,
                GetCurrentProcessId(), event, dllPath, exePath, detail ? detail : "");
        fclose(f);
    }
    if (wait == WAIT_OBJECT_0)
    {
        ReleaseMutex(g_logMutex);
    }
}

// ---------------------------------------------------------------------------
// CPluginBase implementation with a manually declared vtable (slot order is
// fixed by the framework contract, see PlcMcpBridgeAbi.h).
// ---------------------------------------------------------------------------

CPluginBase g_bridgePlugin = {};

void BridgeDeletingDtor(void* self, int alsoDelete)
{
    MarkerLog("CreatePlugin.object.dtor", alsoDelete ? "deleting" : "complete");
    if (self != nullptr && alsoDelete)
    {
        HeapFree(GetProcessHeap(), 0, self);
    }
}

void BridgeCompleteDtor(void* self)
{
    MarkerLog("CreatePlugin.object.dtor", "complete-only");
}

unsigned BridgeGetVersion(const void* /*self*/)
{
    return kBridgePluginVersion;
}

void BridgeGetName(const void* /*self*/, V2008String* retBuf)
{
    if (retBuf != nullptr)
    {
        MakeV2008String(*retBuf, kPluginName);
    }
}

int BridgeGetEnum(const void* /*self*/)
{
    return kBridgePluginEnum;
}

const CPluginBaseVtable g_bridgeVtable = {
    &BridgeDeletingDtor,   // slot 0
    &BridgeCompleteDtor,   // slot 1
    &BridgeGetVersion,     // slot 2
    &BridgeGetName,        // slot 3
    &BridgeGetEnum         // slot 4
};

// ---------------------------------------------------------------------------
// Legacy 2010-era 7-export contract (reference: ACmd.dll / APipe.dll).
// ---------------------------------------------------------------------------

extern "C" {

__declspec(dllexport) const char* GetPlugInName()
{
    return kPluginName;
}

__declspec(dllexport) const char* GetPlugInType()
{
    return kPluginGuid;
}

__declspec(dllexport) int GetPlugInVersion()
{
    return static_cast<int>(kBridgePluginVersion);
}

__declspec(dllexport) const char* GetAuthor()
{
    return "PLCmcp";
}

__declspec(dllexport) const char* GetCopyRight()
{
    return "Reverse-engineered interop; no vendor endorsement; version-locked 4.12.0.2";
}

// Legacy QueryInterface(const char* guid): ACmd/APipe compare the argument
// against the plugin's own type GUID string and return a small integer.
__declspec(dllexport) int QueryInterface(const char* guid)
{
    if (guid != nullptr && std::strcmp(guid, kPluginGuid) == 0)
    {
        return 0; // match
    }
    return 109; // no match (ACmd.dll constant 0x6D)
}

// Legacy GetInterface(const char* guid): APipe.dll returns a pointer to its
// interface vtable when the GUID matches, NULL otherwise.
__declspec(dllexport) void* GetInterface(const char* guid)
{
    if (guid != nullptr && std::strcmp(guid, kPluginGuid) == 0)
    {
        return const_cast<CPluginBaseVtable*>(&g_bridgeVtable);
    }
    return nullptr;
}

// ---------------------------------------------------------------------------
// Current framework contract: CreatePlugin() -> CPluginBase*.
// ---------------------------------------------------------------------------

__declspec(dllexport) CPluginBase* CreatePlugin()
{
    MarkerLog("CreatePlugin.called", "returning CPluginBase with 5-slot vtable");

    g_bridgePlugin.vt = &g_bridgeVtable;
    g_bridgePlugin.reserved0 = 0;
    g_bridgePlugin.version = kBridgePluginVersion;
    g_bridgePlugin.enumValue = kBridgePluginEnum;

    // Optionally start the M4 named-pipe server inside this host process.
    // Disabled by default; enabled via PLCMCP_BRIDGE_PIPE=1 for experiments.
    if (!g_pipeServerStarted)
    {
        char env[8] = {0};
        DWORD n = GetEnvironmentVariableA("PLCMCP_BRIDGE_PIPE", env, sizeof(env));
        if (n > 0 && env[0] == '1')
        {
            g_pipeServerStarted = BridgePipeServer::Start();
            MarkerLog("pipe.server.start", g_pipeServerStarted ? "started" : "failed");
        }
    }

    return &g_bridgePlugin;
}

} // extern "C"

// ---------------------------------------------------------------------------
// DllMain: first marker, proves load directory + host + timing.
// ---------------------------------------------------------------------------

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID reserved)
{
    switch (reason)
    {
    case DLL_PROCESS_ATTACH:
        MarkerLog("DllMain.DLL_PROCESS_ATTACH", "plugin image loaded by host");
        DisableThreadLibraryCalls(module);
        break;
    case DLL_PROCESS_DETACH:
        MarkerLog("DllMain.DLL_PROCESS_DETACH",
                  reserved != nullptr ? "process exit" : "FreeLibrary");
        if (g_pipeServerStarted)
        {
            BridgePipeServer::Stop();
            g_pipeServerStarted = false;
        }
        break;
    default:
        break;
    }
    return TRUE;
}

} // namespace
