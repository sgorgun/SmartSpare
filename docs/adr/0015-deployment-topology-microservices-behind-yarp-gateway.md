# ADR 0015: Deployment topology: microservices behind a YARP gateway

- Status: Accepted
- Date: 2026-11-24
- Amends: [ADR 0002](0002-modular-monolith.md), [ADR 0006](0006-frontend-blazor-wasm-mudblazor-bff.md), [ADR 0009](0009-azure-hosting-and-delivery.md)

## Context

ADR 0002 builds a microservice-ready modular monolith. Each module has its own projects, schema, DbContext and Contracts, and modules talk through Outbox/Inbox events. The plan was to split it after the defence. The backend grading rubric awards its maximum score for microservices that deploy independently ([BACKEND_REQUIREMENTS.md](../architecture/BACKEND_REQUIREMENTS.md#2-maximum-requirements), #56). On 24 November 2026 the product owner decided (D1) that production runs every module as its own service, from the next stage on.

Constraints:

- business code must not fork between topologies;
- local development and the test suite must stay as simple as they are today;
- module boundaries are already enforced by architecture tests.

## Decision

### 1. Two topologies, one codebase

| | Single host (development, tests, CI) | Services (production) |
|---|---|---|
| Processes | `SmartSpare.Host` with all six modules | `SmartSpare.Gateway` + six service hosts `SmartSpare.Services.<Module>` |
| Integration events | In-process transport behind Outbox/Inbox | RabbitMQ behind Outbox/Inbox ([ADR 0017](0017-rabbitmq-integration-event-transport.md)) |
| Contracts queries | In-process implementation of the owning module | HTTP client to the owning service's `/internal/v1/...` endpoint |
| Browser authentication | Cookie in the host | Cookie in the gateway; services accept an internal token ([ADR 0016](0016-authentication-behind-the-gateway.md)) |
| Telemetry | OTLP → Aspire dashboard (the self-hosted Grafana stack under `--topology services`) | OTLP → Grafana Cloud Free ([ADR 0018](0018-observability-with-the-grafana-stack.md)) |

**Module projects are identical in both topologies.** Only the composition root differs. The topology is chosen by the host project and its configuration, never by module code. The shared composition helpers in BuildingBlocks are used by every host:

- installing a module;
- the transport registration;
- the authentication schemes;
- the Contracts clients.

An architecture test fails if a module references YARP, RabbitMQ.Client or a host project.

### 2. Services

| Service | Module and schemas | Public routes (through the gateway) | Background work |
|---|---|---|---|
| `gateway` | — (no database) | `/` (Blazor WebAssembly client), `/bff/*`, `/openapi/*`, `/scalar` | — |
| `identity` | Identity: `iam` | `/api/v1/identity/*` | — |
| `inventory` | Inventory: `catalog`, `inventory`, `raw`, `staging` | `/api/v1/inventory/*` | Import pipeline (in request); reservation expiry as a scheduled job |
| `fleet` | Fleet: `fleet` | `/api/v1/fleet/*` | — |
| `procurement` | Procurement: `procurement` | `/api/v1/procurement/*` | — |
| `forecasting` | Forecasting: `forecast` | `/api/v1/forecasting/*` | ML training as a scheduled job |
| `reporting` | Reporting: `mart`, plus Platform `audit` jobs | `/api/v1/reporting/*` | Mart refresh, audit purge as scheduled jobs |

- The Platform (`ref`, `util`, `audit`) remains a shared kernel installed by the migrations. It is not a service.
- Each service runs its own outbox dispatcher and inbox consumers.
- **Scheduled work runs as Container Apps jobs** (cron) from the owning service's image, because every app scales to zero (ADR 0009 amendment) and an in-process scheduler doesn't run at zero. In the single host the same work runs as Quartz jobs (clustered, `SKIP LOCKED`). The job code is shared; only the trigger differs.

### 3. Gateway (YARP)

`SmartSpare.Gateway` uses YARP (`Yarp.ReverseProxy` 2.3.0, MIT) and is the BFF of [ADR 0006](0006-frontend-blazor-wasm-mudblazor-bff.md):

- hosts the Blazor WebAssembly client with global InteractiveWebAssembly prerendering;
- owns the HttpOnly cookie, antiforgery and the sign-in rate limit;
- proxies the routes below.

| Route | Destination |
|---|---|
| `/api/v1/{module}/{**rest}` | cluster `{module}` → `http://{module}` (Container Apps internal ingress, service discovery by app name) |
| `/openapi/{module}.json` | the service's OpenAPI document ([ADR 0019](0019-public-api-documentation.md)) |
| `/bff/*`, `/scalar`, client assets | handled by the gateway itself |
| `/internal/*`, `/health/*` of services | **never routed** |

Request transforms:
- Remove `Cookie` and any client-supplied `Authorization` header, then add the internal bearer token (ADR 0016).
- Rely on YARP's defaults to replace spoofable `X-Forwarded-*` headers ([YARP header guidelines](https://learn.microsoft.com/aspnet/core/fundamentals/servers/yarp/header-guidelines?view=aspnetcore-10.0)).

Services use internal ingress only and aren't reachable from the internet. `/internal/*` is never routed by the gateway; a route-table test asserts that no YARP route matches it ([ADR 0016](0016-authentication-behind-the-gateway.md) §2).

### 4. Synchronous Contracts queries become HTTP clients

The read-only query interfaces in `*.Contracts` (ADR 0003) get two implementations:

- **In-process:** registered by the owning module's installer, used in the single host.
- **HTTP:** in the Contracts project, calling the owning service's `/internal/v1/...` endpoints, used by the consuming service.

The HTTP client uses the standard resilience handler of ServiceDefaults and propagates the W3C trace context and the caller's internal token. Rules:

- Contracts queries are called only while handling a user request.
- Event consumers and jobs work from their own data, including the deliberate copies kept up to date by events (ARCHITECTURE_DECISIONS §4). This keeps system-to-system calls free of user tokens.
- `/internal` endpoints are excluded from the public OpenAPI documents.

### 5. Data and database logins

- One Azure Database for PostgreSQL Flexible Server, one database, a schema per module ([ADR 0011](0011-database-ownership-per-module.md)). No change to the data model.
- **One database login per service:** `smartspare_identity`, `smartspare_inventory`, `smartspare_fleet`, `smartspare_procurement`, `smartspare_forecasting`, `smartspare_reporting`, plus `smartspare_migrator` for migrations.
  - Locally the logins use SCRAM passwords from user-secrets.
  - In Azure each service has its own Managed Identity mapped to an Entra login, so there are no passwords (#16).
  - Until the per-module roles of MODULE_MAPPING §4.2 replace them, the logins are members of the existing capability roles (`app_write`; `app_ml` for Forecasting; `app_read` and `app_audit` for Reporting). The per-module roles can only be enforced once the transitional cross-module SQL is gone (stages 6–8).
  - The gateway has no database login.
- **Connection budget:** PostgreSQL B1ms allows 35 user connections ([limits](https://learn.microsoft.com/azure/postgresql/configure-maintain/concepts-limits)), so each service caps its pool (`Maximum Pool Size=4`) and runs `maxReplicas: 1` in the dev/demo environment.
- The single host keeps its current logins (`smartspare_api`, `smartspare_jobs`, `smartspare_reporting`).
- Each service applies only its own module's EF migration bundle, under `smartspare_migrator`, before its new revision starts. Migrations are already per module (`<schema>.__ef_migrations_history`).

### 6. Independent deployment

- One container image per service, built with the SDK's container publishing ([Containerize a .NET app with dotnet publish](https://learn.microsoft.com/dotnet/core/containers/sdk-publish)). Images are tagged with the commit SHA and pushed to Azure Container Registry.
- GitHub Actions builds and deploys only the services whose files changed:
  - path filters per service and its module;
  - a BuildingBlocks or Contracts change triggers its dependants.

  Each service deploys with its own Container Apps revision, after the shared CI gates.
- Compatibility rules, so one service can change without the others:
  - HTTP contracts are versioned (`/api/v1`, ADR 0012);
  - internal query endpoints and integration events change only backward-compatibly, or under a new version (ADR 0012);
  - the gateway routes by module, so adding endpoints needs no gateway change.
- Hosting is Azure Container Apps. **Every app scales to zero, the gateway included**, and wakes on HTTP; consuming services also wake on their RabbitMQ queue length. The comparison with App Service, the cold-start mitigation (warm-up workflow before demos) and the cost estimate are in the amendment of [ADR 0009](0009-azure-hosting-and-delivery.md).

### 7. Local development and tests

- `dotnet run --project src/SmartSpare.AppHost` keeps starting the single host. An AppHost option `--topology services` starts the gateway, the six services, RabbitMQ and the Grafana stack as Aspire resources, so the production topology can run locally.
- Unit and integration tests run against the single host.
- One CI topology test starts the gateway, two services and RabbitMQ (Testcontainers) and runs an event round trip and an authenticated request through the gateway.

## Alternatives considered

| Alternative | Why not |
|---|---|
| Keep the modular monolith in production (ADR 0002 as written) | Cheapest and simplest, but it doesn't meet the rubric's maximum score, and the product owner decided against it. |
| Partial split (for example only Forecasting as a service, the rest in one host) | Fewer deployables, but two different production shapes to secure and operate, and it still needs the gateway, broker and token flow. The product owner chose all modules. |
| Separate repositories or code per service | Duplicates BuildingBlocks and loses the single-host development mode. Independent deployment comes from images and pipelines, not from repositories. |
| Kubernetes (AKS) or a service mesh / Dapr sidecars | More capability than seven services need, plus cluster operations and cost. Container Apps gives internal ingress, revisions, jobs and scaling as a managed service (Kubernetes is #23, future). |

## Consequences

- The rubric's microservice, independent-deployment and messaging items can be met. Every module scales and deploys on its own.
- **Cost stays within NFR07 by design, with limits.** Seven apps at zero replicas cost nothing when idle, and messaging and observability use the free CloudAMQP and Grafana Cloud plans. The estimate is about €22–23/month, mostly PostgreSQL and ACR (ADR 0009 amendment). The price is cold starts and the free plans' limits, which ADR 0017 §5 and ADR 0018 §5 design for. The cost question is settled in #66.
- More failure modes: network hops, partial outages, version skew. These are mitigated by resilience handlers, the outbox (no lost events), traces across services (ADR 0018) and backward-compatible contracts.
- One more hop per API call (gateway → service). After idle periods there are cold starts on the gateway and then on the called service, mitigated by the warm-up workflow before demos.
- Developers keep a one-process debugging experience. The topology itself is tested in CI and can run locally on demand.
- A new work-plan stage (stage 4: topology) is inserted before the business modules, and the following stages shift by one.
