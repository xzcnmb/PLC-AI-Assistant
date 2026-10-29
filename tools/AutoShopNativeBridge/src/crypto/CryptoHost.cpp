// CryptoHost.cpp
//
// Verification harness for the EnDecrypt.dll (AutoShop AES library) key-export
// feasibility experiment. Runs entirely outside the vendor installation and
// never touches AutoShop, InoVarSharedService or any PLC.
//
// Phases:
//   A. shim load + resolved whitelist status
//   B. key export: vendor GetPassword/GetPasswordN with PASSWORDTYPE 0..3
//      (the hardcoded 16-byte ASCII keys, dumped as hex + ASCII)
//   C. vendor round trip: Cipher -> InvCipher -> original block back
//   D. vendor-vs-standard AES-128-ECB equivalence: vendor CipherN output
//      compared against a minimal standard AES-128 implementation (encrypt
//      direction, ECB, standard S-box) run in this process
//   E. blob verify loop: for the de-interleaved payload of each --blob= file,
//      vendor InvCipher with every exported key + printable/entropy/marker
//      checks (the known-plaintext verification回路)
//   F. CEncryptAlg::GetKey / CreateNewKey export probe (hardcoded 24-byte key)
//   G. cleanup: PlcMcpCrypt_Unload before exit (releases the vendor object and
//      library handles)
//
// Default blob path is the P3 recon artifact (a1_mainblob.bin); overridable
// with --blob=<path> (repeatable). The harness writes
// %TEMP%\plcmcp-handoff\crypto-harness-report.txt.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <cstdarg>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <cmath>
#include <string>

