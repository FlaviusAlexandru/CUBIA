# Plan 001: Make preprocess_csv_columnar.py parse decimal-comma numbers and stop misreading numbers as dates

> **Executor instructions**: Follow this plan step by step. Run every
> verification command and confirm the expected result before moving to the
> next step. If anything in the "STOP conditions" section occurs, stop and
> report — do not improvise. When done, update the status row for this plan
> in `plans/README.md`.
>
> **Drift check (run first)**: `git diff --stat 3f19a30..HEAD -- Assets/Scripts/Data/preprocess_csv_columnar.py`
> If the file changed since this plan was written, compare the "Current state"
> excerpts against the live code before proceeding; on a mismatch, STOP.

## Status

- **Priority**: P1
- **Effort**: S
- **Risk**: LOW (offline preprocessing script; output format unchanged)
- **Depends on**: none
- **Category**: bug
- **Planned at**: commit `3f19a30`, 2026-10-28

## Why this matters

European-format CSVs (semicolon delimiter, decimal comma, e.g. `468,5`) are
silently corrupted by the Python preprocessor. In
`Assets/StreamingAssets/DataCSV/Rebound_points_50x50_raster.csv`, row 128 has
`Y slab [mm] = 468,5`, but the generated `.cdataset` stores `14735610000`
because the column was classified as **DateTime** (pandas read `468,5` as a
date) and stored as seconds since the column's earliest "date". The X columns
were classified **Categorical** (so positions are category indices, not
millimetres). The source CSV is valid. The C# `CSVImporter.TryParseFlexibleFloat`
already handles decimal commas; the Python path must behave the same.

Measured from the current `.cdataset` (format v2, 468 rows, 9 columns):

| Column | Current type | Should be |
|---|---|---|
| Point, Row, Column, Z slab [mm], Rebound R [-] | Numeric | Numeric |
| X slab [mm], X global [mm], Y global [mm] | Categorical | Numeric |
| Y slab [mm] | DateTime (max 6.52e10) | Numeric (18.5 … 618.5) |

## Current state

- `Assets/Scripts/Data/preprocess_csv_columnar.py` (301 lines) — CSV → `.cdataset` (magic `DVCB`, `VERSION = 2`). Run from repo root; it processes every `*.csv` in `Assets/StreamingAssets/DataCSV/` and writes `ProcessedData/<stem>.cdataset`.
- Type inference (`infer_column_type`):

```python
numeric_series = pd.to_numeric(series, errors='coerce')
numeric_ratio = numeric_series.notna().sum() / len(series)
if numeric_ratio >= 0.9:
    return "Numeric", numeric_ratio
date_series = pd.to_datetime(series, errors='coerce')
date_ratio = date_series.notna().sum() / len(series)
if date_ratio >= 0.9:
    return "DateTime", date_ratio
return "Categorical", 1.0 - max(numeric_ratio, date_ratio)
```

- Numeric branch in `process_csv` repeats `pd.to_numeric(series, errors='coerce')` then `.fillna(0.0)`.
- Delimiter detection (`detect_delimiter`) picks the most frequent of `, ; \t |` in the header line.
- Reference behaviour to mirror — `Assets/Scripts/Data/CSVImporter.cs:142-160`, `TryParseFlexibleFloat`: try invariant parse; on failure replace `,` with `.` and retry.
- Environment: Windows, Python 3.14, pandas 3.0.5. No automated test suite exists in the repo.
- Style: plain methods on `ColumnarDatasetProcessor`, type hints, explanatory block comments (see the DateTime branch). Match it.

## Commands you will need

| Purpose | Command (from repo root) | Expected on success |
|---|---|---|
| Run preprocessor | `python Assets\Scripts\Data\preprocess_csv_columnar.py` | ends `Processing complete: 12 succeeded, 0 failed` |
| Inspect types | `python plans\inspect_cdataset.py <file.cdataset>` (created in step 1) | prints one line per column |

## Scope

**In scope**:
- `Assets/Scripts/Data/preprocess_csv_columnar.py`
- `plans/inspect_cdataset.py` (create; helper for verification)
- Regenerated files in `Assets/StreamingAssets/DataCSV/ProcessedData/`

**Out of scope**:
- `CSVImporter.cs`, `ColumnarBinaryImporter.cs`, any C# — the binary format does not change.
- Any `.csv` source file — the data is correct.
- `VERSION`, magic bytes, or column type codes.

## Git workflow

- Branch: `advisor/001-preprocessor-decimal-comma`
- One commit for the script change, one for regenerated `.cdataset` files. Do NOT push.

## Steps

### Step 1: Snapshot current outputs

