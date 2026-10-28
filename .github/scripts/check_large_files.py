#!/usr/bin/env python3
"""Fail if a pull request adds or modifies a file larger than --limit-mb.

GitHub warns at 50 MB and rejects pushes with files over 100 MB. Existing
large files are only flagged if the pull request changes them.
"""
import argparse
import subprocess
import sys


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--base", required=True)
    ap.add_argument("--head", required=True)
    ap.add_argument("--limit-mb", type=float, default=50)
    args = ap.parse_args()
    limit = int(args.limit_mb * 1024 * 1024)

    changed = subprocess.run(
        ["git", "diff", "--name-only", "-z", "--diff-filter=AM", f"{args.base}...{args.head}"],
        check=True, capture_output=True).stdout.decode("utf-8").split("\0")

    too_big = []
    for path in filter(None, changed):
        size = int(subprocess.run(["git", "cat-file", "-s", f"{args.head}:{path}"],
                                  check=True, capture_output=True, text=True).stdout)
        if size > limit:
            too_big.append((path, size))

    for path, size in too_big:
        print(f"::error file={path}::{path} is {size / 1024 / 1024:.1f} MB (limit {args.limit_mb:g} MB)")
    print(f"Checked {len([c for c in changed if c])} changed files; {len(too_big)} over the limit.")
    if too_big:
        print("Fix: shrink the file, keep it out of git, or move it to Git LFS.")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
