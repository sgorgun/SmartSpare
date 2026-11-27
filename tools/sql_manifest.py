#!/usr/bin/env python3
"""Append new migration SQL scripts to db/sql/applied.sha256 (#152).

The manifest records the SHA-256 (CRLF normalised to LF) of every script a migration runs through
SqlResource(...). Existing entries are never changed: an applied script is never edited, a change is a
new script and a new migration. AppliedMigrationScriptTests enforces this in CI.

Usage: python tools/sql_manifest.py   (from the repository root, or any directory)
"""
import hashlib
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
SQL = ROOT / "db" / "sql"
MANIFEST = SQL / "applied.sha256"
CALL = re.compile(r'SqlResource\([^,]+,\s*"(?P<path>[a-z]+/[A-Za-z0-9_]+\.sql)"\)')
HEADER = """# SHA-256 of every SQL script a merged migration runs (CRLF normalised to LF): hash, script, first migration.
# Never edit a listed script: add a new script and migration instead (#152). Append new ones with
# `python tools/sql_manifest.py`; AppliedMigrationScriptTests fails on a changed, removed or unlisted script.
"""


def digest(path: pathlib.Path) -> str:
    return hashlib.sha256(path.read_bytes().replace(b"\r\n", b"\n")).hexdigest()


def referenced() -> dict[str, str]:
    scripts: dict[str, str] = {}
    files = sorted(p for p in (ROOT / "src").rglob("Migrations/*.cs") if not p.name.endswith(".Designer.cs"))
    for file in files:
        migration = file.stem.split("_", 1)[-1]
        for match in CALL.finditer(file.read_text(encoding="utf-8")):
            scripts.setdefault(match.group("path"), migration)
    return scripts


def main() -> int:
    known = set()
    if MANIFEST.exists():
        for line in MANIFEST.read_text(encoding="utf-8").splitlines():
            if line and not line.startswith("#"):
                known.add(line.split()[1])
    added = [(script, migration) for script, migration in referenced().items() if script not in known]
    with MANIFEST.open("a", encoding="utf-8", newline="\n") as out:
        if not known:
            out.write(HEADER)
        for script, migration in added:
            out.write(f"{digest(SQL / script)}  {script}  {migration}\n")
    for script, migration in added:
        print(f"added {script} ({migration})")
    if not added:
        print("db/sql/applied.sha256 is up to date")
    return 0


if __name__ == "__main__":
    sys.exit(main())
