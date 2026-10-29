// ===========================================================================
// F2RealReadHarness.cpp  (F2: real-read path probe, NOT part of CI)  - v3
//
// Pipeline under test (A1 conclusion, all steps via real exports):
//   tagSoftAddr 5-arg ctor (InterfaceMgr 0x1C10):
//       this+0x08 = CString(address), this+0x10 = int type, +0x18 int,
//       +0x1C enum, +0x20 double value
//   CMap<tagSoftAddr*>::SetAt(key, tag)  (0x12AB0, real)
//   CCommunicateEth::SetCommunicatConfig(loopback, NULL) -> IsEthOnLine
//   CCommunicateEth::ReadElemValue(map, NULL, mode, level)
//   wait -> Lookup -> tag+0x20 double vs Modbus 502 same address
//
// Type discovery: CDataType::GetBasicTypeByName(name) / GetBasicTypeBitLen.
// CStringT (4-byte ATL object) built via mfc90u ordinal 280 (the ctor
// InterfaceMgr itself imports for CStringT(const wchar_t*)).
//
// Safety: read-only vendor calls; loopback only; no install-dir writes.
// ===========================================================================
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <psapi.h>
#include <stdio.h>
#include <string.h>

#ifndef MSG_WAITALL
#define MSG_WAITALL 0x8
#endif

typedef void  (__thiscall* MapCtor)(void*);
typedef void  (__thiscall* TagCtor5)(void* self, const wchar_t* s1, int e,
                                     const wchar_t* s2, int i4, int i5);
typedef void  (__thiscall* TagDtor)(void*);
typedef void  (__thiscall* MapDtor)(void*);
typedef void  (__thiscall* MapSetAt)(void* self, const wchar_t* key, void** value);
typedef int   (__thiscall* MapLookup)(void* self, const wchar_t* key, void** value);
typedef int   (__thiscall* MapGetCount)(void* self);
typedef void  (__thiscall* MapRemoveAll)(void* self);
typedef void  (__thiscall* CStrCtor)(void* self, const void* arg);
typedef void  (__thiscall* CStrDtor)(void* self);

typedef long  (__cdecl* FnReadElemValue)(void* map, void* wnd, int mode, int level);
typedef void  (__cdecl* FnSetCommCfg)(void* info, void* hwnd);
typedef int   (__cdecl* FnIsEthOnLine)();
typedef int   (__cdecl* FnGetBasicTypeBitLen)(int type);
typedef int   (__cdecl* FnGetBasicTypeByName)(void* cstrName); // CStringT by value = ptr to 4-byte ATL object
typedef long  (__cdecl* FnCheckPassword)(int cmd, void* cstrPw, int mode, int level);
typedef long  (__cdecl* FnSimpleCommand)(void* cstrCmd, int mode, int level);
typedef long  (__cdecl* FnSimpleCommonCmd)(int cmdType, int mode, int level);
typedef void* (__cdecl* FnInstance)();

static const size_t TAG_SIZE = 0x48;
static const size_t MAP_SIZE = 0x1C;

static void hexdump(const char* label, const unsigned char* p, size_t n)
{
    printf("  %s:\n    ", label);
    for (size_t i = 0; i < n; ++i) {
        printf("%02x ", p[i]);
        if ((i % 16) == 15) printf("\n    ");
    }
    printf("\n");
}

static double read_double(const unsigned char* p) { double d; memcpy(&d, p, 8); return d; }
static int    read_i32(const unsigned char* p)    { int v; memcpy(&v, p, 4); return v; }

// ---- minimal Modbus TCP (loopback only) -----------------------------------
static SOCKET mb_connect(const char* ip, unsigned short port)
{
    WSADATA wsa; WSAStartup(MAKEWORD(2, 2), &wsa);
    SOCKET s = socket(AF_INET, SOCK_STREAM, 0);
    sockaddr_in a; memset(&a, 0, sizeof(a));
    a.sin_family = AF_INET; a.sin_port = htons(port);
    a.sin_addr.s_addr = inet_addr(ip);
    if (connect(s, (sockaddr*)&a, sizeof(a)) != 0) { closesocket(s); return INVALID_SOCKET; }
    return s;
}

