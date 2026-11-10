#!/usr/bin/env python3
"""Tests for wam_to_cubia.py on the WAM+IA pilot logs. Run: python test_wam_to_cubia.py
(no pytest). Pilot file locations can be overridden with WAMIA_PILOT_EVENT / _SAMPLE / _META and
the preprocessor with WAMIA_PREPROCESSOR."""

import csv
import hashlib
import json
import math
import os
import re
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
def _pilot(name):
    """Pilot log: repo Temp/wamia_pilot (Unity wipes Temp on restart), else WAM's default folder, Documents."""
    for folder in (REF / "Temp/wamia_pilot", Path.home() / "Documents"):
        if (folder / name).is_file():
            return folder / name
    return REF / "Temp/wamia_pilot" / name


EVENT = Path(os.environ.get("WAMIA_PILOT_EVENT", _pilot("log_2026_11_06_10_11_31_0872_Event.csv")))
META = Path(os.environ.get("WAMIA_PILOT_META", _pilot("log_2026_11_06_10_11_31_0872_Meta.csv")))
_sample_in_repo = REF / "Assets/StreamingAssets/DataCSV/log_2026_11_06_10_11_31_0882_Sample.csv"
SAMPLE = Path(os.environ.get("WAMIA_PILOT_SAMPLE", _sample_in_repo if _sample_in_repo.is_file()
                             else _pilot("log_2026_11_06_10_11_31_0882_Sample.csv")))
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
    check_end_time(moles)

    times = [float(r["Time"]) for r in mv]
    orders = [int(r["MoleOrder"]) for r in mv]
    check("movement sorted by Time", all(a <= b for a, b in zip(times, times[1:])))
    check("movement MoleOrder non-decreasing", all(a <= b for a, b in zip(orders, orders[1:])))
    check("movement MoleOrder covers 1..12", sorted(set(orders)) == list(range(1, 13)), sorted(set(orders)))
    check("movement starts at first spawn", times[0] >= 0, times[0])
    check("movement ends at last hit (~37.4 s)", 37.0 < times[-1] < 37.6, times[-1])
    last_end = max(float(m["EndTime"]) for m in moles) if all(is_num(m.get("EndTime", "")) for m in moles) else math.nan
    check("last EndTime = last hit (movement ends there)", times[-1] <= last_end < 37.6, (times[-1], last_end))
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


def check_end_time(moles, tag=""):
    header = list(moles[0].keys())
    check(f"{tag}moles has EndTime right after SpawnTime",
          "EndTime" in header and header.index("EndTime") == header.index("SpawnTime") + 1, header)
    ends = [m.get("EndTime", "") for m in moles]
    filled = all(is_num(e) and math.isfinite(float(e)) for e in ends)
    check(f"{tag}EndTime never empty", filled, ends)
    check(f"{tag}EndTime >= SpawnTime", filled and all(float(e) >= float(m["SpawnTime"]) for e, m in zip(ends, moles)),
          [(m["SpawnTime"], e) for e, m in zip(ends, moles)])
    return filled


