#!/usr/bin/env python3
"""
WAM+IA log converter: Whack-A-Mole VR logs from ONE play-through -> three CUBIA datasets.

    python wam_to_cubia.py --event <..._Event.csv> --sample <..._Sample.csv> [--meta <..._Meta.csv>]
                           --participant P07 --part 1 [--out <folder>] [--no-preprocess]
    python wam_to_cubia.py --logs <folder with WAM logs> --participant P07 --part 1
    python wam_to_cubia.py --auto --logs <WAM log folder> --out <DataCSV> --json     (batch, see
                           AUTO_IMPORT_CONTRACT.md)
    python wam_to_cubia.py --restore P07 2 --out <DataCSV>                         (undo a replacement)

Writes {participant}_part{part}_movement.csv / _moles.csv / _combined.csv into --out
(default: <repo>/Assets/StreamingAssets/DataCSV) and, unless --no-preprocess, converts just
those three files to .cdataset in <out>/ProcessedData with CUBIA's own
preprocess_csv_columnar.py (imported as a module; its process-everything main() is not run).

Privacy: only whitelisted columns are read from the logs and only derived columns are written.
Email, Age, Gender, InjuryDate, ProfileName, DeviceID, ... are never read into memory.

Needs only what CUBIA's preprocessor already needs: Python 3.8+, pandas, numpy.
See README.md for the column glossary.
"""

import argparse
import contextlib
import csv
import datetime as dt_
import importlib.util
import io
import json
import math
import os
import re
import shutil
import sys
import time
import warnings
from pathlib import Path

# numpy/pandas are imported on first use (load_numeric): importing pandas alone takes ~0.4 s, and an
# --auto run with nothing new must stay well under a second.
np = pd = None


def load_numeric():
    global np, pd
    if pd is None:
        import numpy as _np
        import pandas as _pd
        np, pd = _np, _pd

SCRIPT_DIR = Path(__file__).resolve().parent
REPO_ROOT = SCRIPT_DIR.parent.parent
DEFAULT_OUT = REPO_ROOT / "Assets" / "StreamingAssets" / "DataCSV"
DEFAULT_PREPROCESSOR = REPO_ROOT / "Assets" / "Scripts" / "Data" / "preprocess_csv_columnar.py"

TS_FORMAT = "%Y-%m-%d %H:%M:%S.%f"
MISSING = {"", "NULL", "null", "NaN", "nan"}

# --- input whitelists (nothing else is read) -------------------------------------------------
EVENT_COLS = [
    "Timestamp", "Event", "SessionID", "GameTimeSpent", "TestId", "PatternSegmentLabel",
    "PlayPeriod", "MoleId", "MoleSpawnOrder", "MolePositionWorldX", "MolePositionWorldY",
    "MolePositionWorldZ", "MoleActivatedDuration", "MoleLifeTime", "MoleSurfaceHitLocationX",
    "MoleSurfaceHitLocationY", "DistFromLastMole", "RightControllerMain", "ControllerName",
]
SAMPLE_COLS = (
    ["Timestamp", "SessionID"]
    + [f"HeadCameraPosWorld{a}" for a in "XYZ"]
    + [f"HeadCameraRotEuler{a}" for a in "XY"]
    + [f"{h}Controller{k}World{a}" for h in ("Right", "Left") for k in ("Pos", "LaserPos") for a in "XYZ"]
)
META_COLS = ["SessionID", "SamplingFrequency"]

# --- output whitelists (exact column order of each CSV) --------------------------------------
MOVEMENT_COLS = ["Time", "Part", "Phase", "MoleOrder", "MoleOrderLabel",
                 "ControllerX", "ControllerY", "ControllerZ", "ControllerSpeed",
                 "LaserX", "LaserY", "LaserZ", "HeadX", "HeadY", "HeadZ", "HeadYaw", "HeadPitch"]
MOLES_COLS = ["Part", "Phase", "MoleOrder", "MoleOrderLabel", "GridX", "GridY",
              "MoleX", "MoleY", "MoleZ", "SpawnTime", "EndTime", "Outcome", "ReactionTime",
              "ReactionTimeOrLifetime", "HitOffset", "HitOffsetX", "HitOffsetY",
              "DistFromLastMole", "ControllerTravel", "PathEfficiency", "PeakSpeed", "MainHand"]
COMBINED_COLS = ["RowType", "X", "Y", "Z", "Time", "MoleOrder", "Label", "MoleOrderLabel",
                 "Phase", "Part"]
KINDS = ("movement", "moles", "combined")

OUTCOME_EVENTS = {"Mole Hit": "Hit", "Expired Mole Hit": "ExpiredHit", "Mole Expired": "Expired"}
SPEED_SMOOTH_S = 0.05      # centred moving-average window for ControllerSpeed
LASER_PLANE_TOL_M = 0.5    # laser samples further than this from the wall plane are interpolated over


class ConvertError(Exception):
    pass


# ============================================================================================
# small helpers
# ============================================================================================
def normalise_participant(pid):
    """Same rule as StudyLogWriter.BuildFileName / PreparedViews.NormaliseParticipant:
    keep letters, digits, '-' and '_'; strip one leading P/p; empty -> 'unknown'; prefix 'P'."""
    s = "".join(c for c in (pid or "").strip() if c.isalnum() or c in "-_")
    if s[:1] in ("P", "p"):
        s = s[1:]
    return "P" + (s or "unknown")


def output_name(participant, part, kind):
    return f"{normalise_participant(participant)}_part{int(part)}_{kind}"


def to_num(series):
    s = series.astype(str).str.strip()
    s = s.where(~s.isin(MISSING))
    return pd.to_numeric(s, errors="coerce")


def to_text(series):
    s = series.astype(str).str.strip()
    return s.where(~s.isin(MISSING), "")


def parse_ts(series):
    return pd.to_datetime(series.astype(str).str.strip(), format=TS_FORMAT, errors="coerce")


def read_header(path):
    with open(path, "r", encoding="utf-8-sig", newline="") as f:
        return f.readline().rstrip("\r\n").split(";")


def read_logs(path, wanted, required):
    """Read only the whitelisted columns of a ';'-separated WAM log, as text."""
    load_numeric()
    header = read_header(path)
    missing = [c for c in required if c not in header]
    if missing:
        raise ConvertError(f"{Path(path).name}: missing column(s) {', '.join(missing)}")
    use = [c for c in wanted if c in header]
    return pd.read_csv(path, sep=";", usecols=use, dtype=str, keep_default_na=False,
                       encoding="utf-8-sig", engine="c")


def first_session_id(path, max_rows=5000):
    """First non-empty SessionID in the file (Event logs start with setup rows that have none)."""
    with open(path, "r", encoding="utf-8-sig", newline="") as f:
        reader = csv.reader(f, delimiter=";")
        header = next(reader, None)
        if not header or "SessionID" not in header:
            return None
        i = header.index("SessionID")
        for n, row in enumerate(reader):
            if n >= max_rows:
                break
            if i < len(row) and row[i].strip() not in MISSING:
                return row[i].strip()
    return None


