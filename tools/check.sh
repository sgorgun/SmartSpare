#!/usr/bin/env bash
# =============================================================================
# The check set of SmartSpare: exactly what CI runs (.github/workflows/ci.yml calls this script
# step by step), so a green local run means a green CI run.
#
#   tools/check.sh                 # every step, in CI order (needs Docker for the last two)
#   tools/check.sh fast            # every step that needs no Docker (the CI "Build and test" job)
#   tools/check.sh <step> ...      # selected steps, e.g. tools/check.sh build test
#   tools/check.sh e2e             # optional: the Playwright smoke test (Docker and Chromium; CI job "End-to-end")
#   tools/check.sh topology        # optional: gateway + services + PostgreSQL + RabbitMQ (Docker; CI job "Topology test")
#
# Steps: stale-terms deploy-paths restore vulnerabilities build format duplication check-model test coverage coverage-all integration database
# coverage-combined; optional: e2e topology (they need a build first, e.g. tools/check.sh restore build e2e topology)
# Everything runs in the Release configuration, like CI. Test results and coverage (XPlat Code Coverage, as in CI) go
# to TestResults/: the unit tests' to TestResults/<run>/, the integration tests' to TestResults/integration/<run>/.
#
# Memory: CHECK_SEQUENTIAL=1 runs the test projects one at a time and the test collections of each project one at a
# time, so at most one Testcontainers PostgreSQL runs at once. It is the default locally; CI (CI=true, set by GitHub
# Actions) keeps the parallel run unless CHECK_SEQUENTIAL says otherwise. The same tests run either way.
# =============================================================================
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

SOLUTION=SmartSpare.slnx
CONFIGURATION=Release
RESULTS=TestResults
PYTHON="$(command -v python3 || command -v python)"

if [ -z "${CHECK_SEQUENTIAL:-}" ]; then
  if [ "${CI:-}" = "true" ]; then CHECK_SEQUENTIAL=0; else CHECK_SEQUENTIAL=1; fi
fi

# The test projects, in a stable order (sequential mode only).
test_projects() {
  find tests -name '*.csproj' -not -path '*/bin/*' -not -path '*/obj/*' | sort
}

# run_tests FILTER [dotnet test options...]: the whole solution at once, or (CHECK_SEQUENTIAL=1) one project per
# invocation with xUnit collections in series. Each collection fixture disposes its container before the next one
# starts; Ryuk (Testcontainers' reaper, enabled by default) removes anything a crashed run leaves behind.
run_tests() {
  local filter="$1"
  shift
  if [ "$CHECK_SEQUENTIAL" = "1" ]; then
    local project
    for project in $(test_projects); do
      echo "-- $project"
      dotnet test "$project" --no-build --configuration "$CONFIGURATION" --filter "$filter" "$@" \
        -- xUnit.ParallelizeTestCollections=false
    done
  else
    dotnet test "$SOLUTION" --no-build --configuration "$CONFIGURATION" --filter "$filter" "$@"
  fi
}

step_stale_terms() { "$PYTHON" tools/check_stale_terms.py; }

# The deploy workflows' path filters are generated from the project references (STAGE_4_PLAN 8.3); fails if they are stale.
step_deploy_paths() { "$PYTHON" tools/generate_deploy_workflows.py --check; }

step_restore() { dotnet restore "$SOLUTION" --locked-mode; }

step_build() { dotnet build "$SOLUTION" --no-restore --configuration "$CONFIGURATION" -warnaserror; }

step_format() { dotnet format "$SOLUTION" --no-restore --verify-no-changes; }

# Duplicated code (#61, STAGE_5_PLAN section 10): jscpd over src/ (C# and Razor; migrations and generated files
# excluded, see .jscpd.json) fails above 3 % duplicated lines. The SonarQube C# rules run in the build itself.
JSCPD_VERSION=5.4.3
step_duplication() { npx --yes "jscpd@$JSCPD_VERSION" --config .jscpd.json --silent; }

step_check_model() {
  dotnet run --project src/Bootstrapper/SmartSpare.MigrationRunner --no-build --configuration "$CONFIGURATION" -- check-model
}

