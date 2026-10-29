#!/usr/bin/env python3
"""G2 T2: loopback frame-capture TCP proxy (127.0.0.1:<listen> -> 127.0.0.1:<upstream>).

Reuses the F2 wire-capture pattern (see docs/inovance-autoshop-native-bridge.md
section 15.4). Evidence tool for docs/inovance-autoshop-download-closedloop.md.
ONLY 127.0.0.1 on both sides; aborts otherwise. Never contacts a PLC.

Usage: python frame_proxy.py <listen_port> <upstream_port> <logfile>
"""
import socket
import struct
import sys
import threading
import time

LOG_LOCK = threading.Lock()


def log(path, msg):
    with LOG_LOCK, open(path, "a", encoding="utf-8") as h:
        h.write(f"{time.strftime('%H:%M:%S')} {msg}\n")


def hexs(b):
    return b.hex()


def pump(src, dst, label, logpath, conn_id):
    n = 0
    try:
        while True:
            data = src.recv(65536)
            if not data:
                log(logpath, f"[c{conn_id} {label} closed]")
                break
            n += 1
            log(logpath, f"[c{conn_id} {label} #{n} len={len(data)}] {hexs(data)}")
            try:
                dst.sendall(data)
            except OSError:
                break
    except OSError as e:
        log(logpath, f"[c{conn_id} {label} err {e}]")


def main():
    listen, upstream, logpath = int(sys.argv[1]), int(sys.argv[2]), sys.argv[3]
    for p in (listen, upstream):
        if not (0 < p < 65536):
            raise SystemExit("bad port")
    srv = socket.create_server(("127.0.0.1", listen), backlog=16)
    log(logpath, f"[proxy] listening 127.0.0.1:{listen} -> 127.0.0.1:{upstream}")
    cid = 0
    while True:
        c, addr = srv.accept()
        cid += 1
        peer = socket.create_connection(("127.0.0.1", upstream), timeout=5)
        log(logpath, f"[c{cid} opened from {addr}]")
        threading.Thread(target=pump, args=(c, peer, "C->S", logpath, cid), daemon=True).start()
        threading.Thread(target=pump, args=(peer, c, "S->C", logpath, cid), daemon=True).start()


if __name__ == "__main__":
    main()
