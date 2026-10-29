#!/usr/bin/env python3
"""G2 T1: locate download payload filename strings + their xref call sites."""
import sys
import pe_tool

NEEDLES = [
    ("stprog.o", True), ("stprog.o", False),
    ("symbol.sym", True), ("varsinfo.foid", True), ("userprog.foid", True),
    ("net.down", True), ("net.tmp", True), ("net.up", True),
    ("project.hcpp", True), (".hcpp", True), (".hcpp", False),
    ("NetConfig.cfg", True), ("TcpModbusConfig.cfg", True),
    ("Compile", True), ("Download", False),
]

def xrefs_text(pe, va, only_text=True):
    """scan .text for imm32 == va (LE)."""
    import struct
    needle = struct.pack("<I", va)
    out = []
    for nm, sva, vsize, raw in pe["secs"]:
        if only_text and nm != ".text":
            continue
        blob = pe["data"][raw:raw + vsize]
        pos = 0
        while True:
            k = blob.find(needle, pos)
            if k < 0:
                break
            out.append(pe["image_base"] + sva + k)
            pos = k + 1
    return out


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else r"D:/Inovance Control/AutoShop/AutoShop.exe"
    pe = pe_tool.load(path)
    for needle, wide in NEEDLES:
        hits = pe_tool.find_strings(pe, needle, wide)
        print(f"{'w' if wide else 'a'} {needle!r}: {len(hits)} hits {[hex(h) for h in hits[:6]]}")
        for h in hits[:6]:
            xs = xrefs_text(pe, h)
            if xs:
                print(f"    xref@ {needle!r} {hex(h)}: {[hex(x) for x in xs[:8]]}")

if __name__ == "__main__":
    main()
