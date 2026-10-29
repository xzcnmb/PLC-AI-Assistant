// CryptoShim.cpp
//
// x86 shim over the Inovance AES library (EnDecrypt.dll, x86, VC10-era build,
// shipped with AutoShop V4.12.0.2).
//
// EnDecrypt.dll exposes 29 named exports (dumpbin /exports, PE timestamp
// 56975903 = 2016-01-14). Three are declared private in C++ but ARE in the
// export table, so they are callable through GetProcAddress exactly like the
// public ones (C++ access specifiers are compile-time only):
//
//   ?GetPassword@CAESAlg@@AAE_NPAEW4PASSWORDTYPE@@@Z   (private, exported)
//   ?GetPasswordN@CAESAlg@@AAE_NPAEW4PASSWORDTYPE@@@Z  (private, exported)
//   ?CreateNewKey@CEncryptAlg@@AAEXAAV?$vector@I...@std@@@Z (private, exported)
//   ?GetKey@CEncryptAlg@@AAEXAAV?$vector@I...@std@@@Z       (private, exported)
//
// The shim resolves a fixed whitelist of those exports at runtime
// (LoadLibrary + GetProcAddress on the exact mangled names - the same pattern
// as CommStackShim's CSoftAddrMap ctor loader; build.cmd owns no import
// library for EnDecrypt.dll, so static import is not wired into the build).
// Bulk-encryptor exports (CEncryptAlg::Encrypt/EncryptBlock/EncryptFrame) and
// the CEnDecryptAlg composition class are deliberately NOT resolved.
//
// Vendor behavior (reverse-engineered; see docs/inovance-autoshop-encryption.md):
//   * CAESAlg = standard AES-128 (10 rounds, standard S-box/inv-S-box written
//     into a 752-byte workspace by the ctor; standard AES-128-ECB on ONE
//     16-byte block per call). The non-virtual Cipher/InvCipher/CipherN/
//     InvCipherN take (const unsigned char* in, PASSWORDTYPE) and return a
//     pointer to the 16-byte result INSIDE the object workspace (impl+0xE0).
//   * PASSWORDTYPE has 4 values (0..3; `cmp type,3; ja` in GetPassword).
//     GetPassword copies a hardcoded 16-byte ASCII key from .rdata:
//       family "GP"  (GetPassword):  HC.0793.20140217 / HC.0793.20140312 /
//                                    HC.PWD.0793.0306 / HC.0793.20130306
//       family "GPN" (GetPasswordN): Hc.1012.20160217 / Hc.1012.20160312 /
//                                    Hc.PWD.1012.0312 / (type 3 -> same as 1)
//   * The virtual Cipher/InvCipher(in, key, keyLen) take a caller key.
//   * CEncryptAlg::GetKey writes 6 hardcoded 32-bit constants (24 bytes):
//     5D195C26 7B300A41 14A003B7 14A003B7 7B300A41 5D195C26.
//   * EnDecrypt.dll imports NO file/registry/network APIs (KERNEL32 imports
//     are CRT security-cookie boilerplate + Sleep only) - the key material is
//     fully baked into the binary.
//
// Safety: read-only with respect to the system. No file writes, no registry,
// no network, no PLC. The vendor DLL is loaded read-only from the AutoShop
// installation; every vendor-allocated object is released before the host
// process exits (see PlcMcpCrypt_Unload).
//
// All interfaces are reverse-engineered; no vendor endorsement; version-locked
// to the EnDecrypt.dll shipped with AutoShop 4.12.0.2 (x86).

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>