def keep_categorical(series, prefix):
    """CUBIA's preprocessor reads a text column as Numeric if >=90% of it parses as a number and as
    DateTime if >=90% parses as a date ('May', '1'); prefix such labels so the column stays text."""
    s = series.astype(str)
    with warnings.catch_warnings():
        warnings.simplefilter("ignore")
        looks = pd.to_numeric(s, errors="coerce").notna() | pd.to_datetime(s, errors="coerce").notna()
    return prefix + s if looks.mean() >= 0.5 else s


def wrap180(a):
    return (np.asarray(a, dtype=float) + 180.0) % 360.0 - 180.0


def find_triple(folder):
    """Newest Event/Sample(/Meta) set in `folder` that shares a SessionID."""
    folder = Path(folder)
    if not folder.is_dir():
        raise ConvertError(f"--logs folder not found: {folder}")
    groups = {}
    for kind in ("Event", "Sample", "Meta"):
        for p in folder.glob(f"*_{kind}.csv"):
            sid = first_session_id(p)
            if not sid or sid in MISSING:
                continue
            g = groups.setdefault(sid, {})
            if kind not in g or p.stat().st_mtime > g[kind].stat().st_mtime:
                g[kind] = p
    complete = {sid: g for sid, g in groups.items() if "Event" in g and "Sample" in g}
    if not complete:
        raise ConvertError(f"no Event+Sample pair with a common SessionID in {folder}")
    sid = max(complete, key=lambda k: max(p.stat().st_mtime for p in complete[k].values()))
    g = complete[sid]
    return g["Event"], g["Sample"], g.get("Meta"), sid


# ============================================================================================
# conversion
# ============================================================================================
def load_events(path, session_id):
    ev = read_logs(path, EVENT_COLS, ["Timestamp", "Event", "SessionID", "MoleId"])
    ev["Event"] = ev["Event"].str.strip()
    ev = ev[to_text(ev["SessionID"]) == session_id].copy()
    ev["t"] = parse_ts(ev["Timestamp"])
    ev = ev[ev["t"].notna()].reset_index(drop=True)
    if ev.empty:
        raise ConvertError(f"Event log has no rows for SessionID {session_id}")
    return ev


def select_game(ev, warnings):
    """Events of the last game (Game Started .. next Game Started) that spawned moles."""
    starts = list(ev.index[ev["Event"] == "Game Started"])
    if not starts:
        warnings.append("no 'Game Started' event; using the whole Event log")
        return ev
    bounds = starts + [len(ev)]
    games = [ev.iloc[a:b] for a, b in zip(bounds[:-1], bounds[1:])]
    with_moles = [g for g in games if (g["Event"] == "Mole Spawned").any()]
    if not with_moles:
        raise ConvertError("no 'Mole Spawned' events in the Event log")
    if len(with_moles) > 1:
        warnings.append(f"{len(with_moles)} games in this session; using the LAST one")
    return with_moles[-1].reset_index(drop=True)


def detect_hand(game, samples, t_lo, t_hi, forced, warnings):
    """Main hand. Order: --hand, ControllerName on shoot/hit events, last 'Controller Main Set X',
    RightControllerMain, then whichever controller moved more. RightControllerMain is NOT trusted
    first: in the pilot it says False while the right controller did all the work."""
    if forced:
        return forced.capitalize(), "--hand"
    hand, source = None, None
    names = to_text(game.loc[game["Event"].isin(["Pointer Shoot", "Mole Hit"]), "ControllerName"]) \
        if "ControllerName" in game else pd.Series(dtype=str)
    names = names.str.lower()
    r, l = names.str.contains("right").sum(), names.str.contains("left").sum()
    if r or l:
        hand, source = ("Right" if r >= l else "Left"), "ControllerName"
    if hand is None:
        sets = game["Event"][game["Event"].str.startswith("Controller Main Set ")]
        if len(sets):
            hand, source = sets.iloc[-1].split()[-1].capitalize(), "Controller Main Set event"
    if hand is None and "RightControllerMain" in game:
        v = to_text(game["RightControllerMain"]).str.lower()
        v = v[v.isin(["true", "false"])]
        if len(v):
            hand, source = ("Right" if v.iloc[-1] == "true" else "Left"), "RightControllerMain"

    # movement check: which controller actually moved during play
    w = (samples["time"] >= t_lo) & (samples["time"] <= t_hi)
    moved = {}
    for h in ("Right", "Left"):
        p = samples.loc[w, [f"{h}ControllerPosWorld{a}" for a in "XYZ"]].to_numpy(float)
        moved[h] = float(np.nansum(np.linalg.norm(np.diff(p, axis=0), axis=1))) if len(p) > 1 else 0.0
    mover = "Right" if moved["Right"] >= moved["Left"] else "Left"
    if hand is None:
        return mover, "controller movement"
    other = "Left" if hand == "Right" else "Right"
    if moved[hand] < 0.1 * moved[other]:
        warnings.append(f"{source} says {hand} hand but the {other} controller moved "
                        f"{moved[other]:.1f} m vs {moved[hand]:.1f} m; using {other} (override with --hand)")
        return other, "controller movement"
    return hand, source


def interp_rows(t_query, t, values):
    return np.column_stack([np.interp(t_query, t, values[:, i]) for i in range(values.shape[1])])


def path_length(t, pos, t0, t1):
    """Length of the polyline through pos (sampled at t) between t0 and t1, ends interpolated."""
    i0, i1 = np.searchsorted(t, t0, "left"), np.searchsorted(t, t1, "right")
    ends = interp_rows(np.array([t0, t1]), t, pos)
    pts = np.vstack([ends[:1], pos[i0:i1], ends[1:]])
    return float(np.linalg.norm(np.diff(pts, axis=0), axis=1).sum()), ends


