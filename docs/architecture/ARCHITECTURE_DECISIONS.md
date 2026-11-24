# SmartSpare — Architecture Decisions

Status: **approved** (16 November 2026). This is the architecture of SmartSpare; it superseded the database-stage architecture note, which was removed (it remains in the Git history). Individual decisions are recorded as ADRs in [`docs/adr/`](../adr/README.md).

## Summary

SmartSpare is built as a **microservice-ready modular monolith**: one deployment to Azure today, and any module can later be extracted into its own service without rewriting business logic.

| # | Decision | In short | ADR |
|---|---|---|---|
| 1 | Style | Modular monolith: each module has its own projects, PostgreSQL schema and DbContext | [0002](../adr/0002-modular-monolith.md) |
| 2 | Module boundaries | No cross-module FKs, JOINs or shared transactions; modules talk only through Contracts and events | [0003](../adr/0003-module-boundaries-and-integration-events.md) |
| 3 | Integration | Integration events via Outbox/Inbox, eventual consistency between modules | [0003](../adr/0003-module-boundaries-and-integration-events.md) |
| 4 | Inside a module | Vertical slices, DDD tactical patterns, CQRS-lite, Result pattern | [0002](../adr/0002-modular-monolith.md) |
| 5 | Handlers | Own lightweight interfaces and decorators instead of MediatR | [0005](../adr/0005-own-handlers-instead-of-mediatr.md) |
| 6 | Data | Hybrid EF Core code-first: tables in Fluent API, triggers and functions via `migrationBuilder.Sql` | [0004](../adr/0004-hybrid-ef-core-code-first.md) |
| 7 | Frontend | Blazor Web App (global InteractiveWebAssembly) + MudBlazor 9.x, BFF with HttpOnly cookie | [0006](../adr/0006-frontend-blazor-wasm-mudblazor-bff.md) |
| 8 | API contracts | Shared `SmartSpare.ApiContracts` project with DTOs and FluentValidation validators | [0006](../adr/0006-frontend-blazor-wasm-mudblazor-bff.md) |
| 9 | Platform | .NET 10 LTS | [0007](../adr/0007-dotnet-10-lts.md) |
| 10 | Machine learning | ML.NET: gradient boosting + intermittent-demand baselines | [0008](../adr/0008-mlnet-demand-forecasting.md) |
| 11 | Azure | Container from day one, App Service now, Container Apps later; Bicep, GitHub Actions with OIDC | [0009](../adr/0009-azure-hosting-and-delivery.md) |
| 12 | Observability | OpenTelemetry → Application Insights | [0009](../adr/0009-azure-hosting-and-delivery.md) |
| 13 | Testing | Unit, integration with Testcontainers, architecture tests, bUnit, Playwright | — |
| 14 | Repository language | English only | [0010](../adr/0010-english-only-repository.md) |
| 15 | Evolvability | Abstractions only at seams to volatile technology; stable contracts (Contracts, versioned HTTP API, events); claims-based authorization | [0012](../adr/0012-evolvability-principles.md) |
| 16 | Domain model and persistence | Aggregates in `Domain/` mapped directly by EF Core; stage 2 table mirrors removed per module stage; no generic repository | [0013](../adr/0013-domain-model-and-persistence.md) |
| 17 | Browser sign-in | WASM sign-in page posts to Host `/bff` endpoints; ASP.NET Core Identity with custom stores over `iam`; antiforgery on every unsafe request (implemented in stage 3) | [0014](../adr/0014-browser-sign-in-through-bff-endpoints.md) |

## 1. Architectural style

We build a **modular monolith**, not microservices from day one. A single process and a single deployment are cheaper and simpler for the diploma, while strict module boundaries make a later move to microservices mechanical work.

Rules that keep the monolith "splittable":

1. **A module owns its data.** Each module has its own PostgreSQL schema, DbContext and migrations history table. Nobody reads another module's schema.
2. **No cross-module FKs, JOINs or transactions.** A reference to another module's entity is just an identifier; integrity is enforced by code and events. All FKs inside a module stay.
3. **The public surface is `*.Contracts` only.** It holds integration events and narrow query interfaces. Everything inside a module is `internal`.
4. **Modules communicate asynchronously.** State in another module changes only through events; a synchronous call is allowed only for reads, through an interface from Contracts.
5. **Boundaries are checked automatically.** Architecture tests fail if a module references another module's implementation.

