#!/usr/bin/env bash
# =============================================================================
# Verification of the EF-built database (ADR 0011, MODULE_MAPPING section 6, steps 2-4).
#
#   tools/verify_database.sh [parts]        parts = SKUs of the demo dataset, default 2000
#
# Starts a throw-away postgres:17 container and
#   1. builds the database with SmartSpare.MigrationRunner (bootstrap + every EF context);
#   2. loads the demo dataset db/testdata/T001 (psql in the container) and gives the demo users a
#      throw-away password with `MigrationRunner set-demo-passwords` (from the host, it needs .NET);
#   3. runs the SQL suite db/tests/run_tests.sh and requires all 117 assertions to pass;
#   4. runs db/tests/concurrency_test.sh (row lock wait, SS200, no negative stock);
#   5. requires mart.v_data_quality to report 0 failed rows in every check and
#      util.v_privilege_findings to return no rows.
# psql runs inside the container (PostgreSQL 17 client, nothing to install on the host).
#
# Needs Docker, python3 and the .NET SDK. Used locally and by the CI integration job.
#   KEEP=1                       keep the container (prints how to connect)
#   RUNNER_ARGS="--no-build -c Release"   extra arguments for `dotnet run` (CI builds first)
# Exit 0 = everything green.
# =============================================================================
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PARTS="${1:-2000}"
EXPECTED_ASSERTIONS=117
IMAGE="postgres:17"
NAME="ss-verify-$$"
PORT="${PORT:-55434}"
PY="$(command -v python3 || command -v python)"
read -r -a RUNNER_ARGS <<<"${RUNNER_ARGS:-}"