namespace {

// ---------------------------------------------------------------------------
// Mangled export names (exactly as in the EnDecrypt.dll export table).
// ---------------------------------------------------------------------------

const char* kCaesCtorName      = "??0CAESAlg@@QAE@XZ";
const char* kCaesDtorName      = "??1CAESAlg@@UAE@XZ";
const char* kCipherName        = "?Cipher@CAESAlg@@QAEPBEPBEW4PASSWORDTYPE@@@Z";
const char* kCipherNName       = "?CipherN@CAESAlg@@QAEPBEPBEW4PASSWORDTYPE@@@Z";
const char* kInvCipherName     = "?InvCipher@CAESAlg@@QAEPBEPBEW4PASSWORDTYPE@@@Z";
const char* kInvCipherNName    = "?InvCipherN@CAESAlg@@QAEPBEPBEW4PASSWORDTYPE@@@Z";
const char* kCipherKeyName     = "?Cipher@CAESAlg@@UAEPBEPBE0H@Z";
const char* kInvCipherKeyName  = "?InvCipher@CAESAlg@@UAEPBEPBE0H@Z";
const char* kGetPasswordName   = "?GetPassword@CAESAlg@@AAE_NPAEW4PASSWORDTYPE@@@Z";
const char* kGetPasswordNName  = "?GetPasswordN@CAESAlg@@AAE_NPAEW4PASSWORDTYPE@@@Z";
const char* kCencCtorName =
    "??0CEncryptAlg@@QAE@AAV?$vector@IV?$allocator@I@std@@@std@@"
    "AAV?$vector@EV?$allocator@E@std@@@2@I@Z";
const char* kCencDtorName      = "??1CEncryptAlg@@UAE@XZ";
const char* kGetKeyName        = "?GetKey@CEncryptAlg@@AAEXAAV?$vector@IV?$allocator@I@std@@@std@@@Z";
const char* kCreateNewKeyName  = "?CreateNewKey@CEncryptAlg@@AAEXAAV?$vector@IV?$allocator@I@std@@@std@@@Z";

// ---------------------------------------------------------------------------
// Function pointer types. Every vendor method below is __thiscall (x86):
// the `this` pointer arrives in ECX. Signatures are derived from the mangled
// names (`ret N` operands fix the stack-arg byte counts).
// ---------------------------------------------------------------------------

typedef void*                 (__thiscall* FnCaesCtor)(void* self);
typedef void                  (__thiscall* FnCaesDtor)(void* self);
// ret 8: 2 stack args (const unsigned char* in, PASSWORDTYPE type)
typedef const unsigned char*  (__thiscall* FnCaesCipherPwd)(void* self,
                                                            const unsigned char* in,
                                                            int passwordType);
// ret 0Ch: 3 stack args (const unsigned char* in, const unsigned char* key, int keyLen)
typedef const unsigned char*  (__thiscall* FnCaesCipherKey)(void* self,
                                                            const unsigned char* in,
                                                            const unsigned char* key,
                                                            int keyLen);
// ret 8: 2 stack args (unsigned char* out, PASSWORDTYPE type)
typedef bool                  (__thiscall* FnCaesGetPassword)(void* self,
                                                              unsigned char* out,
                                                              int passwordType);
// CEncryptAlg ctor: ret 0Ch (vector<unsigned>&, vector<unsigned char>&, unsigned)
typedef void*                 (__thiscall* FnCencCtor)(void* self,
                                                       void* keyVec,
                                                       void* dataVec,
                                                       unsigned n);
typedef void                  (__thiscall* FnCencDtor)(void* self);
// GetKey / CreateNewKey: ret 4 (vector<unsigned>&)
typedef void                  (__thiscall* FnCencKeyFn)(void* self, void* keyVec);

// VC10 std::vector<unsigned int> on x86 = 12 bytes: {_Myfirst, _Mylast, _Myend}.
// Same layout as VS2022 x86, but we still hand-build the image and never pass
// our own std::vector object (consistent with the PlcMcpBridgeAbi.h rules).
struct StdVectorImage
{
    void* myfirst;
    void* mylast;
    void* myend;
};

// CAESAlg object image: vtable + one pointer to the 752-byte workspace
// (ctor writes [this]=vtable, [this+4]=workspace; dtor frees the workspace).
struct CaesAlgImage
{
    void* vtable;
    void* impl;
};

// ---------------------------------------------------------------------------
// Module state.
// ---------------------------------------------------------------------------

HMODULE g_vendorDll = nullptr;
void*   g_caes = nullptr;                 // CaesAlgImage, allocated lazily
FnCaesCtor        g_caesCtor = nullptr;
FnCaesDtor        g_caesDtor = nullptr;
FnCaesGetPassword g_getPassword = nullptr;
FnCaesGetPassword g_getPasswordN = nullptr;
FnCaesCipherPwd   g_cipher = nullptr;
FnCaesCipherPwd   g_cipherN = nullptr;
FnCaesCipherPwd   g_invCipher = nullptr;
FnCaesCipherPwd   g_invCipherN = nullptr;
FnCaesCipherKey   g_cipherKey = nullptr;
FnCaesCipherKey   g_invCipherKey = nullptr;
FnCencCtor        g_cencCtor = nullptr;
FnCencDtor        g_cencDtor = nullptr;
FnCencKeyFn       g_getKey = nullptr;
FnCencKeyFn       g_createNewKey = nullptr;
char              g_vendorPath[MAX_PATH] = {0};

long ResolveRequired(HMODULE dll, const char* name, void** out)
{
    void* fn = reinterpret_cast<void*>(GetProcAddress(dll, name));
    if (fn == nullptr)
    {
        return -100 - static_cast<long>(strlen(name)); // unique negative per missing name
    }
    *out = fn;
    return 0;
}

long EnsureCaesObject()
{
    if (g_caes != nullptr)
    {
        return 0;
    }
    if (g_caesCtor == nullptr || g_caesDtor == nullptr)
    {
        return -1;
    }
    void* object = malloc(sizeof(CaesAlgImage));
    if (object == nullptr)
    {
        return -2;
    }
    ZeroMemory(object, sizeof(CaesAlgImage));
    g_caesCtor(object); // allocates the 752-byte workspace (vendor CRT)
    g_caes = object;
    return 0;
}

// Copies the 16-byte result out of the vendor workspace. The vendor returns a
// pointer INTO the CAESAlg object buffer (impl+0xE0), so the copy must happen
// before any further call on the same object.
long CopyBlock(const unsigned char* vendorResult, unsigned char* out16)
{
    if (vendorResult == nullptr || out16 == nullptr)
    {
        return -3;
    }
    memcpy(out16, vendorResult, 16);
    return 0;
}

} // namespace