step_test() {
  # Coverage of this run only: coverlet writes one folder per project and run, so drop earlier files first.
  if [ -d "$RESULTS" ]; then
    find "$RESULTS" -name 'coverage.cobertura.xml' -delete
  fi
  run_tests "Category!=Integration&Category!=E2E&Category!=Topology" \
    --logger trx --results-directory "$RESULTS" \
    --collect "XPlat Code Coverage"
}

# Coverage figures (#63, defined by layer on 27 November 2026, BACKEND_REQUIREMENTS):
# - business logic: each module's Domain/ and Features/ (aggregates, value objects, validators, business rules,
#   orchestration of use cases), the SharedKernel, the handler pipeline and the ApiContracts (DTO validators, policies).
#   Not Infrastructure/ (EF configurations, migrations, read models and data-access query handlers, the SQL behind
#   commands), not generated code. An architecture test keeps Domain and Features in this definition and raw SQL out of
#   Features (CoverageDefinitionTests).
# - all code: every SmartSpare assembly except tests, migrations and generated code.
BUSINESS_ASSEMBLIES="+SmartSpare.Modules.*;+SmartSpare.SharedKernel;+SmartSpare.Infrastructure;+SmartSpare.ApiContracts;-*.Contracts"
BUSINESS_FILES="+*Domain*;+*Features*;+*SmartSpare.SharedKernel*;+*Messaging*Decorators*;+*Messaging*HandlerRegistration.cs;+*SmartSpare.ApiContracts*;-*Migrations*;-*obj*;-*.g.cs"
ALL_ASSEMBLIES="+SmartSpare.*;-*Tests*;-SmartSpare.E2E"
ALL_FILES="-*Migrations*;-*obj*;-*.g.cs"

# coverage_report NAME TITLE REPORTS ASSEMBLIES FILES [THRESHOLD]: the report of the given Cobertura files in
# TestResults/NAME, its summary printed and added to the GitHub job summary; fails below THRESHOLD when one is given.
coverage_report() {
  local name="$1" title="$2" reports="$3" assemblies="$4" files="$5" threshold="${6:-}"
  local report="$RESULTS/$name"
  dotnet tool restore >/dev/null
  rm -rf "$report"
  dotnet tool run reportgenerator \
    "-reports:$reports" \
    "-targetdir:$report" \
    "-reporttypes:Html;TextSummary;MarkdownSummaryGithub" \
    "-assemblyfilters:$assemblies" \
    "-filefilters:$files" \
    "-verbosity:Error" \
    ${threshold:+"minimumCoverageThresholds:lineCoverage=$threshold"} \
    && status=0 || status=$?
  echo "-- $title"
  grep -E "Line coverage|Covered lines|Coverable lines" "$report/Summary.txt" || true
  if [ -n "${GITHUB_STEP_SUMMARY:-}" ] && [ -f "$report/SummaryGithub.md" ]; then
    { echo "## $title"; echo; cat "$report/SummaryGithub.md"; echo; } >>"$GITHUB_STEP_SUMMARY"
  fi
  if [ "$status" -ne 0 ]; then
    echo "$title: below $threshold % (see $report/index.html)" >&2
    return "$status"
  fi
}

# The gate: unit tests only (the test step's files, one level below TestResults), business logic at least
# COVERAGE_THRESHOLD (70 %; goal 80 % by stage 9, #63). A pull request that lowers it by more than 2 points says why.
step_coverage() {
  coverage_report coverage "Unit-test coverage of business logic (gate ${COVERAGE_THRESHOLD:-70} %)" \
    "$RESULTS/*/coverage.cobertura.xml" "$BUSINESS_ASSEMBLIES" "$BUSINESS_FILES" "${COVERAGE_THRESHOLD:-70}"
}

# Report only: unit tests over all code, data access included.
step_coverage_all() {
  coverage_report coverage-all "Unit-test coverage of all code (report only)" \
    "$RESULTS/*/coverage.cobertura.xml" "$ALL_ASSEMBLIES" "$ALL_FILES"
}

