// AutoShopInjectBridgeInjector.cpp
//
// x86 injector for the AutoShop inject bridge (E2, phase three).
//
// Usage:
//   AutoShopInjectBridgeInjector.exe launch --exe <AutoShop.exe> --dll <bridge.dll>
//        [--args "<extra AutoShop command line>"] [--pidfile <path>]
//        [--pipe <name>] [--allow-write 1] [--allow-force 0] [--allow-download 0]
//        [--compile-rva <hex|none>] [--compile-root-rva <hex>]
//        [--compile-member-rva <hex>] [--compile-gate-rva <hex>]
//        [--compile-gate-owner-rva <hex>]
//        [--wait-pipe-ms 15000] [--inject-timeout-ms 15000]
//   AutoShopInjectBridgeInjector.exe stop   --pidfile <path> [--tree] [--pid <pid>]
//   AutoShopInjectBridgeInjector.exe eject  --pidfile <path> --dll-name <name.dll> [--pid <pid>]
//   AutoShopInjectBridgeInjector.exe status --pidfile <path>
//
// Compile chain (F1, 2026-10-28): the compile gate is OPEN by default — the
// default RVAs are the F1-verified compile-all chain (entry 0x15A2EC0, root
// 0x1581A70, member 0x1581CA0) with the current-project gate 0x161B620 (via
// accessor 0x1581C20) checked before every invocation. --compile-rva none
// disables the compile surface (the bridge then answers Unsupported).
//
// Safety model (red lines, docs/inovance-integration-plan.md phase three):
// - injection happens ONLY into a process this tool itself started
//   (CreateProcessW with CREATE_SUSPENDED); there is no "inject into PID"
//   mode at all, so a user-interactive AutoShop instance can never be a
//   target;
// - the target executable and the bridge DLL are PE-validated (MZ/PE header,
//   IMAGE_FILE_MACHINE_I386, expected base file name) before anything is
//   written to another process;
// - every started PID is recorded to the pidfile (JSON lines) together with
//   the executable path and the process creation time; stop/eject re-verify
//   the live identity (image path + base name + creation time) against that
//   record and REFUSE on any mismatch (PID reuse, foreign process);
// - rollback: launch terminates its own child on any failure after CreateProcess;
//   stop terminates only re-verified owned PIDs (optionally their tree, with
//   each child's image path required to live under the recorded install
//   directory); eject unloads the bridge DLL from a re-verified owned PID;
// - no registry writes, no installation-directory writes; the per-instance
//   bridge config goes to %TEMP%\plcmcp-handoff\inject-bridge\pending-<pid>.json
//   (consumed and deleted by the bridge DLL on init);
// - no third-party injection library; plain Win32 (OpenProcess rights are
//   requested per operation only).
//
// Sequence (launch):
//   1. validate exe/dll images;
//   2. CreateProcessW(AutoShop.exe, CREATE_SUSPENDED) -> pid;
//   3. write pending-<pid>.json (pipe name, gates) BEFORE ResumeThread;
//   4. VirtualAllocEx + WriteProcessMemory(dll path) + CreateRemoteThread
//      (kernel32!LoadLibraryA) -> remote dll base (x86: thread exit code);
//   5. resolve Bridge_Init RVA from the local DLL export table, call it via
//      CreateRemoteThread(base + rva) -> 0 = pipe server started;
//   6. ResumeThread(main thread);
//   7. optionally wait until the pipe answers (WaitNamedPipeW polling);
//   8. append the ownership record to the pidfile; print a JSON event line.
//
// Output discipline: exactly one JSON object on stdout (parsed by the C#
// launcher); human-readable progress goes to stderr. Exit codes:
//   0 ok, 1 usage, 2 validation, 3 process creation, 4 injection, 5 init,
//   6 pipe wait, 7 pidfile/ownership.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <tlhelp32.h>
#include <cstdio>
#include <cstdarg>
#include <cstring>
#include <string>
#include <vector>

// ---------------------------------------------------------------------------
// small utilities
// ---------------------------------------------------------------------------

