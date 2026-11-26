#!/usr/bin/env bash
# Provisions the Grafana Cloud stack from infra/grafana (STAGE_4_PLAN 6.4, PR 4j) with Terraform 1.17.2 in Docker, so
# nothing but Docker is needed. The state stays in infra/grafana/terraform.tfstate on this machine (git-ignored; it
# holds the alert address and the Telegram token); remote state comes with the first deployment (#85).
#
#   tools/grafana-cloud.sh plan            what would change
#   tools/grafana-cloud.sh apply           dashboards, alert rules, contact point, notification policy
#   tools/grafana-cloud.sh test-alert      apply with a rule that fires at once (delivery check)
#   tools/grafana-cloud.sh test-alert-off  apply without it again
#
# Reads only environment variables, never prints them (infra/grafana/README.md):
#   GRAFANA_URL         https://<stack>.grafana.net
#   GRAFANA_AUTH        service account token of the stack (role Admin, README §1)
#   TF_VAR_alert_email  where alerts go
#   TF_VAR_telegram_bot_token, TF_VAR_telegram_chat_id   optional
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
IMAGE="hashicorp/terraform:1.17.2"

for name in GRAFANA_URL GRAFANA_AUTH TF_VAR_alert_email; do
  if [ -z "${!name:-}" ]; then
    echo "Set $name first (see infra/grafana/README.md)." >&2
    exit 2
  fi
done

terraform() {
  # Git Bash on Windows would rewrite the container paths; MSYS_NO_PATHCONV keeps them.
  MSYS_NO_PATHCONV=1 docker run --rm -i \
    -e GRAFANA_URL -e GRAFANA_AUTH \
    -e TF_VAR_alert_email -e TF_VAR_telegram_bot_token -e TF_VAR_telegram_chat_id \
    -e TF_IN_AUTOMATION=1 \
    -v "$ROOT:/repo" -w /repo/infra/grafana "$IMAGE" "$@"
}

terraform init -input=false -no-color >/dev/null
case "${1:-}" in
  plan) terraform plan -input=false -no-color ;;
  apply) terraform apply -input=false -auto-approve -no-color ;;
  test-alert) terraform apply -input=false -auto-approve -no-color -var test_alert=true ;;
  test-alert-off) terraform apply -input=false -auto-approve -no-color -var test_alert=false ;;
  *)
    echo "usage: $0 plan | apply | test-alert | test-alert-off" >&2
    exit 2
    ;;
esac
