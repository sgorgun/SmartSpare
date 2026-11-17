#!/usr/bin/env bash
# Local development only: drop the local database and rebuild it with SmartSpare.MigrationRunner
# (cluster bootstrap + the EF migrations of every module), the same path as the AppHost and CI.
# For the docker compose server (docker-compose.yml); the AppHost needs no reset script, it builds
# its own database. Connects as the LOCAL superuser; credentials come from .env (see .env.example).
# Needs psql and the .NET SDK. Afterwards load demo data with db/scripts/load_testdata.sh.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "$ROOT/scripts/env.sh"
require_env POSTGRES_PASSWORD SMARTSPARE_MIGRATOR_PASSWORD SMARTSPARE_API_PASSWORD SMARTSPARE_JOBS_PASSWORD SMARTSPARE_REPORTING_PASSWORD

host="${PGHOST:-localhost}"
port="${PGPORT:-5432}"

# The password goes through the environment, not the command line (process list).
PGPASSWORD="$POSTGRES_PASSWORD" psql -X -q -h "$host" -p "$port" -U postgres -d postgres \
  -c "DROP DATABASE IF EXISTS smartspare WITH (FORCE);"

# The runner bootstraps roles and database (the database is missing now), then applies every context.
Migrations__AdminConnectionString="Host=$host;Port=$port;Username=postgres;Password=$POSTGRES_PASSWORD" \
Migrations__Passwords__Migrator="$SMARTSPARE_MIGRATOR_PASSWORD" Migrations__Passwords__Api="$SMARTSPARE_API_PASSWORD" \
Migrations__Passwords__Jobs="$SMARTSPARE_JOBS_PASSWORD" Migrations__Passwords__Reporting="$SMARTSPARE_REPORTING_PASSWORD" \
  dotnet run --project "$ROOT/../src/Bootstrapper/SmartSpare.MigrationRunner"
