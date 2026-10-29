#!/usr/bin/env python3
"""G2 T2: Modbus TCP 502 register snapshot for the h5u simulator (loopback only).

Baseline / liveness comparison per docs/inovance-autoshop-download-closedloop.md:
reads D100-109, D0-9, coils 0-15, R100-101 and reports whether anything is alive.
Optional --write: FC6 write D100=1234 / FC5 coil0=ON then read back (liveness
discriminator: writes work in STOP too, so a program RUN must MOVE registers;
we compare snapshots taken seconds apart).

Evidence tool; never targets non-loopback addresses.
"""
import socket
import struct
import sys
import time

HOST = "127.0.0.1"
PORT = 502


def mbap(pdu, tid=1):
    return struct.pack(">HHHB", tid, 0, len(pdu) + 1, 1) + pdu


def exchange(pdu, timeout=2.0):
    s = socket.create_connection((HOST, PORT), timeout=timeout)
    s.sendall(mbap(pdu))
    r = s.recv(512)
    s.close()
    return r


def read(fc, start, count):
    r = exchange(struct.pack(">BHH", fc, start, count))
    if len(r) < 9:
        return None
    n = r[8]
    return list(struct.unpack(f">{n//2}H", r[9:9 + n])) if n else []


def snapshot():
    return {
        "D100-109": read(3, 100, 10),
        "D0-9": read(3, 0, 10),
        "coils0-15": read(1, 0, 16),
        "R100-101": read(3, 100 + 0, 2),  # R area mapping is profile-specific; keep simple
    }


def main():
    if "--write" in sys.argv:
        # FC6 D100=1234, FC5 coil 0 ON, read back (write/read-back integrity)
        w = exchange(struct.pack(">BHH", 6, 100, 1234))
        c = exchange(struct.pack(">BHH", 5, 0, 1))
        print("FC6 resp:", w.hex())
        print("FC5 resp:", c.hex())
    snap = snapshot()
    print("snapshot @", time.strftime("%H:%M:%S"))
    for k, v in snap.items():
        print(f"  {k}: {v}")
    nonzero = any(v and any(x for x in v) for k, v in snap.items() if k != "coils0-15")
    print("ALIVE" if nonzero or any(snap['coils0-15'] or []) else "ALL-ZERO (STOP/idle)")


if __name__ == "__main__":
    main()