static bool mb_txn(SOCKET s, const unsigned char* req, int reqlen, unsigned char* rsp, int* rsplen)
{
    if (send(s, (const char*)req, reqlen, 0) != reqlen) return false;
    if (recv(s, (char*)rsp, 7, MSG_WAITALL) != 7) return false;
    int len = (rsp[4] << 8) | rsp[5];
    int rest = len - 1;
    if (rest < 0 || rest > 240) return false;
    if (recv(s, (char*)rsp + 7, rest, MSG_WAITALL) != rest) return false;
    *rsplen = 7 + rest;
    return true;
}

static int mb_read_word(SOCKET s, unsigned short addr, unsigned short* out)
{
    unsigned char req[12] = { 0, 1, 0, 0, 0, 6, 1, 3,
                              (unsigned char)(addr >> 8), (unsigned char)addr, 0, 1 };
    unsigned char rsp[256]; int n = 0;
    if (!mb_txn(s, req, sizeof(req), rsp, &n)) return -1;
    if (rsp[7] != 3 || rsp[8] & 0x80) return (rsp[8] & 0x80) ? -(int)rsp[9] : -2;
    *out = ((unsigned short)rsp[9] << 8) | rsp[10];
    return 0;
}
static int mb_read_bit(SOCKET s, unsigned short addr, int* out)
{
    unsigned char req[12] = { 0, 2, 0, 0, 0, 6, 1, 1,
                              (unsigned char)(addr >> 8), (unsigned char)addr, 0, 1 };
    unsigned char rsp[256]; int n = 0;
    if (!mb_txn(s, req, sizeof(req), rsp, &n)) return -1;
    if (rsp[7] != 1 || rsp[8] & 0x80) return (rsp[8] & 0x80) ? -(int)rsp[9] : -2;
    *out = rsp[9] & 1;
    return 0;
}
static int mb_write_word(SOCKET s, unsigned short addr, unsigned short val)
{
    unsigned char req[12] = { 0, 3, 0, 0, 0, 6, 1, 6,
                              (unsigned char)(addr >> 8), (unsigned char)addr,
                              (unsigned char)(val >> 8), (unsigned char)val };
    unsigned char rsp[256]; int n = 0;
    if (!mb_txn(s, req, sizeof(req), rsp, &n)) return -1;
    return (rsp[7] == 6 && !(rsp[8] & 0x80)) ? 0 : -2;
}
static int mb_write_bit(SOCKET s, unsigned short addr, int on)
{
    unsigned char req[12] = { 0, 4, 0, 0, 0, 6, 1, 5,
                              (unsigned char)(addr >> 8), (unsigned char)addr,
                              (unsigned char)(on ? 0xFF : 0), 0 };
    unsigned char rsp[256]; int n = 0;
    if (!mb_txn(s, req, sizeof(req), rsp, &n)) return -1;
    return (rsp[7] == 5 && !(rsp[8] & 0x80)) ? 0 : -2;
}

struct Proc
{
    HMODULE ifmgr, ce, mfc;
    MapCtor mapCtor; MapDtor mapDtor;
    TagCtor5 tagCtor5; TagDtor tagDtor;
    MapSetAt mapSetAt; MapLookup mapLookup; MapGetCount mapGetCount; MapRemoveAll mapRemoveAll;
    CStrCtor cstrCtor; CStrDtor cstrDtor;
    void* refData;   // a real ATL CStringData sample (borrowed from a vendor-constructed CString)
    FnReadElemValue readElemValue;
    FnSetCommCfg setCommCfg;
    FnIsEthOnLine isEthOnLine;
    FnGetBasicTypeBitLen getBitLen;
    FnGetBasicTypeByName getByName;
};

