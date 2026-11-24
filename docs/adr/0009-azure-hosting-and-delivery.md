# ADR 0009: Azure hosting and delivery

- Status: Accepted, amended by [ADR 0015](0015-deployment-topology-microservices-behind-yarp-gateway.md), [ADR 0017](0017-rabbitmq-integration-event-transport.md) and [ADR 0018](0018-observability-with-the-grafana-stack.md) (2026-11-24)
- Date: 2026-11-16

## Context

The system is deployed to Microsoft Azure and must be able to move to microservices later.

## Decision

Ship container images from day one. Production, since the amendment below:
- Azure Container Apps, with one app per service plus the gateway;
- PostgreSQL Flexible Server 17 (Burstable), Blob Storage, Key Vault and Azure Container Registry;
- a small stateful-host VM for RabbitMQ and the Grafana observability stack;
- one Managed Identity and database login per service.

The original plan (App Service with one container, and Application Insights) is superseded; see the amendment. Infrastructure in Bicep (`infra/`), CI/CD with GitHub Actions signing in via OIDC, EF migration bundle as a pipeline step. Background jobs are multi-instance safe (Quartz clustered, `SKIP LOCKED`). .NET Aspire is used for local development only.

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

**Choice: Azure Container Apps** for the gateway and the six services. One workload profiles environment, Consumption profile, internal ingress for the services and external HTTPS ingress only for the gateway.

RabbitMQ and the Prometheus, Loki, Tempo and Grafana containers run on **a small Linux VM with a managed disk (the "stateful host")** in the environment's virtual network. Both RabbitMQ and Prometheus need local-disk semantics, which Azure Files mounts don't provide.

App Service is rejected: it can't host the broker and has no internal service discovery. Its low fixed price was its only advantage.

**Telemetry:** OpenTelemetry → Grafana stack ([ADR 0018](0018-observability-with-the-grafana-stack.md)) replaces Application Insights as the primary target.

**Cost.** The always-on production topology exceeds NFR07 (≤ EUR 35/month) and the Azure for Students credit ($100 for 12 months). It consists of the gateway with one replica minimum, the services, the stateful VM and PostgreSQL Burstable.

Cost controls:
- module services scale to zero (with a cold start on the first request);
- the gateway keeps one replica;
- the VM is a burstable size and is deallocated outside demo periods;
- the whole environment is created and torn down by Bicep;
- Grafana Cloud Free and CloudAMQP are documented fallbacks.

The trade-off is for the product owner to decide, tracked in #66.
