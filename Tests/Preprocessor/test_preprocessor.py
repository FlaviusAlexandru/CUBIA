#!/usr/bin/env python3
"""Regression tests for Assets/Scripts/Data/preprocess_csv_columnar.py.

Usage (from the repo root):
  python Tests/Preprocessor/test_preprocessor.py fixtures            # small fixtures vs expected/
  python Tests/Preprocessor/test_preprocessor.py fixtures --update   # regenerate expected/ (review the diff!)
  python Tests/Preprocessor/test_preprocessor.py committed           # real CSVs vs committed ProcessedData/

`fixtures` checks the parsing rules (decimal commas, dot decimals, timestamps).
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
}
EXPECTED_RANGES = {
    ("decimal_comma", "X slab [mm]"): (32.5, 582.5),
    ("decimal_comma", "Y slab [mm]"): (518.5, 618.5),
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
    """Return {column name: (type name, min, max)} from a .cdataset file."""
    b = path.read_bytes()
    assert b[:4] == b"DVCB", f"{path.name}: bad magic"
    rows, cols = struct.unpack_from("<II", b, 6)
    p, result = 14, {}
    for _ in range(cols):
        n = b[p]; p += 1
        name = b[p:p + n].decode("utf-8"); p += n
        t = b[p]; p += 1
        if t in (1, 3):
            mn, mx = struct.unpack_from("<ff", b, p); p += 8 + rows * 4
            result[name] = (TYPE_NAMES[t], mn, mx)
        else:
            (k,) = struct.unpack_from("<I", b, p); p += 4
            for _ in range(k):
                (ln,) = struct.unpack_from("<H", b, p); p += 2 + ln
            p += rows * 4
            result[name] = (TYPE_NAMES.get(t, f"type {t}"), None, None)
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
            _, mn, mx = read_columns(out / f"{stem}.cdataset")[col]
            if mn is None or abs(mn - lo) > 1e-3 or abs(mx - hi) > 1e-3:
                errors.append(f"{stem}: column '{col}' range is [{mn}, {mx}], expected [{lo}, {hi}]")

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
