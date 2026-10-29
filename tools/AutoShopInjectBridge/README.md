# AutoShopInjectBridge

x86 process-injection bridge for Inovance AutoShop (E2, phase three): an
injector, a bridge DLL that is loaded **into a self-started AutoShop
instance**, and the C# client/worker live on the other side of a named pipe.

**All interfaces are reverse-engineered. There is no vendor endorsement.
Everything is version-locked to AutoShop 4.12.0.2 and x86.** Evidence lives in
[`docs/inovance-autoshop-inject-bridge.md`](../../docs/inovance-autoshop-inject-bridge.md)
(architecture, operation surface, gating rules, security red lines) together
with [`docs/inovance-autoshop-native-bridge.md`](../../docs/inovance-autoshop-native-bridge.md)
(S2 signatures this bridge reuses).

## Layout

| Path | Contents |
|---|---|
| `src/injector/AutoShopInjectBridgeInjector.cpp` | T1 injector: `CreateProcessW(CREATE_SUSPENDED)` -> `CreateRemoteThread(LoadLibraryA)` -> remote `Bridge_Init` -> `ResumeThread`; PE/image validation; PID ownership records; stop/eject/status verbs |
| `src/bridge/BridgeApi.h` | Public contract: exports, protocol constants, error codes, gate defaults |
| `src/bridge/BridgeInternal.h` | Config + JSON helpers + `VendorComm` declarations |
| `src/bridge/VendorComm.cpp` | In-process vendor resolution: read-only statics with proven S2 signatures; the F2 in-process element read (map filled via the non-stub tagSoftAddr ctor + `CMap::SetAt`, value slot read); the F1 compile-all chain (root -> cmpMgr -> entry, per-call SEH isolation with step diagnostics); write/force/download exports resolved-but-never-invoked; loopback-first discipline; `pending-<pid>.json` config |
| `src/bridge/BridgePipeServer.h/.cpp` | Named-pipe JSON-line server (threaded + manual Pump) with the whitelisted operation surface and per-request SEH |
| `src/bridge/AutoShopInjectBridge.cpp` | DllMain + the five flat C exports (`Bridge_Init/SetPipeName/Pump/Shutdown/Version`) |
| `src/host/BridgeTestHost.cpp` | In-process harness: loads the DLL, pumps manual mode, drives a full JSON round trip; asserts honest Unsupported/refusal answers |
| `build.cmd` | VS2022 v143 x86 build; artifacts outside the repository |

## Build

```bat
tools\AutoShopInjectBridge\build.cmd
```

Artifacts land in `%LOCALAPPDATA%\AutoShopInjectBridge-run\` (override with
`PLCMCP_INJECT_RUN` or the first argument). No import library is required:
inside the AutoShop process the vendor modules are already loaded and the
bridge resolves them with `GetProcAddress` / an in-memory export-table scan.

## Operation surface state (F3, 2026-10-28)

* `compile` — **OPEN by default** (F1 signature evidence + E3 x3 + smoke
  verified): the compile-all chain root `0x1581A70` -> cmpMgr `0x1581CA0` ->
  entry `0x15A2EC0` is invoked in-process (async submit; offline behaviour,
  no PLC action). The current-project gate `0x161B620` (this = *(root+0x24)
  via accessor `0x1581C20`, auto-disassembled from the vendor call site) is
  checked first: it returns a clear error instead of the vendor's modal
  62220 dialog when no project is open. Addresses are VAs
  (`--compile-rva <hex|none>`, `--compile-root-rva`, `--compile-member-rva`,
  `--compile-gate-rva`, `--compile-gate-owner-rva`); the exe base
  (0x400000, RELOCS_STRIPPED) and the PE SizeOfImage bounds are re-verified
  before any call, each vendor call runs in its own SEH guard whose step
  marker/exception code is reported on failure. Artifacts appear under
  `<projectDir>\Compile` within seconds.
* `read_elements` — the map is filled through the REAL vendor primitives
  (tagSoftAddr 5-arg ctor `0x1C10` + `CMap::SetAt` `0x12AB0`; the base
  `AddElem` is a stub) and `ReadElemValue(mode=0, level=0)` is accepted
  (rc=0), but the vendor **value write-back is NOT closable** (smoke: values
  stayed 0 against a Modbus-verified non-zero baseline; passing the main
  frame HWND instead of NULL faults) — the UI result pipeline
  (`ReadMemory`/`CMonitElemBuffer`) is unreconstructed. Values are call-level
  evidence, never PLC data.
* `write_elements` / `force` / `download` — reservations stay CLOSED: the
  eth-channel write exports are deterministic stubs (return 0xF390=62352,
  F1-verified) and the vendor Force body terminates the process in a way
  user SEH cannot catch. `resolved:true, invoked:false` -- never success.

## Safety model (red lines)

* Injection happens **only** into a process the injector itself started; there
  is no "inject into PID" verb. A user-interactive AutoShop instance can never
  be targeted.
* The target exe and the bridge DLL are PE-validated (x86, expected base name)
  before any cross-process write.
* Started PIDs are journaled (`owned-instances.jsonl`); `stop`/`eject`
  re-verify the live identity (image path, base name, creation time) and
  refuse on any mismatch (PID reuse, foreign process).
* Rollback: a failed launch terminates its own suspended child and removes the
  pending config; `stop --tree` only touches children whose image lives under
  the recorded install directory.
* Loopback-first: the bridge refuses to drive the vendor communication stack
  until `set_target` stored a `127.0.0.0/8` address; `set_target` refuses
  anything else before storing it. The vendor default target (192.168.1.1)
  is therefore never contacted by this bridge.
* Write/force/download are reservations: gates (C# + bridge config) and the
  loopback check run, the vendor export is resolved, but the vendor function
  is **never invoked** until the parameter semantics are reconstructed and
  verified. The answers state `resolved:true, invoked:false` -- never success.
* No registry writes, no installation-directory writes, no third-party
  injection library.

## Harness

```bat
cd %LOCALAPPDATA%\AutoShopInjectBridge-run
BridgeTestHost.exe
```

Prints a per-check report and writes
`%TEMP%\plcmcp-handoff\inject-bridge\harness-report.txt`.

## Environment overrides

| Variable | Meaning |
|---|---|
| `PLCMCP_INJECT_RUN` | Output directory for the native artifacts (default `%LOCALAPPDATA%\AutoShopInjectBridge-run`) |

The bridge reads `%TEMP%\plcmcp-handoff\inject-bridge\pending-<pid>.json`
(written by the injector before `ResumeThread`, consumed and deleted on init)
for the pipe name and the bridge-side gates.
