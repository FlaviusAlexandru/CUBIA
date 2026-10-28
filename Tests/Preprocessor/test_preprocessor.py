#!/usr/bin/env python3
"""Regression tests for Assets/Scripts/Data/preprocess_csv_columnar.py.

Usage (from the repo root):
  python Tests/Preprocessor/test_preprocessor.py fixtures            # small fixtures vs expected/
  python Tests/Preprocessor/test_preprocessor.py fixtures --update   # regenerate expected/ (review the diff!)
  python Tests/Preprocessor/test_preprocessor.py committed           # real CSVs vs committed ProcessedData/

`fixtures` checks the parsing rules (decimal commas, dot decimals, timestamps,
empty cells -> v3 validity mask).
`committed` checks that every .cdataset in git was regenerated after its CSV
or the script last changed.
"""
import contextlib
import importlib.util
import io
import shutil
import struct
import sys
import tempfile
import warnings
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
SCRIPT = REPO / "Assets" / "Scripts" / "Data" / "preprocess_csv_columnar.py"
HERE = Path(__file__).resolve().parent
FIXTURES = HERE / "fixtures"
EXPECTED = HERE / "expected"
DATA_CSV = REPO / "Assets" / "StreamingAssets" / "DataCSV"

TYPE_NAMES = {1: "Numeric", 2: "Categorical", 3: "DateTime"}

# Parsing rules, stated explicitly so a failure says *what* broke, not just "bytes differ".
EXPECTED_TYPES = {
    "decimal_comma": {
        "Point": "Numeric", "Row": "Numeric", "Column": "Numeric",
        "X slab [mm]": "Numeric", "Y slab [mm]": "Numeric", "Z slab [mm]": "Numeric",
        "Rebound R [-]": "Numeric",
    },
    "dot_decimal": {"Dim_0": "Numeric", "Dim_1": "Numeric", "Dim_2": "Numeric", "label": "Categorical"},
    "timestamps": {"Timestamp": "DateTime", "value": "Numeric", "state": "Categorical"},
    "missing_values": {"id": "Numeric", "value": "Numeric", "group": "Categorical", "when": "DateTime"},
}
EXPECTED_RANGES = {
    ("decimal_comma", "X slab [mm]"): (32.5, 582.5),
    ("decimal_comma", "Y slab [mm]"): (518.5, 618.5),
}
# Validity mask (format v3): rows that have a value, per column. Empty cells must
# be marked missing even though the data arrays still hold 0 / index 0 for them;
# missing_values.csv also has a real 0 in 'value' (row 5) that must stay valid.
EXPECTED_VALID = {
    ("missing_values", "id"): 10,
    ("missing_values", "value"): 9,
    ("missing_values", "group"): 8,
    ("missing_values", "when"): 9,
    ("dot_decimal", "label"): None,  # None = every row
}


def load_processor_class():
    spec = importlib.util.spec_from_file_location("preprocess_csv_columnar", SCRIPT)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module.ColumnarDatasetProcessor


def run_preprocessor(csv_files, out_dir: Path) -> None:
    """Run the real script on csv_files inside a throwaway project root; copy outputs to out_dir."""
    Processor = load_processor_class()
    with tempfile.TemporaryDirectory() as tmp:
        raw = Path(tmp) / "Assets" / "StreamingAssets" / "DataCSV"
        raw.mkdir(parents=True)
        for f in csv_files:
            shutil.copy(f, raw / f.name)
        with warnings.catch_warnings(), contextlib.redirect_stdout(io.StringIO()) as log:
            warnings.simplefilter("ignore")
            Processor(project_root=tmp).process_all_csvs()
        if ", 0 failed" not in log.getvalue():
            print(log.getvalue())
            raise SystemExit("preprocessor reported failures (log above)")
        out_dir.mkdir(parents=True, exist_ok=True)
        for f in (raw / "ProcessedData").glob("*.cdataset"):
            shutil.copy(f, out_dir / f.name)