static void* proc_or_die(HMODULE m, const char* name)
{
    void* p = (void*)GetProcAddress(m, name);
    if (!p) { printf("FATAL: GetProcAddress(%s)\n", name); exit(2); }
    return p;
}

static void* ord_or_die(HMODULE m, int ord)
{
    void* p = (void*)GetProcAddress(m, (LPCSTR)(size_t)ord);
    if (!p) { printf("FATAL: GetProcAddress(ord %d)\n", ord); exit(2); }
    return p;
}

// build a 4-byte ATL CStringT on the caller's storage (manual CStringData
// clone; pStringMgr borrowed from a vendor-constructed CString - read-only use)
static void make_cstr(Proc& p, void* storage, const wchar_t* text)
{
    size_t len = wcslen(text);
    unsigned char* data = (unsigned char*)malloc(16 + (len + 1) * sizeof(wchar_t));
    memcpy(data, &p.refData, 4);                       // +0  pStringMgr
    *(int*)(data + 4) = (int)len;                      // +4  nDataLength
    *(int*)(data + 8) = (int)len;                      // +8  nAllocLength
    *(long*)(data + 12) = 1;                           // +12 nRefs (owned copy)
    wcscpy((wchar_t*)(data + 16), text);
    *(void**)storage = data;
}
static void free_cstr(Proc& p, void* storage)
{
    unsigned char* data = *(unsigned char**)storage;
    if (data) free(data);                              // manager never invoked (nRefs untouched)
    *(void**)storage = NULL;
}