def test_end_time_not_resolved(tmp):
    """The pilot hits every mole. Cut the outcome events after the last spawn (and those between
    spawns 6 and 7) so some moles are NotResolved: EndTime must still be filled."""
    print("EndTime for moles without an outcome")
    with open(EVENT, encoding="utf-8-sig", newline="") as f:
        rows = list(csv.reader(f, delimiter=";"))
    ev = rows[0].index("Event")
    spawn_rows = [i for i, r in enumerate(rows) if len(r) > ev and r[ev].strip() == "Mole Spawned"]
    outcome = ("Mole Hit", "Expired Mole Hit", "Mole Expired")
    cut = [i for i, r in enumerate(rows) if len(r) > ev and r[ev].strip() in outcome
           and (i > spawn_rows[-1] or spawn_rows[5] < i < spawn_rows[6])]
    logs = tmp / "unresolved"
    logs.mkdir()
    event = logs / EVENT.name
    with open(event, "w", encoding="utf-8", newline="") as f:
        csv.writer(f, delimiter=";", lineterminator="\n").writerows(r for i, r in enumerate(rows) if i not in cut)
    out = tmp / "unresolved_csv"
    p, _ = run("--event", event, "--sample", SAMPLE, "--participant", "07", "--part", "1", "--out", out,
               "--no-preprocess")
    check("exit code 0", p.returncode == 0, p.stderr)
    if p.returncode != 0:
        return
    moles, mv = read(out / "P07_part1_moles.csv"), read(out / "P07_part1_movement.csv")
    open_ = [i for i, m in enumerate(moles) if m["Outcome"] == "NotResolved"]
    check("last mole NotResolved, ReactionTime empty", len(moles) == 12 and 11 in open_ and moles[11]["ReactionTime"] == "",
          [m["Outcome"] for m in moles])
    if not check_end_time(moles, "NotResolved: "):
        return
    check("NotResolved EndTime <= next SpawnTime",
          all(float(moles[i]["EndTime"]) <= float(moles[i + 1]["SpawnTime"]) for i in open_ if i + 1 < len(moles)),
          [(i, moles[i]["EndTime"]) for i in open_])
    check("NotResolved last mole: EndTime = game end (>= end of movement)",
          float(moles[11]["EndTime"]) >= float(mv[-1]["Time"]), (moles[11]["EndTime"], mv[-1]["Time"]))


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
                  "SpawnTime": "Numeric", "EndTime": "Numeric", "MoleX": "Numeric"},
        "movement": {"ControllerSpeed": "Numeric", "Time": "Numeric", "ControllerX": "Numeric",
                     "MoleOrderLabel": "Categorical"},
        "combined": {"RowType": "Categorical", "MoleOrderLabel": "Categorical", "X": "Numeric", "Time": "Numeric"},
    }
    for kind, cols in want.items():
        rows, types = read_cdataset_columns(proc / f"P07_part2_{kind}.cdataset")
        bad = {c: types.get(c) for c, t in cols.items() if types.get(c) != t}
        check(f"{kind}.cdataset column types for the prepared view", not bad, bad)
        check(f"{kind}.cdataset has no DateTime column", "DateTime" not in types.values(), types)


# ============================================================================================
# --auto batch mode (AUTO_IMPORT_CONTRACT.md)
# ============================================================================================
PILOT_SID = "c114f1a6-7852-4bec-841e-53edbf908305"
SUFFIX_RE = re.compile(r" R \d{2}-\d{2}-\d{4} \d{2}\.\d{2}( \(\d+\))?\.")
STATE_KEYS = {"participant", "part", "status", "reason", "metaFile", "eventFile", "sampleFile", "timestamp",
              "convertedAt", "outputs", "archived", "archivedAt", "replacedBy", "restoredAt"}


def make_session(logs, sid, pid, ts, state="Finished", program="TestPattern", sample=True, event=True, age=60):
    """Copy the pilot triple into `logs` as a new session: SessionID / ParticipantID / Timestamp /
    SessionState / SessionProgram rewritten in Meta, SessionID in Event and Sample, the rest kept
    (PII columns included, so the privacy checks mean something). ts = 'YYYY-mm-dd HH:MM:SS'."""
    stamp = ts.replace("-", "_").replace(" ", "_").replace(":", "_")
    with open(META, encoding="utf-8-sig", newline="") as f:
        rows = list(csv.reader(f, delimiter=";"))
    h = rows[0]
    row = dict(zip(h, rows[1]))
    row.update(SessionID=sid, ParticipantID=str(pid), Timestamp=ts + ".0872", SessionState=state,
               SessionProgram=program)
    paths = {"Meta": logs / f"log_{stamp}_0872_Meta.csv"}
    with open(paths["Meta"], "w", encoding="utf-8", newline="") as f:
        w = csv.writer(f, delimiter=";", lineterminator="\n")
        w.writerow(h)
        w.writerow([row[k] for k in h])
    if event:
        paths["Event"] = logs / f"log_{stamp}_0872_Event.csv"
        paths["Event"].write_text(EVENT.read_text(encoding="utf-8").replace(PILOT_SID, sid), encoding="utf-8")
    if sample:
        paths["Sample"] = add_sample(logs, sid, ts, age)
    for p in paths.values():
        os.utime(p, (time.time() - age,) * 2)
    return paths


def add_sample(logs, sid, ts, age=60):
    stamp = ts.replace("-", "_").replace(" ", "_").replace(":", "_")
    p = logs / f"log_{stamp}_0882_Sample.csv"   # name stamp a few ms off, like WAM
    p.write_text(SAMPLE.read_text(encoding="utf-8").replace(PILOT_SID, sid), encoding="utf-8")
    os.utime(p, (time.time() - age,) * 2)
    return p