namespace {

void Log(const char* fmt, ...)
{
    va_list args;
    va_start(args, fmt);
    fprintf(stderr, "[injector] ");
    vfprintf(stderr, fmt, args);
    fprintf(stderr, "\n");
    va_end(args);
}

void PrintEvent(const std::string& json)
{
    printf("%s\n", json.c_str());
    fflush(stdout);
}

std::string JsonEscape(const char* text)
{
    std::string out;
    out += '"';
    for (const char* p = text ? text : ""; *p != '\0'; ++p)
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
    return out;
}

std::string Narrow(const std::wstring& wide)
{
    if (wide.empty())
    {
        return std::string();
    }
    int bytes = WideCharToMultiByte(CP_UTF8, 0, wide.c_str(), -1, nullptr, 0, nullptr, nullptr);
    std::string out(bytes > 0 ? bytes - 1 : 0, '\0');
    if (bytes > 0)
    {
        WideCharToMultiByte(CP_UTF8, 0, wide.c_str(), -1, &out[0], bytes, nullptr, nullptr);
        out.resize(bytes - 1);
    }
    return out;
}

std::string WideToAnsiForLoader(const std::wstring& wide)
{
    // LoadLibraryA receives the DLL path inside the remote process. Non-ANSI
    // paths are mapped through the 8.3 short name so the ANSI loader string
    // stays faithful.
    std::wstring candidate = wide;
    wchar_t shortPath[MAX_PATH];
    DWORD shortLen = ::GetShortPathNameW(candidate.c_str(), shortPath, MAX_PATH);
    if (shortLen > 0 && shortLen < MAX_PATH)
    {
        candidate = shortPath;
    }
    int bytes = WideCharToMultiByte(CP_ACP, 0, candidate.c_str(), -1, nullptr, 0, nullptr, nullptr);
    std::string out(bytes > 0 ? bytes - 1 : 0, '\0');
    if (bytes > 0)
    {
        WideCharToMultiByte(CP_ACP, 0, candidate.c_str(), -1, &out[0], bytes, nullptr, nullptr);
        out.resize(bytes - 1);
    }
    return out;
}

std::string LowerAscii(std::string text)
{
    for (size_t i = 0; i < text.size(); ++i)
    {
        if (text[i] >= 'A' && text[i] <= 'Z')
        {
            text[i] = static_cast<char>(text[i] - 'A' + 'a');
        }
    }
    return text;
}

std::string BaseNameOf(const std::wstring& path)
{
    size_t slash = path.find_last_of(L"\\/");
    std::wstring name = slash == std::wstring::npos ? path : path.substr(slash + 1);
    return LowerAscii(Narrow(name));
}

std::string DirNameOf(const std::wstring& path)
{
    size_t slash = path.find_last_of(L"\\/");
    if (slash == std::wstring::npos)
    {
        return std::string();
    }
    return Narrow(path.substr(0, slash + 1));
}

std::string IsoUtcNow()
{
    SYSTEMTIME st;
    ::GetSystemTime(&st);
    char buf[40];
    snprintf(buf, sizeof(buf), "%04u-%02u-%02uT%02u:%02u:%02u.%03uZ",
             st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond, st.wMilliseconds);
    return buf;
}

// ---------------------------------------------------------------------------
// PE validation (target exe and bridge dll)
// ---------------------------------------------------------------------------

// Returns empty string on success, otherwise a failure reason.
std::string ValidateImage(const std::wstring& path, const char* expectedBaseName)
{
    HANDLE file = ::CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr,
                                OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE)
    {
        char msg[256];
        snprintf(msg, sizeof(msg), "cannot open '%s' (GetLastError=%lu)",
                 Narrow(path).c_str(), ::GetLastError());
        return msg;
    }
    unsigned char header[4096] = {0};
    DWORD read = 0;
    BOOL ok = ::ReadFile(file, header, sizeof(header), &read, nullptr);
    ::CloseHandle(file);
    if (!ok || read < 0x40)
    {
        return "image too small or unreadable: " + Narrow(path);
    }
    if (header[0] != 'M' || header[1] != 'Z')
    {
        return "not a PE image (missing MZ): " + Narrow(path);
    }
    DWORD e_lfanew = 0;
    memcpy(&e_lfanew, header + 0x3C, sizeof(e_lfanew));
    if (e_lfanew == 0 || e_lfanew + 6 > read || e_lfanew > 0x1000)
    {
        return "corrupt PE header: " + Narrow(path);
    }
    if (header[e_lfanew] != 'P' || header[e_lfanew + 1] != 'E' ||
        header[e_lfanew + 2] != 0 || header[e_lfanew + 3] != 0)
    {
        return "missing PE signature: " + Narrow(path);
    }
    WORD machine = 0;
    memcpy(&machine, header + e_lfanew + 4, sizeof(machine));
    if (machine != IMAGE_FILE_MACHINE_I386)
    {
        char msg[160];
        snprintf(msg, sizeof(msg), "image is not x86 (machine=0x%04X): ", machine);
        return msg + Narrow(path);
    }
    if (expectedBaseName != nullptr && BaseNameOf(path) != LowerAscii(expectedBaseName))
    {
        char msg[320];
        snprintf(msg, sizeof(msg), "image base name mismatch (expected %s): ", expectedBaseName);
        return msg + Narrow(path);
    }
    return std::string();
}

// ---------------------------------------------------------------------------
// DLL export-table parsing (local file) -> RVA of an export
// ---------------------------------------------------------------------------

