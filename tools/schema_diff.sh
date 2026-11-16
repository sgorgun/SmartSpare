#!/usr/bin/env bash
# =============================================================================
# Schema diff: SQL-built (V001-V016 + R001) database vs. EF-built database
# (ADR 0011, MODULE_MAPPING section 6, step 1).
#
#   tools/schema_diff.sh [schema ...]          default: ref util audit
#
# Starts two throw-away postgres:17 containers:
#   A  bootstrap + db/scripts/migrate.sh (the V016 baseline, built inside the container)
#   B  bootstrap + all EF migrations through SmartSpare.MigrationRunner
# dumps the given schemas with pg_dump --schema-only, normalises the dumps (header and
# session lines removed, statements sorted, because pg_dump orders objects partly by OID)
# and diffs them. Lines matching tools/schema_diff.allow are documented, intended
# differences. Exit code 0 = no other difference, 1 = unexpected difference.
#
# Needs Docker, python3 and the .NET SDK. KEEP=1 keeps the containers for inspection.
# Output: artifacts/schema-diff/{sql,ef}.sql (normalised dumps) and diff.txt.
# =============================================================================
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SCHEMAS=("${@:-ref util audit}")
read -r -a SCHEMAS <<<"${SCHEMAS[*]}"
OUT="$ROOT/artifacts/schema-diff"
IMAGE="postgres:17"
A="ss-diff-sql-$$"
B="ss-diff-ef-$$"
PORT_B="${PORT_B:-55433}"
PY="$(command -v python3 || command -v python)"
mkdir -p "$OUT"

cleanup() { [[ "${KEEP:-0}" == 1 ]] || docker rm -f "$A" "$B" >/dev/null 2>&1 || true; }
trap cleanup EXIT

# Git Bash on Windows: keep container paths as they are, give Docker a Windows path for the mount.
export MSYS_NO_PATHCONV=1
DB_DIR="$ROOT/db"
RUNNER="$ROOT/src/Bootstrapper/SmartSpare.MigrationRunner"
if command -v cygpath >/dev/null; then DB_DIR="$(cygpath -w "$DB_DIR")"; RUNNER="$(cygpath -w "$RUNNER")"; fi

wait_ready() { for _ in $(seq 1 60); do docker exec "$1" pg_isready -U postgres -h localhost >/dev/null 2>&1 && return 0; sleep 1; done; return 1; }

# Throw-away credentials, generated per run and handed over through the environment (never stored).
secret() { "$PY" -c 'import secrets; print(secrets.token_hex(16))'; }
POSTGRES_PASSWORD="$(secret)"; SMARTSPARE_MIGRATOR_PASSWORD="$(secret)"; SMARTSPARE_API_PASSWORD="$(secret)"
SMARTSPARE_JOBS_PASSWORD="$(secret)"; SMARTSPARE_REPORTING_PASSWORD="$(secret)"
export POSTGRES_PASSWORD SMARTSPARE_MIGRATOR_PASSWORD SMARTSPARE_API_PASSWORD SMARTSPARE_JOBS_PASSWORD SMARTSPARE_REPORTING_PASSWORD

echo "== starting $A (SQL baseline) and $B (EF)"
docker run -d --name "$A" -e POSTGRES_PASSWORD -v "$DB_DIR:/db:ro" "$IMAGE" >/dev/null
docker run -d --name "$B" -e POSTGRES_PASSWORD -p "$PORT_B:5432" "$IMAGE" >/dev/null
wait_ready "$A"; wait_ready "$B"
sleep 2   # the image restarts the server once after initdb

echo "== building the V016 baseline in $A"
docker exec -e PGHOST=localhost -e POSTGRES_PASSWORD -e SMARTSPARE_MIGRATOR_PASSWORD -e SMARTSPARE_API_PASSWORD \
  -e SMARTSPARE_JOBS_PASSWORD -e SMARTSPARE_REPORTING_PASSWORD "$A" bash /db/scripts/reset_local.sh >/dev/null