def convert(event_path, sample_path, meta_path, participant, part, downsample=1, hand=None,
            log=print):
    load_numeric()
    warnings = []
    sample_sid = first_session_id(sample_path)
    event_sid = first_session_id(event_path)
    if not sample_sid:
        raise ConvertError(f"no SessionID in {sample_path}")
    if event_sid != sample_sid:
        # Event logs may start with rows from before the session id is set; check all rows
        sids = set(to_text(read_logs(event_path, ["SessionID"], ["SessionID"])["SessionID"]))
        if sample_sid not in sids:
            raise ConvertError(f"Event and Sample logs are from different sessions "
                               f"({event_sid} vs {sample_sid}); pair them by SessionID")
    sampling = None
    if meta_path:
        meta = read_logs(meta_path, META_COLS, ["SessionID"])
        if sample_sid not in set(to_text(meta["SessionID"])):
            raise ConvertError(f"Meta log {Path(meta_path).name} is from another session")
        if "SamplingFrequency" in meta:
            sampling = to_num(meta["SamplingFrequency"]).dropna()
            sampling = float(sampling.iloc[0]) if len(sampling) else None

    # ---- events --------------------------------------------------------------------------
    ev = load_events(event_path, sample_sid)
    game = select_game(ev, warnings)
    t_zero = game.loc[game["Event"] == "Mole Spawned", "t"].iloc[0]
    game = game.assign(time=(game["t"] - t_zero).dt.total_seconds())
    test_id = to_num(game.get("TestId", pd.Series(dtype=str))).dropna()
    if len(test_id) and test_id.iloc[0] not in (0, part):
        warnings.append(f"Event log TestId is {int(test_id.iloc[0])} but --part is {part}")

    spawns = game[game["Event"] == "Mole Spawned"].reset_index(drop=True)
    outcomes = game[game["Event"].isin(OUTCOME_EVENTS)]
    stop = game.loc[game["Event"] == "Game Stopped", "time"]
    game_end = float(stop.iloc[0]) if len(stop) else float(game["time"].max())

    n = len(spawns)
    moles = pd.DataFrame({"Part": part, "MoleOrder": np.arange(1, n + 1)})
    phase = to_text(spawns["PatternSegmentLabel"]) if "PatternSegmentLabel" in spawns else pd.Series([""] * n)
    moles["Phase"] = keep_categorical(phase.replace("", np.nan).ffill().fillna("Unknown"), "Phase ").to_numpy()
    mole_id = to_text(spawns["MoleId"])
    ok = mole_id.str.fullmatch(r"\d{3,}")
    moles["GridX"] = pd.to_numeric(mole_id.str[-3].where(ok), errors="coerce").astype("Int64").to_numpy()
    moles["GridY"] = pd.to_numeric(mole_id.str[-2:].where(ok), errors="coerce").astype("Int64").to_numpy()
    for a in "XYZ":
        moles[f"Mole{a}"] = to_num(spawns[f"MolePositionWorld{a}"]).to_numpy()
    moles["SpawnTime"] = spawns["time"].to_numpy()
    moles["DistFromLastMole"] = to_num(spawns["DistFromLastMole"]).to_numpy() \
        if "DistFromLastMole" in spawns else np.nan

    spawn_t = spawns["time"].to_numpy()
    next_spawn = np.append(spawn_t[1:], np.inf)
    outc, rt, rtl, end_t, hx, hy = [], [], [], [], [], []
    for i in range(n):
        cand = outcomes[(outcomes["MoleId"].str.strip() == mole_id.iloc[i]) &
                        (outcomes["time"] >= spawn_t[i])]
        first = {}   # first event of each outcome kind
        for _, r in cand.iterrows():
            first.setdefault(OUTCOME_EVENTS[r["Event"]], r)
        if "Hit" in first:
            o, r = "Hit", first["Hit"]
        elif "ExpiredHit" in first:
            o, r = "ExpiredHit", first["ExpiredHit"]
        elif "Expired" in first:
            o, r = "Expired", first["Expired"]
        else:
            o, r = "NotResolved", None
        if r is None:
            t_end = min(next_spawn[i], game_end)
            dur = None
        else:
            t_end = float(r["time"])
            dur = to_num(pd.Series([r.get("MoleActivatedDuration", "")])).iloc[0]
            if pd.isna(dur):
                dur = t_end - spawn_t[i]
        outc.append(o)
        end_t.append(t_end)
        hit = o in ("Hit", "ExpiredHit")
        rt.append(dur if hit else np.nan)
        rtl.append(dur if dur is not None else t_end - spawn_t[i])
        if hit:
            hx.append(to_num(pd.Series([r.get("MoleSurfaceHitLocationX", "")])).iloc[0])
            hy.append(to_num(pd.Series([r.get("MoleSurfaceHitLocationY", "")])).iloc[0])
        else:
            hx.append(np.nan)
            hy.append(np.nan)
    moles["Outcome"] = outc
    moles["ReactionTime"] = rt
    moles["ReactionTimeOrLifetime"] = rtl
    moles["HitOffsetX"] = hx
    moles["HitOffsetY"] = hy
    moles["HitOffset"] = np.sqrt(moles["HitOffsetX"] ** 2 + moles["HitOffsetY"] ** 2)
    end_t = np.asarray(end_t, dtype=float)
    moles["EndTime"] = end_t
    resolved_end = [e for e, o in zip(end_t, outc) if o != "NotResolved"]
    play_end = max(resolved_end) if len(resolved_end) == n else game_end

    # ---- samples -------------------------------------------------------------------------
    smp = read_logs(sample_path, SAMPLE_COLS, SAMPLE_COLS)
    smp = smp[to_text(smp["SessionID"]) == sample_sid]
    t_abs = parse_ts(smp["Timestamp"])
    smp = smp[t_abs.notna()]
    smp = smp.assign(time=(t_abs[t_abs.notna()] - t_zero).dt.total_seconds())
    for c in SAMPLE_COLS[2:]:
        smp[c] = to_num(smp[c])
    smp = smp.sort_values("time", kind="stable").reset_index(drop=True)
    if len(smp) < 2:
        raise ConvertError("Sample log has fewer than 2 rows for this session")

    main, hand_src = detect_hand(game, smp, 0.0, play_end, hand, warnings)
    t = smp["time"].to_numpy(float)
    ctrl = smp[[f"{main}ControllerPosWorld{a}" for a in "XYZ"]].interpolate(limit_direction="both").to_numpy(float)
    laser_df = smp[[f"{main}ControllerLaserPosWorld{a}" for a in "XYZ"]].copy()
    in_play = (t >= 0) & (t <= play_end)
    plane_z = np.nanmedian(laser_df.iloc[:, 2].to_numpy()[in_play]) if in_play.any() else np.nan
    off = (laser_df.iloc[:, 2] - plane_z).abs() > LASER_PLANE_TOL_M
    n_off = int((off & in_play).sum())
    laser_df[off] = np.nan
    laser = laser_df.interpolate(limit_direction="both").to_numpy(float)

    # speed: per-step distance / dt, NaN-free, centred moving average
    dt = np.diff(t)
    step = np.linalg.norm(np.diff(ctrl, axis=0), axis=1)
    raw = np.full(len(t), np.nan)
    good = dt > 1e-6
    raw[1:][good] = step[good] / dt[good]
    raw = pd.Series(raw).interpolate(limit_direction="both").fillna(0.0)
    med_dt = float(np.median(dt[good])) if good.any() else 0.01
    win = max(1, int(round(SPEED_SMOOTH_S / med_dt)))
    win += (win + 1) % 2   # odd
    speed = raw.rolling(win, center=True, min_periods=1).mean().to_numpy()

    # per-mole movement metrics
    travel, eff, peak = [], [], []
    for i in range(n):
        t0, t1 = spawn_t[i], end_t[i]
        d, _ = path_length(t, ctrl, t0, t1)
        lp, ends = path_length(t, laser, t0, t1)
        straight = float(np.linalg.norm(ends[1] - ends[0]))
        w = (t >= t0) & (t <= t1)
        travel.append(d)
        eff.append(min(1.0, straight / lp) if lp > 1e-9 else np.nan)
        peak.append(float(speed[w].max()) if w.any() else float(np.interp((t0 + t1) / 2, t, speed)))
    moles["ControllerTravel"] = travel
    moles["PathEfficiency"] = eff
    moles["PeakSpeed"] = peak
    moles["MainHand"] = main
    moles["MoleOrderLabel"] = [f"Mole {k:02d}" for k in moles["MoleOrder"]]   # "M01" would parse as a date

    # ---- movement rows -------------------------------------------------------------------
    keep = np.flatnonzero(in_play)
    if downsample > 1:
        keep = keep[::downsample]
    tk = t[keep]
    k_spawn = np.searchsorted(spawn_t, tk, side="right")          # moles spawned at or before t
    cum_end = np.maximum.accumulate(end_t)                         # all of 1..k resolved by cum_end[k-1]
    idx = np.clip(k_spawn, 1, n) - 1
    order = np.where((k_spawn < n) & (cum_end[idx] < tk), k_spawn + 1, np.maximum(k_spawn, 1))
    head = smp.loc[keep, [f"HeadCameraPosWorld{a}" for a in "XYZ"]].to_numpy(float)
    mv = pd.DataFrame({
        "Time": tk, "Part": part,
        "Phase": moles["Phase"].to_numpy()[order - 1],
        "MoleOrder": order,
        "MoleOrderLabel": moles["MoleOrderLabel"].to_numpy()[order - 1],
        "ControllerX": ctrl[keep, 0], "ControllerY": ctrl[keep, 1], "ControllerZ": ctrl[keep, 2],
        "ControllerSpeed": speed[keep],
        "LaserX": laser[keep, 0], "LaserY": laser[keep, 1], "LaserZ": laser[keep, 2],
        "HeadX": head[:, 0], "HeadY": head[:, 1], "HeadZ": head[:, 2],
        "HeadYaw": wrap180(smp.loc[keep, "HeadCameraRotEulerY"]),
        "HeadPitch": -wrap180(smp.loc[keep, "HeadCameraRotEulerX"]),   # + = looking up
    })
    for c in ("HeadX", "HeadY", "HeadZ", "HeadYaw", "HeadPitch"):
        mv[c] = mv[c].interpolate(limit_direction="both")

    # ---- combined rows -------------------------------------------------------------------
    cur = pd.DataFrame({"RowType": "Cursor", "X": mv["LaserX"], "Y": mv["LaserY"], "Z": mv["LaserZ"],
                        "Time": mv["Time"], "MoleOrder": mv["MoleOrder"],
                        "Label": mv["MoleOrder"].astype(str), "MoleOrderLabel": mv["MoleOrderLabel"],
                        "Phase": mv["Phase"], "Part": part, "_k": 1})
    mol = pd.DataFrame({"RowType": "Mole", "X": moles["MoleX"], "Y": moles["MoleY"], "Z": moles["MoleZ"],
                        "Time": moles["SpawnTime"], "MoleOrder": moles["MoleOrder"],
                        "Label": moles["MoleOrder"].astype(str), "MoleOrderLabel": moles["MoleOrderLabel"],
                        "Phase": moles["Phase"], "Part": part, "_k": 0})
    comb = pd.concat([mol, cur], ignore_index=True).sort_values(["Time", "_k"], kind="stable")
    comb = comb.drop(columns="_k").reset_index(drop=True)

    if n_off:
        warnings.append(f"{n_off} laser sample(s) off the wall plane during play were interpolated over")
    info = {
        "session": sample_sid, "main_hand": main, "hand_source": hand_src, "sampling": sampling,
        "play_seconds": play_end, "warnings": warnings,
    }
    return {"movement": mv[MOVEMENT_COLS], "moles": moles[MOLES_COLS], "combined": comb[COMBINED_COLS]}, info


