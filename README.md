# SmartSpare

Agricultural machinery maintenance planning and spare-parts demand forecasting (EHU bachelor's diploma project, Sergei Gorgun, 2026/27).

Stack: ASP.NET Core 10 · Blazor WebAssembly + MudBlazor · EF Core 10 + Npgsql · **PostgreSQL 17** · ML.NET · Quartz.NET ·
YARP · RabbitMQ · OpenTelemetry + Grafana stack · Azure Container Apps.

Architecture: **one codebase, two topologies**. Development and tests run a modular monolith (one host). Production runs
every module as its own service behind a YARP gateway (ADR 0015, from stage 4). Start with
[ARCHITECTURE_DECISIONS.md](docs/architecture/ARCHITECTURE_DECISIONS.md) and the [ADRs](docs/adr/README.md).
Compliance with the backend grading rubric: [BACKEND_REQUIREMENTS.md](docs/architecture/BACKEND_REQUIREMENTS.md).

## Diploma criteria

Where each criterion is implemented, as of release v0.4.0 (end of stage 4). **done** means it exists on `main` and can be shown;
**planned** names the stage it comes with ([ARCHITECTURE_DECISIONS §13](docs/architecture/ARCHITECTURE_DECISIONS.md#13-work-plan)).

### Back-end

The full matrix with evidence for every rubric row is [BACKEND_REQUIREMENTS.md](docs/architecture/BACKEND_REQUIREMENTS.md).
The database criteria are in [DATABASE_DESIGN.md §0](docs/database/DATABASE_DESIGN.md#0-requirements-compliance-matrix).

| Requirement | Where | Status |
|---|---|---|
| Modern framework, ORM, database | .NET 10 LTS, EF Core 10, PostgreSQL 17 ([ADR 0007](docs/adr/0007-dotnet-10-lts.md), [ADR 0004](docs/adr/0004-hybrid-ef-core-code-first.md)) | done |
| Layered, modular architecture | `src/Modules/<Module>/` (Domain, Features, Infrastructure, Contracts); `tests/SmartSpare.ArchitectureTests` | done |
| Microservices, independently deployable | Gateway + six services in `src/Bootstrapper`; CI job "Topology test"; `deploy-<service>.yml` ([ADR 0015](docs/adr/0015-deployment-topology-microservices-behind-yarp-gateway.md)) | done locally and in CI; deployment in stage 10 |
| Asynchronous messaging | Outbox/Inbox + RabbitMQ transport (`SmartSpare.Infrastructure.RabbitMq`, [ADR 0017](docs/adr/0017-rabbitmq-integration-event-transport.md)); `RabbitMqTransportTests`, CI job "Topology test" | done |
| Security | Cookie BFF, ES256 internal tokens, policies, antiforgery, rate limit, CSP ([ADR 0014](docs/adr/0014-browser-sign-in-through-bff-endpoints.md), [ADR 0016](docs/adr/0016-authentication-behind-the-gateway.md)); `tests/SmartSpare.Host.IntegrationTests` | done |
| Error handling, logging, tracing | ProblemDetails with a trace id; OpenTelemetry → Grafana stack ([TRACING.md](docs/operations/TRACING.md), [ADR 0018](docs/adr/0018-observability-with-the-grafana-stack.md)) | done |
| Monitoring and alerting | Dashboards and 7 alert rules in [`infra/observability`](infra/observability) (local Grafana), provisioned to Grafana Cloud by Terraform in [`infra/grafana`](infra/grafana); test alert delivered by e-mail | done; production traffic from stage 10 (#85) |
| Tests and coverage gate | Unit, integration (Testcontainers), architecture, topology, bUnit, Playwright; `tools/check.sh coverage` fails below 70 % of business logic | done |
| Static analysis | SonarQube C# rules (`SonarAnalyzer.CSharp`) fail the build like the .NET analyzers; `tools/check.sh duplication` (jscpd) fails above 3 % duplicated lines. Results: the build log and the "Check duplication" step of the CI job "Build and test" (JSON report in `TestResults/duplication`) | done |
| CI/CD | `.github/workflows/ci.yml` (gates, integration, E2E, topology, images), `deploy-<service>.yml`, Bicep in [`infra/`](infra) | CI done; first deployment in stage 10 (#85) |
| No secrets in the repository | user-secrets, Key Vault references; gitleaks scan of the whole history ([`.gitleaksignore`](.gitleaksignore)) | done |

### Front-end

Details in [FRONTEND.md](docs/architecture/FRONTEND.md).

| Requirement | Where | Status |
|---|---|---|
| Modern SPA framework, justified | Blazor WebAssembly + MudBlazor 9 in [`src/Web/SmartSpare.Web.Client`](src/Web/SmartSpare.Web.Client) ([ADR 0006](docs/adr/0006-frontend-blazor-wasm-mudblazor-bff.md)) | done |
| Component structure | `Features/<Module>` pages, `Layout/`, `Shared/` components, typed API clients ([FRONTEND.md §2](docs/architecture/FRONTEND.md#2-component-structure)) | done |
| Routing with authentication | `AuthorizeRouteView`, `RedirectToLogin`, role-based menu (`NavigationAreas`), pages guarded by policy (`[Authorize(Policy = ReadInventory)]`) | done |
| Secure sign-in | BFF with an HttpOnly cookie, no token in the browser ([ADR 0014](docs/adr/0014-browser-sign-in-through-bff-endpoints.md)); Playwright `SignInSmokeTest` checks scripts can't read it | done |
| Forms and validation | MudForm with the shared FluentValidation validators of `SmartSpare.ApiContracts` (same rules as the server) | done |
| Error handling | ProblemDetails → readable messages with "Reference: \<trace id\>"; 401 → sign-in, 403 → snackbar (`Http/Handlers.cs`) | done |
| Loading states | Busy buttons, progress bars, the parts grid's loading row, `data-interactive` once WebAssembly runs | done |
| Responsive layout | MudBlazor grid and responsive drawer; the parts grid becomes stacked cards on a phone; Playwright `MobileLayoutTest` at 390 × 844 | done |
| Accessibility | ARIA labels, visible focus, icons next to colour (`StatusChip`), WCAG 2.1 AA target | done for the existing pages |
| Tests | bUnit `tests/SmartSpare.Web.Tests` (27 tests); Playwright `tests/SmartSpare.E2E` (sign-in, API reference, mobile, parts catalogue) in CI job "End-to-end smoke test" | done |
| Server-side data grid | Inventory → Parts catalogue: `MudDataGrid` with server paging and search, loading and error states with the reference ([`InventoryPage.razor`](src/Web/SmartSpare.Web.Client/Features/Inventory/InventoryPage.razor), `InventoryClient`); bUnit `InventoryPageTests`, Playwright `PartsCatalogueTest` | done |
| Business screens | Inventory (rest), Fleet, Procurement, Forecasting, Reporting | planned (stages 5–9) |

### API documentation

Details in [API_DOCUMENTATION.md](docs/architecture/API_DOCUMENTATION.md).

| Requirement | Where | Status |
|---|---|---|
| Machine-readable API description | One OpenAPI 3.1 document per module at `/openapi/{module}.json`, and `/openapi/bff.json` for the session endpoints ([`ApiDocumentation.cs`](src/BuildingBlocks/SmartSpare.Infrastructure/Http/ApiDocumentation.cs), [ADR 0019](docs/adr/0019-public-api-documentation.md) and its amendment 1) | done |
| Interactive documentation | Scalar at `/scalar` with the session and every module document, nonce CSP, no CDN ([`ApiReference.cs`](src/BuildingBlocks/SmartSpare.Bff/ApiReference.cs)) | done |
| Sign-in documented | `/bff/login`, `/bff/logout`, `/bff/me`, `/bff/antiforgery` with request and response examples, 400 `SS-AUTH-001`/`002`, 401, 429 with `Retry-After` ([`BffEndpoints.cs`](src/BuildingBlocks/SmartSpare.Bff/BffEndpoints.cs)) | done |
| Public, but endpoints protected | Documents anonymous in every environment; every `/api/v1` endpoint requires authorization (`ApiDocumentationTests`) | done |
| Versioning | Business API under `/api/v1/{module}`; operational endpoints marked and unversioned ([ADR 0012](docs/adr/0012-evolvability-principles.md)) | done |
| Errors documented | ProblemDetails (RFC 9457) with stable `code` values (`ProblemCodes`); the schema documents `code` and `traceId`, and every error response has an example ([`OpenApiConventions.cs`](src/BuildingBlocks/SmartSpare.Infrastructure/Http/OpenApiConventions.cs)) | done |
| Internal endpoints hidden | `/internal/*` excluded from documents and never routed by the gateway (`GatewayTests`) | done |
| Business endpoints described | Inventory: `GET /api/v1/inventory/parts` (search, paging), `/parts/{id}` (404 ProblemDetails), `/warehouses`, each with summary, parameters, typed responses, field descriptions and examples; `InventoryApiTests`, `ApiDocumentationTests` | done for Inventory reads; the rest with stages 5–9 |

## Quick start (application)

Prerequisites: .NET 10 SDK (version pinned in `global.json`), Docker Desktop (running) and the Aspire 17 CLI bundle
(`~/.aspire`): since Aspire 17 the AppHost is a plain .NET project, and `dotnet run` starts the dashboard and the
orchestrator from that bundle ([migration guide](https://aspire.dev/integrations/dotnet/migrate-dotnet-project-resource/)).

```bash
dotnet run --project src/SmartSpare.AppHost      # PostgreSQL 17 + migrations + host + Aspire dashboard
# or: -- --topology services                      # gateway + six services + RabbitMQ + Grafana stack (below)
```

The AppHost starts PostgreSQL 17, runs `SmartSpare.MigrationRunner` (cluster bootstrap and the EF Core migrations of
every module) and starts the host once the migrations have finished. The Aspire dashboard opens in the browser and
lists the `host` resource with its URL. Every module answers
`GET /api/<module>/ping` (`identity`, `inventory`, `fleet`, `procurement`, `forecasting`, `reporting`) to a signed-in
user; health probes `/health/live` and `/health/ready`, the OpenAPI documents `/openapi/<module>.json` and the API
reference `/scalar` need no sign-in, in every environment (ADR 0019).

**Troubleshooting:** after a WSL or Docker restart, the AppHost may leave `migrations` and `host` waiting because the
persistent `postgres` container stayed stopped; start it with **Start resource** on the `postgres` row in the dashboard.
If it still doesn't start (`docker start smartspare-postgres` reports "network … not found"), stop the AppHost and remove
only the container, `docker rm smartspare-postgres`: the data stays in the volume `smartspare-postgres17-data`, and the
next AppHost start recreates the container on it.

To run the host alone, without Aspire: `dotnet run --project src/Bootstrapper/SmartSpare.Host` (https://localhost:7080; the
session cookies are `Secure`, so the default launch profile is HTTPS).

### The services topology

Production runs every module as its own service behind the gateway (ADR 0015). Locally:

```bash
dotnet run --project src/SmartSpare.AppHost -- --topology services
```

This starts, instead of the single host:
- `gateway` at https://localhost:7090: sign-in, the web app, `/scalar`, `/warmup`;
- the six services `identity` … `reporting`, each on its own database login and only reachable through the gateway;
- `messaging`: RabbitMQ 4.4 with the management UI;
- the observability stack of ADR 0018:
  - `otelcollector`, which forwards everything to the services below and to the Aspire dashboard;
  - `prometheus`, `loki` and `tempo`;
  - `grafana`, with the SmartSpare dashboards and alert rules from `infra/observability/` (anonymous viewer; the admin
    password is the `grafana-admin-password` parameter).

To try the diagnostics probe, sign in and `POST /api/identity/diagnostics/probe` through the gateway. Its trace in Tempo
goes gateway → Identity, and the delivery's trace (publish → Inventory) links to it ([TRACING.md](docs/operations/TRACING.md)).

To use CloudAMQP instead of the local broker, give the AppHost the instance's URL:

```bash
dotnet user-secrets set ConnectionStrings:messaging "amqps://<user>:<password>@<host>/<vhost>" --project src/SmartSpare.AppHost
```

The services then declare their queues on that instance ([BROKER.md](docs/operations/BROKER.md)). Remove the secret to
use the local broker again. The `broker-metrics` resource, started from the Aspire dashboard, runs the broker metrics job
once against whichever broker is configured.

To also send the telemetry to a Grafana Cloud stack, give the AppHost the stack's OTLP endpoint, instance id and an
access policy token with write scopes only ([infra/grafana](infra/grafana/README.md), which also provisions the stack's
dashboards and alert rules with Terraform):

```bash
dotnet user-secrets set GrafanaCloud:OtlpEndpoint "https://otlp-gateway-<region>.grafana.net/otlp" --project src/SmartSpare.AppHost
dotnet user-secrets set GrafanaCloud:InstanceId "<instance id>" --project src/SmartSpare.AppHost
dotnet user-secrets set GrafanaCloud:Token "<access policy token>" --project src/SmartSpare.AppHost
```

Without these secrets nothing leaves the machine.

If the AppHost is stopped abruptly, its containers can stay behind. Remove them with
`docker rm -f $(docker ps -aq --filter name=otelcollector- --filter name=prometheus- --filter name=loki- --filter name=tempo- --filter name=grafana- --filter name=messaging-)`.
The data stays in the `smartspare-*` volumes.

### Sign in as a demo user

1. Start everything: `dotnet run --project src/SmartSpare.AppHost` (Docker running). The Aspire dashboard opens.
2. Load the demo data once: on the `postgres` resource, open the menu "..." and run **Load demo data** (details in the
   database quick start below).
3. Look up the demo users' password: the AppHost parameter `demo-user-password`, under **Parameters** in the dashboard.
   To choose your own, run `dotnet user-secrets set Parameters:demo-user-password <value> --project src/SmartSpare.AppHost`
   before loading the demo data.
4. Open https://localhost:7080 (or the `host` link in the dashboard). You are sent to the sign-in page.
5. Sign in, for example as `tomas.radzevicius` (brand manager: every area) or `lukas.vaitkus` (warehouse keeper:
   inventory, procurement, reporting). The menu follows the user's roles; every endpoint checks its own policy on the
   server. Sign out from the account menu at the top right.

**Demo users.** Every account is created from an employee, and its rights follow from the organisation (ADR 0026): roles
from the employee's position, brand scope from brand-manager assignments, hubs from keeper or service-base assignments.
All of them sign in with the AppHost parameter `demo-user-password`; no password is stored in Git. Reads are never
scoped: every role sees every hub and brand; the brands and hubs below limit approvals and stock changes.

| User | Employee | Position | Roles | Brand scope | Hubs (write access) |
|---|---|---|---|---|---|
| `tomas.radzevicius` | E0001 | Brand manager | BRAND_MANAGER | CLAAS, LEMKEN | — |
| `rasa.kazlauskiene` | E0002 | Brand manager | BRAND_MANAGER | JD, VAD | — |
| `darius.petraitis` | E0003 | Commercial director | BRAND_MANAGER, SPONSOR | FENDT, MF, CASE, NH | — |
| `mantas.petrauskas` | E0004 | Service engineer | SERVICE_ENGINEER | — | PNV (service base) |
| `jonas.balciunas` | E0005 | Service engineer | SERVICE_ENGINEER | — | KNS (service base) |
| `egle.stankeviciute` | E0006 | Warehouse keeper | WAREHOUSE_KEEPER | — | KLP (keeper) |
| `lukas.vaitkus` | E0007 | Warehouse keeper | WAREHOUSE_KEEPER | — | PNV (keeper) |
| `ruta.jankauskaite` | E0008 | Warehouse keeper | WAREHOUSE_KEEPER | — | VLN (keeper) |
| `ignas.zukauskas` | E0009 | Inventory planner | INVENTORY_PLANNER | — | — |
| `vida.mockute` | E0010 | Managing director | SPONSOR | — | — |
| `admin.smartspare` | E0011 | System administrator | ADMINISTRATOR | — | every hub (administrator) |
| `paulius.urbonas` | E0012 | (left, anonymised) | — | — | cannot sign in |
| `superadmin.smartspare` | E0013 | Superadministrator | SUPERADMINISTRATOR (every permission) | every brand | every hub |

`superadmin.smartspare` is **emergency access, not for daily work** (ADR 0028). It signs in with the separate parameter
`superadmin-password` (Key Vault in Azure), never with `demo-user-password`. Only another superadministrator can move
someone into or out of that position, and the database invariants and the four-eyes rule still bind it.

Eight more employees (E0101–E0108, an accountant, workshop foremen, keepers of UTN and MRJ, a planner) have no account:
not every employee works in SmartSpare. The table matches T001, whose rights `AccessMatrixTests` checks on every build.

An error message ends with `Reference: <trace id>`; [docs/operations/TRACING.md](docs/operations/TRACING.md) shows how
to find that request's trace. The same flow runs in CI as a Playwright test in Chromium (`tools/check.sh e2e`).

### Sign-in endpoints (BFF)

The browser signs in through the BFF endpoints of the host ([ADR 0014](docs/adr/0014-browser-sign-in-through-bff-endpoints.md),
[ADR 0016](docs/adr/0016-authentication-behind-the-gateway.md)): `GET /bff/antiforgery` returns a request token,
`POST /bff/login` with `{ "userName", "password", "rememberMe" }` and the token in the `X-XSRF-TOKEN` header sets the
HttpOnly cookie `__Host-SmartSpare`, `GET /bff/me` shows the session, and `POST /bff/logout` ends it. The cookie and
antiforgery cookies are `Secure`, so use the HTTPS URL. Demo users and their password: see the database quick start.
The OpenAPI document `/openapi/bff.json` describes these endpoints with examples and every error; `/scalar` shows it
first.

Module endpoints also accept an **internal token** (`Authorization: Bearer`), the 2-minute ES256 token the gateway
mints. The single host validates it with a key that never lives in a file of the repository. Without a
key, every bearer token is rejected, which is the safe default. To try the token path locally, put a P-256 key into the
host's user secrets:

```bash
openssl ecparam -name prime256v1 -genkey -noout | openssl pkcs8 -topk8 -nocrypt > key.pem
dotnet user-secrets set InternalTokens:SigningKey "$(cat key.pem)" --project src/Bootstrapper/SmartSpare.Host
rm key.pem
```

Services get only the public key as a JWKS document (`InternalTokens:Jwks`).

**Data Protection keys** (cookies and antiforgery tokens, #22). With a database, the host keeps its key ring in
`iam.data_protection_keys` under the application name `SmartSpare`, so every instance of the host accepts the others'
cookies. Protection of the keys at rest is configured per environment in the section `DataProtection`:

| Setting | Effect |
|---|---|
| none (local development) | keys stored unencrypted in the table (readable only by the API login) |
| `DataProtection:CertificatePath` + `DataProtection:CertificatePassword` (secret) | keys encrypted with the certificate from a PFX file |
| `DataProtection:CertificateThumbprint` | keys encrypted with a certificate from the CurrentUser\My store |
| `DataProtection:UseKeyVault` | refused until stage 10, when the production Key Vault exists |

The gateway keeps its key ring in a directory (`DataProtection:KeysDirectory`, locally under `%LOCALAPPDATA%/SmartSpare/gateway-keys`); in Azure it moves to Blob Storage, protected with Key Vault, in stage 10 ([ADR 0016](docs/adr/0016-authentication-behind-the-gateway.md) §6).

| Task | Command |
|---|---|
| **All checks, exactly as CI** (Docker running) | `tools/check.sh` — stale terms, locked restore, Release build with `-warnaserror` (including the SonarQube C# rules), format, duplication, check-model, vulnerable packages, tests (the architecture tests in Debug as well) with the coverage gate (≥ 70 % of business logic), integration tests, database verification, schema diff against the V016 baseline |
| Checks without Docker (the CI "Build and test" job) | `tools/check.sh fast` |
| Selected checks | `tools/check.sh <step> ...` with steps `stale-terms deploy-paths restore vulnerabilities build format duplication check-model test architecture-debug coverage coverage-all integration database schema-diff upgrade coverage-combined` |
| End-to-end smoke test (Docker; installs Chromium) | `tools/check.sh restore build e2e` — Playwright signs in and out in Chromium; screenshots in `TestResults/e2e` |
| Schema diff (Docker) | `tools/check.sh restore build schema-diff` — EF-built schema vs. the frozen V016 baseline; fails on a difference not documented in `tools/schema_diff.allow` (a CI gate in the integration job) |

Run `tools/check.sh` (Git Bash on Windows) before pushing. Stage new files first: the stale-terms check scans tracked
files only. While iterating, plain `dotnet build` / `dotnet test --filter "Category!=Integration"` are fine, but only
the script reproduces CI. Locally it runs the test projects one at a time to save memory (`CHECK_SEQUENTIAL=1`, at
most one Testcontainers PostgreSQL at once; see CONTRIBUTING); CI runs them in parallel.

CI (`.github/workflows/ci.yml`) runs the same script on every push and pull request to `main`: the "Build and test" job
runs the steps of `tools/check.sh fast`, the integration job runs `integration` and `database`, and the end-to-end job
runs `e2e` and uploads screenshots and the Playwright trace when it fails.

## Repository layout

```
src/
  Bootstrapper/SmartSpare.Host          single host (development, tests): all modules, BFF sign-in, the web app with a
                                        strict CSP, health, OpenAPI
  Bootstrapper/SmartSpare.Gateway       production BFF (ADR 0015, 0016): YARP routes to the services, cookie sign-in, internal
                                        tokens per session, the web app, /warmup; references no module
  Bootstrapper/Services/SmartSpare.Services.<Module>
                                        one host per module service (ADR 0015): its module only, RabbitMQ, internal
                                        tokens only; --job <name> runs one job; ServiceHost.cs is shared by all six
  Bootstrapper/SmartSpare.ModuleCatalog the modules and their databases in migration order (shared by host and runner)
  Bootstrapper/SmartSpare.MigrationRunner  bootstrap + EF migrations of every context, or --module <name> for one
                                        service (ADR 0020); check-model
  BuildingBlocks/
    SmartSpare.SharedKernel             Entity, AggregateRoot, ValueObject, Result, Error — no dependencies
    SmartSpare.Infrastructure           command/query handlers + decorators, IModuleInstaller, Result → ProblemDetails,
                                        Persistence (Platform DbContext, unit of work, outbox/inbox, interceptors)
    SmartSpare.Bff                      /bff sign-in endpoints, session cookie, antiforgery, internal-token issuer
    SmartSpare.ServiceDefaults          OpenTelemetry, health checks, service discovery, HTTP resilience
  Modules/<Module>/                     Identity, Inventory, Fleet, Procurement, Forecasting, Reporting
    SmartSpare.Modules.<Module>         internal implementation: Domain/, Features/<UseCase>/, Infrastructure/,
                                        public <Module>ModuleInstaller
    SmartSpare.Modules.<Module>.Contracts  public surface: integration events, read-only query interfaces
  Web/SmartSpare.Web.Client             Blazor WebAssembly + MudBlazor: sign-in page, menu by policy, typed HTTP clients
  Web/SmartSpare.ApiContracts           HTTP DTOs + validators, role, claim, policy and problem-code names
  SmartSpare.AppHost                    .NET Aspire, local development only
tests/
  SmartSpare.SharedKernel.UnitTests, SmartSpare.Infrastructure.UnitTests
  SmartSpare.ArchitectureTests          module boundaries (NetArchTest + project references)
  SmartSpare.Modules.Identity.UnitTests, SmartSpare.Modules.Identity.IntegrationTests
  SmartSpare.Host.IntegrationTests      sign-in, cookie, antiforgery, authorization, internal tokens, CSP (in memory and
                                        on postgres:17)
  SmartSpare.Web.Tests                  bUnit: sign-in page, menu, HTTP handlers
  SmartSpare.E2E                        Playwright smoke test in Chromium (Category=E2E, tools/check.sh e2e)
  SmartSpare.Persistence.IntegrationTests  migrations, ownership, unit of work and outbox on postgres:17 (Testcontainers)
  SmartSpare.Topology.Tests             gateway + Identity + Inventory with postgres:17 and rabbitmq:4.4 (tools/check.sh topology)
infra/                                  Bicep for Azure (applied from stage 10, #85) and observability/ (dashboards, alerts,
                                        the local Grafana stack); see infra/README.md
db/
  bootstrap/000_bootstrap_cluster.sql   roles + database (once per server; run by the MigrationRunner or the server admin)
  sql/<module>/                         SQL parts of the EF migrations: triggers, functions, views, grants, seed
                                        (embedded in the module assembly, applied by migrationBuilder.Sql — ADR 0004)
  testdata/T001__demo_dataset.sql       deterministic synthetic dataset (dev/test only)
  tests/                                SQL test suite (140 assertions) + concurrency test
  scripts/                              reset_local.sh, load_testdata.sh, env.sh (credentials from .env)
  baseline/v016_schema.sql              frozen schema of the former SQL-built database (input of tools/schema_diff.sh)
  ddl/smartspare_schema.sql             complete DDL of the EF-built database (generated)
docs/
  architecture/ARCHITECTURE_DECISIONS.md approved architecture (start here)
  architecture/BACKEND_REQUIREMENTS.md  backend rubric compliance matrix (done / planned / gap)
  architecture/diagrams/                solution architecture diagram
  adr/                                  architecture decision records
  database/DATABASE_DESIGN.md           database design + requirements compliance matrix
  database/MODULE_MAPPING.md            ownership of every database object per module (stage 2)
  database/DATA_DICTIONARY.md           data dictionary (generated from the catalogue)
  database/diagrams/                    logical schema (colour-coded), data flow
  operations/TRACING.md                 from an error reference to its trace
  operations/BROKER.md                  what the CloudAMQP free plan offers, and how to check a broker
  stage-3/STAGE_3_PLAN.md               plan and exit criteria of stage 3
tools/                                  verify_database.sh, schema_diff.sh, DDL / doc / diagram generators
```

## Quick start (database)

The database is built by the **EF Core migrations of each module** (ADR 0004, ADR 0011); triggers, functions, views,
grants and seed data are SQL parts of those migrations in `db/sql/`. There are two local ways to get one.

**With the AppHost** (nothing else to configure): `dotnet run --project src/SmartSpare.AppHost` builds the database on
start. To load the demo data, run the command **Load demo data** on the `postgres` resource in the Aspire dashboard
(resource menu "..."). It runs T001 with the container's `psql` and then `MigrationRunner set-demo-passwords`, using the
connection and the generated passwords Aspire already holds, so nothing is copied from the dashboard. The demo users'
password is the AppHost parameter `demo-user-password` (shown under Parameters in the dashboard; choose your own with
`dotnet user-secrets set Parameters:demo-user-password <value> --project src/SmartSpare.AppHost`). The command loads
into a freshly migrated database; to reload, remove the `smartspare-postgres17-data` volume and start again.

**With docker compose and `.env`** (fixed port 5432, needed by the SQL suite and the doc generators):

```bash
cp .env.example .env                              # then set your own passwords in .env
docker compose up -d                              # PostgreSQL 17 on localhost:5432
db/scripts/reset_local.sh                         # drop + MigrationRunner: bootstrap + all EF migrations
db/scripts/load_testdata.sh                       # demo dataset T001 (2,000 SKUs, ~30 s) + demo-user passwords
db/tests/run_tests.sh                             # SQL test suite (140 assertions)
db/tests/concurrency_test.sh                      # row-lock test (NFR03)
```

Credentials are never stored in Git: copy `.env.example` to `.env` (gitignored) before the first `docker compose up` and
set your own passwords there; `docker compose`, the `db/` scripts and the `tools/` generators read them from `.env`.
The scripts need `psql` (PostgreSQL 17 client) on `PATH`; `tools/verify_database.sh` needs only Docker, because it runs
psql inside its own container.

On Windows run the `.sh` scripts in Git Bash or WSL. Demo users (synthetic) such as `tomas.radzevicius` (brand manager) or `mantas.petrauskas` (service engineer) log in with the password you choose as `SMARTSPARE_DEMO_USER_PASSWORD` in `.env` (any value). No password or hash is stored in Git: T001 writes random placeholder hashes, and `load_testdata.sh` then runs `MigrationRunner set-demo-passwords`, which hashes your password with the application's settings (PBKDF2-HMAC-SHA512, 220,000 iterations).

## Diagrams

| Diagram | Image | Editable (draw.io) |
|---|---|---|
| Logical schema — overview (keys) | `docs/database/diagrams/logical_schema_overview.svg` | `logical_schema_overview.drawio` |
| Logical schema — OLTP core (all columns) | `docs/database/diagrams/logical_schema_core.svg` | `logical_schema_core.drawio` |
| Import data layers raw → staging → core | `docs/database/diagrams/logical_schema_import_layers.svg` | `logical_schema_import_layers.drawio` |
| Data flow through the layers | `docs/database/diagrams/data_flow.svg` | `data_flow.drawio` |
| Solution architecture | `docs/architecture/diagrams/architecture.svg` | `architecture.drawio` |

* **Solution architecture legend:** solid border = implemented, dashed border + "(stage N)" = planned (work plan stages),
  grey = external system; the status is taken from the code and updated when a stage lands.
* **All five diagrams in one file (one page each):** `docs/SmartSpare_diagrams.drawio`.
  Open with [app.diagrams.net](https://app.diagrams.net) (File → Open from → Device), the draw.io desktop app,
  or VS Code with the *Draw.io Integration* extension. Export to PNG/SVG/PDF/VSDX from File → Export as.
* **Schema as code (DBML):** `docs/database/diagrams/smartspare_core.dbml` — 43 tables, 11 enums, 48 relationships,
  8 colour-coded table groups (schemas). References between modules are plain ids, so they have no relationship line
  (ADR 0011). Open [dbdiagram.io](https://dbdiagram.io), create a diagram and paste the file
  (or Import → DBML). Free dbdiagram diagrams are public — do not upload anything confidential.
* The database diagrams, the data dictionary and `db/ddl/smartspare_schema.sql` are **generated** from a database built
  by the migrations with T001 loaded (`docker compose` + `reset_local.sh` + `load_testdata.sh`, then `ANALYZE`):
  `tools/export_ddl.sh && python3 tools/generate_db_docs.py && python3 tools/generate_editable_diagrams.py`
  (needs psql/pg_dump 17 and Graphviz). `data_flow.dot` and `architecture.dot` are written by hand and only rendered.
  Manual layout changes made in draw.io are lost on regeneration — save such versions under a different name.

## Data access layer

| Where | What |
|---|---|
| `src/BuildingBlocks/SmartSpare.Infrastructure/Persistence/` | shared plumbing: unit of work (+ outbox), session context interceptor, `DatabaseErrorMapper`, SQL resource helper for migrations, Platform context (`ref`, `util`, `audit`) |
| `src/Modules/<Module>/…/Infrastructure/Persistence/` | per module: `DbContext`, `Configurations/`, `Migrations/` |
| `db/sql/<module>/` | SQL parts of that module's migrations (triggers, functions, views, grants, seed) |
| `src/Bootstrapper/SmartSpare.MigrationRunner`, `SmartSpare.ModuleCatalog` | migration order of all module databases; bootstrap, migrations (all, or `--module <name>`) and `check-model` locally, in CI and as the Azure migration job (ADR 0020) |

There is no single data project on purpose: every module owns its schema, DbContext and migrations
(ADR 0002, ADR 0011), and EF Core maps the module's domain aggregates directly, with no generic repository
([ADR 0013](docs/adr/0013-domain-model-and-persistence.md)). Details: [DATABASE_DESIGN §2.1](docs/database/DATABASE_DESIGN.md#21-data-access-layer).

## Rules for changing the database

1. Never edit an applied migration — add a new one to the owning module, for example
   `dotnet ef migrations add <Name> --project src/Modules/Fleet/SmartSpare.Modules.Fleet --startup-project src/Bootstrapper/SmartSpare.MigrationRunner --context FleetDbContext --output-dir Infrastructure/Persistence/Migrations`.
   CI fails (`check-model`) when a model change has no migration.
2. Objects EF cannot model (triggers, functions, views, grants, partitions) go into a new numbered file in
   `db/sql/<module>/` and are applied by the migration with `migrationBuilder.Sql` (ADR 0004); never edit a SQL part
   that an applied migration uses.
3. Grant privileges for new objects in the same migration; `db/tests/T03` fails if `util.v_privilege_findings` returns rows.
4. No foreign keys, JOINs or transactions across modules (ADR 0011); a reference to another module is a plain id.
5. A schema change that should differ from the V016 baseline needs an entry with its reason in `tools/schema_diff.allow`.
6. Regenerate the DDL and the docs (see Diagrams). `*.sql` and `*.sh` files are checked out with LF line endings
   (`.gitattributes`), so the scripts run unchanged in the Linux containers.

## Contributing

Development follows GitHub Flow with protected `main`, pull requests and Conventional Commits — see [.github/CONTRIBUTING.md](.github/CONTRIBUTING.md).
