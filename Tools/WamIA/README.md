# WAM+IA log converter

`wam_to_cubia.py` turns the Whack-A-Mole VR logs of **one play-through** (Event + Sample, optional Meta)
into three CUBIA datasets for the prepared views (`PreparedViews.cs`):

| file | rows | prepared view |
|---|---|---|
| `{P}_part{n}_movement.csv` | one per sample, first spawn to last outcome | Controller path (P2/P5) |
| `{P}_part{n}_moles.csv` | one per spawned mole | Accuracy per target (P6/P8, FR-05) |
| `{P}_part{n}_combined.csv` | cursor samples + moles on the wall plane | Targets and cursor (P7/P8) |

It then runs CUBIA's `Assets/Scripts/Data/preprocess_csv_columnar.py` on **just these three files**,
which writes `DataCSV/ProcessedData/*.cdataset`. Other CSVs in DataCSV are not touched.
The participant id is normalised like the CUBIA session log (`07`, `p07` and `P07` all give `P07`).

Needs Python 3.8+ with pandas and numpy, the same packages as the CUBIA preprocessor.

## Usage

Between rounds, pointing at the folder Whack-A-Mole writes its logs to:

    python Tools/WamIA/wam_to_cubia.py --logs "%USERPROFILE%\Documents\<WAM log folder>" --participant P07 --part 1

This picks the newest Event/Sample(/Meta) set that shares a SessionID. Files are paired by SessionID
because the Sample file's name stamp is a few ms off. Explicit files:

    python Tools/WamIA/wam_to_cubia.py --event X_Event.csv --sample Y_Sample.csv [--meta X_Meta.csv] --participant P07 --part 1

Options: `--out <folder>` (default `Assets/StreamingAssets/DataCSV`), `--no-preprocess`,
`--preprocessor <path>`, `--downsample N` (keep every Nth sample), `--hand right|left` (override
main-hand detection), `--verbose`. Tests: `python Tools/WamIA/test_wam_to_cubia.py` (set `WAMIA_REF_ROOT` if the pilot logs live in another checkout).

## Automatic import (`--auto`)

CUBIA runs this itself (see `AUTO_IMPORT_CONTRACT.md`, which both sides follow):

    python Tools/WamIA/wam_to_cubia.py --auto --logs <WAM log folder> --out Assets/StreamingAssets/DataCSV --json

It reads every `*_Meta.csv` in `--logs`, pairs Event/Sample by SessionID, works out part 1/part 2 per
participant (`ParticipantID` 7 -> `P07`) by Meta Timestamp, and converts only sessions not yet in
`<out>/WamIA~/state.json`. A restarted round (`SessionState` Interrupted followed by another session) and
any third session replace the earlier outputs; those are moved to `<out>/WamIA~/Archive/` with
` R dd-MM-yyyy HH.mm` before the extension, never deleted. Undo the last replacement of a part with

    python Tools/WamIA/wam_to_cubia.py --restore P07 2 --out Assets/StreamingAssets/DataCSV

(the files it displaces are archived the same way, so running it again undoes the restore).
The last stdout line with `--json` is `WAMIA_RESULT {...}`. Extra options: `--state <folder>`,
`--program <SessionProgram>`, `--settle-seconds N` (default 5: sessions whose log files changed less than
N s ago are skipped as "in progress" and picked up next run). Skip reasons beyond the contract's:
`unreadable Meta` (half-written Meta; `session` is then the file name), `in progress`, `no participant`,
`out of order` (older than a session already imported for that participant; import it by hand),
`error` (also listed in `errors`). A skip is reported once, when it first happens or its reason changes;
`state.json` always holds the current status. A run with nothing new reads only the Meta files and the
state and takes about 0.04 s.

**Privacy:** only whitelisted log columns are read. Email, age, gender, injury date, profile and device
ids are never read or written. Do not leave raw WAM logs in `DataCSV`: running the preprocessor on its
own converts every CSV there, raw logs and their Email column included.

## Column glossary

All times are in seconds since the first `Mole Spawned` (wall-clock timestamps, shared by the Event
and Sample logs). Positions are Unity world metres. "Main" means the main hand's controller.

**movement**: `Time`, `Part`, `Phase` (PatternSegmentLabel of the current mole), `MoleOrder` (1-based:
the latest mole spawned at or before this sample; samples between a hit and the next spawn belong to
the next mole), `MoleOrderLabel` ("Mole 01", text for colour), `ControllerX/Y/Z` (main controller),
`ControllerSpeed` (m/s, step distance / dt, 50 ms centred moving average, never empty), `LaserX/Y/Z`
(main laser cursor on the wall; samples more than 0.5 m off the wall plane are interpolated over),
`HeadX/Y/Z`, `HeadYaw` (degrees, + = right), `HeadPitch` (degrees, + = up).

**moles**: `Part`, `Phase`, `MoleOrder`, `MoleOrderLabel`, `GridX`, `GridY` (from MoleId
`OOOOXYY`), `MoleX/Y/Z`, `SpawnTime`, `Outcome` (`Hit`, `Expired`, `ExpiredHit`, or `NotResolved` if the
game stopped first), `ReactionTime` (MoleActivatedDuration of the hit; **empty = not hit**),
`ReactionTimeOrLifetime` (always filled: reaction time, or time until expiry),
`HitOffset` (distance of the hit from the mole centre, √(x²+y²) of MoleSurfaceHitLocation, in mole-local
units; **empty = not hit**), `HitOffsetX/Y` (same, per axis), `DistFromLastMole` (from the log),
`ControllerTravel` (m the main controller moved between spawn and outcome), `PathEfficiency` (straight
laser-cursor distance from spawn to outcome divided by the laser path length, 0..1; 1 = straight
line), `PeakSpeed` (max ControllerSpeed in that window), `MainHand`.

**combined**: `RowType` (`Cursor` or `Mole`), `X/Y/Z` (cursor = laser position, mole = mole position),
`Time` (mole = SpawnTime), `MoleOrder`, `Label` (mole number as text: "1", "2", …; CUBIA reads it as
numeric), `MoleOrderLabel` ("Mole 01", categorical: use this one for colour), `Phase`, `Part`.

**Empty cells:** only ReactionTime and HitOffset/X/Y are ever empty, and only for moles that were not
hit. Note that CUBIA's preprocessor reads a numeric column with ≥10% empty cells as Categorical. If
more than 10% of a play-through's moles are missed, those four columns become categorical in the
.cdataset. Use `ReactionTimeOrLifetime` for size/colour when that happens.

**Timing note:** `ReactionTime` comes from the game clock (MoleActivatedDuration), while `SpawnTime`
and `Time` come from log timestamps. The two can differ by up to ~0.1 s per mole.
