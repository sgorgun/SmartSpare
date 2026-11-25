#!/usr/bin/env bash
# =============================================================================
# Runs the database migrations in Azure (ADR 0020) and waits for the result: one execution of the Container Apps job
# "migrations" with this commit's MigrationRunner image and the given arguments, e.g. `--module inventory`. The job's
# template is overridden for this execution only (`az containerapp job start --yaml`, "Start a job execution on demand"
# in the Container Apps jobs docs), so one job serves every module. Exits non-zero if the execution fails.
#
#   tools/run-migration-job.sh <resource-group> <image> [runner arguments...]
# =============================================================================
set -euo pipefail

if [ $# -lt 2 ]; then
  echo "usage: $0 <resource-group> <image> [runner arguments...]" >&2
  exit 2
fi
GROUP="$1"
IMAGE="$2"
shift 2
JOB=migrations
TEMPLATE="$(mktemp --suffix .json)"
trap 'rm -f "$TEMPLATE"' EXIT

# The job's own template (resources, secrets, identity-based environment) with only the image and arguments replaced.
az containerapp job show --name "$JOB" --resource-group "$GROUP" --query properties.template --output json > "$TEMPLATE"
python3 - "$TEMPLATE" "$IMAGE" "$@" <<'PY'
import json, sys
path, image, args = sys.argv[1], sys.argv[2], sys.argv[3:]
template = json.load(open(path))
container = template["containers"][0]
container["image"] = image
container["args"] = ["migrate", *args]
json.dump(template, open(path, "w"))
PY

EXECUTION="$(az containerapp job start --name "$JOB" --resource-group "$GROUP" --yaml "$TEMPLATE" --query name --output tsv)"
echo "== started $JOB execution $EXECUTION: migrate $*"

for _ in $(seq 1 120); do
  STATUS="$(az containerapp job execution show --name "$JOB" --resource-group "$GROUP" --job-execution-name "$EXECUTION" \
    --query properties.status --output tsv)"
  case "$STATUS" in
    Succeeded) echo "== OK: $EXECUTION succeeded"; exit 0 ;;
    Failed | Stopped | Degraded) echo "== FAILED: $EXECUTION ended $STATUS" >&2; exit 1 ;;
  esac
  sleep 10
done

echo "== FAILED: $EXECUTION did not finish within 20 minutes" >&2
exit 1
