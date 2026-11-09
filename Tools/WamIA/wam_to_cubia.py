#!/usr/bin/env python3
"""
WAM+IA log converter: Whack-A-Mole VR logs from ONE play-through -> three CUBIA datasets.

    python wam_to_cubia.py --event <..._Event.csv> --sample <..._Sample.csv> [--meta <..._Meta.csv>]
                           --participant P07 --part 1 [--out <folder>] [--no-preprocess]
    python wam_to_cubia.py --logs <folder with WAM logs> --participant P07 --part 1

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
import importlib.util
import io
import math
import os
import re
import sys
import time
import warnings
from pathlib import Path

import numpy as np
import pandas as pd

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
              "MoleX", "MoleY", "MoleZ", "SpawnTime", "Outcome", "ReactionTime",
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


def preprocess(csv_paths, out_dir, preprocessor_path, verbose=False):
    """Run CUBIA's per-file process_csv + write_binary on just these CSVs. The class constructor
    (which mkdirs under its own project root) and process_all_csvs (which converts EVERY csv in
    DataCSV) are deliberately bypassed."""
    mod = load_preprocessor(preprocessor_path)
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


def main(argv=None):
    ap = argparse.ArgumentParser(description="Whack-A-Mole VR logs (one play-through) -> CUBIA datasets")
    ap.add_argument("--event", help="..._Event.csv")
    ap.add_argument("--sample", help="..._Sample.csv")
    ap.add_argument("--meta", help="..._Meta.csv (optional; only SessionID and SamplingFrequency are read)")
    ap.add_argument("--logs", help="folder with WAM logs: use the newest Event/Sample/Meta set sharing a SessionID")
    ap.add_argument("--participant", required=True, help="e.g. P07 (normalised like the CUBIA session log)")
    ap.add_argument("--part", required=True, type=int, choices=[1, 2], help="study part (1 or 2)")
    ap.add_argument("--out", default=str(DEFAULT_OUT), help=f"output folder (default {DEFAULT_OUT})")
    ap.add_argument("--downsample", type=int, default=1, help="keep every Nth sample (default 1 = all)")
    ap.add_argument("--hand", choices=["right", "left"], help="force the main hand (default: auto)")
    ap.add_argument("--no-preprocess", action="store_true", help="only write the CSVs")
    ap.add_argument("--preprocessor", default=str(DEFAULT_PREPROCESSOR),
                    help="path to CUBIA's preprocess_csv_columnar.py")
    ap.add_argument("--verbose", action="store_true", help="show the preprocessor's own output")
    a = ap.parse_args(argv)

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
