// BridgeTestHost.cpp
//
// Verification harness for the AutoShop inject bridge DLL (loads it
// in-process, exercises the manual Pump mode, and runs JSON-line round
// trips against a real named pipe). The vendor communication stack is NOT
// loaded here, so every comm-dependent request must answer with the honest
// structured error (vendor module absent) -- that is exactly what this
// harness asserts. No AutoShop installation, no PLC, no second process.
//
// Topology: the main thread pumps the bridge (manual mode); a secondary
// thread acts as the pipe client and drives the request set.
//
// Usage: BridgeTestHost.exe
// Report: %TEMP%\plcmcp-handoff\inject-bridge\harness-report.txt

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

namespace {

std::string g_report;
char g_pipeName[96];
std::vector<std::string> g_responses;
volatile LONG g_clientDone = 0;

void Report(const char* fmt, ...)
{
    char line[1024];
    va_list args;
    va_start(args, fmt);
    _vsnprintf_s(line, sizeof(line), _TRUNCATE, fmt, args);
    va_end(args);
    printf("%s\n", line);
    fflush(stdout);
    g_report += line;
    g_report += "\n";
}

// Exports of the bridge DLL (resolved after LoadLibrary).
typedef DWORD (* FnBridgeInit)(void*);
typedef DWORD (* FnBridgeSetPipeName)(const char*);
typedef DWORD (* FnBridgePump)(int);
typedef void  (* FnBridgeShutdown)(void);
typedef const char* (* FnBridgeVersion)(void);

FnBridgeInit g_init = nullptr;
FnBridgeSetPipeName g_setPipeName = nullptr;
FnBridgePump g_pump = nullptr;
FnBridgeShutdown g_shutdown = nullptr;
FnBridgeVersion g_version = nullptr;

std::wstring PipePathWide()
{
    std::string path = "\\\\.\\pipe\\" + std::string(g_pipeName);
    return std::wstring(path.begin(), path.end());
}

// Sends one line and reads one response line (raw byte I/O with manual
// framing, mirroring the C# client).
bool RoundTrip(HANDLE pipe, const std::string& request, std::string* response)
{
    std::string line = request + "\n";
    DWORD written = 0;
    if (!::WriteFile(pipe, line.data(), static_cast<DWORD>(line.size()), &written, nullptr))
    {
        return false;
    }
    response->clear();
    char buffer[8192];
    while (response->find('\n') == std::string::npos)
    {
        DWORD read = 0;
        if (!::ReadFile(pipe, buffer, sizeof(buffer), &read, nullptr) || read == 0)
        {
            return false;
        }
        response->append(buffer, read);
    }
    while (!response->empty() && (response->back() == '\n' || response->back() == '\r'))
    {
        response->pop_back();
    }
    return true;
}

bool Contains(const std::string& haystack, const char* needle)
{
    return haystack.find(needle) != std::string::npos;
}

int Check(bool ok, int& failures, const char* what, const std::string& detail = std::string())
{
    Report("  [%s] %s%s%s", ok ? "PASS" : "FAIL", what,
           detail.empty() ? "" : " -- ", detail.c_str());
    if (!ok)
    {
        failures++;
    }
    return ok ? 0 : 1;
}

// ---------------------------------------------------------------------------
// client thread: connects and drives the whole request set
// ---------------------------------------------------------------------------

struct Exchange
{
    const char* request;
    const char* expect;
    const char* label;
};

const Exchange kExchanges[] = {
    { "{\"id\":\"1\",\"method\":\"handshake\",\"params\":{\"client\":\"harness\"}}",
      "\"workerName\":\"PlcMcpAutoShopInjectBridge\"", "handshake identity" },
    { "{\"id\":\"2\",\"method\":\"capabilities\"}",
      "\"compile\":\"Experimental", "capabilities compile Experimental (F1 gate open)" },
    { "{\"id\":\"3\",\"method\":\"capabilities\"}",
      "\"force\":\"Unsupported: bridge gate closed", "capabilities force gate closed (default)" },
    { "{\"id\":\"4\",\"method\":\"capabilities\"}",
      "\"download\":\"Unsupported: bridge gate closed", "capabilities download gate closed (default)" },
    { "{\"id\":\"5\",\"method\":\"set_target\",\"params\":{\"ip\":\"8.8.8.8\",\"port\":12939}}",
      "-32003", "set_target 8.8.8.8 refused (non-loopback)" },
    { "{\"id\":\"6\",\"method\":\"read_status\"}",
      "-32001", "read_status answers vendor-module-absent outside AutoShop" },
    { "{\"id\":\"7\",\"method\":\"read_elements\",\"params\":{\"addresses\":\"D100\"}}",
      "-32001", "read_elements answers vendor-module-absent outside AutoShop" },
    { "{\"id\":\"8\",\"method\":\"write_elements\",\"params\":{\"addresses\":\"D100\",\"target\":\"127.0.0.1\"}}",
      "-32001", "write_elements answers vendor-module-absent outside AutoShop" },
    { "{\"id\":\"9\",\"method\":\"force\"}",
      "-32005", "force refused by bridge gate (default)" },
    { "{\"id\":\"10\",\"method\":\"download\"}",
      "-32005", "download refused by bridge gate (default)" },
    { "{\"id\":\"11\",\"method\":\"compile\"}",
      "-32006", "compile refused outside AutoShop (exe base mismatch, F1 chain)" },
    { "{\"id\":\"12\",\"method\":\"make_coffee\"}",
      "-32601", "unknown method answered method-not-found" },
    { "{\"id\":\"13\",\"method\":\"shutdown\"}",
      "\"stopping\":true", "shutdown acknowledged" }
};

DWORD WINAPI ClientMain(LPVOID)
{
    HANDLE pipe = INVALID_HANDLE_VALUE;
    ULONGLONG deadline = ::GetTickCount64() + 8000;
    while (::GetTickCount64() < deadline)
    {
        if (::WaitNamedPipeW(PipePathWide().c_str(), 200) ||
            ::GetLastError() == ERROR_FILE_NOT_FOUND)
        {
            pipe = ::CreateFileW(PipePathWide().c_str(), GENERIC_READ | GENERIC_WRITE,
                                 0, nullptr, OPEN_EXISTING, 0, nullptr);
            if (pipe != INVALID_HANDLE_VALUE)
            {
                break;
            }
        }
        ::Sleep(50);
    }
    if (pipe == INVALID_HANDLE_VALUE)
    {
        g_responses.push_back("FATAL: client could not connect");
        ::InterlockedExchange(&g_clientDone, 1);
        return 1;
    }

    for (const Exchange& exchange : kExchanges)
    {
        std::string response;
        if (!RoundTrip(pipe, exchange.request, &response))
        {
            response = "FATAL: no response to " + std::string(exchange.label);
        }
        g_responses.push_back(response);
        if (Contains(response, "\"stopping\":true"))
        {
            break;
        }
    }
    ::FlushFileBuffers(pipe);
    ::DisconnectNamedPipe(pipe);
    ::CloseHandle(pipe);
    ::InterlockedExchange(&g_clientDone, 1);
    return 0;
}

} // namespace

