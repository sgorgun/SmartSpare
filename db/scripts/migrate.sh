#!/usr/bin/env bash
# =============================================================================
# SmartSpare — SQL migration runner (local development / CI).
# Builds the database from the SQL scripts until stage 2 ports the schema to
# EF Core migrations (ADR 0004), which then replace this runner. Semantics:
#   * versioned scripts  db/migrations/V<NNN>__<name>.sql  run once, in order;
#   * repeatable scripts db/seed/R<NNN>__<name>.sql        re-run when changed;
#   * every script runs in ONE transaction together with its journal entry;
#   * the journal meta.schema_migration stores a SHA-256 checksum, and an
#     already applied versioned script that was edited afterwards is an ERROR
#     (history must never be rewritten — add a new V-script instead);
#   * scripts run as the NOLOGIN owner role (SET LOCAL ROLE smartspare_owner),
#     the login role only needs membership in it — no superuser needed.
#   * a transaction-level advisory lock prevents two runners at the same time.
#
# Usage: PGHOST=... PGPORT=... PGDATABASE=smartspare PGUSER=smartspare_migrator \
#        PGPASSWORD=... db/scripts/migrate.sh
# =============================================================================
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OWNER_ROLE="${OWNER_ROLE:-smartspare_owner}"
export PGOPTIONS="${PGOPTIONS:-} -c client_min_messages=warning"
PSQL=(psql -X -q -v ON_ERROR_STOP=1 --no-psqlrc)

sha256() { sha256sum "$1" | cut -d' ' -f1; }

"${PSQL[@]}" <<SQL
SET ROLE ${OWNER_ROLE};
CREATE SCHEMA IF NOT EXISTS meta;
CREATE TABLE IF NOT EXISTS meta.schema_migration (
    script        varchar(200) PRIMARY KEY,
    kind          char(1)      NOT NULL CHECK (kind IN ('V', 'R')),
    version       integer      NOT NULL,
    description   varchar(200) NOT NULL,
    checksum      char(64)     NOT NULL CHECK (checksum ~ '^[0-9a-f]{64}\$'),
    applied_by    name         NOT NULL DEFAULT session_user,
    applied_at    timestamptz  NOT NULL DEFAULT now(),
    execution_ms  integer      NOT NULL CHECK (execution_ms >= 0)
);
COMMENT ON TABLE meta.schema_migration IS 'Schema version history: one row per applied migration script (checksum = SHA-256 of the file).';
SQL

applied=0; skipped=0
for file in "$ROOT"/migrations/V*.sql "$ROOT"/seed/R*.sql; do
  [ -e "$file" ] || continue
  name="$(basename "$file")"
  kind="${name:0:1}"
  version="$(echo "$name" | sed -E 's/^[VR]0*([0-9]+)__.*/\1/')"
  description="$(echo "$name" | sed -E 's/^[VR][0-9]+__(.*)\.sql$/\1/' | tr '_' ' ')"
  checksum="$(sha256 "$file")"
  existing="$("${PSQL[@]}" -tA -c "SELECT checksum FROM meta.schema_migration WHERE script = '$name'")"

  if [ -n "$existing" ]; then
    if [ "$existing" = "$checksum" ]; then skipped=$((skipped+1)); continue; fi
    if [ "$kind" = "V" ]; then
      echo "ERROR: $name was modified after it had been applied (checksum drift)." >&2
      echo "       Applied: $existing  Current: $checksum" >&2
      echo "       Never edit an applied migration — create a new V-script." >&2
      exit 2
    fi
  fi

  start=$(date +%s%3N)
  "${PSQL[@]}" -o /dev/null <<SQL
BEGIN;
SELECT pg_advisory_xact_lock(hashtext('smartspare.migrations'));
SET LOCAL ROLE ${OWNER_ROLE};
SET LOCAL search_path = public;
\i '$file'
INSERT INTO meta.schema_migration (script, kind, version, description, checksum, execution_ms)
VALUES ('$name', '$kind', $version, '$description', '$checksum', 0)
ON CONFLICT (script) DO UPDATE
   SET checksum = EXCLUDED.checksum, applied_at = now(), applied_by = session_user;
COMMIT;
SQL
  ms=$(( $(date +%s%3N) - start ))
  "${PSQL[@]}" -o /dev/null -c "SET ROLE ${OWNER_ROLE}; UPDATE meta.schema_migration SET execution_ms = $ms WHERE script = '$name';"
  echo "applied  $name  (${ms} ms)"
  applied=$((applied+1))
done
echo "Done: $applied applied, $skipped already up to date."
