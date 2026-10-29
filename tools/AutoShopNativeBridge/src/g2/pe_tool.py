#!/usr/bin/env python3
"""G2 static-analysis helper: PE section/IAT/disasm utilities for download-closure RE.

Read-only evidence tool for docs/inovance-autoshop-download-closedloop.md.
Local-simulator / offline analysis only; never contacts any PLC.

Modes (subcommand style):
  sections <pe>
  exports <pe> [filter-substring]
  imports <pe> [filter-substring]           # imported symbols w/ DLL, and IAT slot VA
  callsites <pe> <iat_va_hex>               # ff 15 <iat> call sites (VA)
  dis <pe> <va_hex> [len_hex]               # linear disasm
  backward <pe> <call_va_hex> [backbytes]   # disasm window ending at the call site
  strings <pe> <needle> [--wide]            # find literal VA(s)
"""
import re
import struct
import sys


def load(path):
    with open(path, "rb") as h:
        data = h.read()
    e_lfanew = struct.unpack_from("<I", data, 0x3C)[0]
    machine = struct.unpack_from("<H", data, e_lfanew + 4)[0]
    nsec = struct.unpack_from("<H", data, e_lfanew + 6)[0]
    optsize = struct.unpack_from("<H", data, e_lfanew + 0x14)[0]
    image_base = struct.unpack_from("<I", data, e_lfanew + 0x18 + 0x1C)[0]
    size_of_image = struct.unpack_from("<I", data, e_lfanew + 0x18 + 0x28)[0]
    dirs = struct.unpack_from("<32I", data, e_lfanew + 0x18 + 0x60)
    secs = []
    sec_off = e_lfanew + 0x18 + optsize
    for i in range(nsec):
        off = sec_off + i * 40
        nm = data[off:off + 8].rstrip(b"\0").decode("latin-1")
        vsize, va, rsize, raw = struct.unpack_from("<IIII", data, off + 8)
        secs.append([nm, va, max(vsize, rsize), raw])
    return dict(data=data, secs=secs, image_base=image_base, machine=machine,
                size_of_image=size_of_image, dirs=dirs, e_lfanew=e_lfanew)


def rva2off(pe, rva):
    for nm, sva, vsize, raw in pe["secs"]:
        if sva <= rva < sva + vsize:
            fo = rva - sva + raw
            if fo < len(pe["data"]):
                return fo
    return None


def off2rva(pe, off):
    for nm, sva, vsize, raw in pe["secs"]:
        if raw <= off < raw + vsize:
            return sva + (off - raw)
    return None


def read_cstr(data, fo, wide=False):
    out = []
    if wide:
        while fo + 1 < len(data):
            ch = struct.unpack_from("<H", data, fo)[0]
            if ch == 0:
                break
            out.append(ch)
            fo += 2
        return "".join(chr(c) for c in out)
    while fo < len(data) and data[fo] != 0:
        out.append(data[fo])
        fo += 1
    return bytes(out).decode("latin-1")


def dir_rva(pe, idx):
    rva = pe["dirs"][idx * 2] & 0xFFFFFFFF
    size = pe["dirs"][idx * 2 + 1] & 0xFFFFFFFF
    return (pe["image_base"] + rva, size) if rva else (0, 0)


def iter_exports(pe):
    """yield (ordinal, rva, name) for export dir; forwarder rvamap None ok."""
    va, size = dir_rva(pe, 0)
    if not va:
        return
    fo = rva2off(pe, va - pe["image_base"])
    if fo is None:
        return
    d = pe["data"]
    n_named = struct.unpack_from("<I", d, fo + 0x18)[0]
    aEAT = struct.unpack_from("<I", d, fo + 0x1C)[0]
    aNPT = struct.unpack_from("<I", d, fo + 0x20)[0]
    aOAT = struct.unpack_from("<I", d, fo + 0x24)[0]
    fEAT = rva2off(pe, aEAT)
    fOAT = rva2off(pe, aOAT)
    fNPT = rva2off(pe, aNPT)
    if None in (fEAT, fOAT, fNPT):
        return
    for i in range(n_named):
        name_rva = struct.unpack_from("<I", d, fNPT + i * 4)[0]
        ordinal = struct.unpack_from("<H", d, fOAT + i * 2)[0]
        fn_rva = struct.unpack_from("<I", d, fEAT + i * 4)[0]
        nf_o = rva2off(pe, name_rva)
        nm = read_cstr(d, nf_o) if nf_o else ""
        yield ordinal, fn_rva, nm


def iter_imports(pe):
    """yield (dll, symbol, iat_va, thunk_val). Thunk-driven: walk the IAT itself
    in 4-byte steps until a 0 sentinel — immune to bogus descriptor entries that
    appear in big MSVC exes. Only IATs that begin inside a mapped section are used."""
    va, size = dir_rva(pe, 1)
    if not va:
        return
    fo = rva2off(pe, va - pe["image_base"])
    d = pe["data"]
    ib = pe["image_base"]
    seen_starts = set()
    i = 0
    for _ in range(400):
        ent = fo + i * 20
        if ent + 20 > len(d):
            break
        oft_rva, ts, fwd, name_rva, ft_rva = struct.unpack_from("<5I", d, ent)
        if ft_rva == 0 and oft_rva == 0 and name_rva == 0:
            break
        i += 1
        if ft_rva in seen_starts or ft_rva == 0:
            continue
        seen_starts.add(ft_rva)
        dll_o = rva2off(pe, name_rva)
        dll = read_cstr(d, dll_o) if dll_o else "?"
        j = 0
        while True:
            slot_off = rva2off(pe, ft_rva + j * 4)
            if slot_off is None or slot_off + 4 > len(d):
                break
            val = struct.unpack_from("<I", d, slot_off)[0]
            if val == 0:
                break
            if val & 0x80000000:
                sym = f"#{val & 0xFFFF}"
            else:
                hfo = rva2off(pe, val)
                if hfo is None or hfo + 2 > len(d):
                    break
                sym = read_cstr(d, hfo + 2)
            yield dll, sym, ib + ft_rva + j * 4, val
            j += 1


