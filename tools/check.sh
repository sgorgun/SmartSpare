#!/usr/bin/env bash
# =============================================================================
# The check set of SmartSpare: exactly what CI runs (.github/workflows/ci.yml calls this script
# step by step), so a green local run means a green CI run.
#
#   tools/check.sh                 # every step, in CI order (needs Docker for the last two)
#   tools/check.sh fast            # every step that needs no Docker (the CI "Build and test" job)
#   tools/check.sh <step> ...      # selected steps, e.g. tools/check.sh build test
#
# Steps: stale-terms restore build format check-model test integration database
# Everything runs in the Release configuration, like CI. Test results and coverage
# (XPlat Code Coverage, as in CI) go to TestResults/.
# =============================================================================
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

SOLUTION=SmartSpare.slnx
CONFIGURATION=Release
RESULTS=TestResults
PYTHON="$(command -v python3 || command -v python)"

step_stale_terms() { "$PYTHON" tools/check_stale_terms.py; }

step_restore() { dotnet restore "$SOLUTION" --locked-mode; }

step_build() { dotnet build "$SOLUTION" --no-restore --configuration "$CONFIGURATION" -warnaserror; }

step_format() { dotnet format "$SOLUTION" --no-restore --verify-no-changes; }

step_check_model() {
  dotnet run --project src/Bootstrapper/SmartSpare.MigrationRunner --no-build --configuration "$CONFIGURATION" -- check-model
}

step_test() {
  dotnet test "$SOLUTION" --no-build --configuration "$CONFIGURATION" \
    --filter "Category!=Integration" \
    --logger trx --results-directory "$RESULTS" \
    --collect "XPlat Code Coverage"
}

step_integration() {
  dotnet test "$SOLUTION" --no-build --configuration "$CONFIGURATION" \
    --filter "Category=Integration" \
    --logger trx --results-directory "$RESULTS"
}

step_database() { RUNNER_ARGS="--no-build --configuration $CONFIGURATION" tools/verify_database.sh; }

FAST=(stale-terms restore build format check-model test)
ALL=("${FAST[@]}" integration database)

if [ $# -eq 0 ]; then
  STEPS=("${ALL[@]}")
elif [ "$1" = "fast" ]; then
  STEPS=("${FAST[@]}")
else
  STEPS=("$@")
fi

for step in "${STEPS[@]}"; do
  fn="step_${step//-/_}"
  if ! declare -F "$fn" >/dev/null; then
    echo "unknown step: $step (steps: ${ALL[*]}, or fast)" >&2
    exit 2
  fi
  echo "== $step"
  "$fn"
done
echo "== OK: ${STEPS[*]}"
