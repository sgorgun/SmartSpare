#!/usr/bin/env bash
# Loads the synthetic demo / test dataset db/testdata/T001 into the local database.
#   db/scripts/load_testdata.sh            2,000 SKUs (~30 s)
#   db/scripts/load_testdata.sh 20000      BA scale for load tests
# Connects as smartspare_migrator; credentials come from .env (see .env.example).
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "$ROOT/scripts/env.sh"
connect_as_migrator
psql -X -q -v ON_ERROR_STOP=1 -v parts="${1:-2000}" -f "$ROOT/testdata/T001__demo_dataset.sql"