Extracting a module into a microservice: move its projects into their own host, its schema into its own database, and swap the in-process transport behind Outbox/Inbox for Azure Service Bus. Business code does not change.

## 2. Modules

Six business modules plus a shared platform. The schemas of the original database map onto modules almost unchanged; the supplier tables moved from `catalog` to `procurement`, and the cross-module foreign keys were removed (ADR 0011, implemented in stage 2; every object is mapped in [MODULE_MAPPING.md](../database/MODULE_MAPPING.md)).

| Module | DB schemas | Responsible for | Scenarios |
|---|---|---|---|
| Identity | `iam` | Users, roles, brand assignments, sign-in, GDPR anonymisation | All |
| Inventory | `catalog`, `inventory`, `raw`, `staging` | Parts catalogue, warehouses and routes, stock ledger, reservations, transfers, data import | UC01, import |
| Fleet | `fleet` | Customer machines, engine hours, maintenance plans, service orders | UC02 |
| Procurement | `procurement` (incl. `supplier`, `supplier_part`, `supplier_price`) | Suppliers and prices, purchase orders, four-eyes rule | Purchasing |
| Forecasting | `forecast` | ML models, forecasts, recommendations and decisions on them | Recommendations |
| Reporting | `mart` | Data marts, KPIs, dashboards; read-only | Reports |
| Platform (BuildingBlocks) | `ref`, `audit`, `util` | Reference data, audit, masking, Outbox/Inbox, shared abstractions | — |

Each module also keeps its EF migration history (`<schema>.__ef_migrations_history`) and, where it publishes or consumes integration events, its `outbox_message` / `inbox_message` in its own schema. The former `meta` schema of the SQL migration runner is retired.

Reporting is the one deliberate exception: it reads other modules' data through marts and materialized views, as an analytical store does in real systems. With microservices, the marts will be fed from events.

## 3. Inside a module

Every module has the same shape and consists of two projects: the implementation and `Contracts`. The three Clean Architecture layers live in folders inside one project and are enforced by architecture tests — simpler than four projects per module.

- **Domain** — aggregates, value objects, domain events, invariants. Examples: `TransferOrder` with its state machine, `PurchaseOrder` with the four-eyes rule, `Quantity` and `Money` as value objects. No references to EF or ASP.NET.
- **Features (vertical slices)** — one folder per use case: `ApproveTransfer/` holds the command, handler, validator and endpoint. Changing one use case touches one folder.
- **Infrastructure** — the module's DbContext, EF configurations, migrations, handlers for incoming events.

**CQRS-lite.** Commands go through aggregates and EF Core; queries read straight into DTOs (EF projections or database views). One database, no separate read stores.

**Result pattern.** Expected business errors (insufficient stock, invalid status transition) are returned as `Result<T>` and mapped to ProblemDetails; exceptions are only for the unexpected. Database codes SS100–SS402 map to the same errors via the existing `DatabaseErrorMapper`.

**Own handlers instead of MediatR.** `ICommandHandler<TCommand, TResult>` and `IQueryHandler<TQuery, TResult>` interfaces plus decorators for validation, logging and transactions — about 100 lines of code. Reasons for not using MediatR: it is commercially licensed from version 13, and explicit code is easier to explain at the defence.

## 4. Module integration

Modules change each other only through **integration events** with guaranteed delivery via Outbox/Inbox. In the monolith the transport is in-process; with microservices it becomes Azure Service Bus. Publisher and subscriber code does not change.

- **Outbox.** An event is written to the module's `outbox_message` table in the same transaction as the data change. A background job picks up batches with `FOR UPDATE SKIP LOCKED` and publishes them.
- **Inbox.** The receiver records the id of each processed event in `inbox_message`, so redelivery is safe (idempotency).
- **Synchronous queries** are read-only and go only through an interface in `Contracts` (for example, `IStockQueries`). After extraction its implementation becomes an HTTP client.
- **Deliberate duplication.** A module keeps a copy of the foreign data it needs (for example, Fleet keeps part number and name for maintenance kits), updated by events.

**Example: UC01 (transfer based on a recommendation).**

