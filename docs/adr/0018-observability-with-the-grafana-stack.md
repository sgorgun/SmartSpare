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

On 24 November 2026 the product owner decided (D3) on Prometheus, Loki, Tempo and Grafana, with alerts to Telegram and e-mail, as the primary stack instead of Application Insights.

Facts checked on 24 November 2026:

| Component | Version, licence | Relevant facts |
|---|---|---|
| OpenTelemetry Collector (contrib) | 0.165.0, Apache-2.0 | A collector is recommended in front of backends for retries, batching and filtering ([collector](https://opentelemetry.io/docs/collector/)) |
| Prometheus | 3.16.0, Apache-2.0 | OTLP receiver via `--web.enable-otlp-receiver` at `/api/v1/otlp/v1/metrics`. Promote `service.*` resource attributes. Retention via the config file (the CLI flag is deprecated). **Non-POSIX and NFS filesystems are not supported** ([OTLP guide](https://prometheus.io/docs/guides/opentelemetry/), [storage](https://prometheus.io/docs/prometheus/latest/storage/)) |
| Loki | 3.7.10, AGPL-3.0 | Native OTLP at `/otlp/v1/logs` (schema v13, TSDB). Azure Blob object storage. Retention through the compactor ([OTLP](https://grafana.com/docs/loki/latest/send-data/otel/), [storage](https://grafana.com/docs/loki/latest/configure/storage/), [retention](https://grafana.com/docs/loki/latest/operations/storage/retention/)) |
| Tempo | 3.1.1, AGPL-3.0 | OTLP gRPC 4317 and HTTP 4318 (bind to `0.0.0.0` in containers). Azure Blob backend. `compaction.block_retention`. Metrics-generator for RED metrics and service graphs ([configuration](https://grafana.com/docs/tempo/latest/configuration/)) |
| Grafana OSS | 13.3.1, AGPL-3.0 | Native Telegram and Email contact points; SMTP configured by `GF_SMTP_*` environment variables. Trace-to-logs, trace-to-metrics and exemplar links between data sources ([contact points](https://grafana.com/docs/grafana/latest/alerting/configure-notifications/manage-contact-points/), [trace to logs](https://grafana.com/docs/grafana/latest/datasources/tempo/configure-tempo-data-source/configure-trace-to-logs/)) |

The AGPL-3.0 components are used unmodified as separate services, so the licence doesn't affect SmartSpare's code. All are free, which satisfies the rubric's "no paid libraries".

## Decision

### 1. Pipeline

```
services, gateway ──OTLP──► OpenTelemetry Collector ──► Prometheus (metrics, OTLP receiver)
                                                     ├─► Loki       (logs, OTLP)
                                                     └─► Tempo      (traces, OTLP) ──metrics-generator──► Prometheus
                                          Grafana ◄── data sources: Prometheus, Loki, Tempo; alerting ──► Telegram, e-mail
```

- **Application side:** no code change. Hosts keep the OpenTelemetry SDK and set `OTEL_EXPORTER_OTLP_ENDPOINT` to the Collector. The same configuration points at the Aspire dashboard locally ([Aspire telemetry](https://aspire.dev/fundamentals/telemetry/)).
- **Resource attributes:** `service.name` (gateway, identity, …), `service.version` (commit SHA), `service.instance.id`, `deployment.environment.name`.
- **Collector:** batching, retry, memory limiter, attribute filtering (no personal data, no secrets in attributes), and dropping health-check spans. It uses contrib to start; a trimmed custom build is preferred later.

### 2. What is collected

| Signal | Content |
|---|---|
| Metrics | ASP.NET Core, HttpClient and runtime instrumentation (already on). Plus custom meters per module: outbox lag and pending count, inbox duplicates, dead-lettered messages, transfers approved, forecast run duration. Plus RabbitMQ's Prometheus endpoint (`rabbitmq_prometheus` plugin) and PostgreSQL basics from the Flexible Server metrics |
| Logs | Structured `ILogger` events with scopes (source-generated templates), exported as OTLP logs. Low-cardinality labels only (`service_name`, `deployment_environment`); trace id, span id and the rest go to structured metadata |
| Traces | Incoming HTTP, outgoing HTTP, EF Core/Npgsql commands, and RabbitMQ publish/consume spans with W3C `traceparent` propagated in message headers (ADR 0017). One trace follows a request from the gateway through services and events |

### 3. Correlation

- The W3C trace id is the correlation id. It is in every log record, in every ProblemDetails (`traceId`, #60) and in the UI error message.
- Grafana data sources are linked:
  - Tempo → Loki (filter by trace id);
  - Tempo → Prometheus (trace to metrics);
  - Prometheus exemplars → Tempo;
  - Loki derived field `trace_id` → Tempo.

### 4. Dashboards and alerts

- **Dashboards** are provisioned as code from `infra/observability/`:
  - the .NET team's ASP.NET Core dashboards (grafana.com IDs 19924 and 19925, [linked from Microsoft's metrics docs](https://learn.microsoft.com/aspnet/core/metrics/overview)), checked against Prometheus's OTLP metric names;
  - a SmartSpare overview: requests, error rate and p95 latency per service and endpoint;
  - outbox and queue health;
  - business KPIs.
- **Alert rules** are provisioned as code:

  | Rule | Condition |
  |---|---|
  | 5xx error rate per service | > 2 % for 5 minutes |
  | p95 latency per service | > 2 s for 10 minutes (NFR01) |
  | Readiness | A readiness probe fails |
  | Outbox | A pending outbox message is older than 5 minutes |
  | Dead letters | Any message in a DLQ |
  | Disk | Disk use on the stateful host > 80 % |

- **Contact points:** Telegram (bot token and chat id from Key Vault) and e-mail (SMTP credentials from Key Vault). One test alert is demonstrated for the defence.

### 5. Retention and storage

| Backend | Retention | Storage |
|---|---|---|
| Prometheus | 15 days (`storage.tsdb.retention.time` in the config file) | **Local managed disk** of the stateful host. Azure Files is not used, because Prometheus doesn't support NFS or non-POSIX filesystems. |
| Loki | 31 days (`retention_period: 744h`, compactor retention on) | Azure Blob Storage container, managed identity |
| Tempo | 14 days (`compaction.block_retention: 336h`) | Azure Blob Storage container, managed identity |
| Grafana | Dashboards and alert rules provisioned from Git | Its own small database (`grafana` schema on the PostgreSQL server, own login) for users and state |

Retention is shorter than the 180-day audit retention on purpose. Telemetry is operational data, and the audit trail stays in `audit.audit_log`.

### 6. Hosting

| Environment | Where |
|---|---|
| Single host locally | Aspire dashboard, unchanged |
| Services topology locally | Aspire resources for the Collector, Prometheus, Loki, Tempo and Grafana, with the same configuration files as Azure |
| Azure | Containers on the **stateful host** VM, shared with RabbitMQ (ADR 0017), in the Container Apps environment's virtual network. Grafana is published through the gateway's domain or its own HTTPS endpoint with sign-in. Prometheus, Loki and Tempo are private. |

**Fallback if the VM cost is not covered:** Grafana Cloud Free (10k active metric series, 50 GB logs and 50 GB traces a month, 14-day retention, 3 users; [pricing](https://grafana.com/pricing/)). Only the Collector's exporters change. Azure Managed Grafana was rejected: Standard is the only tier for new workspaces, at roughly $65 a month or more ([overview](https://learn.microsoft.com/azure/managed-grafana/overview)).

## Consequences

- The rubric's monitoring, centralized logs, tracing and alerting items are met with an open, portable stack. The application only speaks OTLP (ADR 0012 seam).
- Application Insights is no longer the target. ADR 0009 is amended.
- Five more components to configure and update (Collector, Prometheus, Loki, Tempo, Grafana). Mitigations: configuration as code in `infra/observability/`, one VM, the same files locally.
- Prometheus data on the VM disk is lost if the disk is lost. Accepted for operational metrics; logs and traces are in Blob Storage.
- Telemetry must stay free of personal data. Collector filters and the existing logging rules enforce it, and review checks new attributes.
