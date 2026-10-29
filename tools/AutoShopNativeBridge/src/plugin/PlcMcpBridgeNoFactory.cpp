// PlcMcpBridgeNoFactory.cpp
//
// Negative-test DLL for M1: exports the legacy 7-export contract but NOT the
// CreatePlugin factory that the current Plugin.dll framework requires. Loading
// it through PluginManager::LoadLibiary must fail cleanly ("Failed to
// initialize plugin" on stderr, no crash, no marker-log entry from
// CreatePlugin).

#define WIN32_LEAN_AND_MEAN
#include <windows.h>

extern "C" {

__declspec(dllexport) const char* GetPlugInName() { return "PlcMcpNoFactory"; }
__declspec(dllexport) const char* GetPlugInType() { return "00000000-0000-0000-0000-000000000000"; }
__declspec(dllexport) int GetPlugInVersion() { return 0x100; }
__declspec(dllexport) const char* GetAuthor() { return "PLCmcp"; }
__declspec(dllexport) const char* GetCopyRight() { return "negative test"; }
__declspec(dllexport) int QueryInterface(const char*) { return 109; }
__declspec(dllexport) void* GetInterface(const char*) { return nullptr; }

} // extern "C"

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID reserved)
{
    (void)module;
    (void)reason;
    (void)reserved;
    return TRUE;
}