# Report only: unit and integration tests together over all code, after the test and integration steps. Data access
# that only runs against PostgreSQL is covered here.
step_coverage_combined() {
  coverage_report coverage-combined "Unit + integration coverage of all code (report only)" \
    "$RESULTS/**/coverage.cobertura.xml" "$ALL_ASSEMBLIES" "$ALL_FILES"
}

# Known-vulnerable packages, direct and transitive (#64). NuGet Audit already fails restore on them; this step names the
# check, prints what it found, and runs weekly on its own (vulnerabilities.yml) so new advisories surface between pushes.
# The command's exit code on findings is undocumented, so the JSON is parsed.
step_vulnerabilities() {
  mkdir -p "$RESULTS"
  dotnet package list --project "$SOLUTION" --include-transitive --vulnerable --format json >"$RESULTS/vulnerable-packages.json"
  "$PYTHON" - "$RESULTS/vulnerable-packages.json" <<'PY'
import json, sys
data = json.load(open(sys.argv[1], encoding="utf-8"))
found = sorted({(p["id"], p.get("resolvedVersion", "?"), v.get("severity", "?"), v.get("advisoryurl", ""))
                for project in data.get("projects", []) for framework in project.get("frameworks", [])
                for kind in ("topLevelPackages", "transitivePackages") for p in framework.get(kind, [])
                for v in p.get("vulnerabilities", [])})
for problem in data.get("problems", []):
    print("problem:", problem.get("text", problem), file=sys.stderr)
if found:
    for package, version, severity, url in found:
        print(f"VULNERABLE {package} {version} ({severity}) {url}")
    sys.exit(1)
print(f"no known vulnerable packages ({len(data.get('projects', []))} projects, direct and transitive)")
PY
}

step_integration() {
  if [ -d "$RESULTS/integration" ]; then
    find "$RESULTS/integration" -name 'coverage.cobertura.xml' -delete
  fi
  run_tests "Category=Integration" \
    --logger trx --results-directory "$RESULTS/integration" \
    --collect "XPlat Code Coverage"
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

# Optional: the services topology in one process (STAGE_4_PLAN section 10, #57). The gateway and the Identity and
# Inventory service hosts in memory, PostgreSQL 17 and RabbitMQ 4.4 in Testcontainers: sign-in through the gateway, the
# diagnostics probe over the broker and its linked trace, and /internal never routed.
step_topology() {
  dotnet test tests/SmartSpare.Topology.Tests --no-build --configuration "$CONFIGURATION" \
    --filter "Category=Topology" \
    --logger trx --results-directory "$RESULTS"
}

# Containers that Testcontainers or verify_database.sh started and that are still there; Ryuk itself is listed while it
# finishes reaping. Printed after steps that start containers.
report_containers() {
  if command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1; then
    echo "-- containers left by the check (docker ps -a, Testcontainers and verify_database):"
    docker ps -a --filter "label=org.testcontainers=true" --format '   {{.Names}}  {{.Image}}  {{.Status}}'
    docker ps -a --filter "name=ss-verify-" --format '   {{.Names}}  {{.Image}}  {{.Status}}'
  fi
}

FAST=(stale-terms deploy-paths restore vulnerabilities build format duplication check-model test coverage coverage-all)
ALL=("${FAST[@]}" integration database coverage-combined)
OPTIONAL=(e2e topology)

if [ $# -eq 0 ]; then
  STEPS=("${ALL[@]}")
elif [ "$1" = "fast" ]; then
  STEPS=("${FAST[@]}")
else
  STEPS=("$@")
fi

echo "== tests run $([ "$CHECK_SEQUENTIAL" = "1" ] && echo "one project at a time (CHECK_SEQUENTIAL=1)" || echo "in parallel (CHECK_SEQUENTIAL=0)")"
for step in "${STEPS[@]}"; do
  fn="step_${step//-/_}"
  if ! declare -F "$fn" >/dev/null; then
    echo "unknown step: $step (steps: ${ALL[*]} ${OPTIONAL[*]}, or fast)" >&2
    exit 2
  fi
  echo "== $step"
  "$fn"
  case "$step" in integration | database | e2e | topology) report_containers ;; esac
done
echo "== OK: ${STEPS[*]}"
