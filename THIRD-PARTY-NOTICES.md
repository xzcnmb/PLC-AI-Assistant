# Third-party notices

This file records the direct dependencies and referenced projects currently used by PLC-MCP. Each dependency's license must be retained when distributing binaries or source. Transitive dependency notices should be generated from the locked restore graph before a release.

## Direct runtime dependency

### S7netplus 0.20.0

- Package: `S7netplus` 0.20.0
- Project: <https://github.com/killnine/s7netplus>
- Repository metadata in the restored package identifies the project as a continuation of the S7.Net library and the repository is MIT licensed.
- License text: <https://github.com/S7NetPlus/s7netplus/blob/main/License.txt>
- Usage here: read-only S7comm session/read adapter. The library has write APIs, but PLC-MCP does not expose them through `IPlcProtocolAdapter` or MCP tools.

## Reference projects and protocol specifications

These are research references, not copied source in this repository. Before copying code, pin a commit and import its license notice.

- `plctap`: <https://github.com/ymxc152/plctap>. LICENSE checked on 2026-09-23: MIT License, Copyright (c) 2026 plctap contributors. Used as an architectural reference for deterministic codecs, protocol adapters, target locks, diagnostics, and gated tools.
- S7NetPlus upstream: <https://github.com/S7NetPlus/s7netplus>. Used for API and S7comm reference; direct runtime package is listed above.
- OPC UA for PLCopen: <https://opcfoundation.cn/guifan/60_44>. Specification/reference material only.
- IronPLC: <https://github.com/ironplc/ironplc>. Compiler/MCP architecture reference only.
- Beremiz: <https://github.com/beremiz/beremiz>. IEC 61131-3/open tooling reference only.
- OpenPLC: <https://github.com/thiagoralves/OpenPLC_v3>. Soft PLC/testbed reference only.
- Mitsubishi GX Works3 bridge: <https://github.com/coder007rahul/gxworks3-mcp-bridge>. UI automation safety/versioning reference only; not a dependency.

## Vendor software and documentation

Siemens TIA Portal/Openness, Siemens Micro/WIN SMART, Omron Sysmac Studio/CX tools, Mitsubishi GX Works/MX Component, Inovance AutoShop/InoProShop, CODESYS and any OPC UA/CIP/fieldbus device packages are external commercial software. Their licenses, SDK terms, profile versions and runtime authorizations are not granted by this repository. A production worker must run only when the customer has installed and licensed the relevant software.

## Release checklist

Before publishing a binary or source release:

1. Run `dotnet list PlcMcp.slnx package --include-transitive` and archive the restore graph.
2. Include the exact license files for direct and transitive dependencies.
3. Keep this notice and upstream copyright text in source/binary distribution packages.
4. Re-run the protocol and MCP tests in an isolated network; no test should target a production IP.