echo "== applying EF migrations to $B"
Migrations__AdminConnectionString="Host=localhost;Port=$PORT_B;Username=postgres;Password=$POSTGRES_PASSWORD" \
Migrations__Passwords__Migrator="$SMARTSPARE_MIGRATOR_PASSWORD" Migrations__Passwords__Api="$SMARTSPARE_API_PASSWORD" \
Migrations__Passwords__Jobs="$SMARTSPARE_JOBS_PASSWORD" Migrations__Passwords__Reporting="$SMARTSPARE_REPORTING_PASSWORD" \
  dotnet run --project "$RUNNER" >/dev/null

dump() {
  local args=()
  for s in "${SCHEMAS[@]}"; do args+=(-n "$s"); done
  docker exec "$1" pg_dump -U postgres -d smartspare --schema-only "${args[@]}"
}

# Normalise: drop comments, SET/search_path lines, the random \restrict key and blank lines;
# split into statements (ending with ';' at end of line) and sort them.
normalise() {
  "$PY" -c '
import re, sys
statements, current = [], []
for line in sys.stdin.read().splitlines():
    if not line.strip() or line.startswith("--") or line.startswith("SET ") \
       or line.startswith("SELECT pg_catalog.set_config") or line.startswith("\\restrict") \
       or line.startswith("\\unrestrict"):
        continue
    current.append(line.rstrip())
    if line.rstrip().endswith(";"):
        statements.append("\n".join(current)); current = []
if current: statements.append("\n".join(current))
sys.stdout.write("\n".join(sorted(statements)) + "\n")
'
}

dump "$A" | normalise >"$OUT/sql.sql"
dump "$B" | normalise >"$OUT/ef.sql"

diff -u "$OUT/sql.sql" "$OUT/ef.sql" >"$OUT/diff.txt" || true

# Compare as sets of statements. tools/schema_diff.allow lists the documented differences:
#   -<regex>  a statement of the SQL baseline that is intentionally absent in the EF database
#   +<regex>  a statement that only the EF database has
# The regex is searched in the statement with its lines joined by single spaces.
echo "== schemas: ${SCHEMAS[*]}"
pypath() { if command -v cygpath >/dev/null; then cygpath -w "$1"; else printf %s "$1"; fi; }
"$PY" - "$(pypath "$OUT/sql.sql")" "$(pypath "$OUT/ef.sql")" "$(pypath "$ROOT/tools/schema_diff.allow")" <<'PYEOF'
import re, sys
def statements(path):
    text = open(path, encoding="utf-8").read()
    parts, cur = [], []
    for line in text.splitlines():
        cur.append(line)
        if line.endswith(";"):
            parts.append(" ".join(l.strip() for l in cur)); cur = []
    return set(parts)
sql, ef = statements(sys.argv[1]), statements(sys.argv[2])
allow = {"-": [], "+": []}
for line in open(sys.argv[3], encoding="utf-8"):
    line = line.rstrip("\n")
    if line[:1] in allow and line[1:].strip():
        allow[line[0]].append(re.compile(line[1:].strip()))
missing, extra = sorted(sql - ef), sorted(ef - sql)
def split(items, patterns):
    ok = [s for s in items if any(p.search(s) for p in patterns)]
    return ok, [s for s in items if s not in ok]
missing_ok, missing_bad = split(missing, allow["-"])
extra_ok, extra_bad = split(extra, allow["+"])
print(f"   statements: SQL {len(sql)}, EF {len(ef)}, identical {len(sql & ef)}")
print(f"   only in SQL: {len(missing)} ({len(missing_ok)} documented), only in EF: {len(extra)} ({len(extra_ok)} documented)")
for title, items in (("documented, only in SQL", missing_ok), ("documented, only in EF", extra_ok)):
    for s in items:
        print(f"   [{title}] {s[:150]}")
if missing_bad or extra_bad:
    print("== UNEXPECTED differences:")
    for s in missing_bad: print("   - " + s)
    for s in extra_bad: print("   + " + s)
    sys.exit(1)
print("== OK: no differences except the documented ones (tools/schema_diff.allow)")
PYEOF
echo "   full diff: $OUT/diff.txt"
