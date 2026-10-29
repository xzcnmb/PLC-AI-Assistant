// TestHost.cpp
//
// M1/M3/M4 verification harness. Replicates the exact plugin load sequence of
// InoVarSharedService.exe (CServiceMgr::InitLoggerLib / InitComLib, from
// disassembly of the service binary):
//
//   1. PluginManager::GetPluginMgr()
//   2. PluginManager::RegisterInterface(PluginInfo)      <- pre-registered
//   3. PluginManager::LoadLibiary("<PluginName>.dll")    <- bare name
//   4. PluginManager::GetPluginByEnum(enum)              <- fetch instance
//   5. exercise CPluginBase vtable slots (version/name/enum)
//
// plus a negative case (DLL without CreatePlugin), communication-stack probes
// through the M3 shim, and a named-pipe round trip (M4).
//
// This harness links the vendor Plugin.lib and runs entirely outside the
// vendor installation; it never touches AutoShop or InoVarSharedService.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstdarg>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>

#include "PlcMcpBridgeAbi.h"

#pragma comment(lib, "Plugin.lib")

using namespace PlcMcpBridge;

namespace {

const char* kHandoffDir = "C:\\Users\\18735\\AppData\\Local\\Temp\\plcmcp-handoff";
const char* kPluginFileName = "PlcMcpBridgePlugin.dll";
const char* kNoFactoryFileName = "PlcMcpBridgeNoFactory.dll";
FILE* g_report = nullptr;

void Report(const char* format, ...)
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

// LoadLibiary takes `const std::string&`; the vendor reads a VS2008 string
// image, so we always pass a hand-built image, never our own std::string
// object. Long file names use a heap image that is released right after the
// call (the vendor only reads the reference).
bool LoadPlugin(PluginManager* mgr, const char* fileName)
{
    V2008String path;
    void* heap = nullptr;
    if (!MakeV2008String(path, fileName, &heap))
    {
        Report("  cannot build VS2008 string image for: %s", fileName);
        return false;
    }
    bool loaded = mgr->LoadLibiary(*reinterpret_cast<const std::string*>(&path));
    if (heap != nullptr)
    {
        HeapFree(GetProcessHeap(), 0, heap);
    }
    return loaded;
}

void ExercisePluginObject(CPluginBase* plugin)
{
    if (plugin == nullptr)
    {
        Report("  GetPluginByEnum returned NULL");
        return;
    }
    unsigned version = plugin->vt->getVersion(plugin);
    int enumValue = plugin->vt->getEnum(plugin);
    V2008String name;
    ZeroMemory(&name, sizeof(name));
    plugin->vt->getName(plugin, &name);
    Report("  vtable slot2 GetPluginVersionEx -> 0x%08X (expected 0x%08X) %s",
           version, kBridgePluginVersion,
           version == kBridgePluginVersion ? "MATCH" : "MISMATCH");
    Report("  vtable slot4 GetPluginEnumEx     -> %d (expected %d) %s",
           enumValue, kBridgePluginEnum,
           enumValue == kBridgePluginEnum ? "MATCH" : "MISMATCH");
    Report("  vtable slot3 GetPluginName       -> \"%s\" (len=%u)",
           name.data.buf, name.mysize);
}

// ---------------------------------------------------------------------------
// Named-pipe client (M4 round trip).
// ---------------------------------------------------------------------------

void PipeRoundTrip()
{
    HANDLE pipe = CreateFileW(L"\\\\.\\pipe\\PlcMcpAutoShopBridge", GENERIC_READ | GENERIC_WRITE,
                              0, nullptr, OPEN_EXISTING, 0, nullptr);
    if (pipe == INVALID_HANDLE_VALUE)
    {
        Report("  pipe connect failed (error %lu)", GetLastError());
        return;
    }
    const char* requests[] = {
        "{\"id\":\"1\",\"method\":\"handshake\",\"params\":{\"client\":\"TestHost\"}}",
        "{\"id\":\"2\",\"method\":\"capabilities\"}",
        "{\"id\":\"3\",\"method\":\"read_status\"}",
        "{\"id\":\"4\",\"method\":\"read_elements\",\"params\":{\"addresses\":\"D100,D101\"}}",
        "{\"id\":\"5\",\"method\":\"shutdown\"}",
    };
    for (const char* request : requests)
    {
        std::string line(request);
        line += "\n";
        DWORD written = 0;
        WriteFile(pipe, line.data(), static_cast<DWORD>(line.size()), &written, nullptr);
        FlushFileBuffers(pipe);
        char buffer[4096];
        std::string response;
        for (;;)
        {
            DWORD read = 0;
            BOOL success = ReadFile(pipe, buffer, sizeof(buffer), &read, nullptr);
            if (!success || read == 0)
            {
                break;
            }
            response.append(buffer, read);
            if (response.find('\n') != std::string::npos)
            {
                break;
            }
        }
        while (!response.empty() && (response.back() == '\n' || response.back() == '\r'))
        {
            response.pop_back();
        }
        Report("  -> %s", request);
        Report("  <- %s", response.empty() ? "<no response>" : response.c_str());
    }
    CloseHandle(pipe);
}

// ---------------------------------------------------------------------------
// Communication-stack probes through the M3 shim.
// ---------------------------------------------------------------------------

typedef long (*FnGetExecMode)(int*);
typedef long (*FnGetGcmState)(int*);
typedef long (*FnGetPlcLable)(void*, int, int);
typedef long (*FnReadElements)(const char*, int*, char*, int);
typedef const char* (*FnVersion)();
typedef long (*FnSetTarget)(const char*, int);
typedef long (*FnIsOnline)();

// Live probe against a configured target (e.g. the local H5U simulator daemon
// on 127.0.0.1:12939). Read-only: stores a connection target and performs the
// vendor liveness probe plus the read-only status queries. No PLC writes.
void CommLiveProbe(const char* ip, int port)
{
    Report("[4] live comm probe target=%s:%d (read-only)", ip, port);
    SetDllDirectoryA("D:\\Inovance Control\\AutoShop");
    char exePath[MAX_PATH];
    GetModuleFileNameA(nullptr, exePath, MAX_PATH);
    std::string shimPath(exePath);
    size_t slash = shimPath.find_last_of("\\/");
    shimPath = shimPath.substr(0, slash + 1) + "CommStackShim.dll";
    HMODULE shim = LoadLibraryA(shimPath.c_str());
    if (shim == nullptr)
    {
        Report("  CommStackShim.dll not loadable (error %lu)", GetLastError());
        return;
    }
    FnVersion version = reinterpret_cast<FnVersion>(GetProcAddress(shim, "PlcMcpComm_Version"));
    FnSetTarget setTarget = reinterpret_cast<FnSetTarget>(GetProcAddress(shim, "PlcMcpComm_SetTarget"));
    FnIsOnline isOnline = reinterpret_cast<FnIsOnline>(GetProcAddress(shim, "PlcMcpComm_IsOnline"));
    FnGetExecMode getExecMode = reinterpret_cast<FnGetExecMode>(GetProcAddress(shim, "PlcMcpComm_GetExecMode"));
    FnGetGcmState getGcmState = reinterpret_cast<FnGetGcmState>(GetProcAddress(shim, "PlcMcpComm_GetGcmState"));
    FnGetPlcLable getPlcLable = reinterpret_cast<FnGetPlcLable>(GetProcAddress(shim, "PlcMcpComm_GetPlcLable"));
    if (version != nullptr)
    {
        Report("  shim: %s", version());
    }
    if (setTarget == nullptr || isOnline == nullptr)
    {
        Report("  SetTarget/IsOnline exports missing from the shim");
        return;
    }
    long setRc = setTarget(ip, port);
    Report("  PlcMcpComm_SetTarget(%s,%d) -> rc=%ld", ip, port, setRc);
    long online = isOnline();
    Report("  PlcMcpComm_IsOnline() -> %ld (%s)", online, online == 0 ? "offline" : "ONLINE");
    if (getExecMode != nullptr)
    {
        int mode = -1;
        long rc = getExecMode(&mode);
        Report("  PlcMcpComm_GetExecMode -> rc=%ld mode=%d", rc, mode);
    }
    if (getGcmState != nullptr)
    {
        int state = -1;
        long rc = getGcmState(&state);
        Report("  PlcMcpComm_GetGcmState -> rc=%ld state=%d", rc, state);
    }
    if (getPlcLable != nullptr)
    {
        unsigned char buffer[4096];
        ZeroMemory(buffer, sizeof(buffer));
        long rc = getPlcLable(buffer, sizeof(buffer), 0);
        Report("  PlcMcpComm_GetPlcLable -> rc=%ld", rc);
        // The _PLC_LABLE layout is unverified; print the first bytes for triage only.
        Report("  label[0..15]: %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X %02X",
               buffer[0], buffer[1], buffer[2], buffer[3], buffer[4], buffer[5],
               buffer[6], buffer[7], buffer[8], buffer[9], buffer[10], buffer[11],
               buffer[12], buffer[13], buffer[14], buffer[15]);
    }
}

void CommProbe()
{
    Report("[3] comm-stack probe (M3 shim)");
    SetDllDirectoryA("D:\\Inovance Control\\AutoShop");
    char exePath[MAX_PATH];
    GetModuleFileNameA(nullptr, exePath, MAX_PATH);
    std::string shimPath(exePath);
    size_t slash = shimPath.find_last_of("\\/");
    shimPath = shimPath.substr(0, slash + 1) + "CommStackShim.dll";
    HMODULE shim = LoadLibraryA(shimPath.c_str());
    if (shim == nullptr)
    {
        Report("  CommStackShim.dll not loadable (error %lu)", GetLastError());
        return;
    }
    FnVersion version = reinterpret_cast<FnVersion>(GetProcAddress(shim, "PlcMcpComm_Version"));
    FnGetExecMode getExecMode = reinterpret_cast<FnGetExecMode>(GetProcAddress(shim, "PlcMcpComm_GetExecMode"));
    FnGetGcmState getGcmState = reinterpret_cast<FnGetGcmState>(GetProcAddress(shim, "PlcMcpComm_GetGcmState"));
    FnGetPlcLable getPlcLable = reinterpret_cast<FnGetPlcLable>(GetProcAddress(shim, "PlcMcpComm_GetPlcLable"));
    FnReadElements readElements = reinterpret_cast<FnReadElements>(GetProcAddress(shim, "PlcMcpComm_ReadElements"));
    if (version != nullptr)
    {
        Report("  shim: %s", version());
    }
    if (getExecMode != nullptr)
    {
        int mode = -1;
        long rc = getExecMode(&mode);
        Report("  PlcMcpComm_GetExecMode -> rc=%ld mode=%d (no PLC connection expected)", rc, mode);
    }
    if (getGcmState != nullptr)
    {
        int state = -1;
        long rc = getGcmState(&state);
        Report("  PlcMcpComm_GetGcmState -> rc=%ld state=%d", rc, state);
    }
    if (getPlcLable != nullptr)
    {
        unsigned char buffer[4096];
        long rc = getPlcLable(buffer, sizeof(buffer), 0);
        Report("  PlcMcpComm_GetPlcLable -> rc=%ld (struct size unverified; 4KB zeroed buffer)", rc);
    }
    if (readElements != nullptr)
    {
        int count = 0;
        char values[256] = {0};
        long rc = readElements("D100,D101", &count, values, sizeof(values));
        Report("  PlcMcpComm_ReadElements -> rc=%ld count=%d detail=%s", rc, count, values);
    }
}

} // namespace

