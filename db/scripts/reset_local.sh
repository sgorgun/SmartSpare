#!/usr/bin/env bash
# Local development only: drop and recreate the database, run bootstrap + migrations.
# Requires a superuser connection to a LOCAL server (docker compose) in PGADMIN_URL.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ADMIN="${PGADMIN_URL:-postgresql://postgres:postgres@localhost:5432/postgres}"
: "${MIGRATOR_PWD:=dev_migrator}" "${API_PWD:=dev_api}" "${JOBS_PWD:=dev_jobs}" "${REPORT_PWD:=dev_report}"
psql -X -q "$ADMIN" -c "DROP DATABASE IF EXISTS smartspare WITH (FORCE);"
psql -X -q "$ADMIN" -v migrator_pwd="$MIGRATOR_PWD" -v api_pwd="$API_PWD" -v jobs_pwd="$JOBS_PWD" \
     -v report_pwd="$REPORT_PWD" -f "$ROOT/bootstrap/000_bootstrap_cluster.sql"
PGHOST="${PGHOST:-localhost}" PGDATABASE=smartspare PGUSER=smartspare_migrator PGPASSWORD="$MIGRATOR_PWD" \
     "$ROOT/scripts/migrate.sh"