def find_callsites(pe, iat_va):
    """scan .text for ff 15 <iat_va le32> (call dword ptr [iat])."""
    d = pe["data"]
    needle = b"\xff\x15" + struct.pack("<I", iat_va)
    hits = []
    for nm, sva, vsize, raw in pe["secs"]:
        if nm != ".text":
            continue
        blob = d[raw:raw + vsize]
        pos = 0
        while True:
            k = blob.find(needle, pos)
            if k < 0:
                break
            hits.append(sva + pe["image_base"] + k)
            pos = k + 1
    return hits


def disasm(pe, va, length, start_align=True):
    from capstone import Cs, CS_ARCH_X86, CS_MODE_32
    rva = va - pe["image_base"]
    fo = rva2off(pe, rva)
    if fo is None:
        return []
    code = pe["data"][fo:fo + length]
    if not code:
        return []
    try:
        md = Cs(CS_ARCH_X86, CS_MODE_32)
        md.skipdata = True
    except Exception as e:
        raise SystemExit(f"capstone unavailable: {e}")
    out = []
    for ins in md.disasm(code, va):
        out.append(ins)
    return out


def ann(ins_list, pe):
    """annotate call/jmp targets + pushed string refs (best effort)."""
    lines = []
    for ins in ins_list:
        line = f"  {ins.address:08X}  {ins.mnemonic:8} {ins.op_str}"
        note = None
        if ins.mnemonic == "call" and ins.op_str.startswith("0x"):
            tgt = int(ins.op_str, 16)
            note = annotate_target(pe, tgt)
        elif ins.mnemonic in ("jmp",) and ins.op_str.startswith("0x"):
            tgt = int(ins.op_str, 16)
            note = annotate_target(pe, tgt)
        if note:
            line += f"\n            ; -> {note}"
        lines.append(line)
    return lines


def annotate_target(pe, tgt):
    if pe["image_base"] <= tgt < pe["image_base"] + pe["size_of_image"]:
        rva = tgt - pe["image_base"]
        for ordn, erva, name in iter_exports(pe):
            if erva == rva:
                return f"EXPORT {name}"
        return None
    return None


def find_strings(pe, needle, wide=False):
    enc = needle.encode("utf-16le") if wide else needle.encode("latin-1")
    hits = []
    d = pe["data"]
    pos = 0
    while True:
        k = d.find(enc, pos)
        if k < 0:
            break
        rva = off2rva(pe, k)
        if rva is not None:
            hits.append(pe["image_base"] + rva)
        pos = k + 1
    return hits


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return 2
    mode, path = sys.argv[1], sys.argv[2]
    pe = load(path)
    if mode == "sections":
        for nm, sva, vsize, raw in pe["secs"]:
            print(f"{nm:8} VA={pe['image_base']+sva:08X} VSZ={vsize:08X} RAW={raw:08X}")
        print(f"image_base={pe['image_base']:08X} size={pe['size_of_image']:08X}")
    elif mode == "exports":
        filt = sys.argv[3] if len(sys.argv) > 3 else ""
        for ordn, erva, name in iter_exports(pe):
            if filt.lower() in name.lower():
                print(f"ord={ordn} RVA=0x{erva:05X} {name}")
    elif mode == "imports":
        filt = sys.argv[3] if len(sys.argv) > 3 else ""
        seen = {}
        for dll, sym, slot, _ in iter_imports(pe):
            if filt.lower() in (dll + "!" + sym).lower():
                print(f"IAT=0x{slot:08X} [{dll}] {sym}")
    elif mode == "callsites":
        iat = int(sys.argv[3], 16)
        for va in find_callsites(pe, iat):
            print(f"call site VA=0x{va:08X}")
    elif mode == "dis":
        va = int(sys.argv[3], 16)
        ln = int(sys.argv[4], 16) if len(sys.argv) > 4 else 0x120
        for ln_txt in ann(disasm(pe, va, ln), pe):
            print(ln_txt)
    elif mode == "backward":
        call_va = int(sys.argv[3], 16)
        back = int(sys.argv[4]) if len(sys.argv) > 4 else 0x60
        for ln_txt in ann(disasm(pe, call_va - back, back + 6), pe):
            marker = " <== call" if ln_txt.startswith(f"  {call_va:08X}") else ""
            print(ln_txt + marker)
    elif mode == "strings":
        needle = sys.argv[3]
        wide = "--wide" in sys.argv[4:]
        for va in find_strings(pe, needle, wide):
            print(f"VA=0x{va:08X}  {needle!r}")
    else:
        print("unknown mode"); return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
