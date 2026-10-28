#!/usr/bin/env python3
"""Print name | type | min | max (or n categories) | missing for each column of a .cdataset file.

v3 files end each column block with a validity mask (ceil(rows / 8) bytes,
1 bit per row, LSB-first, 1 = has a value); v2 files have none (0 missing).
"""
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
        mask_len = (rows + 7) // 8
        for _ in range(cols):
            n, = struct.unpack('<B', f.read(1))
            name = f.read(n).decode('utf-8')
            t, = struct.unpack('<B', f.read(1))
            if t in (1, 3):
                mn, mx = struct.unpack('<ff', f.read(8))
                f.read(rows * 4)
                desc = f"{mn:.6g} | {mx:.6g}"
            elif t == 2:
                k, = struct.unpack('<I', f.read(4))
                for _ in range(k):
                    ln, = struct.unpack('<H', f.read(2))
                    f.read(ln)
                f.read(rows * 4)
                desc = f"{k} categories"
            else:
                raise ValueError(f"unknown type {t}")
            missing = 0
            if version >= 3:
                mask = f.read(mask_len)
                missing = rows - sum(bin(b).count('1') for b in mask)
            print(f"{name} | {t} | {desc} | missing={missing}")


if __name__ == '__main__':
    for p in sys.argv[1:]:
        inspect(p)
