# ADR 0009: Azure hosting and delivery

- Status: Accepted, amended by [ADR 0015](0015-deployment-topology-microservices-behind-yarp-gateway.md), [ADR 0017](0017-rabbitmq-integration-event-transport.md) and [ADR 0018](0018-observability-with-the-grafana-stack.md) (2026-11-24), by [ADR 0020](0020-database-migrations-through-the-migration-runner-job.md) (2026-11-25), and by [ADR 0022](0022-azure-region-north-europe.md) (region North Europe, 2026-11-25)
- Date: 2026-11-16

## Context

The system is deployed to Microsoft Azure and must be able to move to microservices later.

## Decision

Ship container images from day one. Production, since the amendment below:
- Azure Container Apps, with one app per service plus the gateway;
- PostgreSQL Flexible Server 17 (Burstable), Blob Storage, Key Vault and Azure Container Registry;
- CloudAMQP (free shared RabbitMQ) and Grafana Cloud Free for messaging and observability; every app scales to zero;
- one Managed Identity and database login per service.

The original plan (App Service with one container, and Application Insights) is superseded; see the amendment. Infrastructure in Bicep (`infra/`), CI/CD with GitHub Actions signing in via OIDC, database migrations as a MigrationRunner Container Apps job started by the pipeline (ADR 0020). Background jobs are multi-instance safe (Quartz clustered, `SKIP LOCKED`). .NET Aspire is used for local development only.

## Consequences

- No long-lived secrets in CI or configuration.
- Each service has its own image and Container Apps revision.
- Bicep templates must be maintained alongside the code.

## Amendment (2026-11-24)

**Hosting choice for the services topology** ([ADR 0015](0015-deployment-topology-microservices-behind-yarp-gateway.md)): seven .NET services (gateway + six modules), RabbitMQ ([ADR 0017](0017-rabbitmq-integration-event-transport.md)) and the observability stack ([ADR 0018](0018-observability-with-the-grafana-stack.md)). Checked against the official documentation on 24 November 2026:

