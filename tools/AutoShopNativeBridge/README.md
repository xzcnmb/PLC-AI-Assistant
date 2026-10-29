# AutoShopNativeBridge

x86 native bridge for the Inovance AutoShop plugin framework and communication
stack (AutoShop V4.12.0.2, `D:\Inovance Control\AutoShop`).

**All interfaces here are reverse-engineered. There is no vendor endorsement.
Everything is version-locked to AutoShop 4.12.0.2 and x86.** Evidence,
confidence levels and limitations live in
[`docs/inovance-autoshop-native-bridge.md`](../../docs/inovance-autoshop-native-bridge.md).

## Layout

| Path | Contents |
|---|---|
| `src/plugin/PlcMcpBridgeAbi.h` | Reconstructed vendor ABI (VS2008 string image, `PluginInfo`, `CPluginBase` vtable contract, `CSoftAddrMap`/`tagSoftAddr` layouts) with per-field evidence comments |
| `src/plugin/PlcMcpBridgePlugin.cpp` | M1 plugin DLL: `CreatePlugin` (current contract) + the legacy 7 exports; marker logging; hosts the M4 pipe server |
| `src/plugin/PlcMcpBridgeNoFactory.cpp` | Negative-test DLL (no `CreatePlugin`): must be rejected cleanly by the framework |
| `src/pipe/BridgePipeServer.*` | M4 named-pipe JSON-line server (read-only) that runs inside the plugin host |
| `src/shim/CommStackShim.cpp` | M3 shim: static import of the five read-only `CCommunicateEth` statics, flat C API |
| `src/crypto/CryptoShim.cpp` | Crypto shim: runtime resolution (GetProcAddress whitelist) of `EnDecrypt.dll` CAESAlg/CEncryptAlg exports (incl. the private-but-exported `GetPassword`/`GetPasswordN`/`GetKey`/`CreateNewKey`), flat C API `PlcMcpCrypt_*` |
| `src/crypto/CryptoHost.cpp` | Crypto verification harness: key export dump, vendor round trip, FIPS-197 self-test + vendor-vs-standard AES-128-ECB equivalence, de-interleaved-payload verify loop, CEncryptAlg key probe |
| `src/host/TestHost.cpp` | Verification harness: replicates the service's plugin load sequence, pipe round trip, comm-stack probes |
| `scripts/gen_import_lib.py` | Generates the `.def`/import library for `CommunicateEthernet.dll` (only the read-only exports) |
| `build.cmd` | Build script (VS2022 v143, x86) |

## Build

Requirements: Visual Studio 2022 (v143 toolset, x86 support) and Python 3.

```bat
tools\AutoShopNativeBridge\build.cmd
```

