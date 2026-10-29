// AutoShopInjectBridge.cpp
//
// DLL entry + the five flat C exports of the inject bridge. DllMain only
// records the instance handle and disables thread calls: all server work
// happens in Bridge_Init (invoked remotely by the injector AFTER LoadLibrary
// and BEFORE ResumeThread, so AutoShop starts with the bridge already
// serving) or in the manual Pump mode used by the test harness.
//
// Build: see build.cmd (VS2022 v143, x86, /MT). No third-party dependency.

#include "BridgeApi.h"
#include "BridgeInternal.h"
#include "BridgePipeServer.h"

using namespace AutoShopInjectBridge;

static HMODULE g_selfModule = nullptr;

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID /*reserved*/)
{
    switch (reason)
    {
    case DLL_PROCESS_ATTACH:
        g_selfModule = instance;
        ::DisableThreadLibraryCalls(instance);
        break;
    case DLL_PROCESS_DETACH:
        // Best effort only: Bridge_Shutdown is the explicit teardown path.
        break;
    }
    return TRUE;
}

extern "C" {

// Remote-thread entry point (LPTHREAD_START_ROUTINE shape): starts the pipe
// server with the pending-<pid>.json config written by the injector (or the
// built-in defaults). Returns 0 on success.
__declspec(dllexport) DWORD Bridge_Init(void* /*reserved*/)
{
    BridgeConfig config;
    LoadPendingConfig(&config);
    return BridgePipeServer::Start(config) ? 0u : 1u;
}

// Manual mode only: override the pipe name before the server starts.
__declspec(dllexport) DWORD Bridge_SetPipeName(const char* name)
{
    return BridgePipeServer::SetManualPipeName(name) ? 0u : 2u;
}

// Manual mode: one accept/handle cycle. 1 = handled a request, 0 = idle,
// <0 = error (-2 when the threaded server from Bridge_Init is running).
__declspec(dllexport) DWORD Bridge_Pump(int timeoutMs)
{
    if (BridgePipeServer::IsRunning())
    {
        return static_cast<DWORD>(-2);
    }
    return static_cast<DWORD>(BridgePipeServer::Pump(timeoutMs));
}

// Explicit teardown: stops the server thread and releases cached handles.
__declspec(dllexport) void Bridge_Shutdown(void)
{
    BridgePipeServer::Stop();
}

// Static identity string (never freed).
__declspec(dllexport) const char* Bridge_Version(void)
{
    return BRIDGE_VERSION_STRING;
}

} // extern "C"
