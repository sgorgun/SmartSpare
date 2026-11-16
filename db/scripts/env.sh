# shellcheck shell=bash
# =============================================================================
# Local credentials for the db/ scripts. Sourced, not executed.
#
# Reads <repository>/.env (gitignored; copy .env.example and choose your own
# values). Variables already set in the environment win, so CI or a one-off
# `PGPASSWORD=... script` override the file. No password is ever stored in Git.
# =============================================================================

SMARTSPARE_REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SMARTSPARE_ENV_FILE="${SMARTSPARE_ENV_FILE:-$SMARTSPARE_REPO_ROOT/.env}"

if [[ -f "$SMARTSPARE_ENV_FILE" ]]; then
  while IFS= read -r line || [[ -n "$line" ]]; do
    line="${line%$'\r'}"
    [[ -z "${line//[[:space:]]/}" || "$line" =~ ^[[:space:]]*# ]] && continue
    [[ "$line" == *=* ]] || continue
    key="${line%%=*}"; key="${key//[[:space:]]/}"
    value="${line#*=}"
    # Strip one pair of surrounding quotes, as docker compose does.
    if [[ "$value" =~ ^\"(.*)\"$ || "$value" =~ ^\'(.*)\'$ ]]; then value="${BASH_REMATCH[1]}"; fi
    [[ "$key" =~ ^[A-Za-z_][A-Za-z0-9_]*$ ]] || continue
    [[ -n "${!key+x}" ]] || export "$key=$value"
  done <"$SMARTSPARE_ENV_FILE"
fi

# require_env NAME... : fail with a pointer to .env.example when a variable is missing.
require_env() {
  local name missing=()
  for name in "$@"; do [[ -n "${!name:-}" ]] || missing+=("$name"); done
  if ((${#missing[@]})); then
    echo "Missing ${missing[*]}: copy .env.example to .env and set the values (see README)." >&2
    exit 1
  fi
}

# connect_as_superuser / connect_as_migrator : default libpq variables for psql against the
# local smartspare database. PG* variables already set in the environment are kept.
connect_as_superuser() {
  export PGHOST="${PGHOST:-localhost}" PGDATABASE="${PGDATABASE:-smartspare}" PGUSER="${PGUSER:-postgres}"
  if [[ -z "${PGPASSWORD:-}" ]]; then require_env POSTGRES_PASSWORD; export PGPASSWORD="$POSTGRES_PASSWORD"; fi
}

connect_as_migrator() {
  export PGHOST="${PGHOST:-localhost}" PGDATABASE="${PGDATABASE:-smartspare}" PGUSER="${PGUSER:-smartspare_migrator}"
  if [[ -z "${PGPASSWORD:-}" ]]; then require_env SMARTSPARE_MIGRATOR_PASSWORD; export PGPASSWORD="$SMARTSPARE_MIGRATOR_PASSWORD"; fi
}
