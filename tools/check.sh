#!/usr/bin/env bash
# =============================================================================
# The check set of SmartSpare: exactly what CI runs (.github/workflows/ci.yml calls this script
# step by step), so a green local run means a green CI run.
#
#   tools/check.sh                 # every step, in CI order (needs Docker for the last two)
#   tools/check.sh fast            # every step that needs no Docker (the CI "Build and test" job)
#   tools/check.sh <step> ...      # selected steps, e.g. tools/check.sh build test
#   tools/check.sh e2e             # optional: the Playwright smoke test (Docker and Chromium; CI job "End-to-end")
#
# Steps: stale-terms restore build format check-model test integration database; optional: e2e
# (it needs a build first, e.g. tools/check.sh restore build e2e)
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
    --filter "Category!=Integration&Category!=E2E" \
    --logger trx --results-directory "$RESULTS" \
    --collect "XPlat Code Coverage"
}

step_integration() {
  dotnet test "$SOLUTION" --no-build --configuration "$CONFIGURATION" \
    --filter "Category=Integration" \
    --logger trx --results-directory "$RESULTS"
}

step_database() { RUNNER_ARGS="--no-build --configuration $CONFIGURATION" tools/verify_database.sh; }

# Optional, not part of the default run: it needs a browser. Installs Chromium through the Playwright script the build
# produced (CI sets PLAYWRIGHT_INSTALL_ARGS=--with-deps for the system libraries), then runs the smoke test.
# Screenshots of every step go to TestResults/e2e, plus a Playwright trace when the test fails.
step_e2e() {
  # shellcheck disable=SC2086
  pwsh "tests/SmartSpare.E2E/bin/$CONFIGURATION/net10.0/playwright.ps1" install ${PLAYWRIGHT_INSTALL_ARGS:-} chromium
  dotnet test tests/SmartSpare.E2E --no-build --configuration "$CONFIGURATION" \
    --filter "Category=E2E" \
    --logger trx --results-directory "$RESULTS"
}

FAST=(stale-terms restore build format check-model test)
ALL=("${FAST[@]}" integration database)
OPTIONAL=(e2e)

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
    echo "unknown step: $step (steps: ${ALL[*]} ${OPTIONAL[*]}, or fast)" >&2
    exit 2
  fi
  echo "== $step"
  "$fn"
done
echo "== OK: ${STEPS[*]}"
