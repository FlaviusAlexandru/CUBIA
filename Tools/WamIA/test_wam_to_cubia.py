#!/usr/bin/env python3
"""Tests for wam_to_cubia.py on the WAM+IA pilot logs. Run: python test_wam_to_cubia.py
(no pytest). Pilot file locations can be overridden with WAMIA_PILOT_EVENT / _SAMPLE / _META and
the preprocessor with WAMIA_PREPROCESSOR."""

import csv
import math
import os
import shutil
import struct
import subprocess
import sys
import tempfile
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
# Pilot logs live in the repo's git-ignored Temp/wamia_pilot (raw logs contain PII; never commit them).
REF = Path(os.environ.get("WAMIA_REF_ROOT", HERE.parent.parent))
EVENT = Path(os.environ.get("WAMIA_PILOT_EVENT", REF / "Temp/wamia_pilot/log_2026_11_06_10_11_31_0872_Event.csv"))
META = Path(os.environ.get("WAMIA_PILOT_META", REF / "Temp/wamia_pilot/log_2026_11_06_10_11_31_0872_Meta.csv"))
SAMPLE = Path(os.environ.get("WAMIA_PILOT_SAMPLE",
                             REF / "Assets/StreamingAssets/DataCSV/log_2026_11_06_10_11_31_0882_Sample.csv"))
PREPROCESSOR = Path(os.environ.get("WAMIA_PREPROCESSOR", HERE.parent.parent / "Assets/Scripts/Data/preprocess_csv_columnar.py"))
if not PREPROCESSOR.is_file():
    PREPROCESSOR = REF / "Assets/Scripts/Data/preprocess_csv_columnar.py"
SCRIPT = HERE / "wam_to_cubia.py"

EXPECTED_GRID = [(5, 3), (6, 4), (8, 5), (2, 1), (3, 4), (4, 2), (5, 3), (6, 2), (8, 1), (2, 5), (3, 2), (7, 4)]
FORBIDDEN = ["Email", "Age", "Gender", "InjuryDate", "ProfileName", "ProfileID", "DeviceID",
             "Handedness", "ParticipantID", "ParticipantId", "SessionID", "ProfileGroup"]
MAY_BE_EMPTY = {"ReactionTime", "HitOffset", "HitOffsetX", "HitOffsetY"}   # empty = mole not hit

failures = []


def check(name, cond, detail=""):
    print(("  ok   " if cond else "  FAIL ") + name + ("" if cond else f"  ({detail})"))
    if not cond:
        failures.append(name)


def run(*args):
    t0 = time.perf_counter()
    p = subprocess.run([sys.executable, str(SCRIPT), *map(str, args)], capture_output=True, text=True)
    return p, time.perf_counter() - t0


def read(path):
    with open(path, encoding="utf-8", newline="") as f:
        return list(csv.DictReader(f))


def is_num(v):
    try:
        float(v)
        return True
    except ValueError:
        return False


def pii_values():
    with open(META, encoding="utf-8-sig", newline="") as f:
        row = next(csv.DictReader(f, delimiter=";"))
    return [row[k] for k in ("Email", "ProfileName", "DeviceID", "ProfileID", "InjuryDate")
            if row.get(k) and row[k] not in ("NULL", "")]


def read_cdataset_columns(path):
    """Column names + type codes of a .cdataset (format v3, see preprocess_csv_columnar.py)."""
    b = Path(path).read_bytes()
    assert b[:4] == b"DVCB", "bad magic"
    version, rows, cols = struct.unpack_from("<HII", b, 4)
    pos, out = 14, {}
    for _ in range(cols):
        ln = b[pos]; pos += 1
        name = b[pos:pos + ln].decode("utf-8"); pos += ln
        typ = b[pos]; pos += 1
        if typ in (1, 3):
            pos += 8 + 4 * rows
        else:
            (n,) = struct.unpack_from("<I", b, pos); pos += 4
            for _ in range(n):
                (sl,) = struct.unpack_from("<H", b, pos); pos += 2 + sl
            pos += 4 * rows
        if version >= 3:
            pos += (rows + 7) // 8
        out[name] = {1: "Numeric", 2: "Categorical", 3: "DateTime"}[typ]
    assert pos == len(b), "trailing bytes"
    return rows, out


