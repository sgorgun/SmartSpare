# SmartSpare

Agricultural machinery maintenance planning and spare-parts demand forecasting (EHU bachelor's diploma project, Sergei Gorgun, 2026/27).

Stack: ASP.NET Core 10 · Blazor WebAssembly + MudBlazor · EF Core 10 + Npgsql · **PostgreSQL 17** · ML.NET · Quartz.NET ·
YARP · RabbitMQ · OpenTelemetry + Grafana stack · Azure Container Apps.

Architecture: **one codebase, two topologies**. Development and tests run a modular monolith (one host). Production runs
every module as its own service behind a YARP gateway (ADR 0015, from stage 4). Start with
[ARCHITECTURE_DECISIONS.md](docs/architecture/ARCHITECTURE_DECISIONS.md) and the [ADRs](docs/adr/README.md).
Compliance with the backend grading rubric: [BACKEND_REQUIREMENTS.md](docs/architecture/BACKEND_REQUIREMENTS.md).

## Quick start (application)

Prerequisites: .NET 10 SDK (version pinned in `global.json`) and Docker Desktop (running).

```bash
dotnet run --project src/SmartSpare.AppHost      # PostgreSQL 17 + migrations + host + Aspire dashboard
```

The AppHost starts PostgreSQL 17, runs `SmartSpare.MigrationRunner` (cluster bootstrap and the EF Core migrations of
every module) and starts the host once the migrations have finished. The Aspire dashboard opens in the browser and
lists the `host` resource with its URL. Every module answers
`GET /api/<module>/ping` (`identity`, `inventory`, `fleet`, `procurement`, `forecasting`, `reporting`) to a signed-in
user; health probes `/health/live` and `/health/ready` and the OpenAPI document `/openapi/v1.json` (Development) need
no sign-in.

To run the host alone, without Aspire: `dotnet run --project src/Bootstrapper/SmartSpare.Host` (https://localhost:7080; the
session cookies are `Secure`, so the default launch profile is HTTPS).

### Sign-in (server side, stage 3)

The browser signs in through the BFF endpoints of the host ([ADR 0014](docs/adr/0014-browser-sign-in-through-bff-endpoints.md),
[ADR 0016](docs/adr/0016-authentication-behind-the-gateway.md)): `GET /bff/antiforgery` returns a request token,
`POST /bff/login` with `{ "userName", "password", "rememberMe" }` and the token in the `X-XSRF-TOKEN` header sets the
HttpOnly cookie `__Host-SmartSpare`, `GET /bff/me` shows the session, and `POST /bff/logout` ends it. The cookie and
antiforgery cookies are `Secure`, so use the HTTPS URL. Demo users and their password: see the database quick start.
The sign-in page arrives with the Blazor client (PR 3d).

Module endpoints also accept an **internal token** (`Authorization: Bearer`), the 2-minute ES256 token the gateway
will mint in stage 4. The single host validates it with a key that never lives in a file of the repository. Without a
key, every bearer token is rejected, which is the safe default. To try the token path locally, put a P-256 key into the
host's user secrets:

```bash
openssl ecparam -name prime256v1 -genkey -noout | openssl pkcs8 -topk8 -nocrypt > key.pem
dotnet user-secrets set InternalTokens:SigningKey "$(cat key.pem)" --project src/Bootstrapper/SmartSpare.Host
rm key.pem
```

Services in stage 4 get only the public key as a JWKS document (`InternalTokens:Jwks`).

| Task | Command |
|---|---|
| **All checks, exactly as CI** (Docker running) | `tools/check.sh` — stale terms, locked restore, Release build with `-warnaserror`, format, check-model, tests with coverage, integration tests, database verification |
| Checks without Docker (the CI "Build and test" job) | `tools/check.sh fast` |
| Selected checks | `tools/check.sh <step> ...` with steps `stale-terms restore build format check-model test integration database` |
| Schema diff (Docker) | `tools/schema_diff.sh` — EF-built schema vs. the frozen V016 baseline |

Run `tools/check.sh` (Git Bash on Windows) before pushing. Stage new files first: the stale-terms check scans tracked
files only. While iterating, plain `dotnet build` / `dotnet test --filter "Category!=Integration"` are fine, but only
the script reproduces CI.

CI (`.github/workflows/ci.yml`) runs the same script on every push and pull request to `main`: the "Build and test" job
runs the steps of `tools/check.sh fast`, the integration job runs `integration` and `database`.

## Repository layout

```
src/
  Bootstrapper/SmartSpare.Host          single host (development, tests): all modules, BFF (stage 3), health, OpenAPI;
                                        the gateway and one host per module service follow in stage 4 (ADR 0015)
  Bootstrapper/SmartSpare.ModuleCatalog the modules and their databases in migration order (shared by host and runner)
  Bootstrapper/SmartSpare.MigrationRunner  bootstrap + EF migrations of every context; check-model (dev and CI only)
  BuildingBlocks/
    SmartSpare.SharedKernel             Entity, AggregateRoot, ValueObject, Result, Error — no dependencies
    SmartSpare.Infrastructure           command/query handlers + decorators, IModuleInstaller, Result → ProblemDetails,
                                        Persistence (Platform DbContext, unit of work, outbox/inbox, interceptors)
    SmartSpare.ServiceDefaults          OpenTelemetry, health checks, service discovery, HTTP resilience
  Modules/<Module>/                     Identity, Inventory, Fleet, Procurement, Forecasting, Reporting
    SmartSpare.Modules.<Module>         internal implementation: Domain/, Features/<UseCase>/, Infrastructure/,
                                        public <Module>ModuleInstaller
    SmartSpare.Modules.<Module>.Contracts  public surface: integration events, read-only query interfaces
  Web/SmartSpare.ApiContracts           HTTP DTOs + validators shared with the Blazor client
  SmartSpare.AppHost                    .NET Aspire, local development only
tests/
  SmartSpare.SharedKernel.UnitTests, SmartSpare.Infrastructure.UnitTests
  SmartSpare.ArchitectureTests          module boundaries (NetArchTest + project references)
  SmartSpare.Host.IntegrationTests      in-memory smoke test of every module endpoint
  SmartSpare.Persistence.IntegrationTests  migrations, ownership, unit of work and outbox on postgres:17 (Testcontainers)
db/
  bootstrap/000_bootstrap_cluster.sql   roles + database (once per server; run by the MigrationRunner or the server admin)
  sql/<module>/                         SQL parts of the EF migrations: triggers, functions, views, grants, seed
                                        (embedded in the module assembly, applied by migrationBuilder.Sql — ADR 0004)
  testdata/T001__demo_dataset.sql       deterministic synthetic dataset (dev/test only)
  tests/                                SQL test suite (97 assertions) + concurrency test
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
db/tests/run_tests.sh                             # SQL test suite (97 assertions)
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
| `src/Bootstrapper/SmartSpare.MigrationRunner`, `SmartSpare.ModuleCatalog` | migration order of all module databases; bootstrap, migrations and `check-model` locally and in CI |

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