# ============================================================================================
# output
# ============================================================================================
def write_csv(df, path):
    tmp = Path(str(path) + ".tmp")
    df.to_csv(tmp, index=False, sep=",", float_format="%.5f", encoding="utf-8",
              na_rep="")
    os.replace(tmp, path)


def load_preprocessor(path):
    path = Path(path)
    if not path.is_file():
        raise ConvertError(f"CUBIA preprocessor not found: {path} (use --preprocessor or --no-preprocess)")
    spec = importlib.util.spec_from_file_location("cubia_preprocess_csv_columnar", path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def preprocess(csv_paths, out_dir, preprocessor_path, verbose=False, module=None):
    """Run CUBIA's per-file process_csv + write_binary on just these CSVs. The class constructor
    (which mkdirs under its own project root) and process_all_csvs (which converts EVERY csv in
    DataCSV) are deliberately bypassed."""
    load_numeric()
    mod = module or load_preprocessor(preprocessor_path)
    proc = mod.ColumnarDatasetProcessor.__new__(mod.ColumnarDatasetProcessor)
    proc.project_root = Path(out_dir)
    proc.raw_data_dir = Path(out_dir)
    proc.processed_data_dir = Path(out_dir) / "ProcessedData"
    proc.processed_data_dir.mkdir(parents=True, exist_ok=True)
    results, captured = {}, io.StringIO()
    for p in csv_paths:
        sink = sys.stdout if verbose else captured
        with contextlib.redirect_stdout(sink), warnings.catch_warnings():
            warnings.simplefilter("ignore")   # pandas' dateutil-fallback notices for text columns
            ds = proc.process_csv(Path(p))
            out = proc.processed_data_dir / f"{Path(p).stem}.cdataset"
            ok = ds is not None and proc.write_binary(ds, out)
        if not ok:
            raise ConvertError(f"preprocessor failed on {Path(p).name}:\n{captured.getvalue()[-2000:]}")
        results[Path(p).stem] = (out, {c["name"]: c["type"] for c in ds["columns"]})
    return results


# ============================================================================================
# --auto batch mode and --restore (AUTO_IMPORT_CONTRACT.md)
# ============================================================================================
STATE_VERSION = 1
SKIP_REASONS_MSG = {
    "warmup": "warm-up program",
    "program": "other SessionProgram than --program",
    "no moles": "no 'Mole Spawned' event",
    "missing Event": "no Event log with this SessionID yet",
    "missing Sample": "no Sample log with this SessionID yet",
    "in progress": "log files changed in the last seconds (WAM still writing)",
    "out of order": "older than a session already imported for this participant",
}
ARCHIVE_SUFFIX_RE = re.compile(r" R \d{2}-\d{2}-\d{4} \d{2}\.\d{2}(?: \(\d+\))?(?=\.)")
OUT_KINDS_EXT = (".csv", ".cdataset")


def participant_label(pid):
    """Contract rule: integer n -> 'P' + n zero-padded to 2 digits; otherwise 'P' + the id with
    characters outside [A-Za-z0-9_-] removed. Empty/NULL -> None."""
    s = (pid or "").strip()
    if s in MISSING:
        return None
    # "P07" / "p7" count as the number 7, matching the dashboard's WamIaAutoImporter.ParticipantLabel.
    m = re.fullmatch(r"[Pp]?(\d+)", s)
    if m:
        return f"P{int(m.group(1)):02d}"
    s = re.sub(r"[^A-Za-z0-9_-]", "", s)
    return ("P" + s) if s else None


def label_number(label):
    m = re.fullmatch(r"P(\d+)", label or "")
    return int(m.group(1)) if m else None


def label_sort_key(label):
    n = label_number(label)
    return (0, n, "") if n is not None else (1, 0, label)


def parse_meta_ts(s):
    try:
        return dt_.datetime.strptime(s.strip(), TS_FORMAT)
    except (ValueError, AttributeError):
        return None


def read_meta_info(path):
    """Only the fields the batch needs, from the first data row of a Meta log (csv module, no pandas).
    Personal columns are never kept."""
    with open(path, "r", encoding="utf-8-sig", newline="") as f:
        reader = csv.reader(f, delimiter=";")
        header = next(reader, None)
        row = next(reader, None)
    if not header or "SessionID" not in header:
        raise ConvertError("no header / SessionID column")
    if row is None:
        raise ConvertError("no data row")
    if len(row) < len(header):
        raise ConvertError(f"truncated row ({len(row)} of {len(header)} fields)")
    rec = dict(zip(header, row))
    get = lambda k: (rec.get(k) or "").strip()
    sid = get("SessionID")
    if sid in MISSING:
        raise ConvertError("empty SessionID")
    ts = get("Timestamp")
    return {"metaFile": Path(path).name, "session": sid, "pid": get("ParticipantID"),
            "timestamp": ts, "ts": parse_meta_ts(ts), "sessionState": get("SessionState"),
            "program": get("SessionProgram")}


def session_ids(path, first_only):
    """SessionIDs in a log (first non-empty only, or all), via the csv module."""
    found = set()
    with open(path, "r", encoding="utf-8-sig", newline="") as f:
        reader = csv.reader(f, delimiter=";")
        header = next(reader, None)
        if not header or "SessionID" not in header:
            return found
        i = header.index("SessionID")
        for n, row in enumerate(reader):
            if i < len(row) and row[i].strip() not in MISSING:
                found.add(row[i].strip())
                if first_only:
                    break
            if first_only and n > 5000:
                break
    return found


def has_mole_spawned(event_path, sid):
    with open(event_path, "r", encoding="utf-8-sig", newline="") as f:
        reader = csv.reader(f, delimiter=";")
        header = next(reader, None)
        if not header or "Event" not in header or "SessionID" not in header:
            return False
        ie, isd = header.index("Event"), header.index("SessionID")
        for row in reader:
            if len(row) > max(ie, isd) and row[ie].strip() == "Mole Spawned" and row[isd].strip() == sid:
                return True
    return False


def now_local():
    return dt_.datetime.now()


def iso(t):
    return t.isoformat(timespec="seconds")


def archive_suffix(t):
    return t.strftime(" R %d-%m-%Y %H.%M")


def split_name(name):
    """'P07_part2_movement.cdataset.meta' -> ('P07_part2_movement', '.cdataset.meta')."""
    i = name.find(".")
    return (name, "") if i < 0 else (name[:i], name[i:])


def output_location(out, name):
    """Active location of an output file (or its Unity .meta) by name."""
    return (out / "ProcessedData" / name) if ".cdataset" in name else (out / name)


def move_file(src, dst):
    try:
        os.replace(src, dst)
    except OSError:   # other volume (--state elsewhere)
        shutil.move(str(src), str(dst))


def output_names(label, part):
    return [f"{label}_part{part}_{k}{ext}" for ext in OUT_KINDS_EXT for k in KINDS]


class StateError(Exception):
    pass


class AutoImporter:
    def __init__(self, a, log=print):
        self.a = a
        self.log = log
        self.logs = Path(a.logs) if a.logs else Path.home() / "Documents"
        self.out = Path(a.out)
        self.state_dir = Path(a.state) if a.state else self.out / "WamIA~"
        self.state_path = self.state_dir / "state.json"
        self.archive_dir = self.state_dir / "Archive"
        self.result = {"converted": [], "replaced": [], "skipped": [], "warnings": [], "errors": [],
                       "participants": [], "restored": []}
        self.state = {"version": STATE_VERSION, "sessions": {}}
        self._pre_mod = None
        self._lock = None
        self._saved_text = None

    # ---- state ---------------------------------------------------------------------------
    def load_state(self):
        if not self.state_path.is_file():
            return
        try:
            st = json.loads(self.state_path.read_text(encoding="utf-8"))
        except (OSError, ValueError) as e:
            raise StateError(f"cannot read {self.state_path}: {e}")
        if not isinstance(st, dict) or not isinstance(st.get("sessions"), dict):
            raise StateError(f"{self.state_path} is not a WAM+IA state file")
        if st.get("version", STATE_VERSION) > STATE_VERSION:
            raise StateError(f"{self.state_path} has version {st.get('version')} (this converter knows {STATE_VERSION})")
        self.state = st
        self._saved_text = json.dumps(st, indent=1, ensure_ascii=False)

    def save_state(self):
        """Atomic: write state.json.tmp, then replace. Skipped when nothing changed."""
        text = json.dumps(self.state, indent=1, ensure_ascii=False)
        if text == self._saved_text:
            return
        self.state_dir.mkdir(parents=True, exist_ok=True)
        tmp = self.state_path.with_name("state.json.tmp")
        with open(tmp, "w", encoding="utf-8") as f:
            f.write(text)
            f.flush()
            os.fsync(f.fileno())
        os.replace(tmp, self.state_path)
        self._saved_text = text

    def lock(self):
        """One run at a time (CUBIA may start a run on Play and another from the dashboard)."""
        self.state_dir.mkdir(parents=True, exist_ok=True)
        p = self.state_dir / "run.lock"
        for _ in range(2):
            try:
                fd = os.open(p, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
                os.write(fd, str(os.getpid()).encode())
                os.close(fd)
                self._lock = p
                return True
            except FileExistsError:
                try:
                    if time.time() - p.stat().st_mtime > 600:   # stale (crashed run)
                        p.unlink()
                        continue
                except OSError:
                    continue
                return False
        return False

    def unlock(self):
        if self._lock:
            with contextlib.suppress(OSError):
                self._lock.unlink()

    def sessions(self):
        return self.state["sessions"]

    def active_holder(self, label, part):
        for sid, s in self.sessions().items():
            if s.get("participant") == label and s.get("part") == part and s.get("status") == "active":
                return sid
        return None

    # ---- archive -------------------------------------------------------------------------
    def archive_set(self, names):
        """Archive one session's outputs with a common suffix; the ' (k)' counter (same minute)
        is shared by the whole set so restore finds it as one set."""
        stamp = now_local()
        self.archive_dir.mkdir(parents=True, exist_ok=True)
        present = [n for name in names for n in (name, name + ".meta") if output_location(self.out, n).is_file()]
        base = archive_suffix(stamp)
        suffix, k = base, 2
        while any((self.archive_dir / (split_name(n)[0] + suffix + split_name(n)[1])).exists() for n in present):
            suffix, k = f"{base} ({k})", k + 1
        moved = []
        for n in present:
            stem, ext = split_name(n)
            dst = self.archive_dir / f"{stem}{suffix}{ext}"
            move_file(output_location(self.out, n), dst)
            moved.append(dst.name)
        return moved, stamp

    # ---- scanning ------------------------------------------------------------------------
    def scan_metas(self):
        metas, bad = {}, []
        for p in sorted(self.logs.glob("*_Meta.csv")):
            try:
                m = read_meta_info(p)
            except (ConvertError, OSError, UnicodeDecodeError, csv.Error) as e:
                bad.append((p.name, str(e)))
                continue
            m["path"] = p
            old = metas.get(m["session"])
            if old is None or p.stat().st_mtime > old["path"].stat().st_mtime:
                metas[m["session"]] = m
        return metas, bad

    def build_index(self, kind, first_only, exclude):
        idx = {}
        for p in self.logs.glob(f"*_{kind}.csv"):
            if p.name in exclude:
                continue
            try:
                sids = session_ids(p, first_only)
            except (OSError, UnicodeDecodeError, csv.Error):
                continue
            for sid in sids:
                if sid not in idx or p.stat().st_mtime > idx[sid].stat().st_mtime:
                    idx[sid] = p
        return idx

    def skip(self, m, reason, detail=""):
        sid = m["session"]
        old = self.sessions().get(sid, {})
        changed = old.get("status") != "skipped" or old.get("reason") != reason
        self.sessions()[sid] = {
            "participant": m.get("label") or "", "part": 0, "status": "skipped", "reason": reason,
            "metaFile": m["metaFile"], "eventFile": m.get("eventFile", ""), "sampleFile": m.get("sampleFile", ""),
            "timestamp": m["timestamp"], "convertedAt": "", "outputs": [], "archived": old.get("archived", []),
        }
        if changed:   # reported once, not on every run (idempotent output)
            self.result["skipped"].append({"session": sid, "reason": reason})
            self.log(f"  skipped {m.get('label') or '?'} session {sid[:8]} ({m['metaFile']}): {reason}"
                     + (f" - {detail}" if detail else ""))

    # ---- conversion ----------------------------------------------------------------------
    def preprocessor(self):
        if self._pre_mod is None:
            load_numeric()
            self._pre_mod = load_preprocessor(self.a.preprocessor)
        return self._pre_mod

    def stage(self, m, label, part):
        """Convert into <state>/staging (CSV + .cdataset). Raises on any problem; nothing active
        is touched yet."""
        staging = self.state_dir / "staging"
        shutil.rmtree(staging, ignore_errors=True)
        staging.mkdir(parents=True)
        tables, info = convert(m["eventPath"], m["samplePath"], m["path"], label, part,
                               self.a.downsample, self.a.hand, log=self.log)
        paths = []
        for kind in KINDS:
            p = staging / f"{label}_part{part}_{kind}.csv"
            write_csv(tables[kind], p)
            paths.append(p)
        if not self.a.no_preprocess:
            preprocess(paths, staging, self.a.preprocessor, self.a.verbose, module=self.preprocessor())
        return staging, info

    def install(self, staging, label, part):
        names = []
        (self.out / "ProcessedData").mkdir(parents=True, exist_ok=True)
        for name in output_names(label, part):
            src = (staging / "ProcessedData" / name) if name.endswith(".cdataset") else (staging / name)
            if src.is_file():
                move_file(src, output_location(self.out, name))
                names.append(name)
        shutil.rmtree(staging, ignore_errors=True)
        return names

    def warn(self, text):
        self.result["warnings"].append(text)
        self.log(f"  WARNING: {text}")

    def import_session(self, m, label, part, old_sid):
        sid = m["session"]
        try:
            staging, info = self.stage(m, label, part)
        except Exception as e:   # one bad session never stops the batch
            shutil.rmtree(self.state_dir / "staging", ignore_errors=True)
            msg = str(e).splitlines()[0] if str(e) else type(e).__name__
            if "Mole Spawned" in msg:
                self.skip(m, "no moles")
            else:
                self.result["errors"].append(f"{label} session {sid}: {msg}")
                self.skip(m, "error", msg)
            return False

        archived, stamp = [], None
        if old_sid:
            old = self.sessions()[old_sid]
            archived, stamp = self.archive_set(old.get("outputs", []))
            old.update(status="replaced", archived=archived, archivedAt=stamp.isoformat(), replacedBy=sid)
        # files at the target names that the state does not know about (e.g. a manual run): archive too
        foreign, _ = self.archive_set(output_names(label, part))
        if foreign:
            self.warn(f"{label} part {part}: existing files not made by the auto-import were archived: "
                      + ", ".join(foreign))
        names = self.install(staging, label, part)
        self.sessions()[sid] = {
            "participant": label, "part": part, "status": "active", "reason": "",
            "metaFile": m["metaFile"], "eventFile": m["eventPath"].name, "sampleFile": m["samplePath"].name,
            "timestamp": m["timestamp"], "convertedAt": iso(now_local()), "outputs": names, "archived": [],
        }
        self.save_state()
        files = [n for n in names if n.endswith(".cdataset")] or names
        self.result["converted"].append({"participant": label, "part": part, "session": sid, "files": files})
        self.log(f"  converted {label} part {part} <- session {sid[:8]} ({m['metaFile']}), "
                 f"main hand {info['main_hand']}")
        for w in info["warnings"]:
            self.log(f"    note: {w}")
        if old_sid:
            self.result["replaced"].append({"participant": label, "part": part, "oldSession": old_sid,
                                            "newSession": sid, "archived": archived})
            self.log(f"  replaced {label} part {part}: session {old_sid[:8]} -> {sid[:8]}; "
                     f"old files archived in {self.archive_dir}")
        return True

    # ---- the batch -----------------------------------------------------------------------
    def run(self):
        t0 = time.perf_counter()
        if not self.logs.is_dir():
            raise StateError(f"--logs folder not found: {self.logs}")
        self.load_state()
        S = self.sessions()
        metas, bad = self.scan_metas()
        for name, why in bad:
            self.result["skipped"].append({"session": name, "reason": "unreadable Meta"})
            self.log(f"  skipped {name}: unreadable Meta ({why}); re-checked next run")
        for m in metas.values():
            m["label"] = participant_label(m["pid"])
        self.result["participants"] = sorted({m["label"] for m in metas.values() if m["label"]}, key=label_sort_key)
        self.log(f"WAM+IA auto-import: {len(metas)} session(s) in {self.logs}, {len(S)} in state")

        # 1) sessions that still need a decision
        pending = []
        prog = (self.a.program or "").strip().lower()
        for sid, m in metas.items():
            st = S.get(sid, {})
            if st.get("status") in ("active", "replaced"):
                continue
            if "warmup" in m["program"].lower().replace("-", "").replace(" ", "").replace("_", ""):
                self.skip(m, "warmup")
            elif prog and m["program"].lower() != prog:
                self.skip(m, "program")
            elif not m["label"]:
                self.skip(m, "no participant")
            else:
                pending.append(m)
        if not pending:
            return self.finish(t0)

        # 2) pair with Event/Sample by SessionID (files of imported sessions are not re-read)
        known = {S[s].get(k) for s in S if S[s].get("status") in ("active", "replaced")
                 for k in ("eventFile", "sampleFile")}
        ev_idx = smp_idx = None
        now = time.time()
        candidates = []
        for m in pending:
            sid = m["session"]
            same = m["path"].with_name(m["path"].name[:-len("_Meta.csv")] + "_Event.csv")
            try:
                if same.is_file() and sid in session_ids(same, False):
                    ev = same
                else:
                    ev_idx = ev_idx if ev_idx is not None else self.build_index("Event", False, known)
                    ev = ev_idx.get(sid)
                if ev is None:
                    self.skip(m, "missing Event")
                    continue
                m["eventPath"], m["eventFile"] = ev, ev.name
                smp_idx = smp_idx if smp_idx is not None else self.build_index("Sample", True, known)
                sp = smp_idx.get(sid)
                if sp is None:
                    self.skip(m, "missing Sample")
                    continue
                m["samplePath"], m["sampleFile"] = sp, sp.name
                newest = max(p.stat().st_mtime for p in (m["path"], ev, sp))
                if now - newest < self.a.settle_seconds:
                    self.skip(m, "in progress")
                    continue
                if not has_mole_spawned(ev, sid):
                    self.skip(m, "no moles")
                    continue
            except (OSError, UnicodeDecodeError, csv.Error) as e:
                self.skip(m, "error", str(e))
                continue
            candidates.append(m)

        # 3) assign parts in Meta Timestamp order (contract "Which session is which part")
        def key(m):
            return (m["ts"] or dt_.datetime.max, m["timestamp"], m["session"])
        candidates.sort(key=key)
        meta_by_sid = metas
        for i, m in enumerate(candidates):
            label, sid = m["label"], m["session"]
            mine = [(s, S[s]) for s in S if S[s].get("participant") == label
                    and S[s].get("status") in ("active", "replaced")]
            latest = max(mine, key=lambda x: (parse_meta_ts(x[1].get("timestamp", "")) or dt_.datetime.min,
                                              x[1].get("timestamp", "")), default=None)
            if latest and key(m) < ((parse_meta_ts(latest[1]["timestamp"]) or dt_.datetime.max),
                                    latest[1]["timestamp"], latest[0]):
                self.skip(m, "out of order")
                self.warn(f"{label} session {sid} ({m['metaFile']}) is older than the already imported "
                          f"session {latest[0]}; not imported automatically (use the single-session mode)")
                continue
            later_same = any(c["label"] == label for c in candidates[i + 1:])
            latest_meta = meta_by_sid.get(latest[0]) if latest else None
            latest_interrupted = bool(latest and latest[1].get("status") == "active" and latest_meta
                                      and latest_meta["sessionState"].strip().lower() == "interrupted")
            if latest_interrupted:
                part, old = latest[1]["part"], latest[0]
            elif not self.active_holder(label, 1):
                part, old = 1, None
            elif not self.active_holder(label, 2):
                part, old = 2, None
            else:
                part, old = 2, self.active_holder(label, 2)

            if m["sessionState"].strip().lower() == "interrupted" and later_same:
                # restarted round: replaced by the next session before it was ever imported
                nxt = next(c for c in candidates[i + 1:] if c["label"] == label)
                S[sid] = {"participant": label, "part": part, "status": "replaced", "reason": "interrupted",
                          "metaFile": m["metaFile"], "eventFile": m["eventFile"], "sampleFile": m["sampleFile"],
                          "timestamp": m["timestamp"], "convertedAt": "", "outputs": [], "archived": [],
                          "replacedBy": nxt["session"]}
                self.save_state()
                self.result["replaced"].append({"participant": label, "part": part, "oldSession": sid,
                                                "newSession": nxt["session"], "archived": []})
                self.log(f"  replaced {label} part {part}: interrupted session {sid[:8]} -> {nxt['session'][:8]} "
                         f"(not imported)")
                continue

            if not mine:   # first import of this participant: ascending-number check
                nums = sorted({label_number(S[s]["participant"]) for s in S
                               if S[s].get("status") in ("active", "replaced")} - {None})
                n = label_number(label)
                if nums and n is not None and n != nums[-1] + 1:
                    self.warn(f"participant {label} is not the next number after P{nums[-1]:02d} "
                              f"(expected P{nums[-1] + 1:02d}); imported anyway")
            self.import_session(m, label, part, old)
        self.save_state()
        return self.finish(t0)

    def finish(self, t0):
        self.save_state()
        r = self.result
        self.log(f"  done in {time.perf_counter() - t0:.2f} s: {len(r['converted'])} converted, "
                 f"{len(r['replaced'])} replaced, {len(r['skipped'])} skipped, {len(r['errors'])} error(s)")
        return 0

    # ---- restore -------------------------------------------------------------------------
    def restore(self, who, part):
        self.load_state()
        S = self.sessions()
        label = participant_label(who[1:] if re.fullmatch(r"[Pp]\d+", who.strip()) else who)
        cands = [(s, v) for s, v in S.items() if v.get("participant") == label and v.get("part") == part
                 and v.get("status") == "replaced" and v.get("archived")]
        if not cands:
            raise StateError(f"nothing archived for {label} part {part}")
        sid, rec = max(cands, key=lambda x: x[1].get("archivedAt", ""))
        cur = self.active_holder(label, part)
        archived = []
        if cur:
            archived, stamp = self.archive_set(S[cur].get("outputs", []))
            S[cur].update(status="replaced", archived=archived, archivedAt=stamp.isoformat(), replacedBy=sid)
        foreign, _ = self.archive_set(output_names(label, part))
        if foreign:
            self.warn(f"{label} part {part}: untracked files archived: " + ", ".join(foreign))
        (self.out / "ProcessedData").mkdir(parents=True, exist_ok=True)
        back = []
        for an in rec["archived"]:
            src = self.archive_dir / an
            if not src.is_file():
                self.result["errors"].append(f"archived file missing: {src}")
                continue
            name = ARCHIVE_SUFFIX_RE.sub("", an, count=1)
            move_file(src, output_location(self.out, name))
            back.append(name)
        rec.update(status="active", archived=[], replacedBy="", restoredAt=iso(now_local()))
        rec["outputs"] = [n for n in back if not n.endswith(".meta")] or rec.get("outputs", [])
        self.save_state()
        self.result["restored"].append({"participant": label, "part": part, "session": sid, "files": back})
        if cur:
            self.result["replaced"].append({"participant": label, "part": part, "oldSession": cur,
                                            "newSession": sid, "archived": archived})
        self.log(f"restored {label} part {part} <- session {sid[:8]} ({len(back)} file(s))"
                 + (f"; session {cur[:8]} archived (undo with the same command)" if cur else ""))
        return 0


def run_batch(a):
    imp = AutoImporter(a)
    code = 0
    try:
        if not imp.lock():
            imp.result["errors"].append("another WAM+IA import is running; try again in a moment")
            imp.log("another WAM+IA import is running")
        else:
            try:
                if a.restore:
                    try:
                        part = int(a.restore[1])
                    except ValueError:
                        raise StateError(f"--restore part must be 1 or 2, not {a.restore[1]!r}")
                    code = imp.restore(a.restore[0], part)
                else:
                    code = imp.run()
            finally:
                imp.unlock()
    except StateError as e:
        imp.result["errors"].append(str(e))
        print(f"ERROR: {e}", file=sys.stderr)
        code = 2
    if a.json:
        print("WAMIA_RESULT " + json.dumps(imp.result, ensure_ascii=True, separators=(",", ":")), flush=True)
    return code


def main(argv=None):
    ap = argparse.ArgumentParser(description="Whack-A-Mole VR logs (one play-through) -> CUBIA datasets")
    ap.add_argument("--event", help="..._Event.csv")
    ap.add_argument("--sample", help="..._Sample.csv")
    ap.add_argument("--meta", help="..._Meta.csv (optional; only SessionID and SamplingFrequency are read)")
    ap.add_argument("--logs", help="folder with WAM logs: use the newest Event/Sample/Meta set sharing a SessionID "
                                   "(--auto: every session in it; default ~/Documents)")
    ap.add_argument("--participant", help="e.g. P07 (normalised like the CUBIA session log)")
    ap.add_argument("--part", type=int, choices=[1, 2], help="study part (1 or 2)")
    ap.add_argument("--auto", action="store_true",
                    help="batch: import every new session in --logs (see AUTO_IMPORT_CONTRACT.md)")
    ap.add_argument("--restore", nargs=2, metavar=("PARTICIPANT", "PART"),
                    help="move the most recent archived files of PARTICIPANT/PART back (current ones are archived)")
    ap.add_argument("--state", help="--auto/--restore state folder (default <out>/WamIA~)")
    ap.add_argument("--program", help="--auto: only sessions with this SessionProgram (case-insensitive)")
    ap.add_argument("--json", action="store_true", help="--auto/--restore: end with a 'WAMIA_RESULT {json}' line")
    ap.add_argument("--settle-seconds", type=float, default=5.0,
                    help="--auto: skip sessions whose logs changed less than this many seconds ago (default 5)")
    ap.add_argument("--out", default=str(DEFAULT_OUT), help=f"output folder (default {DEFAULT_OUT})")
    ap.add_argument("--downsample", type=int, default=1, help="keep every Nth sample (default 1 = all)")
    ap.add_argument("--hand", choices=["right", "left"], help="force the main hand (default: auto)")
    ap.add_argument("--no-preprocess", action="store_true", help="only write the CSVs")
    ap.add_argument("--preprocessor", default=str(DEFAULT_PREPROCESSOR),
                    help="path to CUBIA's preprocess_csv_columnar.py")
    ap.add_argument("--verbose", action="store_true", help="show the preprocessor's own output")
    a = ap.parse_args(argv)
    if a.auto or a.restore:
        if a.auto and a.restore:
            ap.error("use either --auto or --restore")
        if a.event or a.sample or a.meta or a.participant or a.part:
            ap.error("--auto/--restore take no --event/--sample/--meta/--participant/--part")
        if a.downsample < 1:
            ap.error("--downsample must be >= 1")
        return run_batch(a)
    missing = [f for f, v in (("--participant", a.participant), ("--part", a.part)) if v is None]
    if missing:
        ap.error("the following arguments are required: " + ", ".join(missing))
    load_numeric()

    t_start = time.perf_counter()
    try:
        if a.logs:
            if a.event or a.sample:
                raise ConvertError("use either --logs or --event/--sample, not both")
            a.event, a.sample, a.meta, _ = find_triple(a.logs)
        elif not (a.event and a.sample):
            raise ConvertError("give --event and --sample (or --logs <folder>)")
        if a.downsample < 1:
            raise ConvertError("--downsample must be >= 1")
        out = Path(a.out)
        out.mkdir(parents=True, exist_ok=True)

        tables, info = convert(a.event, a.sample, a.meta, a.participant, a.part, a.downsample, a.hand)
        paths = []
        for kind in KINDS:
            p = out / f"{output_name(a.participant, a.part, kind)}.csv"
            write_csv(tables[kind], p)
            paths.append(p)
        pre = None if a.no_preprocess else preprocess(paths, out, a.preprocessor, a.verbose)
    except ConvertError as e:
        print(f"ERROR: {e}", file=sys.stderr)
        return 2

    m = tables["moles"]
    print("WAM -> CUBIA  " + normalise_participant(a.participant) + f" part {a.part}")
    print(f"  event : {Path(a.event).name}\n  sample: {Path(a.sample).name}"
          + (f"\n  meta  : {Path(a.meta).name}" if a.meta else ""))
    print(f"  session {info['session']}  main hand {info['main_hand']} (from {info['hand_source']})"
          + (f"  sampling {info['sampling']} s" if info["sampling"] else ""))
    for kind, p in zip(KINDS, paths):
        extra = f"  -> ProcessedData/{p.stem}.cdataset" if pre else ""
        print(f"  {kind:<9}{len(tables[kind]):>7} rows  {p}{extra}")
    counts = m["Outcome"].value_counts()
    print(f"  moles {len(m)}: hit {counts.get('Hit', 0)}, expired {counts.get('Expired', 0)}, "
          f"expired-hit {counts.get('ExpiredHit', 0)}, unresolved {counts.get('NotResolved', 0)}")
    rt = m["ReactionTime"].dropna()
    print(f"  mean reaction time {rt.mean():.3f} s" if len(rt) else "  mean reaction time n/a")
    print(f"  play time {info['play_seconds']:.1f} s (first spawn -> last outcome)")
    for w in info["warnings"]:
        print(f"  WARNING: {w}")
    print(f"  done in {time.perf_counter() - t_start:.2f} s")
    return 0


if __name__ == "__main__":
    sys.exit(main())
