# Grafana Cloud as code

Terraform for the Grafana Cloud Free stack (ADR 0018, STAGE_4_PLAN 6.4, PR 4j). It provisions the same files the local
Grafana loads from [`infra/observability`](../observability):

| Resource | From |
|---|---|
| Folder `SmartSpare` | — |
| 5 dashboards | `observability/dashboards/*.json` |
| 7 alert rules in 3 groups | `observability/grafana/provisioning/alerting/rules.json` |
| Contact point `SmartSpare`: e-mail, plus Telegram when a bot token is given | variables |
| Notification policy: everything to that contact point | — |
| Optional test rule `SmartSpare test alert`, firing at once | `test_alert = true` |

The files use the local data source uids `prometheus` and `usage`. Terraform replaces them with the stack's
`grafanacloud-prom` and `grafanacloud-usage`; other uids can be passed as `TF_VAR_prometheus_datasource_uid` and
`TF_VAR_usage_datasource_uid`.

**Versions:** Terraform 1.17.2 in Docker (`hashicorp/terraform`) and the `grafana/grafana` provider 4.54.0, pinned in
`versions.tf` and `.terraform.lock.hcl`.

**CI:** [`grafana.yml`](../../.github/workflows/grafana.yml) runs `fmt`, `validate`, and `terraform test`
([`tests/`](tests)), which plans against a mock provider. It needs no stack and no credentials.

**State:** the state stays on the machine that applies it (`infra/grafana/terraform.tfstate`, git-ignored, because it
holds the alert address and the bot token). Remote state comes with the first deployment (#85).

## 1. What to create (least privilege)

Two tokens, each allowed exactly one job:

| # | Where | What | Scope or role | Used by |
|---|---|---|---|---|
| 1 | grafana.com → **My Account** → **Add stack** | A Free stack in an EU region | — | everything |
| 2 | The stack's Grafana → **Administration** → **Users and access** → **Service accounts** → **Add service account** | Service account `smartspare-terraform`, then **Add service account token** with an expiry (for example 90 days) | Role **Admin**. Editor was tried first and is not enough: applying answered 403 for `folders:read` (26 November 2026). The account can't log in, and its token expires | `GRAFANA_AUTH` for Terraform |
| 3 | grafana.com → the stack → **OpenTelemetry** → **Configure** → **Generate now** | Access policy token for OTLP (the tile also shows the endpoint and the instance ID) | `metrics:write`, `logs:write`, `traces:write` only, for this stack only; no read scopes | the collector of the local services topology (`GrafanaCloud:*` user secrets) |
| 4 (optional) | Telegram → **@BotFather** → `/newbot` | A bot token. Then add the bot to a chat, send it a message, and read the chat id from `https://api.telegram.org/bot<token>/getUpdates` | posts to one chat | `TF_VAR_telegram_bot_token`, `TF_VAR_telegram_chat_id` |

Only this service account has the Admin role; the OTLP token (3) has write scopes only. Rotate the service account token before it expires, and after any time it was stored outside the password manager.

The data sources' uids are listed under **Connections** → **Data sources**. Check that the Prometheus one is
`grafanacloud-prom` and the usage one `grafanacloud-usage`.

## 2. Send the local services topology's telemetry to the stack

AppHost user secrets, from the OpenTelemetry tile (token 3):

```bash
dotnet user-secrets set GrafanaCloud:OtlpEndpoint "https://otlp-gateway-<region>.grafana.net/otlp" --project src/SmartSpare.AppHost
dotnet user-secrets set GrafanaCloud:InstanceId "<instance id>" --project src/SmartSpare.AppHost
dotnet user-secrets set GrafanaCloud:Token "<access policy token>" --project src/SmartSpare.AppHost
dotnet run --project src/SmartSpare.AppHost -- --topology services
```

The collector then sends everything to the stack too ([`collector/grafana-cloud.yaml`](../observability/collector/grafana-cloud.yaml)). Without these secrets, nothing leaves the machine.

## 3. Provision the stack

In Git Bash, with Docker running. `read -s` keeps the tokens out of the shell history and off the screen:

```bash
export GRAFANA_URL="https://<stack>.grafana.net"
read -rsp "Service account token: " GRAFANA_AUTH && export GRAFANA_AUTH && echo
export TF_VAR_alert_email="<alert address>"
# optional, for Telegram:
read -rsp "Telegram bot token: " TF_VAR_telegram_bot_token && export TF_VAR_telegram_bot_token && echo
export TF_VAR_telegram_chat_id="<chat id>"

tools/grafana-cloud.sh plan            # what would change
tools/grafana-cloud.sh apply           # dashboards, rules, contact point, policy
tools/grafana-cloud.sh test-alert      # a rule that fires at once: the alert should arrive within a few minutes
tools/grafana-cloud.sh test-alert-off  # remove it again
```

The test alert travels the same notification policy and contact point as the real rules, so its arrival proves the
whole path, not just the contact point's settings.

**Done on 26 November 2026:** the plan created 11 resources (folder, 5 dashboards, 3 rule groups with 7 rules, the contact point and the policy), and the test alert arrived by e-mail. It first landed in the spam folder; mark Grafana's sender as not spam so real alerts are seen.
