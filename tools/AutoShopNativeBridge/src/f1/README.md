# F1: write-surface signature verification harness (LocalSimulator)

**NOT in CI. Local simulator only.** The harness hard-checks the target to
127.0.0.0/8 before any vendor call and never runs against anything else.

## Contents

| File | Purpose |
|---|---|
| `WriteProbe.cpp` | F1 verification harness (x86): read-only closed loop (SetCommunicatConfig/IsEthOnLine/GetExecMode/GetGCMState) + minimal write closed loop (vendor `CSoftAddrMap` ctor + `tagSoftAddr` ctor5 + `SetAt` + `WriteElemValue`) + Modbus FC3 readback on 127.0.0.1:502 + Force/Unforce call receipts. **Download is never called.** |
| `gen_write_def.py` | Generates the import lib `.def` from the vendor export table (write-surface + read-only baseline). The harness only references the read-only statics plus WriteElemValue/Force/Unforce; the linker pulls only referenced symbols, so Download/EnableUpload/CompleteDownloadCmd/SettingPassword stay un-imported. |
| `build_f1.cmd` | Build script (VS2022 v143 x86). Outputs to `%LOCALAPPDATA%\AutoShopNativeBridge-f1-run\` (not committed). Requires copying `CommunicateEthernet.dll` + `InterfaceMgr.dll` from the vendor install (read-only copies) next to the exe before running. |
| `disasm_tool.py` | Static-analysis helper used to produce the evidence in `docs/inovance-autoshop-write-signatures.md` (VA-range x86 disassembly of the vendor binaries; read-only, offline). |

## Evidence / results

- Full write-signature restoration, enum value tables, map construction
  sequence and the (negative) simulator write-loop verdict:
  **`docs/inovance-autoshop-write-signatures.md`**.
- Runtime verdicts (2026-10-28, loopback daemon PID 61624, cleaned up after):
  - `WriteElemValue` / `Unforce` exports in CommunicateEthernet.dll 4.12.0.2
    are **stubs** (`mov eax,0xF390; ret` = 62352, no behavior, no network
    traffic, no daemon log) - verified at runtime AND by disassembly.
  - `Force` (real body) **terminates the process in a way user SEH cannot
    catch** even with the vendor-forwarding-confirmed parameter order
    (reproducible x2) - the gate for force stays closed.
- Reports: `%TEMP%\plcmcp-handoff\f1-write-probe\` (report.txt =
  full green run; report-run1-skip-force.txt; report-run2-force-crash.txt).

## Safety posture

- Loopback hard check (`ParseLoopbackTarget`): 127.0.0.0/8 or "localhost" only,
  rejected before `SetCommunicatConfig`.
- SEH-wrapped vendor calls (`-32004` style receipts); note that Force escapes
  even SEH (documented negative).
- No registry writes, no install-directory writes; the only file output is the
  report under `%TEMP%\plcmcp-handoff\f1-write-probe\`.
- Version-locked to AutoShop 4.12.0.2 x86; reverse-engineered, no vendor
  endorsement.
