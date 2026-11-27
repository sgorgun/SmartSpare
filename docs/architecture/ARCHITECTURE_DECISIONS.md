# SmartSpare — Architecture Decisions

Status: **approved** (16 November 2026). **Amended** 24 November 2026 by the topology decisions D1–D4 of the product owner (ADRs 0015–0019). Stages 1–6 are implemented (release v0.6.0, 27 November 2026; see the [CHANGELOG](../../CHANGELOG.md)); stage 4 built and tested the services topology locally and in CI, and its first Azure deployment is stage 10 (#85).

This is the architecture of SmartSpare. It superseded the database-stage architecture note, which was removed (it remains in the Git history). Individual decisions are recorded as ADRs in [`docs/adr/`](../adr/README.md).

## Summary

SmartSpare is **one codebase with two topologies**:
- **Development and tests:** a modular monolith, with all six modules in one host.
- **Production:** every module runs as its own independently deployable service behind a YARP gateway that is the BFF.

Business code is identical in both. Only the composition roots differ.

| # | Decision | In short | ADR |
|---|---|---|---|
| 1 | Style | Modular codebase: each module has its own projects, PostgreSQL schema and DbContext | [0002](../adr/0002-modular-monolith.md) |
| 2 | Deployment topology | Production: gateway + six services on Azure Container Apps. Development and tests: single host | [0015](../adr/0015-deployment-topology-microservices-behind-yarp-gateway.md) |
| 3 | Module boundaries | No cross-module FKs, JOINs or shared transactions; modules talk only through Contracts and events | [0003](../adr/0003-module-boundaries-and-integration-events.md) |
| 4 | Integration | Integration events via Outbox/Inbox; RabbitMQ between services, in-process in the single host; events carry the acting user for attribution only, jobs act as a named system user | [0003](../adr/0003-module-boundaries-and-integration-events.md), [0017](../adr/0017-rabbitmq-integration-event-transport.md), [0024](../adr/0024-acting-user-of-integration-events-and-jobs.md) |
| 5 | Inside a module | Vertical slices, DDD tactical patterns, CQRS-lite, Result pattern | [0002](../adr/0002-modular-monolith.md) |
| 6 | Handlers | Own lightweight interfaces and decorators instead of MediatR | [0005](../adr/0005-own-handlers-instead-of-mediatr.md) |
| 7 | Data | Hybrid EF Core code-first: tables in Fluent API, triggers and functions via `migrationBuilder.Sql`; in Azure, migrations run as a MigrationRunner job per service | [0004](../adr/0004-hybrid-ef-core-code-first.md), [0020](../adr/0020-database-migrations-through-the-migration-runner-job.md) |
| 8 | Frontend | Blazor Web App (global InteractiveWebAssembly) + MudBlazor 9.x; the BFF with the HttpOnly cookie is the YARP gateway in production and the host in development | [0006](../adr/0006-frontend-blazor-wasm-mudblazor-bff.md) |
| 9 | API contracts | Shared `SmartSpare.ApiContracts` project with DTOs and FluentValidation validators | [0006](../adr/0006-frontend-blazor-wasm-mudblazor-bff.md) |
| 10 | Platform | .NET 10 LTS | [0007](../adr/0007-dotnet-10-lts.md) |
| 11 | Machine learning | ML.NET: gradient boosting + intermittent-demand baselines | [0008](../adr/0008-mlnet-demand-forecasting.md) |
| 12 | Azure | Container Apps for the gateway and services, all scaling to zero; CloudAMQP (free) for RabbitMQ; Bicep, GitHub Actions with OIDC; about €22–23/month | [0009](../adr/0009-azure-hosting-and-delivery.md) |
| 13 | Observability | OpenTelemetry → Grafana Cloud Free (Prometheus-compatible metrics, Loki, Tempo, Grafana; alerts to Telegram and e-mail); the self-hosted stack and the Aspire dashboard locally | [0018](../adr/0018-observability-with-the-grafana-stack.md) |
| 14 | Testing | Unit, integration with Testcontainers, architecture tests, a topology test, bUnit, Playwright | — |
| 15 | Repository language | English only | [0010](../adr/0010-english-only-repository.md) |
| 16 | Evolvability | Abstractions only at seams to volatile technology; stable contracts (Contracts, versioned HTTP API, events); claims-based authorization | [0012](../adr/0012-evolvability-principles.md) |
| 17 | Domain model and persistence | Aggregates in `Domain/` mapped directly by EF Core; stage 2 table mirrors removed per module stage; no generic repository | [0013](../adr/0013-domain-model-and-persistence.md) |
| 18 | Browser sign-in | WASM sign-in page posts to `/bff` endpoints; ASP.NET Core Identity with custom stores over `iam`; antiforgery on every unsafe request. **Implemented** in stage 3, v0.3.0 (server side PR 3b, WASM client and sign-in page PR 3d, browser smoke test PR 3e) | [0014](../adr/0014-browser-sign-in-through-bff-endpoints.md) |
| 19 | Authentication behind the gateway | The BFF issues the cookie after the Identity module checks credentials; services receive a 2-minute ES256 internal token; one principal in both topologies. Single-host part **implemented** in stage 3, v0.3.0 (PRs 3b–3c: internal tokens, policy scheme, session context, shared key ring in `iam.data_protection_keys`); gateway and services **implemented** in stage 4, v0.4.0 (PRs 4d–4e, topology test 4g) | [0016](../adr/0016-authentication-behind-the-gateway.md) |
| 20 | API documentation | OpenAPI per service + Scalar UI, published without sign-in; endpoints still require authorization | [0019](../adr/0019-public-api-documentation.md) |

## 1. Architectural style

The code is a **modular monolith**:
- six modules with strict boundaries, in one repository and one solution;
- they run in one process during development and tests;
- they run as **six services behind a gateway** in production ([ADR 0015](../adr/0015-deployment-topology-microservices-behind-yarp-gateway.md)).

One process keeps debugging and testing cheap. The deployed services give independent deployment and scaling, plus a real broker. The boundaries make both possible from the same code.

Rules that keep the modules independent:

1. **A module owns its data.** Each module has its own PostgreSQL schema, DbContext and migrations history table. Nobody reads another module's schema.
2. **No cross-module FKs, JOINs or transactions.** A reference to another module's entity is just an identifier; integrity is enforced by code and events. All FKs inside a module stay.
3. **The public surface is `*.Contracts` only.** It holds integration events and narrow query interfaces. Everything inside a module is `internal`.
4. **Modules communicate asynchronously.** State in another module changes only through events. A synchronous call is allowed only for reads, through an interface from Contracts.
5. **Boundaries are checked automatically.** Architecture tests fail if a module references another module's implementation, or if it references a topology concern (YARP, RabbitMQ.Client, a host project).

**Two topologies.**

| | Single host | Services |
|---|---|---|
| Processes | `SmartSpare.Host` | `SmartSpare.Gateway` + `SmartSpare.Services.<Module>` × 6 |
| Events | In-process transport | RabbitMQ |
| Contracts queries | In-process | HTTP to `/internal/v1/...` of the owning service |
| User identity | Cookie | Cookie in the gateway, internal token to services |
| Used for | Development, unit and integration tests, CI | Production; locally on demand (`--topology services`) and one CI topology test |

## 2. Modules

Six business modules plus a shared platform. The schemas of the original database map onto modules almost unchanged. The supplier tables moved from `catalog` to `procurement`, and the cross-module foreign keys were removed (ADR 0011, implemented in stage 2; every object is mapped in [MODULE_MAPPING.md](../database/MODULE_MAPPING.md)).

| Module | DB schemas | Responsible for | Scenarios | Service in production |
|---|---|---|---|---|
| Identity | `iam` | Users, roles, brand assignments, credential checks, GDPR anonymisation | All | `identity` |
| Inventory | `catalog`, `inventory`, `raw`, `staging` | Parts catalogue, warehouses and routes, stock ledger, reservations, transfers, data import | UC01, import | `inventory` |
| Fleet | `fleet` | Customer machines, engine hours, maintenance plans, service orders | UC02 | `fleet` |
| Procurement | `procurement` (incl. `supplier`, `supplier_part`, `supplier_price`) | Suppliers and prices, purchase orders, four-eyes rule | Purchasing | `procurement` |
| Forecasting | `forecast` | ML models, forecasts, recommendations and decisions on them | Recommendations | `forecasting` (+ training job) |
| Reporting | `mart` | Data marts, KPIs, dashboards; read-only | Reports | `reporting` |
| Platform (BuildingBlocks) | `ref`, `audit`, `util` | Reference data, audit, masking, Outbox/Inbox, shared abstractions | — | Shared kernel, not a service |

Each module also keeps:
- its EF migration history (`<schema>.__ef_migrations_history`);
- where it publishes or consumes integration events, its `outbox_message` / `inbox_message` in its own schema.

The former `meta` schema of the SQL migration runner is retired.

Reporting is the one deliberate exception: it reads other modules' data through marts and materialized views, as an analytical store does in real systems. All services share one PostgreSQL database with a schema per module, so this keeps working in production. The marts move to event-fed tables only if Reporting ever gets its own database.

## 3. Inside a module

Every module has the same shape and consists of two projects: the implementation and `Contracts`. The three Clean Architecture layers live in folders inside one project and are enforced by architecture tests — simpler than four projects per module.

- **Domain** — aggregates, value objects, domain events, invariants. Examples: `TransferOrder` with its state machine, `PurchaseOrder` with the four-eyes rule, `Quantity` and `Money` as value objects. No references to EF or ASP.NET.
- **Features (vertical slices)** — one folder per use case: `ApproveTransfer/` holds the command, handler, validator and endpoint. Changing one use case touches one folder.
- **Infrastructure** — the module's DbContext, EF configurations, migrations, handlers for incoming events, and the data access of the use cases: read models and query handlers that only read (raw SQL or EF projections), and the SQL behind commands (SECURITY DEFINER functions, bulk loads). Features call it but never run SQL themselves; the unit-test metric of business logic covers Domain and Features (#63, architecture test `CoverageDefinitionTests`).

**Domain services.** A rule that is a calculation over several inputs, rather than a state change of one aggregate, is a pure domain service in `Domain/`, unit-tested on its own: Fleet's `MaintenanceDueCalculator` (next threshold, usage, projected and calendar due dates, status) is documented in [MAINTENANCE_PLANNING.md](MAINTENANCE_PLANNING.md) and agrees with `fleet.fn_maintenance_due` on the demo fleet.

**CQRS-lite.** Commands go through aggregates and EF Core; queries read straight into DTOs (EF projections or database views). One database, no separate read stores.

**Result pattern.** Expected business errors (insufficient stock, invalid status transition) are returned as `Result<T>` and mapped to ProblemDetails; exceptions are only for the unexpected. Database codes SS100–SS402 map to the same errors via the existing `DatabaseErrorMapper`.

**Own handlers instead of MediatR.** `ICommandHandler<TCommand, TResult>` and `IQueryHandler<TQuery, TResult>` interfaces plus decorators for validation, logging and transactions — about 100 lines of code. Reasons for not using MediatR: it is commercially licensed from version 13, and explicit code is easier to explain at the defence.

## 4. Module integration

Modules change each other only through **integration events** with guaranteed delivery via Outbox/Inbox. The single host delivers them in process. Between services they travel through **RabbitMQ** ([ADR 0017](../adr/0017-rabbitmq-integration-event-transport.md)). Publisher and subscriber code is the same in both.

- **Outbox.** An event is written to the module's `outbox_message` table in the same transaction as the data change. A background dispatcher picks up batches with `FOR UPDATE SKIP LOCKED` and publishes them. With RabbitMQ, `processed_at` is set only after the publisher confirm.
- **Inbox.** The receiver records the id of each processed event in `inbox_message`, so redelivery is safe (idempotency).
- **RabbitMQ topology.**
  - One topic exchange per publishing module, with routing key `<Event>.v<N>`.
  - One quorum queue per consumer, using native delayed retry.
  - One dead-letter queue per consuming module, with an alert on dead letters.
  - Designed within the free CloudAMQP plan's limits: one connection per service, ≤ 100 queues, 1 M messages a month (ADR 0017 §5).
- **Synchronous queries** are read-only and go only through an interface in `Contracts` (for example, `IStockQueries`):
  - in-process in the single host;
  - between services, an HTTP client to the owning service's `/internal/v1/...` endpoint, carrying the caller's internal token.

  They are used only while handling a user request. Consumers and jobs use their own data.
- **Deliberate duplication.** A module keeps a copy of the foreign data it needs (for example, Fleet keeps part number and name for maintenance kits), updated by events.

**Example: UC01 (transfer based on a recommendation).**

1. Forecasting creates a recommendation to move 11 units from KLP to PNV; the planner accepts it.
2. Forecasting publishes `TransferRequested`.
3. Inventory creates a draft `TransferOrder` and reserves stock — all in its own transaction.
4. After approval, Inventory publishes `TransferApproved`.
5. Forecasting marks the recommendation as fulfilled. If the stock is gone, `TransferRejected` arrives and the recommendation goes back to open (compensating action).

**Example: UC02 (service kit for a maintenance order, stage 6, #12).**

1. The engineer logs engine hours; Fleet drafts the service order and writes `ServiceOrderDrafted` in the same transaction.
2. Inventory reserves the kit at the machine's home hub (partially if stock is short, in `(part, hub)` lock order) and answers `ServiceKitReserved`; Fleet stores the held quantities. Until then the order shows "Reserving parts…".
3. Completing the order writes `ServiceOrderCompleted`; Inventory issues the reserved quantities and what stock allows (the shortfall is reported, not refused) and answers `ServiceKitIssued`.
4. Cancelling writes `ServiceOrderCancelled` and Inventory releases the holds; a reservation that arrives after the order was closed is released at once through `ServiceKitReleaseRequested`. Holds not used within 7 days expire (`ServiceKitReservationExpired`).

Consumers act as the user who caused the event, for attribution only; authorization happened when the event was published, and jobs act as a named system user ([ADR 0024](../adr/0024-acting-user-of-integration-events-and-jobs.md)).

Invariants within one module (stock never goes negative, statuses follow the rules) stay strict and transactional; eventual consistency applies only between modules.

## 5. Data layer: hybrid code-first

We move to **EF Core code-first** and port the already-tested schema into it. C# describes the tables; whatever EF cannot express stays as SQL inside migrations. This keeps both the proper .NET approach and every point of the database requirements rubric.

| What | Where it is defined |
|---|---|
| Tables, keys, intra-module FKs, indexes, CHECKs, enum types | Fluent API (`IEntityTypeConfiguration<T>`) |
| Triggers, functions, views and marts, roles and grants, EXCLUDE constraints | `migrationBuilder.Sql(...)` with embedded .sql files |
| Partitioned tables (`stock_movement`, `audit_log`) | DDL in SQL, `ExcludeFromMigrations()` in the EF model |
| snake_case names | `EFCore.NamingConventions` package |
| Reference data | Idempotent seed during migration |

Rules:

- **Migrations per module.** Each DbContext keeps its history in its own schema (`inventory.__ef_migrations_history`). In production each service applies only its own module's migrations before its new revision starts, through the MigrationRunner job (`--module`, ADR 0020).
- **Business rules in the domain, protection in the database.** Triggers and CHECKs remain the last line of defence (stock ledger, stock row locks, audit), protecting data from both application bugs and direct database edits.
- **Session context.** `SessionContextInterceptor` passes the signed-in user's id, roles and home warehouse into `app.user_id`, `app.user_roles` and `app.home_warehouse_id` (from the claims, whichever scheme authenticated the request), so the audit knows who changed what and the SQL role and warehouse checks need no cross-module read (MODULE_MAPPING 3.6).
- **Resilience.** `EnableRetryOnFailure` for Azure, optimistic concurrency via `xmin`, `AsNoTracking` for queries.
- **Production rollout** runs the MigrationRunner image as a one-shot Container Apps job under the dedicated `smartspare_migrator` login ([ADR 0020](../adr/0020-database-migrations-through-the-migration-runner-job.md)). The application never migrates on startup.
- **Database logins.**
  - The single host uses `smartspare_api`, `smartspare_jobs` and `smartspare_reporting`.
  - The services topology uses one login per service (`smartspare_identity` … `smartspare_reporting`); in Azure these are Managed Identities via Entra.
  - Per-module roles follow in stage 10 (MODULE_MAPPING §4.2, #16).
- **Port verification.** The schema produced by EF migrations is diffed against the frozen V016 schema (`db/baseline/v016_schema.sql`, `tools/schema_diff.sh`), and all 97 existing SQL tests, the concurrency test and the demo dataset run on the EF-built database in CI (`tools/verify_database.sh`).

Consequence for the existing database: cross-module FKs are removed, and the custom `SmartSpare.DbMigrator` runner and the V-scripts are replaced by EF migrations (done in stage 2).

## 6. Frontend

The frontend is **Blazor WebAssembly with MudBlazor**, as stated in the topic application and the BA. Sign-in follows the **BFF pattern with an HttpOnly cookie**: tokens never reach the browser, so XSS cannot steal them.

- **Template** — .NET 10 Blazor Web App with global InteractiveWebAssembly render mode. The whole UI runs in the browser; global interactivity is required because MudBlazor does not support static rendering.
- **MudBlazor 9.x** — fully supports .NET 10 and is actively maintained.
- **BFF** — the host that serves the client and owns the cookie:
  - in production, the **YARP gateway** (`SmartSpare.Gateway`), which proxies `/api/*` to the services;
  - in development, `SmartSpare.Host`.

  Both use the same `SmartSpare.Bff` building block ([ADR 0015](../adr/0015-deployment-topology-microservices-behind-yarp-gateway.md), [ADR 0016](../adr/0016-authentication-behind-the-gateway.md)). The frontend is the same in both topologies.
- **Sign-in flow** — the MudBlazor sign-in page posts JSON to `/bff/login`. The BFF asks the Identity module (`ICredentialVerifier`, custom Identity stores over `iam`) to check the credentials, then issues the cookie ([ADR 0014](../adr/0014-browser-sign-in-through-bff-endpoints.md), [ADR 0016](../adr/0016-authentication-behind-the-gateway.md); plan in [STAGE_3_PLAN.md](../stage-3/STAGE_3_PLAN.md)). Implemented in stage 3 and covered end to end by a Playwright test in Chromium (`tests/SmartSpare.E2E`).
- **Structure per module** — `Features/Fleet`, `Features/Inventory`, `Features/Procurement`, `Features/Forecasting`, `Features/Identity`: pages, components and a typed HTTP client per module.
- **`SmartSpare.ApiContracts`** — shared DTOs of the public HTTP API and FluentValidation validators, the same for the form and the server. The frontend never references the modules' internal `*.Contracts`.
- **API documentation** — OpenAPI per module and a Scalar UI at `/scalar`, published without sign-in ([ADR 0019](../adr/0019-public-api-documentation.md)).
- **Errors** — ProblemDetails with SS1xx–SS4xx codes become readable messages in MudSnackbar.
- **State** — plain scoped services, no Fluxor or Redux.
- **Responsiveness (NFR05)** — MudBlazor grid, mobile machine card for the engineer's tablet (WF03).
- **Accessibility** — WCAG 2.1 AA: contrast, ARIA labels, icons next to colour-coded statuses.
- **Localisation** — `IStringLocalizer` and resx files.
- **PWA for field work** — not now; recorded as a future direction.

**Platform and alternatives.** Target version is **.NET 10 LTS** (supported until November 2028); .NET 11 is an STS release and we do not move to it. Considered and rejected: Fluent UI Blazor v5 (fewer ready-made components for a data-heavy admin UI), commercial Telerik, DevExpress and Syncfusion (paid), InteractiveServer and Auto render modes (server state and SignalR hinder scaling and splitting into services).

## 7. Security

Security is layered: browser → BFF → services → database, and each layer checks permissions on its own.

- **Authentication** ([ADR 0016](../adr/0016-authentication-behind-the-gateway.md); the single-host part is implemented since stage 3, the gateway part since stage 4):
  - an HttpOnly, SameSite=Strict cookie between the browser and the BFF;
  - between the gateway and the services, a 2-minute ES256 internal token. Only the gateway holds the private key; services hold only the public key.
  - The browser never holds a token. Services are reachable only through internal ingress.
- **Authorisation** — ASP.NET Core policies by role, plus a brand check on approval actions, on every endpoint. They are claims-based, so they are identical in both topologies. The same role rules are duplicated in the database (`util.require_role`, SECURITY DEFINER functions).
- **Database login per service and role per module** — each service connects with its own login. The per-module roles (stage 10) then let each module see only its own schema. This is both protection and an enforcement of module boundaries at the PostgreSQL level.
- **Secrets** — no passwords in code or configuration:
  - in Azure, Managed Identity per service and Microsoft Entra authentication to PostgreSQL;
  - everything else (token signing key, CloudAMQP URL, Grafana Cloud token, alerting tokens) in Key Vault;
  - locally, user-secrets.
- **Personal data** — already in the database: customer pseudonymisation, masking, GDPR anonymisation, immutable audit with 180-day retention. Telemetry is filtered so it carries no personal data (ADR 0018).
- **Web protection** — in the BFF: HTTPS and HSTS, CSP headers, antiforgery, rate limiting on sign-in and import, forwarded headers from the platform's proxy only (#48), upload size limits.
- **Supply chain** — Dependabot, NuGet Audit, container image scanning in CI.

## 8. Azure and delivery

Production runs on **Azure Container Apps** ([ADR 0009](../adr/0009-azure-hosting-and-delivery.md), amended; App Service was compared there and rejected). All infrastructure is defined as code.

| Component | Production |
|---|---|
| Gateway (BFF) | Container Apps, external HTTPS ingress, scales to zero (warm-up before demos) |
| Six module services | Container Apps, internal ingress, scale to zero; consumers also wake on RabbitMQ queue length (KEDA) |
| Scheduled work | Container Apps jobs (cron) from the owning service's image: reservation expiry, mart refresh, audit purge, ML training, broker metrics |
| Database | PostgreSQL Flexible Server 17, Burstable; one database, schema per module, login per service |
| Events | RabbitMQ on CloudAMQP "Little Lemur" (free shared plan; ADR 0017) |
| Telemetry | OTLP → Grafana Cloud Free (ADR 0018); no Log Analytics workspace |
| Import files, model files, gateway Data Protection keys | Blob Storage |
| Secrets | Key Vault + Managed Identity per service |
| Images | Azure Container Registry, one image per service |

- **IaC** — Bicep modules in `infra/`, dev and prod environments via parameters.
- **CI/CD** — GitHub Actions:
  - one shared quality gate (build, tests, analysis);
  - then, per changed service: image to ACR → the MigrationRunner job for that module (ADR 0020) → new Container Apps revision.

  Azure sign-in via OIDC (federated credentials), no long-lived secrets. Each service deploys independently.
- **Background jobs** — Quartz in the single host; in production, Container Apps jobs with cron triggers, because apps scale to zero. The job code is the same. The outbox is dispatched by the publishing service while it is awake (`SKIP LOCKED`, safe with several replicas).
- **Local development** — .NET Aspire. By default it starts PostgreSQL, the Blob emulator, the single host and the dashboard. `--topology services` additionally starts the gateway, the six services, RabbitMQ and the Grafana stack. Aspire is not used to deploy to Azure — Bicep is.
- **Health** — `/health/live` and `/health/ready` on every service for Container Apps probes. They are not routed by the gateway.
- **Cost** — about €22–23/month (PostgreSQL B1ms and ACR Basic; Container Apps within the free grant; CloudAMQP and Grafana Cloud free), within NFR07 (≤ €35/month). The table, the cold-start trade-off and the rejected self-hosted VM are in the ADR 0009 amendment (#66, closed).

## 9. Demand forecasting (ML.NET)

ML is built with **ML.NET 5.x** inside the Forecasting module: one language, one deployment, no separate Python service. ML.NET's built-in time-series forecasting offers a single algorithm (SSA), which handles intermittent spare-parts demand poorly, so the core model is gradient-boosted regression.

| Model | Role | Implementation |
|---|---|---|
| Gradient boosting (one global model for all SKUs) | Primary | ML.NET LightGBM / FastTreeTweedie; features: lags, rolling means, calendar, engine hours, demand class |
| Croston, SBA, TSB | Baselines for intermittent demand | Own C#, a few dozen lines each |
| Seasonal naive, SSA | Baselines for regular demand | Own C# / ML.NET `ForecastBySsa` |

- **Model selection by demand class** — based on the existing `fn_classify_demand` and `benchmark_result`, evaluated on a chronological holdout.
- **Model lifecycle** — model file in Blob Storage, version and metrics in `ml_model_version`, promotion via `promote_model`.
- **Training** — a background job under the `app_ml` role in the single host; a Container Apps job in production. Inference via `PredictionEnginePool` in the Forecasting service.
- **Risk** — the time-series package depends on Intel MKL, so the image is built for linux-x64.

## 10. Observability

Every host emits OpenTelemetry logs, metrics and traces over OTLP ([ADR 0018](../adr/0018-observability-with-the-grafana-stack.md)).

- **Locally:** the Aspire dashboard.
- **Production:** the apps export OTLP directly to Grafana Cloud Free: Prometheus-compatible metrics, Loki and Tempo. Grafana shows provisioned dashboards (ASP.NET Core, per-service RED metrics, messaging, business KPIs, free-tier usage) and sends alerts to Telegram and e-mail. Locally, `--topology services` runs the same stack self-hosted (Collector, Prometheus, Loki, Tempo, Grafana).
- **Correlation:** the W3C trace id is the correlation id. It is propagated through HTTP and RabbitMQ, written to every log record and returned in every ProblemDetails (#60). The web client shows it as "Reference" in every error message, and the trace includes the request's database commands; [TRACING.md](../operations/TRACING.md) walks from a reference to its trace (#72).
- **Retention:** 14 days in Grafana Cloud Free; limits of 10k active series and 50 GB of logs and of traces per month (ADR 0018 §5).

## 11. Testing and quality

Tests follow the pyramid and run in CI on every pull request.

| Level | Tooling | What it checks |
|---|---|---|
| Unit | xUnit + Shouldly/AwesomeAssertions | Domain: states, invariants, calculations |
| Integration | Testcontainers (real PostgreSQL 17) + WebApplicationFactory on the single host | Endpoints, migrations, triggers, Outbox, cookie and internal-token authentication |
| Topology | Testcontainers (PostgreSQL, RabbitMQ) + gateway and two services | Routing through the gateway, `/internal` never routed (route-table test), internal token, an event round trip over RabbitMQ |
| Database | 118 SQL assertions + concurrency test | Stock, locking, permissions, audit, session context, schema boundaries, service-kit reservation |
| Architecture | NetArchTest | Module and layer boundaries, no topology concerns in modules |
| UI components | bUnit | Forms, validation, status display |
| End-to-end | Playwright | UC01, UC02 on desktop and tablet |

Code quality:
- `Directory.Build.props` with nullable and `TreatWarningsAsErrors`;
- .NET analyzers and `.editorconfig`;
- Central Package Management (`Directory.Packages.props`);
- coverage collected in CI (report and gate: #63).

Decisions are recorded as ADRs in `docs/adr/`. How the backend grading rubric is met, and what is still open, is tracked in [BACKEND_REQUIREMENTS.md](BACKEND_REQUIREMENTS.md). All repository content — code, comments, docs and commit messages — is in English.

## 12. Solution structure

One repository, one `.slnx` solution. Each module is two projects. One host project per topology composes them.

```
SmartSpare/
├─ src/
│  ├─ Bootstrapper/
│  │  ├─ SmartSpare.Host/              # single host: BFF + all modules, WASM hosting (development, tests)
│  │  ├─ SmartSpare.Gateway/           # production BFF: YARP, WASM hosting, cookie, internal tokens
│  │  ├─ Services/SmartSpare.Services.<Module>/  # one host per module service
│  │  ├─ SmartSpare.MigrationRunner/, SmartSpare.ModuleCatalog/
│  ├─ BuildingBlocks/
│  │  ├─ SmartSpare.SharedKernel/      # Entity, ValueObject, Result, domain events
│  │  ├─ SmartSpare.Infrastructure/    # Outbox/Inbox, handlers and decorators, EF interceptors, transport seam
│  │  ├─ SmartSpare.Infrastructure.RabbitMq/  # RabbitMQ transport, broker metrics job
│  │  ├─ SmartSpare.Bff/               # /bff endpoints, cookie, antiforgery, internal-token issuer (stage 3/4)
│  │  └─ SmartSpare.ServiceDefaults/   # OpenTelemetry, health, resilience
│  ├─ Modules/
│  │  ├─ Identity/     SmartSpare.Modules.Identity    + .Contracts
│  │  ├─ Inventory/    SmartSpare.Modules.Inventory   + .Contracts
│  │  ├─ Fleet/        SmartSpare.Modules.Fleet       + .Contracts
│  │  ├─ Procurement/  SmartSpare.Modules.Procurement + .Contracts
│  │  ├─ Forecasting/  SmartSpare.Modules.Forecasting + .Contracts
│  │  └─ Reporting/    SmartSpare.Modules.Reporting   + .Contracts
│  ├─ Web/
│  │  ├─ SmartSpare.Web.Client/        # Blazor WASM + MudBlazor, Features/<Module>
│  │  └─ SmartSpare.ApiContracts/      # DTOs + FluentValidation validators, role/claim/policy names
│  └─ SmartSpare.AppHost/               # Aspire, local development only (both topologies)
├─ tests/
│  ├─ <Module>.UnitTests, <Module>.IntegrationTests
│  ├─ SmartSpare.ArchitectureTests
│  ├─ SmartSpare.Topology.Tests        # gateway + services + RabbitMQ
│  ├─ SmartSpare.Web.Tests (bUnit)
│  └─ SmartSpare.E2E (Playwright)
├─ db/            # SQL parts of migrations, seed, test data, SQL tests
├─ infra/         # Bicep; observability/ (Collector, Prometheus, Loki, Tempo, Grafana provisioning)
├─ docs/          # architecture, database, ADRs, diagrams
├─ .github/workflows/
├─ Directory.Build.props, Directory.Packages.props
└─ SmartSpare.slnx
```

Folders inside a module: `Domain/`, `Features/<UseCase>/`, `Infrastructure/` (DbContext, configurations, migrations, event handlers) and `ModuleInstaller.cs`, which registers the module in whichever host composes it.

## 13. Work plan

Work is done on the developer machine (.NET 10 SDK + Docker), where the code can be built and tested. Each stage ends with a green build and a commit.

**Deadlines** (product owner, 25 November 2026): the written thesis is due on **3 January 2027**, and the defence is on **16 February 2027**. Each stage has a target window and a GitHub milestone (`v0.<stage>.0`). **If a stage runs late, its scope is cut; the later stages are not moved.** Cut scope becomes an issue in a later milestone, or `future`.

*Schedule update (product owner, 26 November 2026):* stage 4 finished a week early, so stages 5–7 moved earlier and the gained days went to stage 8 (Forecasting), which produces the forecast metrics for the thesis. Stages 10 and 11 are unchanged.

1. **Skeleton.** Solution, `Directory.*.props`, BuildingBlocks, host, Aspire, empty modules, architecture tests, CI build.
2. **Data layer.** DbContexts and Fluent configurations for all modules, initial migrations with SQL parts from V001–V016, Outbox/Inbox. Check: schema matches the current one, 97 SQL tests green.
3. **Identity and BFF.** Cookie sign-in, roles, policies, internal-token validation (ADR 0016); Blazor client skeleton with MudBlazor and the sign-in page. **Done:** v0.3.0, PRs 3a–3e ([STAGE_3_PLAN.md](../stage-3/STAGE_3_PLAN.md), section 10).
4. **Topology** (25 Nov – 3 Dec 2026). Service hosts, YARP gateway, RabbitMQ transport, Grafana stack, per-service images, pipelines and Bicep (ADRs 0015–0020), built and tested locally and in CI; CloudAMQP and Grafana Cloud are used from the local services topology. Nothing is deployed to Azure in this stage (product owner, 25 November 2026). Forwarded headers (#48) are configured here and verified behind the real ingress in stage 10. Coverage and vulnerability gates (#63, #64) land before the first per-service pipeline. Plan: [STAGE_4_PLAN.md](../stage-4/STAGE_4_PLAN.md) (#67). **Done:** v0.4.0, PRs 4a–4k; exit criteria with evidence in STAGE_4_PLAN section 12.
5. **Inventory** (26–30 Nov). Stock, movements, reservations, transfers, file import + screens. The read-only parts catalogue and warehouses (API and page) were pulled forward into stage 4 for the API review. Plan: [STAGE_5_PLAN.md](../stage-5/STAGE_5_PLAN.md) (#103). **Done:** v0.5.0, PRs 5a–5l; exit criteria with evidence in STAGE_5_PLAN section 12.1.
6. **Fleet** (26 Nov – 4 Dec; started early after stage 5). Machines, engine hours, the maintenance due calculation, service orders with kits as integration events (UC02, #12) + mobile card. Plan: [STAGE_6_PLAN.md](../stage-6/STAGE_6_PLAN.md) (#118). **Done:** v0.6.0, PRs 6a–6n with 6i built in time; exit criteria with evidence in STAGE_6_PLAN section 10.1.
7. **Procurement** (5–7 Dec). Purchase orders with the four-eyes rule.
8. **Forecasting** (8–18 Dec). ML.NET models and benchmark, recommendations and UC01 events between modules. The forecast metrics are needed for the thesis (3 January).
9. **Reporting** (19–21 Dec). Dashboards and KPIs on the marts.
10. **Azure production** (4–15 Jan 2027). First deployment of the services topology (#85): infrastructure, all services through their pipelines, KEDA scaling, forwarded headers behind the ingress, measured cold start. Then Entra logins per service and per-module roles (#16), Key Vault keys and rotation, alerting live, backups, and cost monitoring against the ADR 0009 estimate.
11. **Defence polish** (16 Jan – 14 Feb 2027). Playwright scenarios, updated diagrams and documentation.

Stages 5–9 follow the same pattern: domain → use cases → endpoints → tests → screens. Until stage 10 they are verified in the single host, the local services topology and the CI topology job; from stage 10 on, every change deploys through the stage 4 pipelines. The target windows above follow the deadlines; scope is cut rather than stages moved.

*Renumbering note (24 November 2026):* stage 4 was inserted, so the former stages 4–10 are now 5–11. Documents and issues were updated; ADRs 0001–0014 keep the old numbers (see the [ADR index](../adr/README.md)).

## 14. Approved decisions

16 November 2026:

- [x] Hybrid EF Core code-first instead of SQL migrations and `SmartSpare.DbMigrator`
- [x] Modular monolith with six modules (section 2) — since 24 November the code organisation and development topology; production runs services
- [x] No cross-module FKs; eventual consistency between modules
- [x] Own handlers instead of MediatR
- [x] NFR04 in the BA: "cookie in the browser, tokens between services" instead of JWT in the browser
- [x] Blazor Web App with global InteractiveWebAssembly and MudBlazor 9.x on .NET 10 LTS
- [x] ML.NET for demand forecasting (gradient boosting + intermittent-demand baselines)
- [x] Azure with Bicep and GitHub Actions with OIDC (the original App Service hosting was replaced on 24 November, see below)
- [x] PWA deferred as a future direction
- [x] Repository content is English only

24 November 2026 (product owner, ADRs 0015–0019):

- [x] D1 — Every module runs as its own service in production, behind a YARP gateway that is the BFF; single host for development and tests; one PostgreSQL server, schema per module, login per service; new stage 4
- [x] D2 — RabbitMQ as the integration-event transport, on the official .NET client, without commercial messaging libraries
- [x] D3 — OpenTelemetry → Prometheus, Loki, Tempo, Grafana with alerts to Telegram and e-mail, replacing Application Insights as the primary stack; Grafana Cloud Free in production, self-hosted locally
- [x] D4 — OpenAPI + Scalar published without sign-in in production
- [x] Hosting on Azure Container Apps with every app scaling to zero; CloudAMQP Little Lemur and Grafana Cloud Free instead of a self-hosted VM, which was rejected on cost against €35/month (App Service rejected: no broker, no service discovery)