int main()
{
    int failures = 0;
    Report("BridgeTestHost: AutoShopInjectBridge in-process harness (no AutoShop, no PLC)");

    wchar_t self[MAX_PATH];
    ::GetModuleFileNameW(nullptr, self, MAX_PATH);
    std::wstring dir(self);
    size_t slash = dir.find_last_of(L"\\/");
    dir = dir.substr(0, slash + 1) + L"AutoShopInjectBridge.dll";

    HMODULE bridge = ::LoadLibraryW(dir.c_str());
    if (bridge == nullptr)
    {
        Report("FATAL: AutoShopInjectBridge.dll not loadable from %ls (GetLastError=%lu)",
               dir.c_str(), ::GetLastError());
        return 2;
    }
    g_version = reinterpret_cast<FnBridgeVersion>(::GetProcAddress(bridge, "Bridge_Version"));
    g_setPipeName = reinterpret_cast<FnBridgeSetPipeName>(::GetProcAddress(bridge, "Bridge_SetPipeName"));
    g_pump = reinterpret_cast<FnBridgePump>(::GetProcAddress(bridge, "Bridge_Pump"));
    g_shutdown = reinterpret_cast<FnBridgeShutdown>(::GetProcAddress(bridge, "Bridge_Shutdown"));
    g_init = reinterpret_cast<FnBridgeInit>(::GetProcAddress(bridge, "Bridge_Init"));

    Report("version: %s", g_version ? g_version() : "(unresolved)");
    Check(g_version != nullptr && g_init != nullptr && g_setPipeName != nullptr &&
              g_pump != nullptr && g_shutdown != nullptr,
          failures, "five flat C exports resolved (Init/SetPipeName/Pump/Shutdown/Version)");

    // Manual mode: unique pipe name for this run; no pending config involved.
    snprintf(g_pipeName, sizeof(g_pipeName), "PlcMcpInjectBridgeHarness-%lu",
             static_cast<unsigned long>(::GetCurrentProcessId()));
    Check(g_setPipeName(g_pipeName) == 0, failures, "Bridge_SetPipeName (manual mode)");

    HANDLE client = ::CreateThread(nullptr, 0, ClientMain, nullptr, 0, nullptr);
    fprintf(stderr, "[dbg] client thread started\n");
    fflush(stderr);

    // Pump loop: manual mode needs one pump per handled line; the loop ends
    // when the client finished (or after a generous deadline).
    ULONGLONG deadline = ::GetTickCount64() + 30000;
    int iterations = 0;
    while (::InterlockedCompareExchange(&g_clientDone, 0, 0) == 0 && ::GetTickCount64() < deadline)
    {
        DWORD pumped = g_pump(100);
        if (++iterations % 10 == 0)
        {
            fprintf(stderr, "[dbg] pump iteration=%d rc=0x%08lX\n", iterations, pumped);
            fflush(stderr);
        }
    }
    fprintf(stderr, "[dbg] pump loop done after %d iterations\n", iterations);
    fflush(stderr);
    ::WaitForSingleObject(client, 5000);
    fprintf(stderr, "[dbg] client joined\n");
    fflush(stderr);
    ::CloseHandle(client);
    g_shutdown();

    size_t expected = sizeof(kExchanges) / sizeof(kExchanges[0]);
    Check(g_responses.size() >= expected, failures, "all exchanges answered",
          "responses=" + std::to_string(g_responses.size()));
    for (size_t i = 0; i < expected && i < g_responses.size(); ++i)
    {
        Check(Contains(g_responses[i], kExchanges[i].expect), failures,
              kExchanges[i].label, g_responses[i]);
    }

    Report("%d failure(s)", failures);
    char temp[MAX_PATH] = {0};
    if (::GetTempPathA(MAX_PATH, temp) > 0)
    {
        char path[MAX_PATH];
        snprintf(path, sizeof(path), "%splcmcp-handoff\\inject-bridge", temp);
        ::CreateDirectoryA(path, nullptr);
        snprintf(path, sizeof(path), "%s\\harness-report.txt", path);
        HANDLE file = ::CreateFileA(path, GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS,
                                    FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file != INVALID_HANDLE_VALUE)
        {
            DWORD written = 0;
            ::WriteFile(file, g_report.data(), static_cast<DWORD>(g_report.size()), &written, nullptr);
            ::CloseHandle(file);
        }
    }
    return failures == 0 ? 0 : 1;
}
