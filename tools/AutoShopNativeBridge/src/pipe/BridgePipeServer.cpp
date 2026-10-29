// BridgePipeServer.cpp
//
// Named-pipe JSON-line server (M4 native side). Single client at a time;
// one request/response per line. Delegates read operations to the M3
// communication-stack shim (CommStackShim.dll) when it can be loaded from the
// plugin directory; otherwise read_status/read_elements answer with a
// structured "unavailable" error instead of failing silently.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstdio>
#include <cstring>
#include <string>

#include "BridgePipeServer.h"

namespace PlcMcpBridge
{
namespace
{

const wchar_t* kPipeName = L"\\\\.\\pipe\\PlcMcpAutoShopBridge";
const char* kProtocolVersion = "1.0";
const char* kWorkerName = "PlcMcpAutoShopNativeBridge";

HANDLE g_thread = nullptr;
HANDLE g_stopEvent = nullptr;
volatile LONG g_running = 0;

// ---------------------------------------------------------------------------
// Comm-stack shim imports (loaded lazily, never required).
// ---------------------------------------------------------------------------
typedef long (*FnGetExecMode)(int* execMode);
typedef long (*FnGetGcmState)(int* gcmState);
typedef long (*FnGetPlcLable)(void* lable, int commType);
typedef long (*FnReadElements)(const char* addressesCsv, int* outCount,
                               char* outValues, int outValuesBytes);

HMODULE g_shim = nullptr;
FnGetExecMode    g_getExecMode = nullptr;
FnGetGcmState    g_getGcmState = nullptr;
FnGetPlcLable    g_getPlcLable = nullptr;
FnReadElements   g_readElements = nullptr;

void LoadShim()
{
    if (g_shim != nullptr)
    {
        return;
    }
    // The shim statically imports CommunicateEthernet.dll / InterfaceMgr.dll
    // from the AutoShop installation, so that directory must be searchable
    // before the shim is loaded. PLCMCP_BRIDGE_INSTALL_DIR overrides the
    // default for hosts with a non-standard installation path.
    char installDir[MAX_PATH] = "D:\\Inovance Control\\AutoShop";
    DWORD n = GetEnvironmentVariableA("PLCMCP_BRIDGE_INSTALL_DIR", installDir, MAX_PATH);
    if (n > 0 && n < MAX_PATH)
    {
        installDir[n] = '\0';
    }
    SetDllDirectoryA(installDir);

    // The shim is expected next to the plugin DLL (candidate plugin directory).
    char dllPath[MAX_PATH] = {0};
    HMODULE self = nullptr;
    GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                           GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                       reinterpret_cast<LPCSTR>(&LoadShim), &self);
    if (self != nullptr)
    {
        GetModuleFileNameA(self, dllPath, MAX_PATH);
        std::string path(dllPath);
        size_t slash = path.find_last_of("\\/");
        if (slash != std::string::npos)
        {
            path = path.substr(0, slash + 1) + "CommStackShim.dll";
            g_shim = LoadLibraryA(path.c_str());
        }
    }
    if (g_shim == nullptr)
    {
        return;
    }
    g_getExecMode = reinterpret_cast<FnGetExecMode>(GetProcAddress(g_shim, "PlcMcpComm_GetExecMode"));
    g_getGcmState = reinterpret_cast<FnGetGcmState>(GetProcAddress(g_shim, "PlcMcpComm_GetGcmState"));
    g_getPlcLable = reinterpret_cast<FnGetPlcLable>(GetProcAddress(g_shim, "PlcMcpComm_GetPlcLable"));
    g_readElements = reinterpret_cast<FnReadElements>(GetProcAddress(g_shim, "PlcMcpComm_ReadElements"));
}

// ---------------------------------------------------------------------------
// Tiny JSON helpers (requests are small and trusted-shape; the C# side is the
// authority for parsing).
// ---------------------------------------------------------------------------

bool ExtractStringField(const char* json, const char* field, char* out, size_t outSize)
{
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

bool ExtractMethod(const char* json, char* out, size_t outSize)
{
    return ExtractStringField(json, "method", out, outSize);
}

bool ExtractId(const char* json, char* out, size_t outSize)
{
    return ExtractStringField(json, "id", out, outSize);
}

void WriteError(std::string& response, const char* id, int code, const char* message)
{
    char buffer[512];
    snprintf(buffer, sizeof(buffer),
             "{\"id\":\"%s\",\"error\":{\"code\":%d,\"message\":\"%s\"}}",
             id, code, message);
    response = buffer;
}

void WriteResult(std::string& response, const char* id, const char* resultJson)
{
    response = "{\"id\":\"";
    response += id;
    response += "\",\"result\":";
    response += resultJson;
    response += "}";
}

// ---------------------------------------------------------------------------
// Request handlers.
// ---------------------------------------------------------------------------

void HandleHandshake(const char* id, std::string& response)
{
    char result[512];
    snprintf(result, sizeof(result),
             "{\"workerName\":\"%s\",\"workerVersion\":\"1.0.0\","
             "\"protocolVersion\":\"%s\",\"vendor\":\"Inovance\",\"bitness\":\"x86\","
             "\"runtimeEnvironment\":\"in-process plugin\","
             "\"capabilities\":[\"environment_inspection\",\"comm_stack_read_only\"],"
             "\"disclaimer\":\"reverse-engineered; no vendor endorsement; version-locked 4.12.0.2\"}",
             kWorkerName, kProtocolVersion);
    WriteResult(response, id, result);
}

void HandleCapabilities(const char* id, std::string& response)
{
    const char* result =
        "{\"comm_stack_read_only\":\"Experimental: static imports into CommunicateEthernet.dll; no PLC verified\","
        "\"variable_read\":\"Unsupported: no verified publisher/connection\","
        "\"compile\":\"Unsupported\",\"download\":\"Unsupported: physical writes forbidden\","
        "\"force\":\"Unsupported: forcing forbidden\",\"run_stop\":\"Unsupported\"}";
    WriteResult(response, id, result);
}

void HandleReadStatus(const char* id, std::string& response)
{
    LoadShim();
    if (g_getExecMode == nullptr)
    {
        WriteError(response, id, -32001, "CommStackShim.dll not available in plugin directory");
        return;
    }
    int execMode = -1;
    int gcmState = -1;
    long rc1 = g_getExecMode(&execMode);
    long rc2 = g_getGcmState != nullptr ? g_getGcmState(&gcmState) : -1;
    char result[256];
    snprintf(result, sizeof(result),
             "{\"execMode\":%d,\"execModeCallResult\":%ld,\"gcmState\":%d,\"gcmStateCallResult\":%ld,"
             "\"evidence\":\"call-return codes only; no PLC connection verified\"}",
             execMode, rc1, gcmState, rc2);
    WriteResult(response, id, result);
}

void HandleReadElements(const char* json, const char* id, std::string& response)
{
    LoadShim();
    if (g_readElements == nullptr)
    {
        WriteError(response, id, -32001, "CommStackShim.dll not available in plugin directory");
        return;
    }
    char addresses[512] = {0};
    if (!ExtractStringField(json, "addresses", addresses, sizeof(addresses)))
    {
        WriteError(response, id, -32602, "missing 'addresses' parameter");
        return;
    }
    char values[2048] = {0};
    int count = 0;
    long rc = g_readElements(addresses, &count, values, sizeof(values));
    if (rc != 0)
    {
        char message[256];
        snprintf(message, sizeof(message),
                 "comm stack read failed (rc=%ld); no PLC connection is verified on this host", rc);
        WriteError(response, id, -32002, message);
        return;
    }
    std::string result = "{\"count\":";
    char buf[32];
    snprintf(buf, sizeof(buf), "%d", count);
    result += buf;
    result += ",\"values\":\"";
    result += values;
    result += "\"}";
    WriteResult(response, id, result.c_str());
}

void HandleLine(const char* line, std::string& response)
{
    char id[64] = "0";
    char method[64] = {0};
    ExtractId(line, id, sizeof(id));
    if (!ExtractMethod(line, method, sizeof(method)))
    {
        WriteError(response, id, -32600, "missing method");
        return;
    }
    if (std::strcmp(method, "handshake") == 0)
    {
        HandleHandshake(id, response);
    }
    else if (std::strcmp(method, "capabilities") == 0)
    {
        HandleCapabilities(id, response);
    }
    else if (std::strcmp(method, "read_status") == 0)
    {
        HandleReadStatus(id, response);
    }
    else if (std::strcmp(method, "read_elements") == 0)
    {
        HandleReadElements(line, id, response);
    }
    else if (std::strcmp(method, "shutdown") == 0)
    {
        WriteResult(response, id, "{\"stopping\":true}");
        SetEvent(g_stopEvent);
    }
    else
    {
        WriteError(response, id, -32601, "method not found");
    }
}

DWORD WINAPI PipeThread(LPVOID)
{
    while (WaitForSingleObject(g_stopEvent, 0) == WAIT_TIMEOUT)
    {
        HANDLE pipe = CreateNamedPipeW(
            kPipeName,
            PIPE_ACCESS_DUPLEX,
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT,
            1, 65536, 65536, 0, nullptr);
        if (pipe == INVALID_HANDLE_VALUE)
        {
            Sleep(200);
            continue;
        }
        BOOL connected = ConnectNamedPipe(pipe, nullptr) ||
                         GetLastError() == ERROR_PIPE_CONNECTED;
        if (connected)
        {
            char buffer[4096];
            std::string pending;
            bool clientOpen = true;
            while (clientOpen && WaitForSingleObject(g_stopEvent, 0) == WAIT_TIMEOUT)
            {
                DWORD read = 0;
                BOOL ok = ReadFile(pipe, buffer, sizeof(buffer), &read, nullptr);
                if (!ok || read == 0)
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
                    std::string response;
                    HandleLine(line.c_str(), response);
                    response += "\n";
                    DWORD written = 0;
                    WriteFile(pipe, response.data(), static_cast<DWORD>(response.size()),
                              &written, nullptr);
                    FlushFileBuffers(pipe);
                    if (line.find("\"shutdown\"") != std::string::npos)
                    {
                        clientOpen = false;
                        break;
                    }
                }
            }
        }
        DisconnectNamedPipe(pipe);
        CloseHandle(pipe);
    }
    InterlockedExchange(&g_running, 0);
    return 0;
}

} // namespace

bool BridgePipeServer::Start()
{
    if (g_running != 0)
    {
        return true;
    }
    if (g_stopEvent == nullptr)
    {
        g_stopEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    }
    else
    {
        ResetEvent(g_stopEvent);
    }
    if (g_stopEvent == nullptr)
    {
        return false;
    }
    InterlockedExchange(&g_running, 1);
    g_thread = CreateThread(nullptr, 0, PipeThread, nullptr, 0, nullptr);
    return g_thread != nullptr;
}

void BridgePipeServer::Stop()
{
    if (g_stopEvent != nullptr)
    {
        SetEvent(g_stopEvent);
    }
    if (g_thread != nullptr)
    {
        WaitForSingleObject(g_thread, 2000);
        CloseHandle(g_thread);
        g_thread = nullptr;
    }
    if (g_shim != nullptr)
    {
        FreeLibrary(g_shim);
        g_shim = nullptr;
        g_getExecMode = nullptr;
        g_getGcmState = nullptr;
        g_getPlcLable = nullptr;
        g_readElements = nullptr;
    }
}

bool BridgePipeServer::IsRunning()
{
    return g_running != 0;
}

} // namespace PlcMcpBridge
