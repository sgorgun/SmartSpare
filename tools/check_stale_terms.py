#!/usr/bin/env python3
"""
Fails when a tracked file describes a retired part of the architecture as if it were current.

Terms: MediatR, SmartSpare.DbMigrator, db/migrations, db/seed, SmartSpare.Api, SmartSpare.Domain
(replaced by own handlers, EF migrations and the module structure; ADR 0002-0013).

Since the topology decisions of 24 November 2026 (ADR 0015-0018) also: "single deployable" / "one deployable",
App Service, Application Insights and Azure Service Bus. They were replaced by Container Apps services, the
Grafana stack and RabbitMQ.

"JWT" is no longer a stale term: the internal gateway-to-service tokens are JWTs (ADR 0016). Only tokens in the
browser are ruled out.

A match is accepted when
  * the file is excluded: docs/adr/ (decisions record history), the frozen baseline db/baseline/,
    EF migration history (**/Migrations/**), or a path listed in tools/stale_terms.allow; or
  * the text around the match (120 characters either side, same line) marks it as history:
    "instead of", "not used", "replaced", "retired", "removed", "superseded", "former", "no longer", ...
The window, not the whole line, is checked, because generated files such as draw.io are one long line.

Usage: python3 tools/check_stale_terms.py     (exit 0 = clean, 1 = stale mentions found)
"""
import fnmatch
import os
import re
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
TERMS = re.compile(
    r"MediatR|DbMigrator|db/migrations|db/seed|SmartSpare\.Api\b|SmartSpare\.Domain\b"
    r"|single deployable|one deployable|App Service|Application Insights|Azure Service Bus")
HISTORY = re.compile(
    r"instead of|rather than|not us(e|ed|ing)|replac(ed|es|ing)|retired|remov(e|ed|al)|supersed|former|no longer|previously|history"
    r"|reject|originally",
    re.IGNORECASE)
WINDOW = 120
EXCLUDED = ["docs/adr/*", "db/baseline/*", "*/Migrations/*", "tools/check_stale_terms.py", "tools/stale_terms.allow"]


def allowed_paths():
    patterns = list(EXCLUDED)
    with open(os.path.join(ROOT, "tools", "stale_terms.allow"), encoding="utf-8") as fh:
        for line in fh:
            line = line.split("#", 1)[0].strip()
            if line:
                patterns.append(line)
    return patterns


def main():
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")    # excerpts contain non-ASCII text
    files = subprocess.run(["git", "ls-files"], cwd=ROOT, capture_output=True, text=True, check=True).stdout.splitlines()
    patterns = allowed_paths()
    findings = []
    for path in files:
        if any(fnmatch.fnmatch(path, p) for p in patterns):
            continue
        try:
            with open(os.path.join(ROOT, path), encoding="utf-8") as fh:
                lines = fh.read().splitlines()
        except (UnicodeDecodeError, FileNotFoundError, IsADirectoryError):
            continue                                   # binary files (images) and removed paths
        for number, line in enumerate(lines, 1):
            for match in TERMS.finditer(line):
                window = line[max(0, match.start() - WINDOW): match.end() + WINDOW]
                if not HISTORY.search(window):
                    excerpt = line[max(0, match.start() - 60): match.end() + 60].strip()
                    findings.append(f"{path}:{number}: '{match.group(0)}' ... {excerpt}")
    if findings:
        print("Stale architecture terms found (describe the current state, or mark the text as history):")
        print("\n".join("  " + f for f in findings))
        return 1
    print("No stale architecture terms.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