extern "C" {

// ---------------------------------------------------------------------------
// Lifecycle.
// ---------------------------------------------------------------------------

__declspec(dllexport) const char* PlcMcpCrypt_Version()
{
    return "PlcMcp CryptoShim 1.0.0 (reverse-engineered, read-only, x86, "
           "EnDecrypt.dll AutoShop 4.12.0.2)";
}

// Loads EnDecrypt.dll (read-only, from the installation - never copied or
// modified) and resolves the whitelist. Returns 0 on success; a negative code
// plus the missing-name marker in vendorPath on failure.
__declspec(dllexport) long PlcMcpCrypt_Load(const char* installDir)
{
    if (g_vendorDll != nullptr)
    {
        return 0; // already loaded
    }
    if (installDir == nullptr || installDir[0] == '\0')
    {
        return -1;
    }
    SetDllDirectoryA(installDir);
    HMODULE dll = LoadLibraryA("EnDecrypt.dll");
    if (dll == nullptr)
    {
        return -2; // EnDecrypt.dll not loadable (check install dir + VC10 runtimes)
    }
    g_vendorDll = dll;
    GetModuleFileNameA(dll, g_vendorPath, MAX_PATH);

    long rc = 0;
    if ((rc = ResolveRequired(dll, kCaesCtorName,     reinterpret_cast<void**>(&g_caesCtor)))     != 0) return rc;
    if ((rc = ResolveRequired(dll, kCaesDtorName,     reinterpret_cast<void**>(&g_caesDtor)))     != 0) return rc;
    if ((rc = ResolveRequired(dll, kCipherName,       reinterpret_cast<void**>(&g_cipher)))       != 0) return rc;
    if ((rc = ResolveRequired(dll, kCipherNName,      reinterpret_cast<void**>(&g_cipherN)))      != 0) return rc;
    if ((rc = ResolveRequired(dll, kInvCipherName,    reinterpret_cast<void**>(&g_invCipher)))    != 0) return rc;
    if ((rc = ResolveRequired(dll, kInvCipherNName,   reinterpret_cast<void**>(&g_invCipherN)))   != 0) return rc;
    if ((rc = ResolveRequired(dll, kCipherKeyName,    reinterpret_cast<void**>(&g_cipherKey)))    != 0) return rc;
    if ((rc = ResolveRequired(dll, kInvCipherKeyName, reinterpret_cast<void**>(&g_invCipherKey))) != 0) return rc;
    if ((rc = ResolveRequired(dll, kGetPasswordName,  reinterpret_cast<void**>(&g_getPassword)))  != 0) return rc;
    if ((rc = ResolveRequired(dll, kGetPasswordNName, reinterpret_cast<void**>(&g_getPasswordN))) != 0) return rc;
    // Key-export probe functions (CEncryptAlg). Missing ones only disable the
    // key-export probe; they do not fail the load.
    (void)ResolveRequired(dll, kCencCtorName,       reinterpret_cast<void**>(&g_cencCtor));
    (void)ResolveRequired(dll, kCencDtorName,       reinterpret_cast<void**>(&g_cencDtor));
    (void)ResolveRequired(dll, kGetKeyName,         reinterpret_cast<void**>(&g_getKey));
    (void)ResolveRequired(dll, kCreateNewKeyName,   reinterpret_cast<void**>(&g_createNewKey));
    return 0;
}

// Releases the CAESAlg object (vendor dtor frees the workspace) and the
// library. MUST be called before the host process exits.
__declspec(dllexport) void PlcMcpCrypt_Unload()
{
    if (g_caes != nullptr && g_caesDtor != nullptr)
    {
        g_caesDtor(g_caes); // frees the 752-byte workspace (vendor CRT)
        free(g_caes);       // the 8-byte object image is ours
        g_caes = nullptr;
    }
    if (g_vendorDll != nullptr)
    {
        FreeLibrary(g_vendorDll);
        g_vendorDll = nullptr;
    }
    g_caesCtor = nullptr;
    g_caesDtor = nullptr;
    g_getPassword = nullptr;
    g_getPasswordN = nullptr;
    g_cipher = nullptr;
    g_cipherN = nullptr;
    g_invCipher = nullptr;
    g_invCipherN = nullptr;
    g_cipherKey = nullptr;
    g_invCipherKey = nullptr;
    g_cencCtor = nullptr;
    g_cencDtor = nullptr;
    g_getKey = nullptr;
    g_createNewKey = nullptr;
    g_vendorPath[0] = '\0';
}

__declspec(dllexport) const char* PlcMcpCrypt_VendorPath()
{
    return g_vendorPath;
}

// ---------------------------------------------------------------------------
// Password (key) export. nFamily: 0 = GetPassword, 1 = GetPasswordN.
// passwordType: 0..3 (PASSWORDTYPE; vendor checks `cmp type,3; ja`).
// ---------------------------------------------------------------------------

__declspec(dllexport) long PlcMcpCrypt_GetPassword(int nFamily, int passwordType,
                                                   unsigned char* out16)
{
    if (out16 == nullptr)
    {
        return -1;
    }
    if (nFamily != 0 && nFamily != 1)
    {
        return -2;
    }
    if (passwordType < 0 || passwordType > 3)
    {
        return -3; // vendor supports 0..3 only
    }
    long rc = EnsureCaesObject();
    if (rc != 0)
    {
        return rc;
    }
    FnCaesGetPassword fn = (nFamily == 0) ? g_getPassword : g_getPasswordN;
    if (fn == nullptr)
    {
        return -4;
    }
    // GetPassword returns false only when the out buffer is null (the type
    // check `cmp type,3; ja` falls through to `return true`); treat false as
    // a vendor rejection.
    if (!fn(g_caes, out16, passwordType))
    {
        return -5;
    }
    return 0;
}

// ---------------------------------------------------------------------------
// Single-block primitives. in16/out16 = 16 bytes. The non-virtual variants use
// the hardcoded PASSWORDTYPE key; the WithKey variants pass a caller key
// (virtual Cipher/InvCipher overloads).
// ---------------------------------------------------------------------------

__declspec(dllexport) long PlcMcpCrypt_CipherBlock(int nFamily, int passwordType,
                                                   const unsigned char* in16,
                                                   unsigned char* out16)
{
    if (in16 == nullptr || out16 == nullptr)
    {
        return -1;
    }
    if (nFamily != 0 && nFamily != 1)
    {
        return -2;
    }
    if (passwordType < 0 || passwordType > 3)
    {
        return -3;
    }
    long rc = EnsureCaesObject();
    if (rc != 0)
    {
        return rc;
    }
    FnCaesCipherPwd fn = (nFamily == 0) ? g_cipher : g_cipherN;
    if (fn == nullptr)
    {
        return -4;
    }
    return CopyBlock(fn(g_caes, in16, passwordType), out16);
}

__declspec(dllexport) long PlcMcpCrypt_InvCipherBlock(int nFamily, int passwordType,
                                                      const unsigned char* in16,
                                                      unsigned char* out16)
{
    if (in16 == nullptr || out16 == nullptr)
    {
        return -1;
    }
    if (nFamily != 0 && nFamily != 1)
    {
        return -2;
    }
    if (passwordType < 0 || passwordType > 3)
    {
        return -3;
    }
    long rc = EnsureCaesObject();
    if (rc != 0)
    {
        return rc;
    }
    FnCaesCipherPwd fn = (nFamily == 0) ? g_invCipher : g_invCipherN;
    if (fn == nullptr)
    {
        return -4;
    }
    return CopyBlock(fn(g_caes, in16, passwordType), out16);
}

__declspec(dllexport) long PlcMcpCrypt_CipherBlockWithKey(const unsigned char* in16,
                                                          const unsigned char* key,
                                                          int keyLen,
                                                          unsigned char* out16)
{
    if (in16 == nullptr || key == nullptr || out16 == nullptr)
    {
        return -1;
    }
    if (keyLen <= 0 || keyLen > 64)
    {
        return -2; // generous bound: the workspace holds keys at impl+0x20..0x30
    }
    long rc = EnsureCaesObject();
    if (rc != 0)
    {
        return rc;
    }
    return CopyBlock(g_cipherKey(g_caes, in16, key, keyLen), out16);
}

__declspec(dllexport) long PlcMcpCrypt_InvCipherBlockWithKey(const unsigned char* in16,
                                                             const unsigned char* key,
                                                             int keyLen,
                                                             unsigned char* out16)
{
    if (in16 == nullptr || key == nullptr || out16 == nullptr)
    {
        return -1;
    }
    if (keyLen <= 0 || keyLen > 64)
    {
        return -2;
    }
    long rc = EnsureCaesObject();
    if (rc != 0)
    {
        return rc;
    }
    return CopyBlock(g_invCipherKey(g_caes, in16, key, keyLen), out16);
}

// ---------------------------------------------------------------------------
// Round-trip check: Cipher then InvCipher on one block; report tells whether
// the original block came back.
// ---------------------------------------------------------------------------

__declspec(dllexport) long PlcMcpCrypt_RoundTrip(int nFamily, int passwordType,
                                                 const unsigned char* block16,
                                                 char* report, int reportBytes)
{
    if (report != nullptr && reportBytes > 0)
    {
        report[0] = '\0';
    }
    if (block16 == nullptr)
    {
        return -1;
    }
    unsigned char enc[16];
    unsigned char dec[16];
    long rc = PlcMcpCrypt_CipherBlock(nFamily, passwordType, block16, enc);
    if (rc != 0)
    {
        return rc;
    }
    rc = PlcMcpCrypt_InvCipherBlock(nFamily, passwordType, enc, dec);
    if (rc != 0)
    {
        return rc;
    }
    bool ok = memcmp(dec, block16, 16) == 0;
    if (report != nullptr && reportBytes > 0)
    {
        snprintf(report, reportBytes,
                 "roundtrip family=%d type=%d -> %s (dec=%02x%02x%02x%02x...)",
                 nFamily, passwordType, ok ? "MATCH" : "MISMATCH",
                 dec[0], dec[1], dec[2], dec[3]);
    }
    return ok ? 0 : 1;
}

// ---------------------------------------------------------------------------
// Bulk payload decrypt: InvCipher per 16-byte block; the trailing partial
// block (< 16 bytes) is copied verbatim and reported. Pure memory operation.
// ---------------------------------------------------------------------------

__declspec(dllexport) long PlcMcpCrypt_DecryptPayload(int nFamily, int passwordType,
                                                      const unsigned char* payload,
                                                      int payloadLen,
                                                      unsigned char* out, int outLen)
{
    if (payload == nullptr || out == nullptr)
    {
        return -1;
    }
    if (payloadLen < 0 || outLen < payloadLen)
    {
        return -2;
    }
    if (payloadLen == 0)
    {
        return 0;
    }
    int blocks = payloadLen / 16;
    for (int i = 0; i < blocks; ++i)
    {
        long rc = PlcMcpCrypt_InvCipherBlock(nFamily, passwordType, payload + i * 16,
                                             out + i * 16);
        if (rc != 0)
        {
            return rc;
        }
    }
    int tail = payloadLen % 16;
    if (tail > 0)
    {
        memcpy(out + blocks * 16, payload + blocks * 16, tail);
    }
    return blocks; // number of decrypted blocks (>= 0)
}

// ---------------------------------------------------------------------------
// Decrypted-buffer analysis: printable fraction, Shannon entropy (bits/byte),
// first known-plaintext marker hit. Fills a human-readable report line.
// ---------------------------------------------------------------------------

__declspec(dllexport) long PlcMcpCrypt_Analyze(const unsigned char* data, int len,
                                               char* report, int reportBytes)
{
    if (report != nullptr && reportBytes > 0)
    {
        report[0] = '\0';
    }
    if (data == nullptr || len <= 0)
    {
        return -1;
    }
    unsigned int histogram[256];
    ZeroMemory(histogram, sizeof(histogram));
    int printable = 0;
    for (int i = 0; i < len; ++i)
    {
        histogram[data[i]]++;
        if ((data[i] >= 32 && data[i] < 127) || data[i] == 9 || data[i] == 10 || data[i] == 13)
        {
            printable++;
        }
    }
    double entropy = 0.0;
    for (int i = 0; i < 256; ++i)
    {
        if (histogram[i] == 0)
        {
            continue;
        }
        double p = static_cast<double>(histogram[i]) / len;
        entropy -= p * (log(p) / log(2.0));
    }

    // Known-plaintext markers for .hcp source content (XML-ish project data).
    static const char* kMarkers[] = {
        "<?xml", "xml ", "<Project", "project", "AutoShop", "H5U", "H3U",
        ".hcp", "MainProgram", "Password", "Ladder", "<Html", "var ",
    };
    const char* hit = nullptr;
    for (const char* marker : kMarkers)
    {
        size_t mlen = strlen(marker);
        for (int i = 0; i + static_cast<int>(mlen) <= len; ++i)
        {
            if (memcmp(data + i, marker, mlen) == 0)
            {
                hit = marker;
                break;
            }
        }
        if (hit != nullptr)
        {
            break;
        }
    }

    if (report != nullptr && reportBytes > 0)
    {
        snprintf(report, reportBytes,
                 "len=%d printable=%.3f entropy=%.3f marker=%s",
                 len, static_cast<double>(printable) / len, entropy,
                 hit != nullptr ? hit : "none");
    }
    return (hit != nullptr) ? 1 : 0;
}

// ---------------------------------------------------------------------------
// CEncryptAlg key-export probe. GetKey writes the 6 hardcoded 32-bit key
// words into the caller vector (24 bytes). The vector image references OUR
// buffer with a generous capacity, so the vendor push_backs stay inside
// memory we own and no vendor-CRT allocation escapes.
// ---------------------------------------------------------------------------

__declspec(dllexport) long PlcMcpCrypt_GetKeyWords(unsigned int* outWords, int maxWords,
                                                   int* outCount)
{
    if (outCount != nullptr)
    {
        *outCount = 0;
    }
    if (outWords == nullptr || maxWords < 6 || outCount == nullptr)
    {
        return -1;
    }
    if (g_cencCtor == nullptr || g_cencDtor == nullptr || g_getKey == nullptr)
    {
        return -2; // key-export exports not resolvable
    }
    unsigned char obj[64];
    ZeroMemory(obj, sizeof(obj)); // CEncryptAlg object image (>= 0x24 bytes)
    StdVectorImage dataVec = {nullptr, nullptr, nullptr};
    g_cencCtor(obj, &dataVec, &dataVec, 0); // ctor: GetKey(local) -> member vector

    StdVectorImage vec = {outWords, outWords, outWords + maxWords};
    g_getKey(obj, &vec);
    int count = static_cast<int>(reinterpret_cast<unsigned int*>(vec.mylast) -
                                 reinterpret_cast<unsigned int*>(vec.myfirst));
    g_cencDtor(obj); // frees the member vector (vendor CRT)
    if (count < 0 || count > maxWords)
    {
        return -3;
    }
    *outCount = count;
    return 0;
}

// CreateNewKey expands the key words IN the vector (clear + append). The
// caller buffer must have capacity for the expansion (AES-128: 44 words,
// AES-192: 52, AES-256: 60; pass maxWords >= 64).
__declspec(dllexport) long PlcMcpCrypt_CreateNewKeyWords(const unsigned int* inWords,
                                                         int inCount,
                                                         unsigned int* outWords,
                                                         int maxWords, int* outCount)
{
    if (outCount != nullptr)
    {
        *outCount = 0;
    }
    if (inWords == nullptr || outWords == nullptr || outCount == nullptr)
    {
        return -1;
    }
    if (inCount <= 0 || inCount > maxWords)
    {
        return -2;
    }
    if (g_cencCtor == nullptr || g_cencDtor == nullptr || g_createNewKey == nullptr)
    {
        return -3;
    }
    unsigned char obj[64];
    ZeroMemory(obj, sizeof(obj));
    StdVectorImage dataVec = {nullptr, nullptr, nullptr};
    g_cencCtor(obj, &dataVec, &dataVec, 0);

    memcpy(outWords, inWords, inCount * sizeof(unsigned int));
    StdVectorImage vec = {outWords, outWords + inCount, outWords + maxWords};
    g_createNewKey(obj, &vec);
    int count = static_cast<int>(reinterpret_cast<unsigned int*>(vec.mylast) -
                                 reinterpret_cast<unsigned int*>(vec.myfirst));
    g_cencDtor(obj);
    if (count < 0 || count > maxWords)
    {
        return -4;
    }
    *outCount = count;
    return 0;
}

} // extern "C"