def test_csv_outputs(tmp):
    print("CSV conversion (--no-preprocess)")
    out = tmp / "csv"
    p, secs = run("--event", EVENT, "--sample", SAMPLE, "--meta", META, "--participant", "07",
                  "--part", "1", "--out", out, "--no-preprocess")
    check("exit code 0", p.returncode == 0, p.stderr)
    print(f"  (runtime {secs:.2f} s)")
    check("well under 2 minutes (FR-04)", secs < 30, f"{secs:.1f} s")
    files = {k: out / f"P07_part1_{k}.csv" for k in ("movement", "moles", "combined")}
    check("three CSVs named P07_part1_{kind}.csv", all(f.is_file() for f in files.values()),
          sorted(x.name for x in out.iterdir()))
    if failures:
        return
    moles, mv, comb = read(files["moles"]), read(files["movement"]), read(files["combined"])

    check("12 moles", len(moles) == 12, len(moles))
    grid = [(int(m["GridX"]), int(m["GridY"])) for m in moles]
    check("GridX/GridY = pattern D1 then C1", grid == EXPECTED_GRID, grid)
    check("every mole Hit", all(m["Outcome"] == "Hit" for m in moles), [m["Outcome"] for m in moles])
    check("mole 1 ReactionTime ~ 6.47 s", abs(float(moles[0]["ReactionTime"]) - 6.4718) < 1e-3, moles[0]["ReactionTime"])
    check("mole 2 ReactionTime ~ 2.51 s", abs(float(moles[1]["ReactionTime"]) - 2.5077) < 1e-3, moles[1]["ReactionTime"])
    check("mole 1 HitOffset = sqrt(0.0204^2+0.0126^2)",
          abs(float(moles[0]["HitOffset"]) - math.hypot(0.0204, 0.0126)) < 1e-4, moles[0]["HitOffset"])
    check("MoleOrder 1..12 in moles", [int(m["MoleOrder"]) for m in moles] == list(range(1, 13)))
    check("PathEfficiency within 0..1", all(0 <= float(m["PathEfficiency"]) <= 1 for m in moles))
    check("MainHand Right (pilot RightControllerMain=False is wrong)", all(m["MainHand"] == "Right" for m in moles))

    times = [float(r["Time"]) for r in mv]
    orders = [int(r["MoleOrder"]) for r in mv]
    check("movement sorted by Time", all(a <= b for a, b in zip(times, times[1:])))
    check("movement MoleOrder non-decreasing", all(a <= b for a, b in zip(orders, orders[1:])))
    check("movement MoleOrder covers 1..12", sorted(set(orders)) == list(range(1, 13)), sorted(set(orders)))
    check("movement starts at first spawn", times[0] >= 0, times[0])
    check("movement ends at last hit (~37.4 s)", 37.0 < times[-1] < 37.6, times[-1])
    check("ControllerSpeed has no empty/NaN",
          all(is_num(r["ControllerSpeed"]) and not math.isnan(float(r["ControllerSpeed"])) for r in mv))
    check("laser rows on the wall plane", all(6.0 < float(r["LaserZ"]) < 7.0 for r in mv))

    kinds = {r["RowType"] for r in comb}
    check("combined has Cursor and Mole rows", kinds == {"Cursor", "Mole"}, kinds)
    check("combined row count = movement + moles", len(comb) == len(mv) + len(moles))
    check("combined Mole rows labelled 1..12",
          [r["Label"] for r in comb if r["RowType"] == "Mole"] == [str(i) for i in range(1, 13)])

    # privacy + CUBIA classification rules
    secrets = pii_values()
    for kind, f in files.items():
        text = f.read_text(encoding="utf-8")
        header = text.split("\n", 1)[0].split(",")
        check(f"{kind}: no '@'", "@" not in text)
        check(f"{kind}: no PII column", not (set(header) & set(FORBIDDEN)), set(header) & set(FORBIDDEN))
        check(f"{kind}: no PII value from Meta", not any(s in text for s in secrets))
        rows = read(f)
        for col in header:
            vals = [r[col] for r in rows]
            filled = [v for v in vals if v != ""]
            if filled and sum(map(is_num, filled)) / len(filled) >= 0.9 and col not in MAY_BE_EMPTY:
                empty = 1 - len(filled) / len(vals)
                check(f"{kind}.{col}: <10% empty", empty < 0.10, f"{empty:.0%}")


