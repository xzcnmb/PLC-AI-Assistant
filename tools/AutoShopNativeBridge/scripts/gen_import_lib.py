#!/usr/bin/env python3
"""Generate a .def file with the read-only CCommunicateEth statics.

The communication stack ships no import library, so the import library for
CommStackShim.dll is generated from the vendor export table. Only the five
read-only methods are imported; write/force/download entry points are
deliberately excluded so the shim cannot call them even by accident.

Usage:
    python gen_import_lib.py <CommunicateEthernet.dll> <out.def>
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

READ_ONLY_MARKERS = (
    "ReadElemValue",
    "ReadSeriesWordElem",
    "GetExecMode",
    "GetPlcLable",
    "GetGCMState",
    # Connection configuration and TCP liveness probe. Both are read-only with
    # respect to the PLC: SetCommunicatConfig only stores the local connection
    # target, IsEthOnLine performs the vendor's own TCP reachability probe.
    # Write/force/download entry points remain deliberately excluded.
    "SetCommunicatConfig",
    "IsEthOnLine",
)


def main() -> int:
    if len(sys.argv) != 3:
        print(__doc__)
        return 2
    dll_path, out_path = sys.argv[1], sys.argv[2]

    # dumpbin must be run with stdout redirected to a file: its console output
    # is not reliably captured through a pipe on all Windows configurations.
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
        if not any(marker in name for marker in READ_ONLY_MARKERS):
            continue
        names.append(name)

    found_markers = {marker for name in names for marker in READ_ONLY_MARKERS if marker in name}
    missing = [marker for marker in READ_ONLY_MARKERS if marker not in found_markers]
    if missing:
        print(f"[gen_import_lib] exports missing for markers: {missing}; found {len(names)}")
        for name in names:
            print("  " + name)
        return 1

    with open(out_path, "w", encoding="ascii") as handle:
        # The LIBRARY statement fixes the target DLL name of the generated
        # import library (lib.exe otherwise derives it from the .def file name).
        handle.write("LIBRARY CommunicateEthernet.dll\n")
        handle.write("EXPORTS\n")
        for name in sorted(names):
            handle.write(f"  {name}\n")
    print(f"[gen_import_lib] wrote {len(names)} exports to {out_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