namespace {

const char* kHandoffDir = "C:\\Users\\18735\\AppData\\Local\\Temp\\plcmcp-handoff";
const char* kDefaultBlob = "p3-blob-recon\\a1_mainblob.bin";
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

void HexDump(const unsigned char* data, int len)
{
    for (int i = 0; i < len; ++i)
    {
        printf("%02x", data[i]);
        if (g_report != nullptr)
        {
            fprintf(g_report, "%02x", data[i]);
        }
    }
    printf("\n");
    if (g_report != nullptr)
    {
        fprintf(g_report, "\n");
        fflush(g_report);
    }
}

// ---------------------------------------------------------------------------
// Shim C API (mirrors CryptoShim.cpp exports).
// ---------------------------------------------------------------------------

typedef const char* (*FnVersion)();
typedef const char* (*FnVendorPath)();
typedef long (*FnLoad)(const char*);
typedef void (*FnUnload)();
typedef long (*FnGetPassword)(int, int, unsigned char*);
typedef long (*FnCipherBlock)(int, int, const unsigned char*, unsigned char*);
typedef long (*FnInvCipherBlock)(int, int, const unsigned char*, unsigned char*);
typedef long (*FnCipherBlockWithKey)(const unsigned char*, const unsigned char*, int, unsigned char*);
typedef long (*FnInvCipherBlockWithKey)(const unsigned char*, const unsigned char*, int, unsigned char*);
typedef long (*FnRoundTrip)(int, int, const unsigned char*, char*, int);
typedef long (*FnDecryptPayload)(int, int, const unsigned char*, int, unsigned char*, int);
typedef long (*FnAnalyze)(const unsigned char*, int, char*, int);
typedef long (*FnGetKeyWords)(unsigned int*, int, int*);
typedef long (*FnCreateNewKeyWords)(const unsigned int*, int, unsigned int*, int, int*);

struct Shim
{
    HMODULE dll;
    FnVersion version;
    FnVendorPath vendorPath;
    FnLoad load;
    FnUnload unload;
    FnGetPassword getPassword;
    FnCipherBlock cipherBlock;
    FnInvCipherBlock invCipherBlock;
    FnCipherBlockWithKey cipherBlockWithKey;
    FnInvCipherBlockWithKey invCipherBlockWithKey;
    FnRoundTrip roundTrip;
    FnDecryptPayload decryptPayload;
    FnAnalyze analyze;
    FnGetKeyWords getKeyWords;
    FnCreateNewKeyWords createNewKeyWords;
};

bool LoadShim(Shim& shim)
{
    ZeroMemory(&shim, sizeof(shim));
    char exePath[MAX_PATH];
    GetModuleFileNameA(nullptr, exePath, MAX_PATH);
    std::string shimPath(exePath);
    size_t slash = shimPath.find_last_of("\\/");
    shimPath = shimPath.substr(0, slash + 1) + "CryptoShim.dll";
    shim.dll = LoadLibraryA(shimPath.c_str());
    if (shim.dll == nullptr)
    {
        Report("  CryptoShim.dll not loadable from %s (error %lu)", shimPath.c_str(), GetLastError());
        return false;
    }
    shim.version            = reinterpret_cast<FnVersion>(GetProcAddress(shim.dll, "PlcMcpCrypt_Version"));
    shim.vendorPath         = reinterpret_cast<FnVendorPath>(GetProcAddress(shim.dll, "PlcMcpCrypt_VendorPath"));
    shim.load               = reinterpret_cast<FnLoad>(GetProcAddress(shim.dll, "PlcMcpCrypt_Load"));
    shim.unload             = reinterpret_cast<FnUnload>(GetProcAddress(shim.dll, "PlcMcpCrypt_Unload"));
    shim.getPassword        = reinterpret_cast<FnGetPassword>(GetProcAddress(shim.dll, "PlcMcpCrypt_GetPassword"));
    shim.cipherBlock        = reinterpret_cast<FnCipherBlock>(GetProcAddress(shim.dll, "PlcMcpCrypt_CipherBlock"));
    shim.invCipherBlock     = reinterpret_cast<FnInvCipherBlock>(GetProcAddress(shim.dll, "PlcMcpCrypt_InvCipherBlock"));
    shim.cipherBlockWithKey = reinterpret_cast<FnCipherBlockWithKey>(GetProcAddress(shim.dll, "PlcMcpCrypt_CipherBlockWithKey"));
    shim.invCipherBlockWithKey = reinterpret_cast<FnInvCipherBlockWithKey>(GetProcAddress(shim.dll, "PlcMcpCrypt_InvCipherBlockWithKey"));
    shim.roundTrip          = reinterpret_cast<FnRoundTrip>(GetProcAddress(shim.dll, "PlcMcpCrypt_RoundTrip"));
    shim.decryptPayload     = reinterpret_cast<FnDecryptPayload>(GetProcAddress(shim.dll, "PlcMcpCrypt_DecryptPayload"));
    shim.analyze            = reinterpret_cast<FnAnalyze>(GetProcAddress(shim.dll, "PlcMcpCrypt_Analyze"));
    shim.getKeyWords        = reinterpret_cast<FnGetKeyWords>(GetProcAddress(shim.dll, "PlcMcpCrypt_GetKeyWords"));
    shim.createNewKeyWords  = reinterpret_cast<FnCreateNewKeyWords>(GetProcAddress(shim.dll, "PlcMcpCrypt_CreateNewKeyWords"));
    return shim.load != nullptr && shim.unload != nullptr;
}

// ---------------------------------------------------------------------------
// Minimal standard AES-128 (encrypt direction, ECB, standard S-box) for the
// vendor-vs-standard equivalence check. Independent of the vendor code; used
// only inside this harness.
// ---------------------------------------------------------------------------

const unsigned char kStdSbox[256] = {
    0x63,0x7c,0x77,0x7b,0xf2,0x6b,0x6f,0xc5,0x30,0x01,0x67,0x2b,0xfe,0xd7,0xab,0x76,
    0xca,0x82,0xc9,0x7d,0xfa,0x59,0x47,0xf0,0xad,0xd4,0xa2,0xaf,0x9c,0xa4,0x72,0xc0,
    0xb7,0xfd,0x93,0x26,0x36,0x3f,0xf7,0xcc,0x34,0xa5,0xe5,0xf1,0x71,0xd8,0x31,0x15,
    0x04,0xc7,0x23,0xc3,0x18,0x96,0x05,0x9a,0x07,0x12,0x80,0xe2,0xeb,0x27,0xb2,0x75,
    0x09,0x83,0x2c,0x1a,0x1b,0x6e,0x5a,0xa0,0x52,0x3b,0xd6,0xb3,0x29,0xe3,0x2f,0x84,
    0x53,0xd1,0x00,0xed,0x20,0xfc,0xb1,0x5b,0x6a,0xcb,0xbe,0x39,0x4a,0x4c,0x58,0xcf,
    0xd0,0xef,0xaa,0xfb,0x43,0x4d,0x33,0x85,0x45,0xf9,0x02,0x7f,0x50,0x3c,0x9f,0xa8,
    0x51,0xa3,0x40,0x8f,0x92,0x9d,0x38,0xf5,0xbc,0xb6,0xda,0x21,0x10,0xff,0xf3,0xd2,
    0xcd,0x0c,0x13,0xec,0x5f,0x97,0x44,0x17,0xc4,0xa7,0x7e,0x3d,0x64,0x5d,0x19,0x73,
    0x60,0x81,0x4f,0xdc,0x22,0x2a,0x90,0x88,0x46,0xee,0xb8,0x14,0xde,0x5e,0x0b,0xdb,
    0xe0,0x32,0x3a,0x0a,0x49,0x06,0x24,0x5c,0xc2,0xd3,0xac,0x62,0x91,0x95,0xe4,0x79,
    0xe7,0xc8,0x37,0x6d,0x8d,0xd5,0x4e,0xa9,0x6c,0x56,0xf4,0xea,0x65,0x7a,0xae,0x08,
    0xba,0x78,0x25,0x2e,0x1c,0xa6,0xb4,0xc6,0xe8,0xdd,0x74,0x1f,0x4b,0xbd,0x8b,0x8a,
    0x70,0x3e,0xb5,0x66,0x48,0x03,0xf6,0x0e,0x61,0x35,0x57,0xb9,0x86,0xc1,0x1d,0x9e,
    0xe1,0xf8,0x98,0x11,0x69,0xd9,0x8e,0x94,0x9b,0x1e,0x87,0xe9,0xce,0x55,0x28,0xdf,
    0x8c,0xa1,0x89,0x0d,0xbf,0xe6,0x42,0x68,0x41,0x99,0x2d,0x0f,0xb0,0x54,0xbb,0x16,
};

unsigned char StdXtime(unsigned char x)
{
    return static_cast<unsigned char>((x << 1) ^ ((x >> 7) * 0x1b));
}

void StdKeyExpansion(const unsigned char* key16, unsigned char* roundKeys /* 176 bytes */)
{
    static const unsigned char kRcon[11] = {
        0x00,0x01,0x02,0x04,0x08,0x10,0x20,0x40,0x80,0x1b,0x36,
    };
    memcpy(roundKeys, key16, 16);
    for (int i = 4; i < 44; ++i)
    {
        unsigned char temp[4];
        memcpy(temp, roundKeys + (i - 1) * 4, 4);
        if (i % 4 == 0)
        {
            // RotWord + SubWord + Rcon
            unsigned char shuffled[4] = {temp[1], temp[2], temp[3], temp[0]};
            for (int j = 0; j < 4; ++j)
            {
                shuffled[j] = kStdSbox[shuffled[j]];
            }
            shuffled[0] = static_cast<unsigned char>(shuffled[0] ^ kRcon[i / 4]);
            memcpy(temp, shuffled, 4);
        }
        for (int j = 0; j < 4; ++j)
        {
            roundKeys[i * 4 + j] =
                static_cast<unsigned char>(roundKeys[(i - 4) * 4 + j] ^ temp[j]);
        }
    }
}

// Standard AES-128-ECB single-block encrypt (FIPS-197). Flat state layout:
// state[i] with i = r + 4c (column-major, as AES fills the state from the
// input). Validated against the FIPS-197 test vector in Phase D.
void StdAes128EcbEncryptBlock(const unsigned char* in16, const unsigned char* key16,
                              unsigned char* out16)
{
    unsigned char roundKeys[176];
    StdKeyExpansion(key16, roundKeys);

    unsigned char state[16];
    memcpy(state, in16, 16);
    for (int i = 0; i < 16; ++i) // AddRoundKey 0
    {
        state[i] = static_cast<unsigned char>(state[i] ^ roundKeys[i]);
    }

    for (int round = 1; round <= 10; ++round)
    {
        // SubBytes
        for (int i = 0; i < 16; ++i)
        {
            state[i] = kStdSbox[state[i]];
        }
        // ShiftRows: row r (bytes r, r+4, r+8, r+12) rotated left by r
        unsigned char shifted[16];
        for (int r = 0; r < 4; ++r)
        {
            for (int c = 0; c < 4; ++c)
            {
                shifted[r + 4 * c] = state[r + 4 * ((c + r) % 4)];
            }
        }
        // MixColumns (skipped in the final round)
        if (round < 10)
        {
            for (int c = 0; c < 4; ++c)
            {
                unsigned char a0 = shifted[4 * c], a1 = shifted[4 * c + 1];
                unsigned char a2 = shifted[4 * c + 2], a3 = shifted[4 * c + 3];
                unsigned char t = static_cast<unsigned char>(a0 ^ a1 ^ a2 ^ a3);
                shifted[4 * c]     = static_cast<unsigned char>(a0 ^ t ^ StdXtime(static_cast<unsigned char>(a0 ^ a1)));
                shifted[4 * c + 1] = static_cast<unsigned char>(a1 ^ t ^ StdXtime(static_cast<unsigned char>(a1 ^ a2)));
                shifted[4 * c + 2] = static_cast<unsigned char>(a2 ^ t ^ StdXtime(static_cast<unsigned char>(a2 ^ a3)));
                shifted[4 * c + 3] = static_cast<unsigned char>(a3 ^ t ^ StdXtime(static_cast<unsigned char>(a3 ^ a0)));
            }
        }
        memcpy(state, shifted, 16);
        for (int i = 0; i < 16; ++i) // AddRoundKey
        {
            state[i] = static_cast<unsigned char>(state[i] ^ roundKeys[round * 16 + i]);
        }
    }
    memcpy(out16, state, 16);
}

// ---------------------------------------------------------------------------
// Blob de-interleave (majority-vote cycle, stride 2, period 11) - mirrors
// MainBlobScanner.ExtractPayload (see docs/inovance-autoshop-program-blob.md).
// ---------------------------------------------------------------------------

bool DeInterleave(const unsigned char* blob, int blobLen, unsigned char** payloadOut, int* payloadLen)
{
    if (blobLen < 44)
    {
        return false;
    }
    int slots = blobLen / 2;
    // majority vote per residue class of the odd stream, period 11
    int counts[11][256];
    ZeroMemory(counts, sizeof(counts));
    for (int i = 0; i < slots; ++i)
    {
        counts[i % 11][blob[i * 2 + 1]]++;
    }
    unsigned char cycle[11];
    for (int r = 0; r < 11; ++r)
    {
        int best = 0, bestN = -1;
        for (int b = 0; b < 256; ++b)
        {
            if (counts[r][b] > bestN)
            {
                bestN = counts[r][b];
                best = b;
            }
        }
        cycle[r] = static_cast<unsigned char>(best);
    }
    unsigned char* payload = static_cast<unsigned char*>(malloc(blobLen));
    if (payload == nullptr)
    {
        return false;
    }
    int n = 0;
    int slotIdx = 0;
    for (int i = 0; i < blobLen; ++i)
    {
        if (i % 2 != 1)
        {
            payload[n++] = blob[i];
        }
        else
        {
            if (blob[i] != cycle[slotIdx % 11])
            {
                payload[n++] = blob[i];
            }
            slotIdx++;
        }
    }
    *payloadOut = payload;
    *payloadLen = n;
    return true;
}

// ---------------------------------------------------------------------------
// Phases.
// ---------------------------------------------------------------------------

void PhaseB_Keys(Shim& shim)
{
    Report("[B] key export (vendor GetPassword/GetPasswordN, PASSWORDTYPE 0..3)");
    const char* familyNames[2] = {"GP (GetPassword)", "GPN (GetPasswordN)"};
    for (int family = 0; family < 2; ++family)
    {
        for (int type = 0; type < 4; ++type)
        {
            unsigned char key[16];
            long rc = shim.getPassword(family, type, key);
            if (rc != 0)
            {
                Report("  %s type=%d -> rc=%ld", familyNames[family], type, rc);
                continue;
            }
            char ascii[17];
            memcpy(ascii, key, 16);
            ascii[16] = '\0';
            Report("  %s type=%d -> ", familyNames[family], type);
            HexDump(key, 16);
            Report("      ascii: \"%s\"", ascii);
        }
    }
}

void PhaseC_RoundTrip(Shim& shim)
{
    Report("[C] vendor round trip (Cipher -> InvCipher -> original)");
    unsigned char block[16];
    for (int i = 0; i < 16; ++i)
    {
        block[i] = static_cast<unsigned char>((i * 37 + 11) & 0xFF);
    }
    for (int family = 0; family < 2; ++family)
    {
        for (int type = 0; type < 4; ++type)
        {
            char report[128] = {0};
            long rc = shim.roundTrip(family, type, block, report, sizeof(report));
            Report("  %s", report);
            if (rc != 0 && rc != 1)
            {
                Report("  roundtrip rc=%ld (call failure)", rc);
            }
        }
    }
}

void PhaseD_Equivalence(Shim& shim)
{
    Report("[D] vendor vs standard AES-128-ECB (encrypt direction)");
    // FIPS-197 self-test of the in-harness standard implementation first:
    // key 000102...0f, plaintext 00112233...ff -> 69c4e0d86a7b0430d8cdb78070b4c55a
    {
        unsigned char fipsKey[16], fipsPlain[16], fipsExpected[16], fipsOut[16];
        for (int i = 0; i < 16; ++i)
        {
            fipsKey[i] = static_cast<unsigned char>(i);
            fipsPlain[i] = static_cast<unsigned char>(i * 0x11);
        }
        const char* expectedHex = "69c4e0d86a7b0430d8cdb78070b4c55a";
        for (int i = 0; i < 16; ++i)
        {
            unsigned int b = 0;
            sscanf_s(expectedHex + i * 2, "%2x", &b);
            fipsExpected[i] = static_cast<unsigned char>(b);
        }
        StdAes128EcbEncryptBlock(fipsPlain, fipsKey, fipsOut);
        bool selfOk = memcmp(fipsOut, fipsExpected, 16) == 0;
        Report("  FIPS-197 self-test (in-harness standard AES-128) -> %s",
               selfOk ? "PASS" : "FAIL (standard reference is broken)");
        if (!selfOk)
        {
            return; // the comparison below would be meaningless
        }
    }
    unsigned char block[16];
    for (int i = 0; i < 16; ++i)
    {
        block[i] = static_cast<unsigned char>((i * 91 + 5) & 0xFF);
    }
    int matches = 0, total = 0;
    for (int family = 0; family < 2; ++family)
    {
        for (int type = 0; type < 4; ++type)
        {
            unsigned char key[16];
            if (shim.getPassword(family, type, key) != 0)
            {
                continue;
            }
            unsigned char vendor[16];
            if (shim.cipherBlock(family, type, block, vendor) != 0)
            {
                continue;
            }
            unsigned char standard[16];
            StdAes128EcbEncryptBlock(block, key, standard);
            bool equal = memcmp(vendor, standard, 16) == 0;
            total++;
            matches += equal ? 1 : 0;
            Report("  family=%d type=%d key=%-16.16s vendor=%02x%02x%02x%02x... standard=%02x%02x%02x%02x... -> %s",
                   family, type, reinterpret_cast<const char*>(key),
                   vendor[0], vendor[1], vendor[2], vendor[3],
                   standard[0], standard[1], standard[2], standard[3],
                   equal ? "MATCH" : "DIFFER");
        }
    }
    Report("  equivalence: %d/%d keys produce standard AES-128-ECB ciphertext", matches, total);
}

void PhaseE_Blobs(Shim& shim, const char blobPaths[][MAX_PATH], int blobCount)
{
    Report("[E] blob verify loop (de-interleave -> vendor InvCipher -> plaintext checks)");
    for (int b = 0; b < blobCount; ++b)
    {
        FILE* f = fopen(blobPaths[b], "rb");
        if (f == nullptr)
        {
            Report("  blob not readable: %s (skipped)", blobPaths[b]);
            continue;
        }
        fseek(f, 0, SEEK_END);
        long size = ftell(f);
        fseek(f, 0, SEEK_SET);
        unsigned char* blob = static_cast<unsigned char*>(malloc(size));
        if (blob == nullptr)
        {
            fclose(f);
            continue;
        }
        size_t got = fread(blob, 1, size, f);
        fclose(f);
        if (got != static_cast<size_t>(size))
        {
            free(blob);
            continue;
        }
        unsigned char* payload = nullptr;
        int payloadLen = 0;
        if (!DeInterleave(blob, static_cast<int>(size), &payload, &payloadLen))
        {
            Report("  %s: de-interleave failed (blob %ld B, skipped)", blobPaths[b], size);
            free(blob);
            continue;
        }
        Report("  blob=%s (%ld B) -> payload %d B", blobPaths[b], size, payloadLen);

        unsigned char* plain = static_cast<unsigned char*>(malloc(payloadLen));
        if (plain == nullptr)
        {
            free(payload);
            free(blob);
            continue;
        }
        for (int family = 0; family < 2; ++family)
        {
            for (int type = 0; type < 4; ++type)
            {
                long rc = shim.decryptPayload(family, type, payload, payloadLen,
                                              plain, payloadLen);
                if (rc < 0)
                {
                    Report("    family=%d type=%d -> decrypt rc=%ld", family, type, rc);
                    continue;
                }
                char report[160] = {0};
                long markerHit = shim.analyze(plain, payloadLen, report, sizeof(report));
                Report("    family=%d type=%d blocks=%ld -> %s%s", family, type, rc, report,
                       markerHit == 1 ? "  [KNOWN-PLAINTEXT HIT]" : "");
            }
        }
        // Also probe the 24-byte CEncryptAlg key (AES-192 length) through the
        // virtual InvCipher overload on the payload's first block.
        unsigned int keyWords[64];
        int keyCount = 0;
        if (shim.getKeyWords(keyWords, 64, &keyCount) == 0 && keyCount > 0)
        {
            const unsigned char* keyBytes = reinterpret_cast<const unsigned char*>(keyWords);
            int keyBytesLen = keyCount * 4;
            unsigned char first[16];
            unsigned char vendor[16];
            memcpy(first, payload, 16);
            long rc = shim.invCipherBlockWithKey(first, keyBytes, keyBytesLen, vendor);
            char report[160] = {0};
            shim.analyze(vendor, 16, report, sizeof(report));
            Report("    CEncryptAlg key (%d words) inv-cipher first block -> rc=%ld %s",
                   keyCount, rc, report);
        }
        free(plain);
        free(payload);
        free(blob);
    }
}

void PhaseF_KeyExport(Shim& shim)
{
    Report("[F] CEncryptAlg key export probe (GetKey / CreateNewKey, exported privates)");
    unsigned int keyWords[64];
    int keyCount = 0;
    long rc = shim.getKeyWords(keyWords, 64, &keyCount);
    if (rc != 0)
    {
        Report("  GetKeyWords -> rc=%ld (probe unavailable)", rc);
        return;
    }
    Report("  GetKey -> %d words:", keyCount);
    for (int i = 0; i < keyCount; ++i)
    {
        Report("    [%02d] %08X", i, keyWords[i]);
    }
    int expandedCount = 0;
    unsigned int expanded[64];
    rc = shim.createNewKeyWords(keyWords, keyCount, expanded, 64, &expandedCount);
    if (rc != 0)
    {
        Report("  CreateNewKeyWords -> rc=%ld", rc);
        return;
    }
    Report("  CreateNewKey -> %d words (%d bytes) expanded key; first words:",
           expandedCount, expandedCount * 4);
    for (int i = 0; i < expandedCount && i < 8; ++i)
    {
        Report("    [%02d] %08X", i, expanded[i]);
    }
    // AES round count inference from the expanded key length:
    // 44 words = AES-128 (10 rounds), 52 = AES-192 (12), 60 = AES-256 (14).
    const char* inferred =
        expandedCount == 44 ? "AES-128 (10 rounds)" :
        expandedCount == 52 ? "AES-192 (12 rounds)" :
        expandedCount == 60 ? "AES-256 (14 rounds)" : "nonstandard";
    Report("  expanded key length %d words -> %s", expandedCount, inferred);
}

} // namespace

