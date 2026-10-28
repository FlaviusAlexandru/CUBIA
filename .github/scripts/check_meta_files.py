#!/usr/bin/env python3
"""Fail if a tracked asset under Assets/ has no .meta, or a tracked .meta has no asset.

Missing .meta files make Unity generate new GUIDs on other machines, which
silently breaks every reference to that asset. Orphan .meta files are left
behind when an asset is deleted or renamed outside Unity.

Uses `git ls-files`, so it checks what is committed, not what is on disk.
"""
import subprocess
import sys

ROOT = "Assets"


def unity_ignores(path: str) -> bool:
    """Unity skips hidden files/folders, names ending in '~', 'cvs' and *.tmp."""
    for part in path.split("/"):
        if part.startswith(".") or part.endswith("~") or part.lower() == "cvs" or part.endswith(".tmp"):
            return True
    return False


def main() -> int:
    out = subprocess.run(["git", "ls-files", "-z", ROOT], check=True, capture_output=True).stdout
    tracked = {p for p in out.decode("utf-8").split("\0") if p and not unity_ignores(p)}

    metas = {p for p in tracked if p.endswith(".meta")}
    assets = tracked - metas

    folders = set()
    for a in assets:
        parts = a.split("/")[:-1]
        for i in range(2, len(parts) + 1):  # skip "Assets" itself
            folders.add("/".join(parts[:i]))

    missing = sorted(p for p in assets | folders if p + ".meta" not in metas)
    orphans = sorted(m for m in metas if m[:-5] not in assets and m[:-5] not in folders)

    for p in missing:
        print(f"::error file={p}::missing .meta for {p}")
    for m in orphans:
        print(f"::error file={m}::orphan .meta (no asset or folder): {m}")

    print(f"Checked {len(assets)} files, {len(folders)} folders, {len(metas)} .meta files: "
          f"{len(missing)} missing, {len(orphans)} orphans.")
    if missing or orphans:
        print("Fix: open the project in Unity so it creates/removes .meta files, then commit them "
              "together with their assets.")
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
