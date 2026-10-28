#!/usr/bin/env python3
"""Print name | type | min | max (or n categories) for each column of a .cdataset file."""
import struct
import sys


def inspect(path):
    with open(path, 'rb') as f:
        magic = f.read(4)
        if magic != b'DVCB':
            raise ValueError(f"bad magic {magic!r}")
        version, = struct.unpack('<H', f.read(2))
        rows, = struct.unpack('<I', f.read(4))
        cols, = struct.unpack('<I', f.read(4))
        print(f"{path}: version={version} rows={rows} cols={cols}")
        for _ in range(cols):
            n, = struct.unpack('<B', f.read(1))
            name = f.read(n).decode('utf-8')
            t, = struct.unpack('<B', f.read(1))
            if t in (1, 3):
                mn, mx = struct.unpack('<ff', f.read(8))
                f.read(rows * 4)
                print(f"{name} | {t} | {mn:.6g} | {mx:.6g}")
            elif t == 2:
                k, = struct.unpack('<I', f.read(4))
                for _ in range(k):
                    ln, = struct.unpack('<H', f.read(2))
                    f.read(ln)
                f.read(rows * 4)
                print(f"{name} | {t} | {k} categories")
            else:
                raise ValueError(f"unknown type {t}")


if __name__ == '__main__':
    for p in sys.argv[1:]:
        inspect(p)