int main(int argc, char** argv)
{
    char reportPath[MAX_PATH];
    snprintf(reportPath, sizeof(reportPath), "%s\\crypto-harness-report.txt", kHandoffDir);
    fopen_s(&g_report, reportPath, "w");

    Report("=== PlcMcp EnDecrypt.dll crypto verification harness ===");
    Report("host pid=%lu", GetCurrentProcessId());

    char blobPaths[8][MAX_PATH];
    int blobCount = 0;
    char installDir[MAX_PATH];
    strncpy_s(installDir, "D:\\Inovance Control\\AutoShop", sizeof(installDir) - 1);
    for (int i = 1; i < argc; ++i)
    {
        if (std::strncmp(argv[i], "--blob=", 7) == 0 && blobCount < 8)
        {
            strncpy_s(blobPaths[blobCount], argv[i] + 7, MAX_PATH - 1);
            blobCount++;
        }
        else if (std::strncmp(argv[i], "--install=", 10) == 0)
        {
            strncpy_s(installDir, argv[i] + 10, sizeof(installDir) - 1);
        }
        else
        {
            Report("  unknown argument: %s", argv[i]);
            if (g_report != nullptr)
            {
                fclose(g_report);
            }
            return 2;
        }
    }
    if (blobCount == 0)
    {
        snprintf(blobPaths[0], MAX_PATH, "%s\\%s", kHandoffDir, kDefaultBlob);
        blobCount = 1;
    }

    Shim shim;
    if (!LoadShim(shim))
    {
        if (g_report != nullptr)
        {
            fclose(g_report);
        }
        return 1;
    }
    if (shim.version != nullptr)
    {
        Report("  shim: %s", shim.version());
    }

    long rc = shim.load(installDir);
    Report("[A] PlcMcpCrypt_Load(\"%s\") -> rc=%ld", installDir, rc);
    if (rc != 0)
    {
        Report("  vendor load FAILED (error %lu); cannot continue", GetLastError());
        if (g_report != nullptr)
        {
            fclose(g_report);
        }
        return 1;
    }
    if (shim.vendorPath != nullptr)
    {
        Report("  vendor: %s", shim.vendorPath());
    }

    PhaseB_Keys(shim);
    PhaseC_RoundTrip(shim);
    PhaseD_Equivalence(shim);
    PhaseE_Blobs(shim, blobPaths, blobCount);
    PhaseF_KeyExport(shim);

    // Red line: release the vendor object and library handles before exit.
    shim.unload();
    Report("[G] PlcMcpCrypt_Unload -> vendor object + library released");

    if (g_report != nullptr)
    {
        fclose(g_report);
        g_report = nullptr;
    }
    Report("=== done; report written to %s ===", reportPath);
    return 0;
}
