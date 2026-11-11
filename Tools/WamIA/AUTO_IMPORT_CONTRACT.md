# WAM+IA auto-import contract

Shared by the Python converter (`wam_to_cubia.py --auto`) and CUBIA's C# side
(`WamIaAutoImporter` + dashboard). Both sides must follow it exactly.

## When it runs
CUBIA runs the converter in the background every time the data scene starts (each Play press in the
editor, each launch of a PC build) and again when the facilitator presses "Import new logs" on the
dashboard. CUBIA never blocks on it.

## Command
```
python wam_to_cubia.py --auto --logs <WAM log folder> --out <DataCSV folder> --json
                       [--state <state folder>] [--program <pattern name>] [--preprocessor <path>]
```
- `--logs`: folder WAM writes to (default: the user's Documents folder). Non-recursive.
- `--out`: CUBIA's `StreamingAssets/DataCSV`. CSVs go there, `.cdataset` files to `<out>/ProcessedData`.
- `--state`: default `<out>/WamIA~` (the trailing `~` makes Unity ignore the folder: no import, not
  shipped). Holds `state.json` and the `Archive/` folder.
- `--program`: if given, only sessions whose Meta `SessionProgram` equals it (case-insensitive) are used.
  Sessions whose SessionProgram contains "warmup" are always skipped (reason "warmup").
- Exit code 0 unless something fatal happened (bad arguments, unreadable state). Per-session problems
  are reported, never fatal.

## Participant label
Meta `ParticipantID` integer n (optionally written with a leading P, e.g. `P07`) -> label `P` + n zero-padded to 2 digits (`7` -> `P07`, `12` -> `P12`,
`0` -> `P00`). Non-integer IDs: `P` + the ID with characters outside [A-Za-z0-9_-] removed.
Output files: `{label}_part{part}_{movement|moles|combined}.csv/.cdataset`.

## Which session is which part
For each participant, sessions (Meta + Event + Sample sharing a SessionID, with at least one
"Mole Spawned" event) are sorted by Meta `Timestamp`:
1. A session with Meta `SessionState` = "Interrupted" that is followed by a later session of the same
   participant is a restarted round: it is replaced by that next session.
2. Of the remaining sessions, the first is part 1 and the second part 2.
3. Any further session replaces part 2 (newest wins).
Replacements are not errors: they produce a warning (toast + Debug.LogWarning) and the replaced
outputs are archived, never deleted.

## Archive (undo)
When a part's outputs are replaced, the old files (`.csv`, `.cdataset`, and their Unity `.meta` files
if present) move to `<state>/Archive/` with ` R dd-MM-yyyy HH.mm` inserted before the extension
(local time of the replacement; `/` and `:` are not allowed in Windows file names), e.g.
`P07_part2_movement R 09-11-2026 14.35.cdataset`. `state.json` records each archived file with the
session it came from. To undo, copy the archived files back and remove the ` R …` part
(`--restore P07 2` does this for the most recent archive of that participant and part).

## State (`<state>/state.json`)
```
{ "version": 1,
  "sessions": { "<SessionID>": { "participant": "P07", "part": 1, "status": "active|replaced|skipped",
                  "reason": "", "metaFile": "...", "eventFile": "...", "sampleFile": "...",
                  "timestamp": "2026-11-09 14:35:02.1234", "convertedAt": "2026-11-09T14:36:10",
                  "outputs": ["P07_part1_movement.csv", "..."], "archived": ["..."] } } }
```
A session already "active" or "replaced" in state is not converted again. "skipped" sessions are
re-checked each run (e.g. their Sample file may have appeared since).

## Ascending-number check
Participant numbers should go up by 1. When a new participant number is not exactly max+1 of the
numbers already in state (a gap, or lower than an existing one), emit a warning but convert anyway.

## Machine-readable result (`--json`)
The LAST line of stdout is `WAMIA_RESULT ` followed by one line of JSON:
```
{ "converted":  [ { "participant": "P07", "part": 1, "session": "...", "files": ["P07_part1_movement.cdataset", "..."] } ],
  "replaced":   [ { "participant": "P07", "part": 2, "oldSession": "...", "newSession": "...", "archived": ["..."] } ],
  "skipped":    [ { "session": "...", "reason": "warmup|no moles|missing Event|missing Sample|program" } ],
  "warnings":   [ "..." ],
  "errors":     [ "..." ],
  "participants": [ "P00", "P07" ] }
```
`participants` = every participant label found in Meta files in `--logs` (including ones not
converted yet), sorted by number. All arrays are always present (possibly empty).
