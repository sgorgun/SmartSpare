#!/usr/bin/env bash
# Upgrade test (STAGE_7_PLAN 3.2, product owner 28 November 2026): builds the stage 6 database (release v0.6.0) with
# that release's own MigrationRunner and demo dataset, records a fingerprint of the data that later migrations must
# keep (brand ids and codes and every row that points at a brand, users, parts, machines, stock), then applies the
# migrations of the working tree and compares. Any difference fails.
#
#   tools/upgrade_test.sh            # from v0.6.0
#   FROM=v0.5.0 tools/upgrade_test.sh
#
# Needs Docker and git. The release is checked out in a temporary worktree; CI's shallow clone fetches the tag.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
FROM="${FROM:-v0.6.0}"
PARTS="${PARTS:-300}"
IMAGE="postgres:17"
NAME="ss-upgrade-$$"
PORT="${PORT:-55436}"
PY="$(command -v python3 || command -v python)"
read -r -a RUNNER_ARGS <<<"${RUNNER_ARGS:-}"
OLD="$(mktemp -d)/smartspare-$FROM"
OUT="$ROOT/artifacts/upgrade"

cleanup() {
  docker rm -f "$NAME" >/dev/null 2>&1 || true
  (unset MSYS_NO_PATHCONV; git -C "$ROOT" worktree remove --force "$OLD" >/dev/null 2>&1) || true
}
trap cleanup EXIT

secret() { "$PY" -c 'import secrets; print(secrets.token_hex(16))'; }
POSTGRES_PASSWORD="$(secret)"; MIGRATOR="$(secret)"; API="$(secret)"; JOBS="$(secret)"; REPORTING="$(secret)"
export POSTGRES_PASSWORD

if ! git -C "$ROOT" rev-parse -q --verify "refs/tags/$FROM" >/dev/null; then
  echo "== fetching $FROM"
  git -C "$ROOT" fetch -q --depth 1 origin "refs/tags/$FROM:refs/tags/$FROM"
fi
echo "== checking out $FROM"
git -C "$ROOT" worktree add -q --detach "$OLD" "$FROM"

OLD_DB="$OLD/db"; OLD_RUNNER="$OLD/src/Bootstrapper/SmartSpare.MigrationRunner"; RUNNER="$ROOT/src/Bootstrapper/SmartSpare.MigrationRunner"
if command -v cygpath >/dev/null; then OLD_DB="$(cygpath -w "$OLD_DB")"; OLD_RUNNER="$(cygpath -w "$OLD_RUNNER")"; RUNNER="$(cygpath -w "$RUNNER")"; fi

psql_q() {
  docker exec -e PGPASSWORD="$POSTGRES_PASSWORD" "$NAME" psql -X -q -v ON_ERROR_STOP=1 -tA -h localhost -U postgres -d smartspare -c "$1"
}

run_runner() { # project, extra args...
  local project="$1"; shift
  Migrations__AdminConnectionString="Host=localhost;Port=$PORT;Username=postgres;Password=$POSTGRES_PASSWORD" \
  Migrations__Passwords__Migrator="$MIGRATOR" Migrations__Passwords__Api="$API" \
  Migrations__Passwords__Jobs="$JOBS" Migrations__Passwords__Reporting="$REPORTING" \
    dotnet run --project "$project" "$@" >"$OUT/runner.log" 2>&1 || { tail -40 "$OUT/runner.log"; echo "== FAILED: the MigrationRunner of $project"; exit 1; }
}

# The brand table, wherever the schema of the database has it (ref.brand until PR 7h, catalog.brand after).
brand_table() { psql_q "SELECT coalesce(to_regclass('catalog.brand'), to_regclass('ref.brand'))::text"; }

fingerprint() {
  local brand; brand="$(brand_table)"
  {
    echo "-- brands ($brand)"
    psql_q "SELECT id || ' ' || code || ' ' || name FROM $brand ORDER BY id"
    echo "-- parts per brand"
    psql_q "SELECT brand_id || ' ' || count(*) || ' ' || md5(string_agg(oem_part_number, ',' ORDER BY id)) FROM catalog.part GROUP BY brand_id ORDER BY brand_id"
    echo "-- machine models per brand"
    psql_q "SELECT brand_id || ' ' || count(*) FROM fleet.machine_model GROUP BY brand_id ORDER BY brand_id"
    echo "-- users"
    psql_q "SELECT count(*) || ' ' || md5(string_agg(user_name, ',' ORDER BY id)) FROM iam.app_user"
    echo "-- machines, stock, movements"
    psql_q "SELECT (SELECT count(*) FROM fleet.machine) || ' ' || (SELECT sum(on_hand_qty) FROM inventory.stock_balance) || ' ' || (SELECT count(*) FROM inventory.stock_movement)"
  }
}

mkdir -p "$OUT"
export MSYS_NO_PATHCONV=1
echo "== starting $NAME ($IMAGE)"
docker run -d --name "$NAME" -e POSTGRES_PASSWORD -p "$PORT:5432" -v "$OLD_DB:/db:ro" "$IMAGE" \
  -c password_encryption=scram-sha-256 -c shared_preload_libraries=pg_stat_statements >/dev/null
for _ in $(seq 1 60); do docker exec "$NAME" pg_isready -U postgres -h localhost >/dev/null 2>&1 && break; sleep 1; done
sleep 2   # the image restarts the server once after initdb
for _ in $(seq 1 30); do docker exec "$NAME" pg_isready -U postgres -h localhost >/dev/null 2>&1 && break; sleep 1; done

echo "== 1. building the $FROM database with its own MigrationRunner"
run_runner "$OLD_RUNNER"
echo "== 2. loading the $FROM demo dataset ($PARTS SKUs)"
docker exec -e PGPASSWORD="$MIGRATOR" "$NAME" \
  psql -X -q -v ON_ERROR_STOP=1 -h localhost -U smartspare_migrator -d smartspare -v parts="$PARTS" -f /db/testdata/T001__demo_dataset.sql
fingerprint >"$OUT/before.txt"

echo "== 3. applying the migrations of the working tree"
run_runner "$RUNNER" "${RUNNER_ARGS[@]}"
fingerprint >"$OUT/after.txt"

# Only the brand table's location may differ.
if diff <(grep -v '^-- brands (' "$OUT/before.txt") <(grep -v '^-- brands (' "$OUT/after.txt") >"$OUT/diff.txt"; then
  echo "   brands: $(grep -c . <(sed -n '/^-- brands/,/^-- parts/p' "$OUT/after.txt" | grep -v '^--')) kept with their ids, in $(brand_table)"
  echo "== OK: the $FROM database with demo data upgrades without losing or renumbering data"
else
  cat "$OUT/diff.txt"
  echo "== FAILED: the upgrade from $FROM changed the data (artifacts/upgrade)"
  exit 1
fi