cleanup() {
  if [[ "${KEEP:-0}" == 1 ]]; then
    echo "== kept container $NAME on port $PORT (docker rm -f $NAME to remove)"
  else
    docker rm -f "$NAME" >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT

# Git Bash on Windows: keep container paths as they are, give Docker a Windows path for the mount.
export MSYS_NO_PATHCONV=1
DB_DIR="$ROOT/db"
RUNNER="$ROOT/src/Bootstrapper/SmartSpare.MigrationRunner"
if command -v cygpath >/dev/null; then DB_DIR="$(cygpath -w "$DB_DIR")"; RUNNER="$(cygpath -w "$RUNNER")"; fi

# Throw-away credentials, generated per run and handed over through the environment (never stored).
secret() { "$PY" -c 'import secrets; print(secrets.token_hex(16))'; }
POSTGRES_PASSWORD="$(secret)"; SMARTSPARE_MIGRATOR_PASSWORD="$(secret)"; SMARTSPARE_API_PASSWORD="$(secret)"
SMARTSPARE_JOBS_PASSWORD="$(secret)"; SMARTSPARE_REPORTING_PASSWORD="$(secret)"
export POSTGRES_PASSWORD SMARTSPARE_MIGRATOR_PASSWORD SMARTSPARE_API_PASSWORD SMARTSPARE_JOBS_PASSWORD SMARTSPARE_REPORTING_PASSWORD

# in_db SCRIPT [ARGS]: run a db/ script inside the container (it reads the credentials via db/scripts/env.sh).
in_db() {
  docker exec -e PGHOST=localhost -e POSTGRES_PASSWORD -e SMARTSPARE_MIGRATOR_PASSWORD "$NAME" bash "$@"
}
psql_scalar() {
  docker exec -e PGPASSWORD="$POSTGRES_PASSWORD" "$NAME" \
    psql -X -q -v ON_ERROR_STOP=1 -tA -h localhost -U postgres -d smartspare -c "$1"
}

echo "== starting $NAME ($IMAGE)"
docker run -d --name "$NAME" -e POSTGRES_PASSWORD -p "$PORT:5432" -v "$DB_DIR:/db:ro" "$IMAGE" \
  -c password_encryption=scram-sha-256 -c shared_preload_libraries=pg_stat_statements >/dev/null
for _ in $(seq 1 60); do docker exec "$NAME" pg_isready -U postgres -h localhost >/dev/null 2>&1 && break; sleep 1; done
sleep 2   # the image restarts the server once after initdb
for _ in $(seq 1 30); do docker exec "$NAME" pg_isready -U postgres -h localhost >/dev/null 2>&1 && break; sleep 1; done

echo "== 1. building the database with the MigrationRunner"
Migrations__AdminConnectionString="Host=localhost;Port=$PORT;Username=postgres;Password=$POSTGRES_PASSWORD" \
Migrations__Passwords__Migrator="$SMARTSPARE_MIGRATOR_PASSWORD" Migrations__Passwords__Api="$SMARTSPARE_API_PASSWORD" \
Migrations__Passwords__Jobs="$SMARTSPARE_JOBS_PASSWORD" Migrations__Passwords__Reporting="$SMARTSPARE_REPORTING_PASSWORD" \
  dotnet run --project "$RUNNER" "${RUNNER_ARGS[@]}" >/dev/null

echo "== 2. loading the demo dataset T001 ($PARTS SKUs)"
start=$(date +%s)
PGPASSWORD="$SMARTSPARE_MIGRATOR_PASSWORD" docker exec -e PGPASSWORD "$NAME"   psql -X -q -v ON_ERROR_STOP=1 -h localhost -U smartspare_migrator -d smartspare -v parts="$PARTS" -f /db/testdata/T001__demo_dataset.sql
SMARTSPARE_DEMO_USER_PASSWORD="$(secret)" Migrations__AdminConnectionString="Host=localhost;Port=$PORT;Username=postgres;Password=$POSTGRES_PASSWORD" Migrations__Passwords__Migrator="$SMARTSPARE_MIGRATOR_PASSWORD"   dotnet run --project "$RUNNER" "${RUNNER_ARGS[@]}" -- set-demo-passwords >/dev/null
echo "   loaded in $(($(date +%s) - start)) s"

echo "== 3. SQL test suite"
suite="$(in_db /db/tests/run_tests.sh 2>&1)" || { echo "$suite"; echo "== FAILED: SQL test suite"; exit 1; }
echo "$suite"
passed="$(grep -oE 'All tests passed: [0-9]+' <<<"$suite" | grep -oE '[0-9]+$' || echo 0)"
if [[ "$passed" != "$EXPECTED_ASSERTIONS" ]]; then
  echo "== FAILED: expected $EXPECTED_ASSERTIONS assertions, $passed passed"
  exit 1
fi

echo "== 4. concurrency test"
concurrency="$(in_db /db/tests/concurrency_test.sh 2>&1)" || { echo "$concurrency"; echo "== FAILED: concurrency test"; exit 1; }
echo "$concurrency" | sed 's/^/   /'
grep -q "PASS" <<<"$concurrency" || { echo "== FAILED: concurrency test reported no PASS"; exit 1; }

echo "== 5. data quality and privileges"
quality="$(psql_scalar "SELECT check_name || ': ' || failed_rows FROM mart.v_data_quality ORDER BY check_name")"
echo "$quality" | sed 's/^/   /'
failed="$(psql_scalar "SELECT coalesce(sum(failed_rows), 0) FROM mart.v_data_quality")"
[[ "$failed" == 0 ]] || { echo "== FAILED: mart.v_data_quality reports $failed failed rows"; exit 1; }
findings="$(psql_scalar "SELECT count(*) FROM util.v_privilege_findings")"
if [[ "$findings" != 0 ]]; then
  psql_scalar "SELECT * FROM util.v_privilege_findings" | sed 's/^/   /'
  echo "== FAILED: util.v_privilege_findings returned $findings row(s)"
  exit 1
fi
echo "   util.v_privilege_findings: no rows"

echo "== OK: $passed assertions, concurrency test, T001, data quality and privileges green on the EF-built database"
