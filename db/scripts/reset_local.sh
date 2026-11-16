#!/usr/bin/env bash
# Local development only: drop and recreate the database, run bootstrap + migrations.
# Connects as the superuser of a LOCAL server (docker compose). Credentials come from
# .env (see .env.example); PGADMIN_URL may point to another local server instead.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "$ROOT/scripts/env.sh"
require_env SMARTSPARE_MIGRATOR_PASSWORD SMARTSPARE_API_PASSWORD SMARTSPARE_JOBS_PASSWORD SMARTSPARE_REPORTING_PASSWORD

if [[ -n "${PGADMIN_URL:-}" ]]; then
  admin_psql() { psql -X -q "$PGADMIN_URL" "$@"; }
else
  require_env POSTGRES_PASSWORD
  # The password goes through the environment, not the command line (process list).
  admin_psql() { PGPASSWORD="$POSTGRES_PASSWORD" psql -X -q -h "${PGHOST:-localhost}" -p "${PGPORT:-5432}" -U postgres -d postgres "$@"; }
fi

admin_psql -c "DROP DATABASE IF EXISTS smartspare WITH (FORCE);"
admin_psql -v migrator_pwd="$SMARTSPARE_MIGRATOR_PASSWORD" -v api_pwd="$SMARTSPARE_API_PASSWORD" \
           -v jobs_pwd="$SMARTSPARE_JOBS_PASSWORD" -v report_pwd="$SMARTSPARE_REPORTING_PASSWORD" \
           -f "$ROOT/bootstrap/000_bootstrap_cluster.sql"
PGHOST="${PGHOST:-localhost}" PGPORT="${PGPORT:-5432}" PGDATABASE=smartspare PGUSER=smartspare_migrator \
  PGPASSWORD="$SMARTSPARE_MIGRATOR_PASSWORD" "$ROOT/scripts/migrate.sh"
