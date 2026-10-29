#!/usr/bin/env python3
"""F1: generate a .def file with the CCommunicateEth WRITE-SURFACE exports.

The communication stack ships no import library, so the import library for
WriteProbe.exe is generated from the vendor export table. The .def lists the
write-surface names (documented in docs/inovance-autoshop-write-signatures.md);
the harness only REFERENCES the read-only statics plus WriteElemValue/Force/
Unforce - the linker pulls only referenced symbols, so Download/EnableUpload/
CompleteDownloadCmd/SettingPassword/... stay un-imported even though listed.

Usage:
    python gen_write_def.py <CommunicateEthernet.dll> <out.def>
"""
import re
import subprocess
import sys
import tempfile
import os

DUMPBIN = (
    r"C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Tools"
    r"\MSVC\14.34.31933\bin\Hostx64\x64\dumpbin.exe"
)

# CCommunicateEth exports the harness may reference (write surface + the S2
# read-only baseline). Anything NOT in this list is never imported.
ALLOWED_MARKERS = (
    "WriteElemValue",
    "Force@CCommunicateEth",
    "Unforce@CCommunicateEth",
    "ReadElemValue",
    "GetExecMode",
    "GetGCMState@CCommunicateEth",
    "SetCommunicatConfig",
    "IsEthOnLine",
    # Listed for signature documentation only; never referenced by WriteProbe:
    "Download@CCommunicateEth",
    "EnableUpload@CCommunicateEth",
    "CompleteDownloadCmd",
    "SimpleCommand@CCommunicateEth",
    "SingleCommand@CCommunicateEth",
    "SimpleCommonCmd",
    "SingleSimpleCommand",
    "SettingPassword",
    "CheckPassword",
)


def main() -> int:
    if len(sys.argv) != 3:
        print(__doc__)
        return 2
    dll_path, out_path = sys.argv[1], sys.argv[2]

    with tempfile.NamedTemporaryFile(
        mode="w", suffix=".txt", delete=False, encoding="latin-1"
    ) as tmp:
        tmp_path = tmp.name
    try:
        with open(tmp_path, "w", encoding="latin-1") as out:
            subprocess.run([DUMPBIN, "/exports", dll_path], stdout=out, check=True)
        with open(tmp_path, "r", encoding="latin-1") as handle:
            output = handle.read()
    finally:
        os.unlink(tmp_path)

    names = []
    for line in output.splitlines():
        match = re.match(r"\s*(\d+)\s+([0-9A-F]+)\s+([0-9A-F]+)\s+(\S+)", line)
        if not match:
            continue
        name = match.group(4)
        if "CCommunicateEth" not in name:
            continue
        if not any(marker in name for marker in ALLOWED_MARKERS):
            continue
        names.append((match.group(2), name))

    found = {m for _, n in names for m in ALLOWED_MARKERS if m in n}
    missing = [m for m in ALLOWED_MARKERS if m not in found]
    if missing:
        print(f"[gen_write_def] markers missing from export table: {missing}")
        return 1

    with open(out_path, "w", encoding="ascii") as handle:
        handle.write("LIBRARY CommunicateEthernet.dll\n")
        handle.write("EXPORTS\n")
        for rva, name in sorted(names, key=lambda t: (t[0], t[1])):
            handle.write(f"  {name}\n")
    print(f"[gen_write_def] wrote {len(names)} exports to {out_path}")
    for rva, name in names:
        print(f"  RVA 0x{rva} {name[:120]}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