def test_logs_folder_and_preprocessor(tmp):
    print("--logs fast path + CUBIA preprocessor")
    logs = tmp / "Documents"
    logs.mkdir()
    for f in (EVENT, SAMPLE, META):
        shutil.copy(f, logs / f.name)
    # decoy: a newer Event log of another session without a Sample -> must be ignored
    decoy = logs / "log_2026_11_07_09_00_00_0000_Event.csv"
    lines = EVENT.read_text(encoding="utf-8").splitlines()
    decoy.write_text("\n".join(l.replace("c114f1a6-7852-4bec-841e-53edbf908305", "deadbeef") for l in lines[:50]),
                     encoding="utf-8")
    repo_out = tmp / "Assets/StreamingAssets/DataCSV"
    repo_out.mkdir(parents=True)
    unrelated = repo_out / "unrelated.csv"
    unrelated.write_text("a,b\n1,2\n", encoding="utf-8")
    p, secs = run("--logs", logs, "--participant", "P07", "--part", "2", "--out", repo_out,
                  "--preprocessor", PREPROCESSOR)
    check("exit code 0", p.returncode == 0, p.stderr)
    print(f"  (runtime incl. preprocessing {secs:.2f} s)")
    check("picked the pilot session", "c114f1a6" in p.stdout, p.stdout)
    proc = repo_out / "ProcessedData"
    made = sorted(x.name for x in proc.glob("*.cdataset")) if proc.is_dir() else []
    check("exactly the three .cdataset files (unrelated.csv untouched)",
          made == ["P07_part2_combined.cdataset", "P07_part2_moles.cdataset", "P07_part2_movement.cdataset"], made)
    if len(made) != 3:
        return
    want = {
        "moles": {"Phase": "Categorical", "Outcome": "Categorical", "MainHand": "Categorical",
                  "MoleOrderLabel": "Categorical", "HitOffset": "Numeric", "ReactionTimeOrLifetime": "Numeric",
                  "SpawnTime": "Numeric", "MoleX": "Numeric"},
        "movement": {"ControllerSpeed": "Numeric", "Time": "Numeric", "ControllerX": "Numeric",
                     "MoleOrderLabel": "Categorical"},
        "combined": {"RowType": "Categorical", "MoleOrderLabel": "Categorical", "X": "Numeric", "Time": "Numeric"},
    }
    for kind, cols in want.items():
        rows, types = read_cdataset_columns(proc / f"P07_part2_{kind}.cdataset")
        bad = {c: types.get(c) for c, t in cols.items() if types.get(c) != t}
        check(f"{kind}.cdataset column types for the prepared view", not bad, bad)
        check(f"{kind}.cdataset has no DateTime column", "DateTime" not in types.values(), types)


def main():
    for f in (EVENT, SAMPLE, META, SCRIPT):
        if not f.is_file():
            print(f"missing input: {f}")
            return 2
    tmp = Path(tempfile.mkdtemp(prefix="wamia_test_"))
    try:
        test_csv_outputs(tmp)
        if PREPROCESSOR.is_file():
            test_logs_folder_and_preprocessor(tmp)
        else:
            print(f"(skipped preprocessor test: {PREPROCESSOR} not found)")
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    print("\nALL PASSED" if not failures else f"\n{len(failures)} FAILED: {failures}")
    return 0 if not failures else 1


if __name__ == "__main__":
    sys.exit(main())