1. Forecasting creates a recommendation to move 11 units from KLP to PNV; the planner accepts it.
2. Forecasting publishes `TransferRequested`.
3. Inventory creates a draft `TransferOrder` and reserves stock — all in its own transaction.
4. After approval, Inventory publishes `TransferApproved`.
5. Forecasting marks the recommendation as fulfilled. If the stock is gone, `TransferRejected` arrives and the recommendation goes back to open (compensating action).

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

- **Migrations per module.** Each DbContext keeps its history in its own schema (`inventory.__ef_migrations_history`).
- **Business rules in the domain, protection in the database.** Triggers and CHECKs remain the last line of defence (stock ledger, stock row locks, audit), protecting data from both application bugs and direct database edits.
- **Session context.** `SessionContextInterceptor` passes the user id into `app.user_id` so the audit knows who changed what.
- **Resilience.** `EnableRetryOnFailure` for Azure, optimistic concurrency via `xmin`, `AsNoTracking` for queries.
- **Production rollout** uses an EF migration bundle from CI under the dedicated `smartspare_migrator` login. The application never migrates on startup.
- **Port verification.** The schema produced by EF migrations is diffed against the frozen V016 schema (`db/baseline/v016_schema.sql`, `tools/schema_diff.sh`), and all 97 existing SQL tests, the concurrency test and the demo dataset run on the EF-built database in CI (`tools/verify_database.sh`).

Consequence for the existing database: cross-module FKs are removed, and the custom `SmartSpare.DbMigrator` runner and the V-scripts are replaced by EF migrations (done in stage 2).

## 6. Frontend

The frontend is **Blazor WebAssembly with MudBlazor**, as stated in the topic application and the BA. Sign-in follows the **BFF pattern with an HttpOnly cookie**: tokens never reach the browser, so XSS cannot steal them.

- **Template** — .NET 10 Blazor Web App with global InteractiveWebAssembly render mode. The whole UI runs in the browser; global interactivity is required because MudBlazor does not support static rendering.
- **MudBlazor 9.x** — fully supports .NET 10 and is actively maintained.
- **BFF today** — the API host itself: ASP.NET Core Identity (same PBKDF2 hashes as in the database) issues an HttpOnly, SameSite=Strict cookie with CSRF protection.
- **Sign-in flow** — the MudBlazor sign-in page posts JSON to `/bff/login`; Identity runs with custom stores over the `iam` tables ([ADR 0014](../adr/0014-browser-sign-in-through-bff-endpoints.md); plan in [STAGE_3_PLAN.md](../stage-3/STAGE_3_PLAN.md)).
- **BFF with microservices** — a YARP gateway that accepts the cookie and calls services with tokens. The frontend does not change.
- **Structure per module** — `Features/Fleet`, `Features/Inventory`, `Features/Procurement`, `Features/Forecasting`, `Features/Identity`: pages, components and a typed HTTP client per module.
- **`SmartSpare.ApiContracts`** — shared DTOs of the public HTTP API and FluentValidation validators, the same for the form and the server. The frontend never references the modules' internal `*.Contracts`.
- **Errors** — ProblemDetails with SS1xx–SS4xx codes become readable messages in MudSnackbar.
- **State** — plain scoped services, no Fluxor or Redux.
- **Responsiveness (NFR05)** — MudBlazor grid, mobile machine card for the engineer's tablet (WF03).
- **Accessibility** — WCAG 2.1 AA: contrast, ARIA labels, icons next to colour-coded statuses.
- **Localisation** — `IStringLocalizer` and resx files.
- **PWA for field work** — not now; recorded as a future direction.

**Platform and alternatives.** Target version is **.NET 10 LTS** (supported until November 2028); .NET 11 is an STS release and we do not move to it. Considered and rejected: Fluent UI Blazor v5 (fewer ready-made components for a data-heavy admin UI), commercial Telerik, DevExpress and Syncfusion (paid), InteractiveServer and Auto render modes (server state and SignalR hinder scaling and splitting into services).

## 7. Security

Security is layered: browser → API → database, and each layer checks permissions on its own.

- **Authentication** — HttpOnly cookie in the browser, tokens only between services (section 6).
- **Authorisation** — ASP.NET Core policies by role and brand on every endpoint; the same rules are duplicated in the database (`iam.user_has_role`, SECURITY DEFINER functions).
- **Database role per module** — each module runs under its own role and sees only its own schema. This is both protection and an enforcement of module boundaries at the PostgreSQL level.
- **Secrets** — no passwords in code or configuration: in Azure, Managed Identity and Microsoft Entra authentication to PostgreSQL, everything else in Key Vault; locally, user-secrets.
- **Personal data** — already in the database: customer pseudonymisation, masking, GDPR anonymisation, immutable audit with 180-day retention.
- **Web protection** — HTTPS and HSTS, CSP headers, rate limiting on sign-in and import, upload size limits.
- **Supply chain** — Dependabot, CodeQL and container image scanning in CI.