bool GetExportRvaFromFile(const std::wstring& dllPath, const char* exportName, DWORD* outRva)
{
    *outRva = 0;
    HANDLE file = ::CreateFileW(dllPath.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr,
                                OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE)
    {
        return false;
    }
    DWORD size = ::GetFileSize(file, nullptr);
    bool found = false;
    if (size != INVALID_FILE_SIZE && size > 0x400)
    {
        HANDLE mapping = ::CreateFileMappingW(file, nullptr, PAGE_READONLY, 0, 0, nullptr);
        if (mapping != nullptr)
        {
            const BYTE* base = reinterpret_cast<const BYTE*>(
                ::MapViewOfFile(mapping, FILE_MAP_READ, 0, 0, 0));
            if (base != nullptr)
            {
                const IMAGE_DOS_HEADER* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
                if (dos->e_magic == IMAGE_DOS_SIGNATURE)
                {
                    const IMAGE_NT_HEADERS32* nt =
                        reinterpret_cast<const IMAGE_NT_HEADERS32*>(base + dos->e_lfanew);
                    if (nt->Signature == IMAGE_NT_SIGNATURE)
                    {
                        DWORD exportRva =
                            nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXPORT].VirtualAddress;
                        // Convert the export-directory RVA into a file offset
                        // through the section table.
                        const IMAGE_SECTION_HEADER* sections =
                            IMAGE_FIRST_SECTION(nt);
                        const BYTE* exportDir = nullptr;
                        for (WORD i = 0; i < nt->FileHeader.NumberOfSections; ++i)
                        {
                            DWORD va = sections[i].VirtualAddress;
                            DWORD vsize = sections[i].Misc.VirtualSize;
                            if (exportRva >= va && exportRva < va + vsize)
                            {
                                exportDir = base + sections[i].PointerToRawData + (exportRva - va);
                                break;
                            }
                        }
                        if (exportDir != nullptr)
                        {
                            const IMAGE_EXPORT_DIRECTORY* exports =
                                reinterpret_cast<const IMAGE_EXPORT_DIRECTORY*>(exportDir);
                            // Names/ordinals/functions RVAs -> file offsets
                            auto rvaToOffset = [&](DWORD rva) -> const BYTE* {
                                for (WORD i = 0; i < nt->FileHeader.NumberOfSections; ++i)
                                {
                                    DWORD va = sections[i].VirtualAddress;
                                    DWORD vsize = sections[i].Misc.VirtualSize;
                                    if (rva >= va && rva < va + vsize)
                                    {
                                        return base + sections[i].PointerToRawData + (rva - va);
                                    }
                                }
                                return nullptr;
                            };
                            const DWORD* names = reinterpret_cast<const DWORD*>(rvaToOffset(exports->AddressOfNames));
                            const WORD* ordinals = reinterpret_cast<const WORD*>(rvaToOffset(exports->AddressOfNameOrdinals));
                            const DWORD* functions = reinterpret_cast<const DWORD*>(rvaToOffset(exports->AddressOfFunctions));
                            if (names != nullptr && ordinals != nullptr && functions != nullptr)
                            {
                                for (DWORD i = 0; i < exports->NumberOfNames; ++i)
                                {
                                    const BYTE* namePtr = rvaToOffset(names[i]);
                                    if (namePtr != nullptr &&
                                        std::strcmp(reinterpret_cast<const char*>(namePtr), exportName) == 0)
                                    {
                                        *outRva = functions[ordinals[i]];
                                        found = true;
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }
                ::UnmapViewOfFile(base);
            }
            ::CloseHandle(mapping);
        }
    }
    ::CloseHandle(file);
    return found;
}

// ---------------------------------------------------------------------------
// pending-<pid>.json (bridge configuration, consumed by the DLL on init)
// ---------------------------------------------------------------------------

std::wstring HandoffDir()
{
    wchar_t temp[MAX_PATH] = {0};
    ::GetTempPathW(MAX_PATH, temp);
    std::wstring dir = std::wstring(temp) + L"plcmcp-handoff\\inject-bridge";
    ::CreateDirectoryW(dir.c_str(), nullptr); // may already exist
    return dir;
}

bool WritePendingConfig(DWORD pid, const std::string& pipeName,
                        bool allowWrite, bool allowForce, bool allowDownload,
                        bool compileEnabled,
                        const char* compileEntryRvaText,
                        const char* compileRootRvaText,
                        const char* compileMemberRvaText,
                        const char* compileGateRvaText,
                        const char* compileGateOwnerRvaText)
{
    std::wstring path = HandoffDir() + L"\\pending-" + std::to_wstring(pid) + L".json";
    char json[1280];
    snprintf(json, sizeof(json),
             "{\"pid\":%lu,\"pipeName\":%s,\"allowWrite\":%s,\"allowForce\":%s,"
             "\"allowDownload\":%s,\"compileEnabled\":%s,"
             "\"compileEntryRva\":%s,\"compileRootRva\":%s,\"compileMemberRva\":%s,"
             "\"compileProjectGateRva\":%s,\"compileGateOwnerRva\":%s}",
             static_cast<unsigned long>(pid), JsonEscape(pipeName.c_str()).c_str(),
             allowWrite ? "true" : "false",
             allowForce ? "true" : "false",
             allowDownload ? "true" : "false",
             compileEnabled ? "true" : "false",
             (compileEntryRvaText != nullptr && compileEntryRvaText[0] != '\0') ? compileEntryRvaText : "null",
             (compileRootRvaText != nullptr && compileRootRvaText[0] != '\0') ? compileRootRvaText : "null",
             (compileMemberRvaText != nullptr && compileMemberRvaText[0] != '\0') ? compileMemberRvaText : "null",
             (compileGateRvaText != nullptr && compileGateRvaText[0] != '\0') ? compileGateRvaText : "null",
             (compileGateOwnerRvaText != nullptr && compileGateOwnerRvaText[0] != '\0') ? compileGateOwnerRvaText : "null");

    HANDLE file = ::CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr,
                                CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE)
    {
        return false;
    }
    DWORD written = 0;
    ::WriteFile(file, json, static_cast<DWORD>(strlen(json)), &written, nullptr);
    ::CloseHandle(file);
    return true;
}

// ---------------------------------------------------------------------------
// ownership records (pidfile, JSON lines)
// ---------------------------------------------------------------------------

struct OwnedRecord
{
    DWORD pid = 0;
    std::string processName;
    std::string exe;
    std::string dll;
    std::string pipeName;
    ULONGLONG startedFileTime = 0; // hex-encoded in the record
    std::string startedIso;
    std::string event; // launch | stop
};

std::vector<OwnedRecord> ReadPidFile(const std::string& pidFile)
{
    std::vector<OwnedRecord> records;
    HANDLE file = ::CreateFileA(pidFile.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
                                nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE)
    {
        return records;
    }
    char buffer[65536] = {0};
    DWORD read = 0;
    ::ReadFile(file, buffer, sizeof(buffer) - 1, &read, nullptr);
    ::CloseHandle(file);

    // Minimal JSONL scan: each record is one line with flat string/number keys.
    // The C# side owns the authority format; only the documented keys are read.
    char* context = nullptr;
    char* line = strtok_s(buffer, "\r\n", &context);
    while (line != nullptr)
    {
        if (std::strstr(line, "\"pid\"") != nullptr)
        {
            OwnedRecord record;
            const char* pid = std::strstr(line, "\"pid\"");
            if (pid != nullptr)
            {
                record.pid = static_cast<DWORD>(strtoul(pid + 5, nullptr, 10));
            }
            auto field = [&line](const char* key) -> std::string {
                char pattern[48];
                snprintf(pattern, sizeof(pattern), "\"%s\"", key);
                const char* p = std::strstr(line, pattern);
                if (p == nullptr)
                {
                    return std::string();
                }
                p += std::strlen(pattern);
                while (*p == ' ' || *p == ':')
                {
                    ++p;
                }
                if (*p != '"')
                {
                    return std::string();
                }
                ++p;
                std::string out;
                while (*p != '"' && *p != '\0')
                {
                    if (*p == '\\' && *(p + 1) != '\0')
                    {
                        ++p; // skip escape; record paths keep single backslashes
                    }
                    out += *p++;
                }
                return out;
            };
            record.event = field("event").empty() ? "launch" : field("event");
            record.processName = field("processName");
            record.exe = field("exe");
            record.dll = field("dll");
            record.pipeName = field("pipeName");
            record.startedIso = field("startedUtc");
            const char* ft = std::strstr(line, "\"startedFileTime\"");
            if (ft != nullptr)
            {
                record.startedFileTime = strtoull(ft + 17, nullptr, 16);
            }
            if (record.pid != 0)
            {
                records.push_back(record);
            }
        }
        line = strtok_s(nullptr, "\r\n", &context);
    }
    return records;
}

void AppendPidRecord(const std::string& pidFile, const OwnedRecord& record)
{
    std::wstring dir;
    size_t slash = pidFile.find_last_of("\\/");
    if (slash != std::string::npos)
    {
        dir = std::wstring(pidFile.begin(), pidFile.begin() + slash);
        ::CreateDirectoryW(dir.c_str(), nullptr); // best effort; parents must exist
    }
    HANDLE file = ::CreateFileA(pidFile.c_str(), FILE_APPEND_DATA, FILE_SHARE_READ, nullptr,
                                OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE)
    {
        Log("WARNING: cannot append ownership record to %s (GetLastError=%lu)",
            pidFile.c_str(), ::GetLastError());
        return;
    }
    char line[1024];
    char ft[32];
    snprintf(ft, sizeof(ft), "%llx", static_cast<unsigned long long>(record.startedFileTime));
    snprintf(line, sizeof(line),
             "{\"event\":\"%s\",\"pid\":%lu,\"processName\":%s,\"exe\":%s,\"dll\":%s,"
             "\"pipeName\":%s,\"startedUtc\":%s,\"startedFileTime\":\"%s\",\"recordedUtc\":%s}\n",
             record.event.c_str(), static_cast<unsigned long>(record.pid),
             JsonEscape(record.processName.c_str()).c_str(),
             JsonEscape(record.exe.c_str()).c_str(),
             JsonEscape(record.dll.c_str()).c_str(),
             JsonEscape(record.pipeName.c_str()).c_str(),
             JsonEscape(record.startedIso.c_str()).c_str(),
             ft,
             JsonEscape(IsoUtcNow().c_str()).c_str());
    ::SetFilePointer(file, 0, nullptr, FILE_END);
    DWORD written = 0;
    ::WriteFile(file, line, static_cast<DWORD>(strlen(line)), &written, nullptr);
    ::CloseHandle(file);
}

// ---------------------------------------------------------------------------
// live identity verification (ownership protection)
// ---------------------------------------------------------------------------

struct LiveIdentity
{
    bool opened = false;
    std::wstring exePath;
    std::string baseName;
    ULONGLONG creationFileTime = 0;
};

LiveIdentity QueryLiveIdentity(DWORD pid)
{
    LiveIdentity identity;
    HANDLE process = ::OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (process == nullptr)
    {
        return identity;
    }
    identity.opened = true;
    wchar_t path[MAX_PATH] = {0};
    DWORD size = MAX_PATH;
    if (::QueryFullProcessImageNameW(process, 0, path, &size) && size > 0)
    {
        identity.exePath = path;
        identity.baseName = BaseNameOf(path);
    }
    FILETIME create, exit, kernel, user;
    if (::GetProcessTimes(process, &create, &exit, &kernel, &user))
    {
        identity.creationFileTime =
            (static_cast<ULONGLONG>(create.dwHighDateTime) << 32) | create.dwLowDateTime;
    }
    ::CloseHandle(process);
    return identity;
}

// Re-verification against the record taken at launch: any mismatch refuses.
bool VerifyOwnership(const OwnedRecord& record, std::string* reason)
{
    LiveIdentity live = QueryLiveIdentity(record.pid);
    if (!live.opened)
    {
        *reason = "process could not be opened (maybe exited)";
        return false;
    }
    std::string recordBase = BaseNameOf(std::wstring(record.exe.begin(), record.exe.end()));
    if (!recordBase.empty() && !live.baseName.empty() && live.baseName != recordBase)
    {
        char msg[300];
        snprintf(msg, sizeof(msg),
                 "image base name mismatch (record '%s' vs live '%s'); PID may have been reused",
                 recordBase.c_str(), live.baseName.c_str());
        *reason = msg;
        return false;
    }
    if (record.startedFileTime != 0 && live.creationFileTime != 0)
    {
        const ULONGLONG tolerance = 10ULL * 10000000ULL; // 10 s in 100 ns units
        ULONGLONG delta = live.creationFileTime > record.startedFileTime
                              ? live.creationFileTime - record.startedFileTime
                              : record.startedFileTime - live.creationFileTime;
        if (delta > tolerance)
        {
            char msg[200];
            snprintf(msg, sizeof(msg),
                     "creation time mismatch (delta %llu s); PID may have been reused",
                     delta / 10000000ULL);
            *reason = msg;
            return false;
        }
    }
    return true;
}

bool TerminatePid(DWORD pid, std::string* error)
{
    HANDLE process = ::OpenProcess(PROCESS_TERMINATE | SYNCHRONIZE, FALSE, pid);
    if (process == nullptr)
    {
        *error = "OpenProcess failed (GetLastError=" + std::to_string(::GetLastError()) + ")";
        return false;
    }
    BOOL ok = ::TerminateProcess(process, 0);
    if (ok)
    {
        ::WaitForSingleObject(process, 10000);
    }
    ::CloseHandle(process);
    if (!ok)
    {
        *error = "TerminateProcess failed (GetLastError=" + std::to_string(::GetLastError()) + ")";
    }
    return ok != FALSE;
}

// ---------------------------------------------------------------------------
// remote helpers
// ---------------------------------------------------------------------------

// Returns the remote base of the just-loaded DLL (thread exit code on x86).
DWORD RemoteLoadLibrary(HANDLE process, const std::string& dllPathAnsi, DWORD timeoutMs, std::string* error)
{
    LPVOID remoteMemory = ::VirtualAllocEx(process, nullptr, dllPathAnsi.size() + 1,
                                           MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    if (remoteMemory == nullptr)
    {
        *error = "VirtualAllocEx failed (GetLastError=" + std::to_string(::GetLastError()) + ")";
        return 0;
    }
    SIZE_T written = 0;
    if (!::WriteProcessMemory(process, remoteMemory, dllPathAnsi.c_str(), dllPathAnsi.size() + 1, &written) ||
        written != dllPathAnsi.size() + 1)
    {
        *error = "WriteProcessMemory failed (GetLastError=" + std::to_string(::GetLastError()) + ")";
        ::VirtualFreeEx(process, remoteMemory, 0, MEM_RELEASE);
        return 0;
    }
    FARPROC loadLibraryA = ::GetProcAddress(::GetModuleHandleW(L"kernel32.dll"), "LoadLibraryA");
    if (loadLibraryA == nullptr)
    {
        *error = "kernel32!LoadLibraryA not resolved";
        ::VirtualFreeEx(process, remoteMemory, 0, MEM_RELEASE);
        return 0;
    }
    HANDLE thread = ::CreateRemoteThread(process, nullptr, 0,
                                         reinterpret_cast<LPTHREAD_START_ROUTINE>(loadLibraryA),
                                         remoteMemory, 0, nullptr);
    if (thread == nullptr)
    {
        *error = "CreateRemoteThread(LoadLibraryA) failed (GetLastError=" + std::to_string(::GetLastError()) + ")";
        ::VirtualFreeEx(process, remoteMemory, 0, MEM_RELEASE);
        return 0;
    }
    ::WaitForSingleObject(thread, timeoutMs);
    DWORD exitCode = 0;
    ::GetExitCodeThread(thread, &exitCode);
    ::CloseHandle(thread);
    ::VirtualFreeEx(process, remoteMemory, 0, MEM_RELEASE);
    if (exitCode == 0 || exitCode == STILL_ACTIVE)
    {
        *error = "LoadLibraryA remote thread did not return a module handle";
        return 0;
    }
    return exitCode;
}

DWORD RemoteCall0(HANDLE process, void* address, DWORD timeoutMs, std::string* error)
{
    HANDLE thread = ::CreateRemoteThread(process, nullptr, 0,
                                         reinterpret_cast<LPTHREAD_START_ROUTINE>(address),
                                         nullptr, 0, nullptr);
    if (thread == nullptr)
    {
        *error = "CreateRemoteThread failed (GetLastError=" + std::to_string(::GetLastError()) + ")";
        return 0x80000000;
    }
    ::WaitForSingleObject(thread, timeoutMs);
    DWORD exitCode = 0;
    ::GetExitCodeThread(thread, &exitCode);
    ::CloseHandle(thread);
    return exitCode;
}

// ---------------------------------------------------------------------------
// verbs
// ---------------------------------------------------------------------------

struct LaunchOptions
{
    std::wstring exe;
    std::wstring dll;
    std::string extraArgs;
    std::string pidFile;
    std::string pipeName = "PlcMcpAutoShopInjectBridge";
    bool allowWrite = true;
    bool allowForce = false;
    bool allowDownload = false;
    bool compileEnabled = true; // F1: compile gate open by default; "none" disables
    std::string compileRva = "22687424";      // 0x15A2EC0 compile-all entry (decimal)
    std::string compileRootRva = "22551152";  // 0x1581A70 root-manager getter
    std::string compileMemberRva = "22551712";// 0x1581CA0 compile-all manager accessor
    std::string compileGateRva = "23180832";  // 0x161B620 current-project gate
    std::string compileGateOwnerRva = "22551584"; // 0x1581C20 gate-owner accessor
    DWORD waitPipeMs = 15000;
    DWORD injectTimeoutMs = 15000;
};

int RunLaunch(const LaunchOptions& options)
{
    std::string reason = ValidateImage(options.exe, "AutoShop.exe");
    if (!reason.empty())
    {
        PrintEvent(std::string("{\"event\":\"failed\",\"stage\":\"validate-exe\",\"reason\":") +
                   JsonEscape(reason.c_str()) + "}");
        return 2;
    }
    reason = ValidateImage(options.dll, nullptr);
    if (!reason.empty())
    {
        PrintEvent(std::string("{\"event\":\"failed\",\"stage\":\"validate-dll\",\"reason\":") +
                   JsonEscape(reason.c_str()) + "}");
        return 2;
    }

    std::wstring exeFull = options.exe;
    {
        wchar_t full[MAX_PATH];
        if (::GetFullPathNameW(options.exe.c_str(), MAX_PATH, full, nullptr) > 0)
        {
            exeFull = full;
        }
    }
    std::wstring dllFull = options.dll;
    {
        wchar_t full[MAX_PATH];
        if (::GetFullPathNameW(options.dll.c_str(), MAX_PATH, full, nullptr) > 0)
        {
            dllFull = full;
        }
    }

    // Build the command line: quoted exe path + optional pass-through args.
    std::wstring command = L"\"" + exeFull + L"\"";
    if (!options.extraArgs.empty())
    {
        std::wstring wideArgs(options.extraArgs.begin(), options.extraArgs.end());
        command += L" " + wideArgs;
    }

    STARTUPINFOW startup;
    ZeroMemory(&startup, sizeof(startup));
    startup.cb = sizeof(startup);
    PROCESS_INFORMATION process;
    ZeroMemory(&process, sizeof(process));
    if (!::CreateProcessW(exeFull.c_str(), &command[0], nullptr, nullptr, FALSE,
                          CREATE_SUSPENDED, nullptr, nullptr, &startup, &process))
    {
        char json[512];
        snprintf(json, sizeof(json),
                 "{\"event\":\"failed\",\"stage\":\"create-process\",\"reason\":\"CreateProcessW failed\",\"lastError\":%lu}",
                 ::GetLastError());
        PrintEvent(json);
        return 3;
    }

    DWORD pid = process.dwProcessId;
    Log("created suspended AutoShop instance pid=%lu", static_cast<unsigned long>(pid));

    // Rollback helper: everything after CreateProcess cleans up our own child.
    auto rollback = [&](const char* stage, const std::string& detail, int code) {
        ::TerminateProcess(process.hProcess, (UINT)-1);
        ::WaitForSingleObject(process.hProcess, 10000);
        std::wstring pending = HandoffDir() + L"\\pending-" + std::to_wstring(pid) + L".json";
        ::DeleteFileW(pending.c_str());
        char json[768];
        snprintf(json, sizeof(json),
                 "{\"event\":\"failed\",\"stage\":\"%s\",\"pid\":%lu,\"reason\":%s,\"rolledBack\":true}",
                 stage, static_cast<unsigned long>(pid), JsonEscape(detail.c_str()).c_str());
        PrintEvent(json);
        ::CloseHandle(process.hThread);
        ::CloseHandle(process.hProcess);
        return code;
    };

    if (!WritePendingConfig(pid, options.pipeName, options.allowWrite,
                            options.allowForce, options.allowDownload,
                            options.compileEnabled,
                            options.compileRva.c_str(),
                            options.compileRootRva.c_str(),
                            options.compileMemberRva.c_str(),
                            options.compileGateRva.c_str(),
                            options.compileGateOwnerRva.c_str()))
    {
        return rollback("write-config", "pending-<pid>.json could not be written", 4);
    }

    std::string dllAnsi = WideToAnsiForLoader(dllFull);
    std::string injectError;
    DWORD remoteBase = RemoteLoadLibrary(process.hProcess, dllAnsi, options.injectTimeoutMs, &injectError);
    if (remoteBase == 0)
    {
        return rollback("inject", injectError, 4);
    }
    Log("bridge dll loaded remotely at 0x%08lX", remoteBase);

    DWORD initRva = 0;
    if (!GetExportRvaFromFile(dllFull, "Bridge_Init", &initRva) || initRva == 0)
    {
        return rollback("resolve-init", "Bridge_Init export not found in the bridge DLL", 5);
    }
    void* remoteInit = reinterpret_cast<void*>(remoteBase + initRva);
    DWORD initResult = RemoteCall0(process.hProcess, remoteInit, options.injectTimeoutMs, &injectError);
    if (initResult != 0)
    {
        char detail[192];
        snprintf(detail, sizeof(detail), "Bridge_Init remote call returned %lu", initResult);
        return rollback("init", injectError.empty() ? detail : injectError + " / " + detail, 5);
    }

    if (::ResumeThread(process.hThread) == (DWORD)-1)
    {
        return rollback("resume", "ResumeThread failed", 5);
    }
    Log("main thread resumed");

    bool pipeReady = false;
    if (options.waitPipeMs > 0)
    {
        wchar_t widePipe[256];
        MultiByteToWideChar(CP_UTF8, 0, options.pipeName.c_str(), -1, widePipe, 256);
        std::wstring pipePath = std::wstring(L"\\\\.\\pipe\\") + widePipe;
        ULONGLONG deadline = ::GetTickCount64() + options.waitPipeMs;
        while (::GetTickCount64() < deadline)
        {
            if (::WaitNamedPipeW(pipePath.c_str(), 200))
            {
                pipeReady = true;
                break;
            }
            DWORD err = ::GetLastError();
            if (err == ERROR_PIPE_BUSY)
            {
                pipeReady = true; // pipe exists and is busy with the first client
                break;
            }
            ::Sleep(100);
        }
        if (!pipeReady)
        {
            Log("WARNING: pipe '%s' did not become available within %lu ms",
                options.pipeName.c_str(), options.waitPipeMs);
        }
    }

    OwnedRecord record;
    record.event = "launch";
    record.pid = pid;
    record.processName = "AutoShop.exe";
    record.exe = Narrow(exeFull);
    record.dll = Narrow(dllFull);
    record.pipeName = options.pipeName;
    FILETIME create, exitT, kernel, user;
    if (::GetProcessTimes(process.hProcess, &create, &exitT, &kernel, &user))
    {
        record.startedFileTime =
            (static_cast<ULONGLONG>(create.dwHighDateTime) << 32) | create.dwLowDateTime;
    }
    SYSTEMTIME st;
    ::FileTimeToSystemTime(&create, &st);
    char iso[40];
    snprintf(iso, sizeof(iso), "%04u-%02u-%02uT%02u:%02u:%02u.%03uZ",
             st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond, st.wMilliseconds);
    record.startedIso = iso;
    if (!options.pidFile.empty())
    {
        AppendPidRecord(options.pidFile, record);
    }

    char json[1024];
    snprintf(json, sizeof(json),
             "{\"event\":\"launched\",\"pid\":%lu,\"dllBase\":\"0x%08lX\",\"init\":0,"
             "\"resumed\":true,\"pipeReady\":%s,\"pipeName\":%s,\"pidfile\":%s}",
             static_cast<unsigned long>(pid), remoteBase,
             pipeReady ? "true" : "false",
             JsonEscape(options.pipeName.c_str()).c_str(),
             JsonEscape(options.pidFile.c_str()).c_str());
    PrintEvent(json);

    ::CloseHandle(process.hThread);
    ::CloseHandle(process.hProcess);
    return 0;
}

int RunStop(const std::string& pidFile, bool killTree, bool pidGiven, DWORD pidFilter)
{
    std::vector<OwnedRecord> records = ReadPidFile(pidFile);
    if (records.empty())
    {
        PrintEvent(std::string("{\"event\":\"stop\",\"stopped\":0,\"refused\":0,\"reason\":") +
                   JsonEscape("no ownership records found") + "}");
        return pidFile.empty() ? 7 : 0;
    }

    int stopped = 0;
    int refused = 0;
    std::string details;
    for (const OwnedRecord& record : records)
    {
        if (record.event != "launch")
        {
            continue;
        }
        if (pidGiven && record.pid != pidFilter)
        {
            continue;
        }
        std::string reason;
        if (!VerifyOwnership(record, &reason))
        {
            refused++;
            details += "pid " + std::to_string(record.pid) + " REFUSED (" + reason + "); ";
            Log("refused to stop pid %lu: %s", static_cast<unsigned long>(record.pid), reason.c_str());
            continue;
        }
        if (killTree)
        {
            // Children are only touched when their image lives under the
            // recorded install directory (taskkill /T semantics, restricted).
            HANDLE snapshot = ::CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snapshot != INVALID_HANDLE_VALUE)
            {
                PROCESSENTRY32W entry;
                entry.dwSize = sizeof(entry);
                if (::Process32FirstW(snapshot, &entry))
                {
                    do
                    {
                        if (entry.th32ParentProcessID == record.pid)
                        {
                            LiveIdentity child = QueryLiveIdentity(entry.th32ProcessID);
                            std::string childDir = DirNameOf(child.exePath);
                            std::string parentDir = DirNameOf(std::wstring(record.exe.begin(), record.exe.end()));
                            if (!childDir.empty() && _stricmp(childDir.c_str(), parentDir.c_str()) == 0)
                            {
                                std::string childError;
                                TerminatePid(entry.th32ProcessID, &childError);
                            }
                        }
                    } while (::Process32NextW(snapshot, &entry));
                }
                ::CloseHandle(snapshot);
            }
        }
        std::string error;
        if (TerminatePid(record.pid, &error))
        {
            stopped++;
            details += "pid " + std::to_string(record.pid) + " stopped; ";
            OwnedRecord stopRecord = record;
            stopRecord.event = "stop";
            AppendPidRecord(pidFile, stopRecord);
        }
        else
        {
            refused++;
            details += "pid " + std::to_string(record.pid) + " FAILED (" + error + "); ";
        }
    }

    char json[1024];
    snprintf(json, sizeof(json), "{\"event\":\"stop\",\"stopped\":%d,\"refused\":%d,\"details\":%s}",
             stopped, refused, JsonEscape(details.c_str()).c_str());
    PrintEvent(json);
    return refused > 0 ? 7 : 0;
}

int RunEject(const std::string& pidFile, const std::string& dllName, bool pidGiven, DWORD pidFilter)
{
    std::vector<OwnedRecord> records = ReadPidFile(pidFile);
    int ejected = 0;
    int refused = 0;
    std::string details;
    for (const OwnedRecord& record : records)
    {
        if (record.event != "launch")
        {
            continue;
        }
        if (pidGiven && record.pid != pidFilter)
        {
            continue;
        }
        std::string reason;
        if (!VerifyOwnership(record, &reason))
        {
            refused++;
            details += "pid " + std::to_string(record.pid) + " REFUSED (" + reason + "); ";
            continue;
        }
        HANDLE process = ::OpenProcess(
            PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION |
                PROCESS_VM_WRITE | PROCESS_VM_READ | SYNCHRONIZE,
            FALSE, record.pid);
        if (process == nullptr)
        {
            refused++;
            details += "pid " + std::to_string(record.pid) + " OPEN FAILED; ";
            continue;
        }
        bool unloaded = false;
        HANDLE snapshot = ::CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, record.pid);
        if (snapshot != INVALID_HANDLE_VALUE)
        {
            MODULEENTRY32W module;
            module.dwSize = sizeof(module);
            if (::Module32FirstW(snapshot, &module))
            {
                do
                {
                    std::string moduleName = LowerAscii(Narrow(module.szModule));
                    if (moduleName == LowerAscii(dllName))
                    {
                        FARPROC freeLibrary = ::GetProcAddress(::GetModuleHandleW(L"kernel32.dll"), "FreeLibrary");
                        if (freeLibrary != nullptr)
                        {
                            HANDLE thread = ::CreateRemoteThread(
                                process, nullptr, 0,
                                reinterpret_cast<LPTHREAD_START_ROUTINE>(freeLibrary),
                                module.modBaseAddr, 0, nullptr);
                            if (thread != nullptr)
                            {
                                ::WaitForSingleObject(thread, 10000);
                                ::CloseHandle(thread);
                                unloaded = true;
                            }
                        }
                        break;
                    }
                } while (::Module32NextW(snapshot, &module));
            }
            ::CloseHandle(snapshot);
        }
        ::CloseHandle(process);
        if (unloaded)
        {
            ejected++;
            details += "pid " + std::to_string(record.pid) + " ejected " + dllName + "; ";
        }
        else
        {
            refused++;
            details += "pid " + std::to_string(record.pid) + " EJECT FAILED; ";
        }
    }
    char json[1024];
    snprintf(json, sizeof(json), "{\"event\":\"eject\",\"ejected\":%d,\"refused\":%d,\"details\":%s}",
             ejected, refused, JsonEscape(details.c_str()).c_str());
    PrintEvent(json);
    return refused > 0 ? 7 : 0;
}

int RunStatus(const std::string& pidFile)
{
    std::vector<OwnedRecord> records = ReadPidFile(pidFile);
    std::string items;
    for (const OwnedRecord& record : records)
    {
        if (items.size() > 0)
        {
            items += ",";
        }
        LiveIdentity live = QueryLiveIdentity(record.pid);
        char item[512];
        snprintf(item, sizeof(item),
                 "{\"event\":%s,\"pid\":%lu,\"alive\":%s,\"identityMatch\":%s}",
                 JsonEscape(record.event.c_str()).c_str(),
                 static_cast<unsigned long>(record.pid),
                 live.opened ? "true" : "false",
                 (live.opened && VerifyOwnership(record, nullptr)) ? "true" : "false");
        items += item;
    }
    char json[512];
    snprintf(json, sizeof(json), "{\"event\":\"status\",\"pidfile\":%s,\"records\":[%s]}",
             JsonEscape(pidFile.c_str()).c_str(), items.c_str());
    PrintEvent(json);
    return 0;
}

// ---------------------------------------------------------------------------
// argument parsing
// ---------------------------------------------------------------------------

bool TakeValue(int argc, wchar_t** argv, int& i, std::string* out)
{
    if (i + 1 >= argc)
    {
        return false;
    }
    *out = Narrow(argv[++i]);
    return true;
}

// The bridge's JsonExtractInt parses decimal only; accept a 0x-prefixed hex
// token (or a plain decimal) and normalize to a decimal string. "none" keeps
// the value empty (the JSON then carries null and the DLL default stands).
std::string DecimalRvaOrHex(const std::string& value)
{
    if (value.rfind("0x", 0) == 0 || value.rfind("0X", 0) == 0)
    {
        unsigned long parsed = strtoul(value.c_str() + 2, nullptr, 16);
        char buf[16];
        snprintf(buf, sizeof(buf), "%lu", parsed);
        return buf;
    }
    return value;
}

bool TakeValueWide(int argc, wchar_t** argv, int& i, std::wstring* out)
{
    if (i + 1 >= argc)
    {
        return false;
    }
    *out = argv[++i];
    return true;
}

void PrintUsage()
{
    fprintf(stderr,
            "AutoShopInjectBridgeInjector - x86 injector for the AutoShop inject bridge\n"
            "usage:\n"
            "  launch --exe <AutoShop.exe> --dll <bridge.dll> [--args \"...\"] [--pidfile <path>]\n"
            "         [--pipe <name>] [--allow-write 1] [--allow-force 0] [--allow-download 0]\n"
            "         [--compile-rva <hex|none>] [--compile-root-rva <hex>]\n"
            "         [--compile-member-rva <hex>] [--compile-gate-rva <hex>]\n"
            "         [--compile-gate-owner-rva <hex>]\n"
            "         [--wait-pipe-ms 15000] [--inject-timeout-ms 15000]\n"
            "  stop   --pidfile <path> [--tree] [--pid <pid>]\n"
            "  eject  --pidfile <path> --dll-name <name.dll> [--pid <pid>]\n"
            "  status --pidfile <path>\n");
}

} // namespace

int wmain(int argc, wchar_t** argv)
{
    if (argc < 2)
    {
        PrintUsage();
        return 1;
    }
    std::string verb = Narrow(argv[1]);
    if (verb == "launch")
    {
        LaunchOptions options;
        std::string pidFileDefault;
        {
            std::wstring dir = HandoffDir();
            pidFileDefault = Narrow(dir) + "\\owned-instances.jsonl";
        }
        options.pidFile = pidFileDefault;
        for (int i = 2; i < argc; ++i)
        {
            std::string arg = Narrow(argv[i]);
            if (arg == "--exe")
            {
                if (!TakeValueWide(argc, argv, i, &options.exe)) return 1;
            }
            else if (arg == "--dll")
            {
                if (!TakeValueWide(argc, argv, i, &options.dll)) return 1;
            }
            else if (arg == "--args")
            {
                if (!TakeValue(argc, argv, i, &options.extraArgs)) return 1;
            }
            else if (arg == "--pidfile")
            {
                if (!TakeValue(argc, argv, i, &options.pidFile)) return 1;
            }
            else if (arg == "--pipe")
            {
                if (!TakeValue(argc, argv, i, &options.pipeName)) return 1;
            }
            else if (arg == "--allow-write" || arg == "--allow-force" || arg == "--allow-download")
            {
                std::string value;
                if (!TakeValue(argc, argv, i, &value)) return 1;
                bool on = value == "1" || LowerAscii(value) == "true";
                if (arg == "--allow-write") options.allowWrite = on;
                if (arg == "--allow-force") options.allowForce = on;
                if (arg == "--allow-download") options.allowDownload = on;
            }
            else if (arg == "--compile-rva" || arg == "--compile-root-rva" || arg == "--compile-member-rva" ||
                     arg == "--compile-gate-rva" || arg == "--compile-gate-owner-rva")
            {
                std::string value;
                if (!TakeValue(argc, argv, i, &value)) return 1;
                if (arg == "--compile-rva")
                {
                    if (LowerAscii(value) == "none")
                    {
                        options.compileEnabled = false; // explicit disable beats the defaults
                    }
                    else
                    {
                        options.compileRva = DecimalRvaOrHex(value);
                    }
                }
                if (arg == "--compile-root-rva") options.compileRootRva = DecimalRvaOrHex(value);
                if (arg == "--compile-member-rva") options.compileMemberRva = DecimalRvaOrHex(value);
                if (arg == "--compile-gate-rva") options.compileGateRva = DecimalRvaOrHex(value);
                if (arg == "--compile-gate-owner-rva") options.compileGateOwnerRva = DecimalRvaOrHex(value);
            }
            else if (arg == "--wait-pipe-ms")
            {
                std::string value;
                if (!TakeValue(argc, argv, i, &value)) return 1;
                options.waitPipeMs = static_cast<DWORD>(strtoul(value.c_str(), nullptr, 10));
            }
            else if (arg == "--inject-timeout-ms")
            {
                std::string value;
                if (!TakeValue(argc, argv, i, &value)) return 1;
                options.injectTimeoutMs = static_cast<DWORD>(strtoul(value.c_str(), nullptr, 10));
            }
            else
            {
                PrintUsage();
                return 1;
            }
        }
        if (options.exe.empty() || options.dll.empty())
        {
            PrintUsage();
            return 1;
        }
        return RunLaunch(options);
    }
    if (verb == "stop")
    {
        std::string pidFile;
        std::string pidValue;
        bool tree = false;
        for (int i = 2; i < argc; ++i)
        {
            std::string arg = Narrow(argv[i]);
            if (arg == "--pidfile")
            {
                if (!TakeValue(argc, argv, i, &pidFile)) return 1;
            }
            else if (arg == "--pid")
            {
                if (!TakeValue(argc, argv, i, &pidValue)) return 1;
            }
            else if (arg == "--tree")
            {
                tree = true;
            }
            else
            {
                PrintUsage();
                return 1;
            }
        }
        if (pidFile.empty())
        {
            PrintUsage();
            return 1;
        }
        return RunStop(pidFile, tree, !pidValue.empty(),
                       static_cast<DWORD>(strtoul(pidValue.c_str(), nullptr, 10)));
    }
    if (verb == "eject")
    {
        std::string pidFile;
        std::string dllName;
        std::string pidValue;
        for (int i = 2; i < argc; ++i)
        {
            std::string arg = Narrow(argv[i]);
            if (arg == "--pidfile")
            {
                if (!TakeValue(argc, argv, i, &pidFile)) return 1;
            }
            else if (arg == "--dll-name")
            {
                if (!TakeValue(argc, argv, i, &dllName)) return 1;
            }
            else if (arg == "--pid")
            {
                if (!TakeValue(argc, argv, i, &pidValue)) return 1;
            }
            else
            {
                PrintUsage();
                return 1;
            }
        }
        if (pidFile.empty() || dllName.empty())
        {
            PrintUsage();
            return 1;
        }
        return RunEject(pidFile, dllName, !pidValue.empty(),
                        static_cast<DWORD>(strtoul(pidValue.c_str(), nullptr, 10)));
    }
    if (verb == "status")
    {
        std::string pidFile;
        for (int i = 2; i < argc; ++i)
        {
            std::string arg = Narrow(argv[i]);
            if (arg == "--pidfile")
            {
                if (!TakeValue(argc, argv, i, &pidFile)) return 1;
            }
        }
        if (pidFile.empty())
        {
            PrintUsage();
            return 1;
        }
        return RunStatus(pidFile);
    }

    PrintUsage();
    return 1;
}
