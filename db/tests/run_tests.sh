#!/usr/bin/env bash
# Runs all database tests (each file in its own rolled-back transaction).
# Needs a connection that may SET ROLE to the application roles (local superuser
# or the server admin). Exit code != 0 on the first failing assertion.
set -euo pipefail
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
pass=0
for f in "$DIR"/T*.sql; do
  echo "== $(basename "$f")"
  out="$(psql -X -q -v ON_ERROR_STOP=1 -f "$f" 2>&1)" || { echo "$out" | grep -E "PASS|FAIL|ERROR" ; exit 1; }
  echo "$out" | grep -oE "PASS .*" | sed 's/^/   /'
  pass=$((pass + $(echo "$out" | grep -c "PASS")))
done
echo "All tests passed: $pass assertions."