## 8. Azure and delivery

Today: one container on App Service and managed PostgreSQL; later the same image and new services move to Container Apps. All infrastructure is defined as code.

| Component | Monolith (now) | Microservices (later) |
|---|---|---|
| Application | App Service (Linux, container), Basic with Always On | Azure Container Apps, one service per module |
| Database | PostgreSQL Flexible Server 17, Burstable | Same server with schemas → a database per service |
| Events | In-process + Outbox/Inbox | Azure Service Bus (topics) |
| Import files | Blob Storage | Blob Storage |
| Secrets | Key Vault + Managed Identity | Key Vault + Managed Identity per service |
| Telemetry | OpenTelemetry → Application Insights | Same + distributed tracing |
| Entry point | Host = BFF | YARP gateway (BFF) |
| Images | Azure Container Registry | Azure Container Registry |

- **IaC** — Bicep modules in `infra/`, dev and prod environments via parameters.
- **CI/CD** — GitHub Actions: build and tests → image to ACR → EF migration bundle → deploy. Azure sign-in via OIDC (federated credentials), no long-lived secrets.
- **Database identity** — one Managed Identity with `SET ROLE` per module while it is a monolith; a separate identity per service after the split.
- **Background jobs** — safe with several instances: Quartz in clustered mode (mart refresh, reservation expiry, audit purge), Outbox via `SKIP LOCKED`.
- **Local development** — .NET Aspire: PostgreSQL, Blob emulator and telemetry dashboard with one command. Aspire is not used to deploy to Azure — Bicep is.
- **Health** — `/health/live` and `/health/ready` health checks for App Service and Container Apps.
- **ML model** — ML.NET inside the Forecasting module; training runs as a scheduled background job, and as a separate Container Apps Job after the split (section 9).

## 9. Demand forecasting (ML.NET)

ML is built with **ML.NET 5.x** inside the Forecasting module: one language, one deployment, no separate Python service. ML.NET's built-in time-series forecasting offers a single algorithm (SSA), which handles intermittent spare-parts demand poorly, so the core model is gradient-boosted regression.

| Model | Role | Implementation |
|---|---|---|
| Gradient boosting (one global model for all SKUs) | Primary | ML.NET LightGBM / FastTreeTweedie; features: lags, rolling means, calendar, engine hours, demand class |
| Croston, SBA, TSB | Baselines for intermittent demand | Own C#, a few dozen lines each |
| Seasonal naive, SSA | Baselines for regular demand | Own C# / ML.NET `ForecastBySsa` |

- **Model selection by demand class** — based on the existing `fn_classify_demand` and `benchmark_result`, evaluated on a chronological holdout.
- **Model lifecycle** — model file in Blob Storage, version and metrics in `ml_model_version`, promotion via `promote_model`.
- **Training** — scheduled background job under the `app_ml` role; inference via `PredictionEnginePool`.
- **Risk** — the time-series package depends on Intel MKL, so the image is built for linux-x64.

## 10. Testing and quality

Tests follow the pyramid and run in CI on every pull request.

| Level | Tooling | What it checks |
|---|---|---|
| Unit | xUnit + Shouldly/AwesomeAssertions | Domain: states, invariants, calculations |
| Integration | Testcontainers (real PostgreSQL 17) + WebApplicationFactory | Endpoints, migrations, triggers, Outbox |
| Database | Existing 97 SQL assertions + concurrency test | Stock, locking, permissions, audit |
| Architecture | NetArchTest | Module and layer boundaries |
| UI components | bUnit | Forms, validation, status display |
| End-to-end | Playwright | UC01, UC02 on desktop and tablet |

Code quality: `Directory.Build.props` with nullable and `TreatWarningsAsErrors`, .NET analyzers, `.editorconfig`, Central Package Management (`Directory.Packages.props`), coverage reported in CI. Decisions are recorded as ADRs in `docs/adr/`. All repository content — code, comments, docs and commit messages — is in English.

## 11. Solution structure