def read_columns(path: Path):
    """Return {column name: (type name, min, max, rows with a value)} from a .cdataset file.

    v2 files have no validity mask (every row counts as having a value); v3 files
    end each column block with ceil(rows / 8) mask bytes, 1 bit per row, LSB-first.
    """
    b = path.read_bytes()
    assert b[:4] == b"DVCB", f"{path.name}: bad magic"
    (version,) = struct.unpack_from("<H", b, 4)
    rows, cols = struct.unpack_from("<II", b, 6)
    mask_len = (rows + 7) // 8
    p, result = 14, {}
    for _ in range(cols):
        n = b[p]; p += 1
        name = b[p:p + n].decode("utf-8"); p += n
        t = b[p]; p += 1
        if t in (1, 3):
            mn, mx = struct.unpack_from("<ff", b, p); p += 8 + rows * 4
        else:
            mn = mx = None
            (k,) = struct.unpack_from("<I", b, p); p += 4
            for _ in range(k):
                (ln,) = struct.unpack_from("<H", b, p); p += 2 + ln
            p += rows * 4
        valid = rows
        if version >= 3:
            mask = b[p:p + mask_len]; p += mask_len
            assert len(mask) == mask_len, f"{path.name}: truncated validity mask for '{name}'"
            valid = sum(bin(byte).count("1") for byte in mask)
        result[name] = (TYPE_NAMES.get(t, f"type {t}"), mn, mx, valid)
    assert p == len(b), f"{path.name}: {len(b) - p} trailing bytes after the last column"
    return result


def check_fixtures(update: bool) -> list:
    errors = []
    with tempfile.TemporaryDirectory() as tmp:
        out = Path(tmp)
        run_preprocessor(sorted(FIXTURES.glob("*.csv")), out)

        for stem, types in EXPECTED_TYPES.items():
            cols = read_columns(out / f"{stem}.cdataset")
            for col, want in types.items():
                got = cols.get(col, ("missing",))[0]
                if got != want:
                    errors.append(f"{stem}: column '{col}' is {got}, expected {want}")
        for (stem, col), (lo, hi) in EXPECTED_RANGES.items():
            _, mn, mx, _ = read_columns(out / f"{stem}.cdataset")[col]
            if mn is None or abs(mn - lo) > 1e-3 or abs(mx - hi) > 1e-3:
                errors.append(f"{stem}: column '{col}' range is [{mn}, {mx}], expected [{lo}, {hi}]")
        for (stem, col), want in EXPECTED_VALID.items():
            path = out / f"{stem}.cdataset"
            (version,) = struct.unpack_from("<H", path.read_bytes(), 4)
            if version < 3:
                errors.append(f"{stem}: format version {version}, expected >= 3 (validity mask)")
                continue
            got = read_columns(path).get(col, (None, None, None, None))[3]
            if want is None:
                want = struct.unpack_from("<I", path.read_bytes(), 6)[0]
            if got != want:
                errors.append(f"{stem}: column '{col}' has {got} rows with a value, expected {want}")

        if update:
            EXPECTED.mkdir(exist_ok=True)
            for f in out.glob("*.cdataset"):
                shutil.copy(f, EXPECTED / f.name)
            print(f"Updated {EXPECTED} — review the diff before committing.")
        else:
            for f in sorted(out.glob("*.cdataset")):
                ref = EXPECTED / f.name
                if not ref.exists():
                    errors.append(f"{f.name}: no expected file (run with --update)")
                elif ref.read_bytes() != f.read_bytes():
                    errors.append(f"{f.name}: output bytes differ from expected/{f.name}")
    return errors


def check_committed() -> list:
    errors = []
    csvs = sorted(DATA_CSV.glob("*.csv"))
    with tempfile.TemporaryDirectory() as tmp:
        out = Path(tmp)
        run_preprocessor(csvs, out)
        for csv in csvs:
            name = f"{csv.stem}.cdataset"
            committed = DATA_CSV / "ProcessedData" / name
            if not committed.exists():
                errors.append(f"{name}: missing from ProcessedData/ (run the preprocessor and commit it)")
            elif committed.read_bytes() != (out / name).read_bytes():
                errors.append(f"{name}: out of date with {csv.name} (run the preprocessor and commit it)")
    print(f"Checked {len(csvs)} CSVs against ProcessedData/.")
    return errors


def main() -> int:
    mode = sys.argv[1] if len(sys.argv) > 1 else "fixtures"
    if mode == "fixtures":
        errors = check_fixtures(update="--update" in sys.argv)
    elif mode == "committed":
        errors = check_committed()
    else:
        raise SystemExit(__doc__)
    for e in errors:
        print(f"::error::{e}")
    print("OK" if not errors else f"{len(errors)} problem(s)")
    return 1 if errors else 0


if __name__ == "__main__":
    sys.exit(main())
