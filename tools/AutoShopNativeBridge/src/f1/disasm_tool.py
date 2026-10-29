#!/usr/bin/env python3
"""F1 static-analysis helper: disassemble a VA range from a PE (x86) file.

Read-only evidence tool for docs/inovance-autoshop-write-signatures.md.
Local-simulator / offline analysis only; never contacts any PLC.

Usage:
  python disasm_tool.py <pe_path> <image_base_hex> <va_start_hex> <length>
"""
import struct
import sys


def load_sections(path):
    with open(path, "rb") as h:
        data = h.read()
    e_lfanew = struct.unpack_from("<I", data, 0x3C)[0]
    nsec = struct.unpack_from("<H", data, e_lfanew + 6)[0]
    optsize = struct.unpack_from("<H", data, e_lfanew + 0x14)[0]
    secs = []
    sec_off = e_lfanew + 0x18 + optsize
    for i in range(nsec):
        off = sec_off + i * 40
        nm = data[off:off + 8].rstrip(b"\0").decode("latin-1")
        vsize, va, rsize, raw = struct.unpack_from("<IIII", data, off + 8)
        secs.append((nm, va, vsize, raw))
    return data, secs


def va_to_off(secs, va, image_base):
    rva = va - image_base
    for nm, sva, vsize, raw in secs:
        if sva <= rva < sva + vsize:
            fo = rva - sva + raw
            if fo < len_data:
                return fo
            return None
    return None


def main():
    from capstone import Cs, CS_ARCH_X86, CS_MODE_32
    path = sys.argv[1]
    base = int(sys.argv[2], 16)
    start = int(sys.argv[3], 16)
    length = int(sys.argv[4], 16) if len(sys.argv) > 4 else 0x120
    data, secs = load_sections(path)
    globals()["len_data"] = len(data)
    rva = start - base
    for nm, sva, vsize, raw in secs:
        if sva <= rva < sva + vsize:
            fo = rva - sva + raw
            break
    else:
        print("VA not mapped"); return 1
    code = data[fo:fo + length]
    md = Cs(CS_ARCH_X86, CS_MODE_32)
    md.detail = False
    for ins in md.disasm(code, start):
        print(f"  {ins.address:08X}  {ins.mnemonic:8} {ins.op_str}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