One repository, one `.slnx` solution; each module is two projects that can be moved into their own service as a whole.

```
SmartSpare/
├─ src/
│  ├─ Bootstrapper/
│  │  └─ SmartSpare.Host/              # ASP.NET Core: BFF, module registration, WASM hosting
│  ├─ BuildingBlocks/
│  │  ├─ SmartSpare.SharedKernel/      # Entity, ValueObject, Result, domain events
│  │  └─ SmartSpare.Infrastructure/    # Outbox/Inbox, handlers and decorators, EF interceptors
│  ├─ Modules/
│  │  ├─ Identity/     SmartSpare.Modules.Identity    + .Contracts
│  │  ├─ Inventory/    SmartSpare.Modules.Inventory   + .Contracts
│  │  ├─ Fleet/        SmartSpare.Modules.Fleet       + .Contracts
│  │  ├─ Procurement/  SmartSpare.Modules.Procurement + .Contracts
│  │  ├─ Forecasting/  SmartSpare.Modules.Forecasting + .Contracts
│  │  └─ Reporting/    SmartSpare.Modules.Reporting   + .Contracts
│  ├─ Web/
│  │  ├─ SmartSpare.Web.Client/        # Blazor WASM + MudBlazor, Features/<Module>
│  │  └─ SmartSpare.ApiContracts/      # DTOs + FluentValidation validators
│  └─ SmartSpare.AppHost/               # Aspire, local development only
├─ tests/
│  ├─ <Module>.UnitTests, <Module>.IntegrationTests
│  ├─ SmartSpare.ArchitectureTests
│  ├─ SmartSpare.Web.Tests (bUnit)
│  └─ SmartSpare.E2E (Playwright)
├─ db/            # SQL parts of migrations, seed, test data, SQL tests
├─ infra/         # Bicep
├─ docs/          # architecture, database, ADRs, diagrams
├─ .github/workflows/
├─ Directory.Build.props, Directory.Packages.props
└─ SmartSpare.slnx
```

Folders inside a module: `Domain/`, `Features/<UseCase>/`, `Infrastructure/` (DbContext, configurations, migrations, event handlers) and `ModuleInstaller.cs`, which registers the module in the host.

## 12. Work plan

Work is done on the developer machine (.NET 10 SDK + Docker), where the code can be built and tested. Each stage ends with a green build and a commit.

1. **Skeleton.** Solution, `Directory.*.props`, BuildingBlocks, host, Aspire, empty modules, architecture tests, CI build.
2. **Data layer.** DbContexts and Fluent configurations for all modules, initial migrations with SQL parts from V001–V016, Outbox/Inbox. Check: schema matches the current one, 97 SQL tests green.
3. **Identity and BFF.** Cookie sign-in, roles, policies; Blazor client skeleton with MudBlazor and the sign-in page.
4. **Inventory.** Stock, movements, reservations, transfers, file import + screens.
5. **Fleet.** Machines, engine hours, service orders (UC02) + mobile card.
6. **Procurement.** Purchase orders with the four-eyes rule.
7. **Forecasting.** ML.NET models and benchmark, recommendations and UC01 events between modules.
8. **Reporting.** Dashboards and KPIs on the marts.
9. **Azure.** Bicep, GitHub Actions with OIDC, first deployment, telemetry.
10. **Defence polish.** Playwright scenarios, updated diagrams and documentation.

Stages 4–8 follow the same pattern: domain → use cases → endpoints → tests → screens. The order can shift to match diploma submission deadlines.

## 13. Approved decisions (16 November 2026)

- [x] Hybrid EF Core code-first instead of SQL migrations and `SmartSpare.DbMigrator`
- [x] Modular monolith with six modules (section 2)
- [x] No cross-module FKs; eventual consistency between modules
- [x] Own handlers instead of MediatR
- [x] NFR04 in the BA: "cookie in the browser, tokens between services" instead of JWT in the browser
- [x] Blazor Web App with global InteractiveWebAssembly and MudBlazor 9.x on .NET 10 LTS
- [x] ML.NET for demand forecasting (gradient boosting + intermittent-demand baselines)
- [x] Azure: container on App Service, Bicep, GitHub Actions with OIDC
- [x] One Managed Identity + `SET ROLE` per module in the monolith; an identity per service after the split
- [x] PWA deferred as a future direction
- [x] Repository content is English only