Copy `Assets/StreamingAssets/DataCSV/ProcessedData/` to `%TEMP%\cdataset_before\`.
Create `plans/inspect_cdataset.py` that reads a `.cdataset` (header: 4-byte magic,
`<H` version, `<I` rows, `<I` cols; per column: `<B` name length, name, `<B` type;
types 1/3 → `<f` min, `<f` max, rows×float32; type 2 → `<I` n, n×(`<H` len + utf-8),
rows×int32) and prints `name | type | min | max` (or `n categories`).

**Verify**: `python plans\inspect_cdataset.py Assets\StreamingAssets\DataCSV\ProcessedData\Rebound_points_50x50_raster.cdataset`
→ shows `Y slab [mm] | 3 |` and `X slab [mm] | 2 |`, matching the table above.
Also run it on every other `.cdataset` and save the output to `%TEMP%\types_before.txt`.

### Step 2: Add a flexible numeric converter

Add a method `to_numeric_flexible(self, series: pd.Series) -> pd.Series`:
1. `result = pd.to_numeric(series, errors='coerce')`.
2. For entries where `result` is NaN but `series` is not null, take
   `series.astype(str).str.strip().str.replace(',', '.', regex=False)` and
   `pd.to_numeric(..., errors='coerce')`; fill those positions in `result`.
3. Return `result`.

Only apply step 2 when `self.current_delimiter != ','` (store the detected
delimiter on `self` in `process_csv`). With a comma delimiter a comma inside a
field is a quoted thousands separator, not a decimal.

Use `to_numeric_flexible` in both `infer_column_type` and the Numeric branch of
`process_csv`, replacing both bare `pd.to_numeric(series, errors='coerce')` calls.

**Verify**: write a 5-line scratch script (outside the repo) that imports the module from `Assets/Scripts/Data`, builds an instance via `ColumnarDatasetProcessor.__new__(ColumnarDatasetProcessor)`, sets `current_delimiter = ';'`, and prints `to_numeric_flexible(pd.Series(['468,5','1032,5','12.5','abc'])).tolist()`
→ `[468.5, 1032.5, 12.5, nan]`

### Step 3: Guard DateTime inference against number-like strings

In `infer_column_type`, before calling `pd.to_datetime`, compute the share of
non-null values matching `^[+-]?\d+([.,]\d+)?$`
(`series.dropna().astype(str).str.strip().str.fullmatch(r'[+-]?\d+([.,]\d+)?')`).
If that share is ≥ 0.9, skip DateTime and return Categorical. Add a short
comment explaining why (pandas/dateutil happily parse `468,5` as a date).

Also print a line when a column is classified DateTime, e.g.
`  -> DateTime chosen for '<name>' (date_ratio=..)`, so misclassification is visible.

**Verify**: same scratch script, `infer_column_type(pd.Series(['18,5','468,5']*10))` with `current_delimiter=';'` → `('Numeric', 1.0)`.

### Step 4: Regenerate and compare

Run the preprocessor. Then run `inspect_cdataset.py` on all outputs.

**Verify**:
- Rebound file: `X slab`, `Y slab`, `X global`, `Y global` are type 1; `Y slab` min 18.5, max 618.5; `X slab` min 32.5, max 1782.5.
- For every other dataset, the inspect output is identical to `%TEMP%\types_before.txt` (in particular the `log_2026_09_22_...` dataset keeps any DateTime columns as type 3).
- `fc /b` of each non-Rebound `.cdataset` against `%TEMP%\cdataset_before\` reports no differences.

## Test plan

No test framework exists; verification is the inspect script plus byte comparison
in step 4. Keep `plans/inspect_cdataset.py` in the repo for later checks.

## Done criteria

- [ ] Preprocessor ends with `12 succeeded, 0 failed`
- [ ] Rebound `.cdataset`: no type-2 or type-3 columns among X/Y slab/global; Y slab range [18.5, 618.5]
- [ ] All other `.cdataset` files byte-identical to before
- [ ] Only in-scope files modified (`git status`)
- [ ] In Unity Play mode, Rebound row 128 tooltip shows `Y (Y slab [mm]): 468,5`
- [ ] `plans/README.md` status row updated

## STOP conditions

- Any non-Rebound `.cdataset` changes bytes or column types.
- The `log_2026_...` dataset loses a DateTime column.
- The preprocessor fails on any file.
- The fix seems to need a C# change or a format-version bump.

## Maintenance notes

- Python and C# importers now share one rule: invariant parse first, decimal-comma fallback. Change both together.
- A CSV that uses `.` as a thousands separator and `,` as decimal (`1.234,5`) is still not supported; out of scope until a dataset needs it.
- The project-knowledge copy of `preprocess_csv_columnar.py` is an old v1 snapshot; refresh it after this lands.