int main(int argc, char** argv)
{
    char reportPath[MAX_PATH];
    snprintf(reportPath, sizeof(reportPath), "%s\\m1-harness-report.txt", kHandoffDir);
    fopen_s(&g_report, reportPath, "w");

    Report("=== PlcMcp AutoShop native bridge test host ===");
    Report("host pid=%lu", GetCurrentProcessId());

    bool skipPlugin = false;
    bool skipComm = false;
    char targetIp[64] = {0};
    int targetPort = 0;
    for (int i = 1; i < argc; ++i)
    {
        if (std::strcmp(argv[i], "--comm-only") == 0)
        {
            skipPlugin = true;
        }
        else if (std::strcmp(argv[i], "--plugin-only") == 0)
        {
            skipComm = true;
        }
        else if (std::strncmp(argv[i], "--target=", 9) == 0)
        {
            const char* spec = argv[i] + 9;
            const char* colon = std::strchr(spec, ':');
            if (colon == nullptr)
            {
                Report("  bad --target (expected ip:port): %s", spec);
                return 2;
            }
            size_t ipLen = static_cast<size_t>(colon - spec);
            if (ipLen >= sizeof(targetIp))
            {
                ipLen = sizeof(targetIp) - 1;
            }
            std::memcpy(targetIp, spec, ipLen);
            targetIp[ipLen] = '\0';
            targetPort = std::atoi(colon + 1);
            skipPlugin = true;
        }
        else
        {
            Report("  unknown argument: %s", argv[i]);
            return 2;
        }
    }

    if (targetPort != 0)
    {
        CommLiveProbe(targetIp, targetPort);
        if (g_report != nullptr)
        {
            fclose(g_report);
            g_report = nullptr;
        }
        Report("=== done; report written to %s ===", reportPath);
        return 0;
    }

    if (!skipPlugin)
    {
        Report("[1] plugin contract (replicates InoVarSharedService load sequence)");
        PluginManager* mgr = PluginManager::GetPluginMgr();
        Report("  GetPluginMgr -> %s", mgr != nullptr ? "singleton" : "NULL");

        PluginInfo info;
        MakeV2008String(info.name, "PmNativeBridge");
        info.enumValue = kBridgePluginEnum;
        info.version = kBridgePluginVersion;
        mgr->RegisterInterface(info);
        Report("  RegisterInterface(name=\"%s\", enum=%d, version=0x%08X) done",
               info.name.data.buf, info.enumValue, info.version);

        Report("[1a] negative: DLL without CreatePlugin export");
        bool badLoaded = LoadPlugin(mgr, kNoFactoryFileName);
        Report("  LoadLibiary(\"%s\") -> %s (framework prints 'Failed to initialize plugin' to stderr)",
               kNoFactoryFileName, badLoaded ? "true" : "false");
        mgr->UnloadAllLibrary();

        Report("[1b] positive: load %s", kPluginFileName);
        bool loaded = LoadPlugin(mgr, kPluginFileName);
        Report("  LoadLibiary -> %s", loaded ? "true" : "false");
        CPluginBase* plugin = mgr->GetPluginByEnum(static_cast<PluginEnum>(kBridgePluginEnum));
        Report("  GetPluginByEnum(%d) -> %s", kBridgePluginEnum,
               plugin != nullptr ? "instance" : "NULL");
        ExercisePluginObject(plugin);

        Report("[1c] duplicate load (framework name->library map)");
        bool again = LoadPlugin(mgr, kPluginFileName);
        Report("  second LoadLibiary -> %s", again ? "true" : "false");

        Report("[2] M4 named pipe round trip (pipe server started by CreatePlugin)");
        SetEnvironmentVariableA("PLCMCP_BRIDGE_PIPE", "1");
        mgr->UnloadAllLibrary();
        bool pipeLoaded = LoadPlugin(mgr, kPluginFileName);
        Report("  plugin reload with PLCMCP_BRIDGE_PIPE=1 -> %s", pipeLoaded ? "true" : "false");
        Sleep(300);
        PipeRoundTrip();
    }

    if (!skipComm)
    {
        CommProbe();
    }

    if (g_report != nullptr)
    {
        fclose(g_report);
        g_report = nullptr;
    }
    Report("=== done; report written to %s ===", reportPath);
    return 0;
}