def auto(logs, out, *extra):
    p, secs = run("--auto", "--logs", logs, "--out", out, "--json", "--preprocessor", PREPROCESSOR, *extra)
    return p, secs, parse_result(p)


def parse_result(p):
    lines = p.stdout.rstrip("\n").splitlines()
    if not lines or not lines[-1].startswith("WAMIA_RESULT "):
        return None
    return json.loads(lines[-1][len("WAMIA_RESULT "):])


def state_of(out):
    return json.loads((out / "WamIA~" / "state.json").read_text(encoding="utf-8"))


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def sid(n):
    return f"00000000-0000-4000-8000-{n:012d}"


def test_auto(tmp):
    print("--auto batch mode")
    logs, out = tmp / "auto_logs", tmp / "auto_out" / "DataCSV"
    logs.mkdir()
    out.mkdir(parents=True)
    arc = out / "WamIA~" / "Archive"
    proc = out / "ProcessedData"

    # run 1: warm-up + two finished sessions of P01, an unreadable (half-written) Meta
    make_session(logs, sid(1), 1, "2026-11-09 09:00:00", program="WarmUp")
    make_session(logs, sid(2), 1, "2026-11-09 09:05:00")
    make_session(logs, sid(3), 1, "2026-11-09 09:10:00")
    (logs / "log_2026_11_09_09_11_00_0000_Meta.csv").write_text("Age;SessionID;Timestamp\n", encoding="utf-8")
    p, secs, r = auto(logs, out)
    print(f"  (run 1: {secs:.2f} s)")
    check("run 1 exit 0", p.returncode == 0, p.stderr)
    check("run 1 WAMIA_RESULT is the last line", r is not None, p.stdout[-500:])
    if r is None:
        return
    print("  sample: " + p.stdout.rstrip().splitlines()[-1][:400])
    check("all result arrays present", all(isinstance(r.get(k), list) for k in
          ("converted", "replaced", "skipped", "warnings", "errors", "participants")), r)
    conv = {(c["participant"], c["part"]): c["session"] for c in r["converted"]}
    check("two finished sessions -> P01 part1 + part2", conv == {("P01", 1): sid(2), ("P01", 2): sid(3)}, conv)
    check("converted files are the .cdataset names",
          r["converted"] and r["converted"][0]["files"] == ["P01_part1_movement.cdataset", "P01_part1_moles.cdataset",
                                                          "P01_part1_combined.cdataset"], r["converted"])
    reasons = {s["session"]: s["reason"] for s in r["skipped"]}
    check("warm-up skipped", reasons.get(sid(1)) == "warmup", reasons)
    check("half-written Meta skipped, not fatal",
          reasons.get("log_2026_11_09_09_11_00_0000_Meta.csv") == "unreadable Meta", reasons)
    check("files in DataCSV and ProcessedData",
          all((out / f"P01_part{n}_{k}.csv").is_file() and (proc / f"P01_part{n}_{k}.cdataset").is_file()
              for n in (1, 2) for k in ("movement", "moles", "combined")))
    check("no staging/lock left behind", not (out / "WamIA~" / "staging").exists()
          and not (out / "WamIA~" / "run.lock").exists())
    part2_b = {f: sha(f) for f in [out / "P01_part2_moles.csv", proc / "P01_part2_moles.cdataset"]}

    # run 2: nothing new -> idempotent (the half-written Meta is reported again: it is re-checked)
    (logs / "log_2026_11_09_09_11_00_0000_Meta.csv").unlink()
    st_before = (out / "WamIA~" / "state.json").read_text(encoding="utf-8")
    p, secs, r = auto(logs, out)
    print(f"  (no-op run: {secs:.3f} s)")
    check("no-op: empty result, participants still listed",
          r == {"converted": [], "replaced": [], "skipped": [], "warnings": [], "errors": [],
                "participants": ["P01"], "restored": []}, r)
    check("no-op: state.json unchanged", (out / "WamIA~" / "state.json").read_text(encoding="utf-8") == st_before)

    # run 3: a third finished session replaces part 2; Unity .meta siblings move along
    (out / "P01_part2_movement.csv.meta").write_text("guid: a\n", encoding="utf-8")
    (proc / "P01_part2_movement.cdataset.meta").write_text("guid: b\n", encoding="utf-8")
    make_session(logs, sid(4), 1, "2026-11-09 09:20:00")
    p, secs, r = auto(logs, out)
    check("3rd session -> converted as part 2",
          [(c["participant"], c["part"], c["session"]) for c in r["converted"]] == [("P01", 2, sid(4))], r)
    rep = r["replaced"]
    check("3rd session -> result.replaced old=2nd new=3rd",
          len(rep) == 1 and rep[0]["oldSession"] == sid(3) and rep[0]["newSession"] == sid(4) and rep[0]["part"] == 2, rep)
    names = sorted(x.name for x in arc.iterdir()) if arc.is_dir() else []
    check("archive: 6 outputs + 2 .meta, all with ' R dd-MM-yyyy HH.mm' before the extension",
          len(names) == 8 and all(SUFFIX_RE.search(n) for n in names), names)
    check("archive: .meta keeps its asset's extension",
          any(n.startswith("P01_part2_movement R ") and n.endswith(".cdataset.meta") for n in names) and
          any(n.startswith("P01_part2_movement R ") and n.endswith(".csv.meta") for n in names), names)
    check("archived list in result = files in Archive", sorted(rep[0]["archived"]) == names if rep else False)
    st = state_of(out)["sessions"]
    check("state: 2nd session replaced, 3rd active",
          st[sid(3)]["status"] == "replaced" and st[sid(4)]["status"] == "active" and st[sid(4)]["part"] == 2,
          {k: v["status"] for k, v in st.items()})
    check("old .meta no longer next to the new output", not (out / "P01_part2_movement.csv.meta").exists())

    # run 4: P03 (jump from P01) with an Interrupted round; run 5: its restart replaces it
    make_session(logs, sid(5), 3, "2026-11-09 10:00:00", state="Interrupted")
    p, secs, r = auto(logs, out)
    check("P03 interrupted round imported as part 1",
          [(c["participant"], c["part"]) for c in r["converted"]] == [("P03", 1)], r)
    check("ascending warning P01 -> P03", any("P03" in w and "P02" in w for w in r["warnings"]), r["warnings"])
    make_session(logs, sid(6), 3, "2026-11-09 10:05:00")
    p, secs, r = auto(logs, out)
    check("restart replaces the interrupted part 1",
          [(c["participant"], c["part"], c["session"]) for c in r["converted"]] == [("P03", 1, sid(6))]
          and len(r["replaced"]) == 1 and r["replaced"][0]["oldSession"] == sid(5), r)
    check("interrupted outputs archived with ' R '",
          len([n for n in os.listdir(arc) if n.startswith("P03_part1_") and " R " in n]) == 6)
    st = state_of(out)["sessions"]
    check("state: interrupted session replaced", st[sid(5)]["status"] == "replaced" and st[sid(5)]["replacedBy"] == sid(6))
    check("no ascending warning for P03's second session", not r["warnings"], r["warnings"])

    # run 6: P04 without Sample -> skipped; still being written (fresh mtime) -> 'in progress'
    make_session(logs, sid(7), 4, "2026-11-09 11:00:00", sample=False)
    make_session(logs, sid(8), 5, "2026-11-09 11:30:00", age=0)
    p, secs, r = auto(logs, out)
    reasons = {s["session"]: s["reason"] for s in r["skipped"]}
    check("missing Sample -> skipped", reasons.get(sid(7)) == "missing Sample" and not r["converted"], r)
    check("fresh files -> skipped 'in progress'", reasons.get(sid(8)) == "in progress", reasons)
    check("participants lists not-yet-converted ones", r["participants"] == ["P01", "P03", "P04", "P05"], r["participants"])
    p, secs, r = auto(logs, out)
    check("unchanged skip is not reported again", r["skipped"] == [] and r["converted"] == [], r)
    add_sample(logs, sid(7), "2026-11-09 11:00:00")
    os.utime(next(logs.glob("log_2026_11_09_11_30_00_*_Meta.csv")), (time.time() - 60,) * 2)
    for f in logs.glob("log_2026_11_09_11_30_00_*"):
        os.utime(f, (time.time() - 60,) * 2)
    p, secs, r = auto(logs, out)
    conv = [(c["participant"], c["part"], c["session"]) for c in r["converted"]]
    check("Sample appeared -> converted on a later run", ("P04", 1, sid(7)) in conv and ("P05", 1, sid(8)) in conv, conv)
    check("P04/P05 follow P03: no ascending warning", not r["warnings"], r["warnings"])

    # --restore P01 2: the 2nd session's files come back, the 3rd's are archived; then undo
    p, _ = run("--restore", "P01", "2", "--out", out, "--json")
    r = parse_result(p)
    check("restore exit 0", p.returncode == 0, p.stderr + p.stdout)
    check("restore brings the replaced files back", all(f.is_file() and sha(f) == h for f, h in part2_b.items()))
    check("restored .meta siblings back in place", (out / "P01_part2_movement.csv.meta").read_text() == "guid: a\n"
          and (proc / "P01_part2_movement.cdataset.meta").read_text() == "guid: b\n")
    st = state_of(out)["sessions"]
    check("restore: state 2nd active, 3rd replaced",
          st[sid(3)]["status"] == "active" and st[sid(4)]["status"] == "replaced", (st[sid(3)]["status"], st[sid(4)]["status"]))
    check("restore: 3rd session's files archived",
          len([n for n in os.listdir(arc) if n.startswith("P01_part2_")]) == 6 and r and r["restored"], r)
    p, _ = run("--restore", "1", "2", "--out", out)
    st = state_of(out)["sessions"]
    check("restore is undoable (3rd active again)", st[sid(4)]["status"] == "active" and st[sid(3)]["status"] == "replaced")
    p, secs, r = auto(logs, out)
    check("auto after restore: nothing to do", not r["converted"] and not r["replaced"], r)

    # privacy: no personal column or value anywhere CUBIA can see, and nothing but whitelisted keys in state
    secrets = pii_values()
    every = [f for f in out.rglob("*") if f.is_file() and f.suffix in (".csv", ".json", ".meta")]
    leaks = [f.name for f in every if "@" in f.read_text(encoding="utf-8", errors="replace")
             or any(x in f.read_text(encoding="utf-8", errors="replace") for x in secrets)]
    check("no PII value in any output, archive or state.json", not leaks, leaks)
    headers = set()
    for f in out.rglob("*.csv"):
        headers |= set(f.read_text(encoding="utf-8").split("\n", 1)[0].split(","))
    check("no PII column in any CSV", not (headers & set(FORBIDDEN)), headers & set(FORBIDDEN))
    st = state_of(out)
    extra = {k for v in st["sessions"].values() for k in v} - STATE_KEYS
    check("state.json holds only contract fields", set(st) == {"version", "sessions"} and not extra, extra)