The script locates `vcvarsall.bat` under
`C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\`
(adjust the `VCVARS` variable for other installations), generates the import
library from the vendor export table, and compiles all four artifacts.

## Artifacts (output directory)

Binaries are **not** committed to the repository. `build.cmd` writes them to

```
%LOCALAPPDATA%\AutoShopNativeBridge-run\        (default)
```

overridable with `set PLCMCP_NATIVE_RUN=<dir>` or by passing the directory as
the first argument (`build.cmd D:\some\dir`):

| File | Role |
|---|---|
| `PlcMcpBridgePlugin.dll` | M1 plugin (CreatePlugin + legacy 7 exports + pipe server) |
| `PlcMcpBridgeNoFactory.dll` | M1 negative test (no factory export) |
| `CommStackShim.dll` | M3 read-only comm-stack shim (flat C API `PlcMcpComm_*`) |
| `TestHost.exe` | Verification harness |
| `Plugin.dll`, `Plugin.lib` | Vendor import library, copied from `AutoVarShared\` |
| `mfc90u/msvcp90/msvcr90.dll` | VC90 runtimes copied from `WinSxS\Fusion\...` (needed so the vendor DLLs load outside their host) |

## Run the verification harness

```bat
cd %LOCALAPPDATA%\AutoShopNativeBridge-run
TestHost.exe                :: plugin contract + pipe round trip + comm probes
TestHost.exe --comm-only    :: comm-stack probes only
TestHost.exe --plugin-only  :: plugin contract + pipe round trip only
```

The harness prints a report and writes
`%TEMP%\plcmcp-handoff\m1-harness-report.txt`. Lifecycle markers of the plugin
(DllMain / CreatePlugin) are appended to `%TEMP%\plcmcp-handoff\m1-marker.log`
with host PID, DLL path, host executable and timestamp.

## Environment overrides

| Variable | Meaning |
|---|---|
| `PLCMCP_BRIDGE_PIPE=1` | Start the named-pipe server when `CreatePlugin` runs |
| `PLCMCP_BRIDGE_INSTALL_DIR` | AutoShop installation directory (default `D:\Inovance Control\AutoShop`), used to resolve `CommunicateEthernet.dll` / `InterfaceMgr.dll` |

## Loading the plugin into a vendor host

No installed host on this machine loads third-party plugins: `InoVarSharedService.exe`
loads exactly two hardcoded DLLs (`PluginLogger.dll`, `PluginCommunicate.dll`)
from its own directory after pre-registering their `PluginInfo`, and `AutoShop.exe`
does not use the `Plugin.dll` framework at all. See
`docs/inovance-autoshop-native-bridge.md` section M1 for the full evidence.
The plugin is therefore verified inside the bundled `TestHost.exe` harness,
which replicates the service's load sequence against the real `Plugin.dll`.

## Crypto shim (EnDecrypt.dll) — D2 experiment

`src/crypto/` holds the shim and harness for the EnDecrypt.dll (AutoShop AES
library) key-export feasibility experiment. Full evidence lives in
[`docs/inovance-autoshop-encryption.md`](../../docs/inovance-autoshop-encryption.md).

Key facts (all reverse-engineered, no vendor endorsement):

* EnDecrypt.dll (x86, VC10-era, PE timestamp 2016-01-14) hardcodes **8 16-byte
  ASCII AES-128 keys** in .rdata (`HC.0793.*` / `Hc.1012.*` families, returned
  by the exported `GetPassword`/`GetPasswordN` with PASSWORDTYPE 0..3) plus **8
  hardcoded 32-bit key words** (32 B) inlined in `CEncryptAlg::GetKey`.
  `CreateNewKey`/`GetKey` are declared private but are in the export table, so
  `GetProcAddress` reaches them like any public export.
* The vendor `CAESAlg::Cipher/InvCipher` are **standard AES-128-ECB single-block**
  primitives (10 rounds, standard S-box) — proven 8/8 key-equivalent to an
  independent standard implementation after a FIPS-197 self-test.
* The shim resolves a whitelist of 12 exports at runtime (no import library is
  wired into `build.cmd`); bulk encryptors (`CEncryptAlg::Encrypt/EncryptBlock/
  EncryptFrame`) and the `CEnDecryptAlg` composition class are deliberately not
  resolved. Read-only: no file writes, no registry, no network, no PLC.
* AutoShop itself never references EnDecrypt.dll; the .hcp Main blob payload
  does **not** decrypt with any of the exported keys (verified through the
  vendor's own InvCipher on both template samples).

Build (same toolchain as `build.cmd`, not wired into it — VS2022 v143 x86;
`/Fo:` keeps the intermediate .obj files out of the current directory):

```bat
call "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvarsall.bat" x86
set RUN=%LOCALAPPDATA%\AutoShopNativeBridge-run
cl /nologo /W3 /EHsc /LD /MT src\crypto\CryptoShim.cpp /Fo:%RUN%\CryptoShim.obj /Fe:%RUN%\CryptoShim.dll
cl /nologo /W3 /EHsc /MT src\crypto\CryptoHost.cpp /Fo:%RUN%\CryptoHost.obj /Fe:%RUN%\CryptoHost.exe
```

Run the verification harness (default blob = the P3 recon artifact; `--blob=`
repeatable, `--install=` overridable):

```bat
cd %LOCALAPPDATA%\AutoShopNativeBridge-run
CryptoHost.exe
```

The harness prints a report and writes
`%TEMP%\plcmcp-handoff\crypto-harness-report.txt`, and releases the vendor
object + library handles before exit (`PlcMcpCrypt_Unload`).

## Safety

* Read-only by construction: the shim imports no write/force/download export,
  and the pipe protocol has no write method.
* The plugin never writes to the installation; experiment copies are removed
  after use (see the marker log).
* No physical PLC access exists on this host; all comm-stack calls are
  call-return evidence only.
