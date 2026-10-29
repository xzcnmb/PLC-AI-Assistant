#!/usr/bin/env python3
"""G2 T2(b): download a repacked project.hcpp over the proven D1 12939 file-transfer
protocol to the h5u simulator daemon (loopback only), then (optionally) issue
raw register-chain commands to observe the load-poll / RUN behaviour.

Reuse of D1 (docs/inovance-autoshop-download-protocol.md): login -> type2 session
-> type5 chunks -> 0x0F finish. All frames target 127.0.0.1 ONLY.

Sub-commands:
  download <filename> <path> [chunk]      login + file transfer
  login                                    login only (for register experiments)
  reg <payload_hex> [serverid]             send a raw register-chain payload (default id 1)
"""
import socket
import struct
import sys
import time

PORT = 12939
HOST = "127.0.0.1"
if not HOST.startswith("127."):
    raise SystemExit("loopback only")


def recv_exact(s, n, timeout=3):
    s.settimeout(timeout)
    buf = b""
    while len(buf) < n:
        c = s.recv(n - len(buf))
        if not c:
            raise ConnectionError("closed")
        buf += c
    return buf


def recv_response(s, timeout=3):
    s.settimeout(timeout)
    try:
        first = s.recv(4096)
        if not first:
            return None
        declared = struct.unpack("<H", first[0:2])[0]
        rest = b""
        try:
            while len(first) + len(rest) < declared and declared <= 4096:
                c = s.recv(declared - len(first) - len(rest))
                if not c:
                    break
                rest += c
        except socket.timeout:
            pass
        return first + rest
    except socket.timeout:
        return None


def frame(payload, ver=2, sid=4):
    return struct.pack("<H", len(payload) + 8) + bytes([ver, sid]) + b"\x00" * 4 + payload


def exchange(s, payload, sid, label, log):
    s.sendall(frame(payload, sid=sid))
    r = recv_response(s)
    log.append(f"{label}: tx={payload.hex()} rx={r.hex() if r else None}")
    return r


def do_login(s, log):
    payload = bytes([1, 0]) + b"plcmcp".ljust(32, b"\0") + b"0".ljust(32, b"\0")
    r = exchange(s, payload, 0x80, "login", log)
    return r is not None and len(r) >= 9 and r[8] == 0x06


def do_download(s, filename, content, chunk, log):
    cmd = bytes([2, 0]) + b"\x00" * 8 + struct.pack("<I", chunk) + struct.pack("<I", len(content)) + filename.encode() + b"\0"
    r = exchange(s, cmd, 4, "cmd(type2)", log)
    if not r or r[8] != 0x06:
        log.append("session rejected")
        return False
    idx = 0
    for off in range(0, len(content), chunk):
        p = bytes([5, 0]) + struct.pack("<I", idx) + struct.pack("<I", min(chunk, len(content) - off)) + content[off:off + chunk]
        r = exchange(s, p, 4, f"data[{idx}]", log)
        idx += 1
    exchange(s, bytes([0x0F, 0]) + b"\x00" * 4, 4, "finish", log)
    return True


def main():
    sub = sys.argv[1]
    logpath = "g2-dl.log"
    log = [f"=== {sub} @ {time.strftime('%H:%M:%S')} port={PORT} ==="]
    s = socket.create_connection((HOST, PORT), timeout=5)
    try:
        if sub == "download":
            filename = sys.argv[2]
            content = open(sys.argv[3], "rb").read()
            chunk = int(sys.argv[4]) if len(sys.argv) > 4 else 256
            if not do_login(s, log):
                log.append("LOGIN FAILED")
            else:
                ok = do_download(s, filename, content, chunk, log)
                log.append(f"download {'OK' if ok else 'FAIL'}")
        elif sub == "login":
            do_login(s, log)
        elif sub == "reg":
            # serverId default 1 (register chain); payload from hex
            do_login(s, log)
            payload = bytes.fromhex(sys.argv[2])
            sid = int(sys.argv[3], 0) if len(sys.argv) > 3 else 1
            r = exchange(s, payload, sid, "reg", log)
            log.append(f"reg rx={r.hex() if r else None}")
    finally:
        s.close()
    print("\n".join(log))
    with open(logpath, "a", encoding="utf-8") as h:
        h.write("\n".join(log) + "\n")


if __name__ == "__main__":
    main()