def test_auto_program_and_noop_30(tmp):
    print("--auto with 30 sessions: no-op timing, --program filter")
    logs, out = tmp / "logs30", tmp / "out30"
    logs.mkdir()
    for i in range(30):
        make_session(logs, sid(100 + i), 1 + i // 2, f"2026-11-10 {8 + i // 6:02d}:{(i % 6) * 10:02d}:00")
    make_session(logs, sid(200), 16, "2026-11-10 14:00:00", program="Other")
    p, secs, r = auto(logs, out, "--program", "testpattern")
    print(f"  (30 conversions incl. preprocessing: {secs:.1f} s)")
    check("30 sessions -> 15 participants x 2 parts", r and len(r["converted"]) == 30 and not r["errors"],
          r and (len(r["converted"]), r["errors"]))
    check("--program: other program skipped", r and {"session": sid(200), "reason": "program"} in r["skipped"], r and r["skipped"])
    check("no ascending warnings P01..P15", r and not r["warnings"], r and r["warnings"])
    best = min(auto(logs, out, "--program", "testpattern")[1] for _ in range(3))
    print(f"  (no-op run with 31 sessions: {best:.3f} s wall, incl. interpreter start)")
    check("no-op run well under 1 s", best < 0.5, f"{best:.3f} s")


def main():
    for f in (EVENT, SAMPLE, META, SCRIPT):
        if not f.is_file():
            print(f"missing input: {f}")
            return 2
    tmp = Path(tempfile.mkdtemp(prefix="wamia_test_"))
    try:
        test_csv_outputs(tmp)
        test_end_time_not_resolved(tmp)
        if PREPROCESSOR.is_file():
            test_logs_folder_and_preprocessor(tmp)
            test_auto(tmp)
            test_auto_program_and_noop_30(tmp)
        else:
            print(f"(skipped preprocessor test: {PREPROCESSOR} not found)")
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    print("\nALL PASSED" if not failures else f"\n{len(failures)} FAILED: {failures}")
    return 0 if not failures else 1


if __name__ == "__main__":
    sys.exit(main())
