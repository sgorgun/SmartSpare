#!/usr/bin/env bash
# Loads the synthetic demo / test dataset db/testdata/T001 into the local database and gives the demo users
# the password SMARTSPARE_DEMO_USER_PASSWORD (any value you choose in .env; issue #7).
#   db/scripts/load_testdata.sh            2,000 SKUs (~30 s)
#   db/scripts/load_testdata.sh 20000      BA scale for load tests
# Connects as smartspare_migrator; credentials come from .env (see .env.example).
# Needs psql and the .NET SDK. T001 stores only random placeholder hashes (no password in Git);
# `MigrationRunner set-demo-passwords` then hashes the chosen password with the application's settings.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "$ROOT/scripts/env.sh"
connect_as_migrator
require_env SMARTSPARE_DEMO_USER_PASSWORD
psql -X -q -v ON_ERROR_STOP=1 -v parts="${1:-2000}" -f "$ROOT/testdata/T001__demo_dataset.sql"

# The password goes through the environment, not the command line (process list).
Migrations__ConnectionString="Host=$PGHOST;Port=${PGPORT:-5432};Database=$PGDATABASE;Username=$PGUSER;Password=$PGPASSWORD" \
  dotnet run --project "$ROOT/../src/Bootstrapper/SmartSpare.MigrationRunner" -- set-demo-passwords