int main(int argc, char** argv)
{
    const char* vendorDir = (argc > 1) ? argv[1] : "D:\\Inovance Control\\AutoShop";
    const char* ip = (argc > 2) ? argv[2] : "127.0.0.1";
    int port = (argc > 3) ? atoi(argv[3]) : 12939;

    printf("== F2 real-read harness v3 ==\n");
    setvbuf(stdout, NULL, _IONBF, 0);
    printf("vendor=%s target=%s:%d (loopback experiment)\n", vendorDir, ip, port);

    Proc p; memset(&p, 0, sizeof(p));
    SetDllDirectoryA(vendorDir);
    char path[MAX_PATH];
    snprintf(path, sizeof(path), "%s\\InterfaceMgr.dll", vendorDir);
    p.ifmgr = LoadLibraryA(path);
    if (!p.ifmgr) { printf("LoadLibrary(InterfaceMgr.dll) err=%lu\n", GetLastError()); return 2; }
    snprintf(path, sizeof(path), "%s\\CommunicateEthernet.dll", vendorDir);
    p.ce = LoadLibraryA(path);
    if (!p.ce) { printf("LoadLibrary(CommunicateEthernet.dll) err=%lu\n", GetLastError()); return 2; }
    p.mfc = GetModuleHandleA("mfc90u.dll");

    printf("[1] vendor DLLs loaded: ifmgr=%p ce=%p\n", (void*)p.ifmgr, (void*)p.ce);
    // walk loaded modules to see how mfc90u got resolved
    {
        HMODULE mods[512]; DWORD need = 0;
        HANDLE me = GetCurrentProcess();
        if (EnumProcessModules(me, mods, sizeof(mods), &need)) {
            int n = need / sizeof(HMODULE);
            if (n > 512) n = 512;
            for (int i = 0; i < n; ++i) {
                char nm[MAX_PATH]; nm[0] = 0;
                GetModuleFileNameA(mods[i], nm, MAX_PATH);
                char base[MAX_PATH]; _splitpath_s(nm, NULL, 0, NULL, 0, base, MAX_PATH, NULL, 0);
                if (_strnicmp(base, "mfc", 3) == 0 || _strnicmp(base, "msv", 3) == 0 ||
                    _strnicmp(base, "InterfaceMgr", 12) == 0 || _strnicmp(base, "Communicate", 11) == 0)
                    printf("    mod: %s\n", nm);
                if (_strnicmp(base, "mfc90u", 6) == 0 && !p.mfc) p.mfc = mods[i];
            }
        }
    }
    if (!p.mfc) p.mfc = GetModuleHandleA("mfc90u.dll");
    if (!p.mfc) p.mfc = LoadLibraryA("mfc90u.dll");
    if (!p.mfc) { printf("mfc90u not loadable (err=%lu)\n", GetLastError()); return 2; }

    p.mapCtor   = (MapCtor)proc_or_die(p.ifmgr, "??0CSoftAddrMap@@QAE@XZ");
    p.mapDtor   = (MapDtor)proc_or_die(p.ifmgr, "??1CSoftAddrMap@@UAE@XZ");
    p.tagCtor5  = (TagCtor5)proc_or_die(p.ifmgr, "??0tagSoftAddr@@QAE@PB_WW4EnumForceQueryResult@@0HH@Z");
    p.tagDtor   = (TagDtor)proc_or_die(p.ifmgr, "??1tagSoftAddr@@UAE@XZ");
    p.mapSetAt  = (MapSetAt)proc_or_die(p.ifmgr, "?SetAt@?$CMap@V?$CStringT@_WV?$StrTraitMFC_DLL@_WV?$ChTraitsCRT@_W@ATL@@@@@ATL@@PB_WPAUtagSoftAddr@@AAPAU3@@@QAEXPB_WAAPAUtagSoftAddr@@@Z");
    p.mapLookup = (MapLookup)proc_or_die(p.ifmgr, "?Lookup@?$CMap@V?$CStringT@_WV?$StrTraitMFC_DLL@_WV?$ChTraitsCRT@_W@ATL@@@@@ATL@@PB_WPAUtagSoftAddr@@AAPAU3@@@QBEHPB_WAAPAUtagSoftAddr@@@Z");
    p.mapGetCount = (MapGetCount)proc_or_die(p.ifmgr, "?GetCount@?$CMap@V?$CStringT@_WV?$StrTraitMFC_DLL@_WV?$ChTraitsCRT@_W@ATL@@@@@ATL@@PB_WPAUtagSoftAddr@@AAPAU3@@@QBEHXZ");
    p.mapRemoveAll = (MapRemoveAll)proc_or_die(p.ifmgr, "?RemoveAll@?$CMap@V?$CStringT@_WV?$StrTraitMFC_DLL@_WV?$ChTraitsCRT@_W@ATL@@@@@ATL@@PB_WPAUtagSoftAddr@@AAPAU3@@@QAEXXZ");
    p.cstrCtor  = (CStrCtor)ord_or_die(p.mfc, 280);   // (unused fallback; manual CStringData is used)
    p.refData   = NULL;
    p.readElemValue = (FnReadElemValue)proc_or_die(p.ce, "?ReadElemValue@CCommunicateEth@@SAJPAVCSoftAddrMap@@PAVCWnd@@W4EnumExecuteMode@@W4EnumCommandLevel@@@Z");
    p.setCommCfg  = (FnSetCommCfg)proc_or_die(p.ce, "?SetCommunicatConfig@CCommunicateEth@@SAXPAUtagEthernetInfo@@PAUHWND__@@@Z");
    p.isEthOnLine = (FnIsEthOnLine)proc_or_die(p.ce, "?IsEthOnLine@CCommunicateEth@@SAHXZ");
    p.getBitLen   = (FnGetBasicTypeBitLen)proc_or_die(p.ifmgr, "?GetBasicTypeBitLen@CDataType@@SAHW4EnumGCMDataType@@@Z");
    // ?GetBasicTypeByName@CDataType@@SA?AW4EnumGCMDataType@@V?$CStringT@...@@Z  (static, returns enum, CStringT by value)
    p.getByName   = (FnGetBasicTypeByName)proc_or_die(p.ifmgr, "?GetBasicTypeByName@CDataType@@SA?AW4EnumGCMDataType@@V?$CStringT@_WV?$StrTraitMFC_DLL@_WV?$ChTraitsCRT@_W@ATL@@@@@ATL@@@Z");
    // --- type discovery ------------------------------------------------------
    // borrow a real CStringData sample from a vendor-constructed tag string
    {
        void* probe = malloc(TAG_SIZE); memset(probe, 0, TAG_SIZE);
        p.tagCtor5(probe, L"PROBE", 0, L"", 0, 0);
        p.refData = *(void**)((unsigned char*)probe + 8);
        printf("[2] refData (vendor CStringData) = %p\n", p.refData);
        p.tagDtor(probe);
        free(probe);
        if (!p.refData) { printf("FATAL: no refData\n"); return 2; }
    }
    printf("[2b] EnumGCMDataType discovery via GetBasicTypeBitLen sweep:\n");
    int tWord = -1, tBit = -1;
    {
        int words[16], bits[16], nw = 0, nb = 0;
        for (int t = 0; t <= 0x420; ++t) {
            int bl = p.getBitLen(t);
            if (bl == 16 && nw < 16) words[nw++] = t;
            if (bl == 1 && nb < 16) bits[nb++] = t;
            if (bl == 8 && nw + nb < 31 && nw < 16) { /* note byte types */ }
        }
        printf("    bitlen==16 types:");
        for (int i = 0; i < nw; ++i) printf(" %d", words[i]);
        printf("\n    bitlen==1 types :");
        for (int i = 0; i < nb; ++i) printf(" %d", bits[i]);
        printf("\n");
        if (nw > 0) tWord = words[0];
        if (nb > 0) tBit = bits[0];
    }
    printf("    chosen: tWord=%d tBit=%d\n", tWord, tBit);

    // --- connection config ---------------------------------------------------
    unsigned char ethInfo[20]; memset(ethInfo, 0, sizeof(ethInfo));
    memcpy(ethInfo + 0, &port, 4);
    int ip0 = 127, ip1 = 0, ip2 = 0, ip3 = 1;
    memcpy(ethInfo + 4, &ip0, 4); memcpy(ethInfo + 8, &ip1, 4);
    memcpy(ethInfo + 12, &ip2, 4); memcpy(ethInfo + 16, &ip3, 4);
    p.setCommCfg(ethInfo, NULL);
    printf("[3] SetCommunicatConfig(loopback); IsEthOnLine=%d\n", p.isEthOnLine());

    // --- build map -----------------------------------------------------------
    void* map = malloc(MAP_SIZE); memset(map, 0, MAP_SIZE);
    p.mapCtor(map);
    struct Entry { const wchar_t* key; void* tag; int type; };
    Entry entries[2] = { { L"D100", NULL, tWord }, { L"M0", NULL, tBit } };
    for (int i = 0; i < 2; ++i) {
        void* tag = malloc(TAG_SIZE); memset(tag, 0, TAG_SIZE);
        p.tagCtor5(tag, entries[i].key, 0, L"", entries[i].type, 0);
        void* slot = tag;
        p.mapSetAt(map, entries[i].key, &slot);
        entries[i].tag = tag;
    }
    printf("[4] map built: GetCount=%d (D100 type=%d, M0 type=%d)\n",
           p.mapGetCount(map), entries[0].type, entries[1].type);
    hexdump("tag[D100] before read", (unsigned char*)entries[0].tag, TAG_SIZE);

    // --- Modbus baseline -----------------------------------------------------
    SOCKET ms = mb_connect(ip, 502);
    unsigned short mbD100 = 0; int mbM0 = 0;
    if (ms != INVALID_SOCKET) {
        mb_write_word(ms, 100, 0x1234);
        mb_write_bit(ms, 0, 1);
        mb_read_word(ms, 100, &mbD100);
        mb_read_bit(ms, 0, &mbM0);
        printf("[5] Modbus 502 baseline: D100=0x%04x(%d) M0=%d\n", mbD100, mbD100, mbM0);
    } else {
        printf("[5] Modbus 502 connect FAILED\n");
    }

    // --- ReadElemValue -------------------------------------------------------
    // optional: login attempt first (CheckPassword with empty password, several cmd values)
    {
        void* cs = malloc(8); memset(cs, 0, 8);
        make_cstr(p, cs, L"");
        FnCheckPassword checkPw = (FnCheckPassword)GetProcAddress(p.ce,
            "?CheckPassword@CCommunicateEth@@SAJW4EnumCommunicaterCmd@@ABV?$CStringT@_WV?$StrTraitMFC_DLL@_WV?$ChTraitsCRT@_W@ATL@@@@@ATL@@W4EnumExecuteMode@@W4EnumCommandLevel@@@Z");
        if (checkPw) {
            for (int cmd = 0; cmd <= 6; ++cmd) {
                long rc = checkPw(cmd, cs, 0, 0);
                printf("[6a] CheckPassword(cmd=%d, \"\") rc=0x%lx\n", cmd, (unsigned long)rc);
                if (rc == 0) break;
            }
        } else {
            printf("[6a] CheckPassword export missing\n");
        }
        free_cstr(p, cs); free(cs);
    }
    long rc = p.readElemValue(map, NULL, 0, 0);
    printf("[6] ReadElemValue(mode=0 level=0) rc=0x%lx\n", (unsigned long)rc);

    // --- wait and inspect ----------------------------------------------------
    for (int round = 0; round < 10; ++round) {
        Sleep(700);
        bool changed = false;
        static unsigned char prev[2][TAG_SIZE];
        for (int i = 0; i < 2; ++i) {
            void* pv = NULL;
            void* tag = entries[i].tag;
            if (p.mapLookup(map, entries[i].key, &pv) && pv) tag = pv;
            unsigned char* b = (unsigned char*)tag;
            if (memcmp(prev[i], b, TAG_SIZE) != 0) changed = true;
            memcpy(prev[i], b, TAG_SIZE);
            if (i == 0 || changed) {
                char label[64];
                snprintf(label, sizeof(label), "tag[%ls] t+%dms", entries[i].key, (round + 1) * 700);
                hexdump(label, b, TAG_SIZE);
                printf("    +0x08 str-ptr=%08x +0x10 type=%d +0x18=%d +0x1C=%d +0x20 double=%.6g +0x28=%d\n",
                       read_i32(b + 8), read_i32(b + 0x10), read_i32(b + 0x18),
                       read_i32(b + 0x1C), read_double(b + 0x20), read_i32(b + 0x28));
            }
        }
        if (!changed && round >= 3) { printf("[7] tags stable - stop waiting\n"); break; }
    }

    void* pvD = NULL;
    unsigned char* bD = (unsigned char*)entries[0].tag;
    if (p.mapLookup(map, entries[0].key, &pvD) && pvD) bD = (unsigned char*)pvD;
    double v20 = read_double(bD + 0x20);
    int v18 = read_i32(bD + 0x18);
    int v1c = read_i32(bD + 0x1C);
    printf("[8] verdict: D100 tag +0x18=%d +0x1C=%d +0x20=%.6g vs Modbus D100=%d => %s\n",
           v18, v1c, v20, (int)mbD100,
           (mbD100 != 0 && ((double)mbD100 == v20 || (double)mbD100 == (double)v18))
               ? "MATCH (real read closed)" : "NO MATCH");

    p.mapRemoveAll(map);
    p.mapDtor(map);
    free(map);
    for (int i = 0; i < 2; ++i) { p.tagDtor(entries[i].tag); free(entries[i].tag); }
    if (ms != INVALID_SOCKET) closesocket(ms);
    printf("[9] cleanup done\n");
    FreeLibrary(p.ce);
    FreeLibrary(p.ifmgr);
    WSACleanup();
    return 0;
}
