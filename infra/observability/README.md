# Observability as code

Dashboards, alert rules and the local stack's configuration (ADR 0018, STAGE_4_PLAN 6.3–6.4). The AppHost mounts this
folder with `--topology services`. The same dashboards and rules are provisioned to Grafana Cloud by
[`infra/grafana`](../grafana) (Terraform, PR 4j).

| Path | What |
|---|---|
| `collector/config.yaml` | OpenTelemetry Collector: OTLP in; metrics to Prometheus, logs to Loki, traces to Tempo, everything to the Aspire dashboard |
| `collector/grafana-cloud.yaml` | Added by the AppHost when it has the `GrafanaCloud:*` user secrets: everything also to the Grafana Cloud stack ([infra/grafana](../grafana/README.md#2-send-the-local-services-topologys-telemetry-to-the-stack)) |
| `prometheus/prometheus.yml` | OTLP receiver, the promoted resource attributes, Tempo's span metrics by remote write |
| `loki/config.yaml`, `tempo/tempo.yaml` | Single-binary Loki and Tempo on local storage |
| `grafana/provisioning/` | Data sources (local only; `usage` is an alias of the local Prometheus for Grafana Cloud's usage data source), the dashboard provider, and the alert rules (`alerting/rules.json`) |
| `dashboards/` | The dashboards, by uid |

**Dashboards**
- `aspnetcore` and `aspnetcore-endpoint`: the .NET team's ASP.NET Core dashboards, grafana.com IDs 19924 and 19925, which [Microsoft's metrics docs](https://learn.microsoft.com/aspnet/core/metrics/overview) link to. They are stored with the data source fixed to the uid `prometheus`.
- `smartspare-overview`: requests, 5xx rate and p95 per service and route.
- `smartspare-messaging`: outbox lag and parked events per module, broker queue depth, dead letters and the monthly message count. The broker panels fill when the broker metrics job runs: locally, start the `broker-metrics` resource in the Aspire dashboard ([BROKER.md](../../docs/operations/BROKER.md#broker-metrics-job)).
- `smartspare-free-tier`: active series (Grafana Cloud's `grafanacloud_instance_active_series`, locally Prometheus' head series).

**Alert rules** (ADR 0018 §4): 8 rules in 3 groups.

| Group | Rule | Condition |
|---|---|---|
| `smartspare-http` | 5xx rate per service | > 2 % for 5 minutes |
| | p95 latency per service | > 2 s for 10 minutes |
| `smartspare-messaging` | Outbox lag | Oldest pending event older than 5 minutes |
| | Dead letters | Any dead-lettered message |
| | Queued messages | > 5,000 for 5 minutes |
| | Monthly message quota | > 70 % of 1 M |
| `smartspare-platform` | Active series | > 8,000 for 15 minutes |
| | Warm-up failed | Any service not ready during a warm-up call |

The p95 rule doesn't yet exclude cold starts by a `cold_start` attribute; that comes with the measured cold start of stage 10 (#85).

Metric names are those of Prometheus' OTLP translation, for example `http_server_request_duration_seconds_count` with `job` = `service.name`, and `smartspare_outbox_oldest_pending_age_seconds`.
