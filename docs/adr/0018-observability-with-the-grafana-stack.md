# ADR 0018: Observability with the Grafana stack

- Status: Accepted
- Date: 2026-11-24
- Amends: [ADR 0009](0009-azure-hosting-and-delivery.md) (Application Insights as the telemetry target)

## Context

Every host already emits OpenTelemetry logs, metrics and traces over OTLP (`SmartSpare.ServiceDefaults`). Locally they go to the Aspire dashboard, which is "a developer visualization tool, not a production monitoring tool" ([OTLP example](https://learn.microsoft.com/dotnet/core/diagnostics/observability-otlp-example)). ADR 0009 planned Application Insights for Azure.

The backend rubric's maximum requires:
- metrics with dashboards;
- centralized, searchable structured logs;
- request tracing with a correlation id;
- alerts to a messenger or e-mail.

The product owner decided on 24 November 2026:
- **D3:** Prometheus, Loki, Tempo and Grafana, with alerts to Telegram and e-mail, as the primary stack instead of Application Insights.
- **Hosting:** production uses **Grafana Cloud Free**. The self-hosted stack runs locally, so it is still shown and tested.

Facts checked on 24 November 2026:

| Component | Version, licence | Relevant facts |
|---|---|---|
| Grafana Cloud Free | managed | 10k active metric series, 50 GB logs and 50 GB traces ingested per month, 14-day retention, 3 active users. The free tier includes "All Grafana Cloud services, with usage limits" ([pricing](https://grafana.com/pricing/)). OTLP endpoint per stack, credentials generated in the stack's OpenTelemetry tile. Grafana recommends Alloy (a collector) for production, and direct SDK export for development and testing ([send OTLP data](https://grafana.com/docs/grafana-cloud/send-data/otlp/send-data-otlp/)). Default ruler limits are 35 rule groups and 20 rules per group ([usage limits](https://grafana.com/docs/grafana-cloud/cost-management-and-billing/understand-your-invoice/usage-limits/)). |
| OpenTelemetry Collector (contrib) | 0.165.0, Apache-2.0 | Recommended in front of backends for retries, batching and filtering ([collector](https://opentelemetry.io/docs/collector/)). Used locally. |
| Prometheus | 3.16.0, Apache-2.0 | OTLP receiver via `--web.enable-otlp-receiver`. Promote `service.*` resource attributes. Retention in the config file. Non-POSIX and NFS filesystems not supported ([OTLP guide](https://prometheus.io/docs/guides/opentelemetry/), [storage](https://prometheus.io/docs/prometheus/latest/storage/)) |
| Loki | 3.7.10, AGPL-3.0 | Native OTLP at `/otlp/v1/logs` (schema v13, TSDB) ([OTLP](https://grafana.com/docs/loki/latest/send-data/otel/)) |
| Tempo | 3.1.1, AGPL-3.0 | OTLP gRPC 4317 and HTTP 4318 (bind to `0.0.0.0` in containers). Metrics-generator for RED metrics ([configuration](https://grafana.com/docs/tempo/latest/configuration/)) |
| Grafana OSS | 13.3.1, AGPL-3.0 | Telegram and Email contact points ([contact points](https://grafana.com/docs/grafana/latest/alerting/configure-notifications/manage-contact-points/)). "In Grafana Cloud, SMTP configuration is not required" ([email](https://grafana.com/docs/grafana/latest/alerting/configure-notifications/manage-contact-points/integrations/configure-email/)). Trace-to-logs and trace-to-metrics links ([trace to logs](https://grafana.com/docs/grafana/latest/datasources/tempo/configure-tempo-data-source/configure-trace-to-logs/)) |

The AGPL-3.0 components are used unmodified as separate services, so the licence doesn't affect SmartSpare's code. Everything is free, which satisfies the rubric's "no paid libraries".

## Decision

### 1. Pipelines

```
Production:  gateway, services, jobs ──OTLP/HTTP + Basic auth──► Grafana Cloud OTLP gateway
                                                                ├─► metrics (Prometheus-compatible)
                                                                ├─► Loki   (logs)
                                                                └─► Tempo  (traces)
             Grafana Cloud: dashboards, alert rules ──► Telegram, e-mail

Local (Aspire --topology services):
             gateway, services ──OTLP──► OpenTelemetry Collector ──► Prometheus · Loki · Tempo ──► Grafana
Single host: OTLP ──► Aspire dashboard (unchanged)
```

- **Application side:** no code change. Hosts keep the OpenTelemetry SDK and are configured by environment variables only ([Aspire telemetry](https://aspire.dev/fundamentals/telemetry/)):
  - `OTEL_EXPORTER_OTLP_ENDPOINT` points at Grafana Cloud's OTLP endpoint in production and at the Collector or the Aspire dashboard locally;
  - `OTEL_EXPORTER_OTLP_HEADERS` carries the Basic-auth token from Key Vault.
- **Direct export in production, no collector.** Grafana recommends Alloy for production. A collector, though, is one more app that has to be reachable whenever another app sends telemetry, and the topology scales everything to zero (ADR 0009). We accept the SDK's weaker transport handling (bounded retry and batching in the OTLP exporter) for a demo environment. A collector Container App can be added later without touching application code, since only the endpoint changes.
- **Resource attributes:** `service.name` (gateway, identity, …), `service.version` (commit SHA), `service.instance.id`, `deployment.environment.name`.
- **Filtering moves into the application, because there is no collector in production:**
  - no personal data and no secrets in span or log attributes (code-review rule plus an OpenTelemetry processor that drops non-allow-listed attributes);
  - health-check spans are already filtered in `ServiceDefaults`.

### 2. What is collected

| Signal | Content |
|---|---|
| Metrics | ASP.NET Core, HttpClient and runtime instrumentation (already on). Custom meters per module: outbox lag and pending count, inbox duplicates, published messages, transfers approved, forecast run duration. **Broker metrics** (queue depth, dead-letter count, connections) come from a Container Apps job (cron, every 15 minutes) that reads the CloudAMQP management HTTP API and exports them over OTLP, because a service asleep at zero can't report them. |
| Logs | Structured `ILogger` events with scopes (source-generated templates), exported as OTLP logs. Low-cardinality labels only (`service_name`, `deployment_environment`); trace id and the rest as structured metadata. |
| Traces | Incoming HTTP, outgoing HTTP, EF Core/Npgsql commands, and RabbitMQ publish/consume spans with W3C `traceparent` in message headers (ADR 0017). One trace follows a request from the gateway through services and events. |

### 3. Correlation

- The W3C trace id is the correlation id. It is in every log record, in every ProblemDetails (`traceId`, #60) and in the UI error message.
- Data sources are linked:
  - Tempo → Loki (filter by trace id);
  - Tempo → Prometheus (trace to metrics);
  - exemplars → Tempo;
  - Loki derived field `trace_id` → Tempo.

### 4. Dashboards and alerts

Dashboards and alert rules are provisioned from `infra/observability/`: file provisioning locally, the Grafana Cloud API from CD.

- **Dashboards:**
  - the .NET team's ASP.NET Core dashboards (grafana.com IDs 19924 and 19925, [linked from Microsoft's metrics docs](https://learn.microsoft.com/aspnet/core/metrics/overview)), checked against OTLP metric names;
  - a SmartSpare overview: requests, error rate and p95 per service and endpoint;
  - messaging (outbox lag, queue depth, dead letters, monthly message count);
  - business KPIs;
  - free-tier usage (active series, ingest volume).
- **Alert rules** (well inside the default ruler limits):

  | Rule | Condition |
  |---|---|
  | 5xx rate per service | > 2 % for 5 minutes |
  | p95 latency per service | > 2 s for 10 minutes (NFR01; cold starts excluded by a `cold_start` span attribute) |
  | Outbox | Pending outbox message older than 5 minutes |
  | Dead letters | Any message in a dead-letter queue |
  | Queued messages | Total queued > 5,000 (broker limit 10,000) |
  | Message quota | Published messages > 70 % of the monthly broker quota |
  | Active series | Above 80 % of the free tier's 10k |
  | Warm-up | The warm-up job failed (ADR 0009) |

  There is no readiness alert: apps at zero replicas are healthy by design.
- **Contact points:** Telegram (bot token and chat id from Key Vault) and e-mail (no SMTP setup in Grafana Cloud). The free tier lists all Grafana Cloud services. Each contact point's availability on the free stack is checked when the stack is created (#59). If Telegram isn't offered there, e-mail alone satisfies the rubric's "messenger or e-mail". One test alert is demonstrated for the defence.

### 5. Limits and retention

| | Production (Grafana Cloud Free) | Local (Aspire) |
|---|---|---|
| Metrics | 10k active series. Cardinality is controlled by OpenTelemetry views: keep `http.route`, `http.response.status_code` and `http.request.method`; drop unused runtime instruments; coarse histogram buckets. 7 services × a few dozen routes stays well under the limit, and the usage dashboard alerts at 80 % | Prometheus, 15 days |
| Logs | 50 GB/month. Information level and above, no request bodies | Loki, local volume |
| Traces | 50 GB/month. Head sampling 100 % in the demo environment, lowered by configuration if needed | Tempo, local volume |
| Retention | 14 days for all signals | As configured in `infra/observability/` |
| Users | 3 active users (developer, supervisor, examiner) | — |

Telemetry retention is shorter than the 180-day audit retention on purpose: telemetry is operational data, and the audit trail stays in `audit.audit_log`.

## Alternatives considered

| Alternative | Why not |
|---|---|
| Self-hosted Prometheus, Loki, Tempo and Grafana on a VM in Azure | The same stack under our control, but rejected for **cost against the €35/month target**: a B2s VM alone is about €31.46/month ([ADR 0009](0009-azure-hosting-and-delivery.md) amendment), plus patching and backups. Prometheus also can't use Container Apps' Azure Files mounts. The stack runs self-hosted locally instead. |
| Azure Managed Grafana | Standard is the only tier for new workspaces, at roughly $65/month or more ([overview](https://learn.microsoft.com/azure/managed-grafana/overview)). |
| Application Insights (ADR 0009 as written) | Replaced by the product owner's decision D3. It would also tie the telemetry back end to Azure. |

## Consequences

- The rubric's monitoring, centralized logs, tracing and alerting items are met with an open, portable stack. The application only speaks OTLP (ADR 0012 seam), and switching between Grafana Cloud and self-hosted is configuration.
- Application Insights is no longer the target. ADR 0009 is amended.
- No observability infrastructure to run in Azure. The free tier's limits must be respected and watched (§5), and retention is 14 days.
- Without a collector, telemetry filtering is the application's job, and transport is less robust than with Alloy. This is accepted for a demo environment.
- The self-hosted stack exists only locally. Its configuration files in `infra/observability/` are kept working, and a local run is part of the stage 4 acceptance.