| Criterion | App Service (Linux) | Azure Container Apps |
|---|---|---|
| Non-HTTP workloads (AMQP broker) | Not possible: "only supports HTTP and HTTPS", one exposed port per app ([considerations](https://learn.microsoft.com/azure/architecture/guide/container-service-general-considerations), [custom containers](https://learn.microsoft.com/azure/app-service/configure-custom-container)) | Internal TCP ingress between apps without a custom VNet; external TCP needs a VNet ([ingress](https://learn.microsoft.com/azure/container-apps/ingress-overview)) |
| Service-to-service calls | No built-in discovery | Internal ingress, discovery by app name `http://<app>` ([connect apps](https://learn.microsoft.com/azure/container-apps/connect-apps)) |
| Seven independently deployed apps | One plan can host several apps on the same VMs. B1: up to 8 apps, about EUR 12/month per plan ([hosting plans](https://learn.microsoft.com/azure/app-service/overview-hosting-plans); price from the Azure Retail Prices API) | One app per service, each with its own revisions and scale rules, in a workload profiles (v2) environment with the Consumption profile ([environment types](https://learn.microsoft.com/azure/container-apps/structure)) |
| Scaling and idle cost | Fixed plan cost, no scale to zero | Scale to zero costs nothing. Monthly free grant per subscription: 180,000 vCPU-s, 360,000 GiB-s and 2 M requests. One always-on 0.25 vCPU / 0.5 GiB replica costs about $8/month idle and up to about $28/month active ([billing](https://learn.microsoft.com/azure/container-apps/billing)) |
| Scheduled jobs (ML training) | WebJobs | Container Apps jobs with cron schedules ([jobs](https://learn.microsoft.com/azure/container-apps/jobs)) |
| Secrets and images | Key Vault references, managed identity | Key Vault secret references and ACR pull with managed identity ([secrets](https://learn.microsoft.com/azure/container-apps/manage-secrets), [image pull](https://learn.microsoft.com/azure/container-apps/managed-identity-image-pull)) |
| Stateful workloads | Not intended | "Designed for stateless workloads"; persistent data only on Azure Files mounts ([reliability](https://learn.microsoft.com/azure/reliability/reliability-container-apps), [storage](https://learn.microsoft.com/azure/container-apps/storage-mounts)) |

**Choice: Azure Container Apps** for the gateway and the six services. One workload profiles environment with the Consumption profile; internal ingress for the services, external HTTPS ingress only for the gateway. App Service is rejected: it can't host a broker and has no internal service discovery. Its low fixed price was its only advantage.

**Managed free tiers instead of self-hosting** (product owner, 24 November 2026; resolves #66):

| Concern | Production service | Why |
|---|---|---|
| Message broker | **CloudAMQP "Little Lemur"** (free, shared RabbitMQ broker) | Real AMQP 0-9-1 RabbitMQ at no cost. Limits and how the design fits them: [ADR 0017](0017-rabbitmq-integration-event-transport.md) §4. Fallback if a limit is hit: "Tough Tiger", the paid shared plan ($19/month, 10 M messages, 100 connections, 1,000 queues; [plans](https://www.cloudamqp.com/plans.html)). |
| Metrics, logs, traces, dashboards, alerting | **Grafana Cloud Free** (Prometheus-compatible metrics, Loki, Tempo, Grafana) fed by OTLP | The same open stack, managed and free. Limits: [ADR 0018](0018-observability-with-the-grafana-stack.md) §5. |

Locally, Aspire runs RabbitMQ and Prometheus, Loki, Tempo and Grafana as containers. The self-hosted setup is still shown and tested; only production uses the managed tiers.

**Scale to zero, including the gateway.** Every Container App has `minReplicas: 0`:
- The gateway and the services scale on HTTP (default rule).
- Consuming services also scale on their RabbitMQ queues (KEDA `rabbitmq` scaler as a custom rule; polling interval 30 s, cool-down 300 s before going back to zero; [scale rules](https://learn.microsoft.com/azure/container-apps/scale-app)).
- Scheduled work runs as Container Apps jobs with cron triggers, because Quartz doesn't run while an app is at zero: reservation expiry, mart refresh, audit purge, ML training.

**Cold start:** the first request after an idle period waits while the image is pulled and .NET starts ([reducing cold-start time](https://learn.microsoft.com/azure/container-apps/cold-start)). It goes first to the gateway and then to the service it calls, so it can take seconds to tens of seconds. Mitigations:
- small images, with ACR in the same region;
- liveness probes that tolerate start-up;
- **warm-up before demos and the defence:** a GitHub Actions workflow `warm-up.yml` (manual `workflow_dispatch`, plus a weekly cron) calls the gateway's anonymous, rate-limited `GET /warmup`. The gateway calls `/health/ready` on each service over internal ingress, which wakes every app. It is run manually 15 minutes before the defence. The weekly run also keeps the CloudAMQP instance and its queues from being removed as idle (ADR 0017 §4).
- The UI shows a loading state instead of an error while a cold start is in progress.

**Monthly cost estimate (dev/demo environment, West Europe, EUR; the environment is in North Europe since [ADR 0022](0022-azure-region-north-europe.md), whose prices are lower, so this is an upper bound).** Unit prices are from the [Azure Retail Prices API](https://prices.azure.com/api/retail/prices), retrieved 24 November 2026; 730 h per month.

| Item | Size | Unit price | Monthly | Pricing page |
|---|---|---|---|---|
| Container Apps (gateway + 6 services + jobs) | 0.25 vCPU / 0.5 GiB per replica, scale to zero | Free grant per subscription per month: 180,000 vCPU-s, 360,000 GiB-s, 2 M requests ([billing](https://learn.microsoft.com/azure/container-apps/billing)) | **€0** within the grant (about 200 replica-hours a month at this size) | [Container Apps pricing](https://azure.microsoft.com/pricing/details/container-apps/) |
| Container Apps environment logs | Logs destination "Don't save logs"; telemetry goes to Grafana Cloud | — | **€0** (no Log Analytics workspace; [log options](https://learn.microsoft.com/azure/container-apps/log-options)) | — |
| PostgreSQL Flexible Server | Burstable B1ms (1 vCore, 2 GiB) | €0.0179 / hour | **€13.07** | [PostgreSQL pricing](https://azure.microsoft.com/pricing/details/postgresql/flexible-server/) |
| PostgreSQL storage | 32 GiB (smallest size; [compute options](https://learn.microsoft.com/azure/postgresql/compute-storage/concepts-compute)) | €0.1229 / GB-month | **€3.93** | same |
| PostgreSQL backup storage | 7-day retention of a small database | €0.0925 / GB-month | **< €1** | same |
| Azure Container Registry | Basic | €0.1496 / day | **€4.55** | [ACR pricing](https://azure.microsoft.com/pricing/details/container-registry/) |
| Key Vault | Standard, operations only | €0.0269 / 10,000 operations | **< €0.10** | [Key Vault pricing](https://azure.microsoft.com/pricing/details/key-vault/) |
| Blob Storage | Hot LRS, a few GB (imports, ML models, gateway key ring) | €0.0162–0.0176 / GB-month | **< €0.20** | [Blob pricing](https://azure.microsoft.com/pricing/details/storage/blobs/) |
| CloudAMQP | Little Lemur | free | **€0** | [CloudAMQP plans](https://www.cloudamqp.com/plans.html) |
| Grafana Cloud | Free | free | **€0** | [Grafana pricing](https://grafana.com/pricing/) |
| **Total** | | | **≈ €22–23** | |

**Fit with NFR07 (≤ €35/month):** the estimate leaves about €12 of headroom. It assumes:
- Container Apps usage stays within the free grant; the demo and defence pattern of a few hours a day fits it;
- the free tiers' limits hold.

Above the grant, an active 0.25 vCPU / 0.5 GiB replica costs about $0.03 per hour (billing page rates). The first triggers to watch are the PostgreSQL connection limit (B1ms: 35 user connections, [limits](https://learn.microsoft.com/azure/postgresql/configure-maintain/concepts-limits)) and CloudAMQP's quotas. The Azure for Students credit ($100 for 12 months) alone does not cover the PostgreSQL and ACR baseline for a full year.

**PostgreSQL connections:** B1ms allows 35 user connections. Each service sets `Maximum Pool Size=4` and `maxReplicas: 1` in the dev/demo environment, so six services use at most 24, leaving room for jobs and the migrator. More replicas need B2s (414 user connections) or a connection pooler.

**Rejected: a self-hosted "stateful host" VM** for RabbitMQ and the Grafana stack. It would give local-disk semantics, which RabbitMQ and Prometheus prefer and Container Apps' Azure Files mounts lack. The VM is rejected on **cost against the €35/month target**: a B2s Linux VM alone costs €0.0431/hour, about €31.46/month before its managed disk. That is almost the whole budget, and it would run whether or not anyone uses the system. It would also add patching and backups. A B1ms VM (€0.0215/hour) is too small (2 GiB) for RabbitMQ plus four observability components.

**Telemetry:** OpenTelemetry → Grafana Cloud ([ADR 0018](0018-observability-with-the-grafana-stack.md)) replaces Application Insights as the primary target.

## Amendment (2026-11-25): migrations

[ADR 0020](0020-database-migrations-through-the-migration-runner-job.md) replaces "EF migration bundle as a pipeline step" with the MigrationRunner image run as a manual Container Apps job before each service's new revision: one tested program for bootstrap, catalogue order and migrations, a `--module` filter for per-service deployments, and no database access from GitHub runners. The comparison is in [STAGE_4_PLAN.md](../stage-4/STAGE_4_PLAN.md), section 8.4.
