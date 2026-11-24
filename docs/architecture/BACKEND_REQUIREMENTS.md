# SmartSpare — Backend Requirements Compliance

Status as of **24 November 2026** (main after stage 2 and the stage 3 plan). This matrix maps the backend grading rubric of the EHU diploma to evidence in the repository. The database part of the rubric is covered by [DATABASE_DESIGN.md §0](../database/DATABASE_DESIGN.md#0-requirements-compliance-matrix).

**Status values**

| Status | Meaning |
|---|---|
| **done** | Exists on `main` and can be shown. Evidence is a file, test, CI step or ADR. |
| **planned (stage N)** | Decided in the architecture or a stage plan. Not on `main` yet. |
| **gap** | Not covered by the plan yet. A `rubric` issue tracks it. |

A row is marked done only for what is on `main`, not for intentions. Each row is updated by the PR that changes its status.

---

## 1. Minimum requirements

| Requirement | Status | Evidence | Notes |
|---|---|---|---|
| Modern framework | done | .NET 10 LTS, ASP.NET Core 10 ([ADR 0007](../adr/0007-dotnet-10-lts.md)); `net10.0` in [`Directory.Build.props`](../../Directory.Build.props), SDK pinned in `global.json` | Supported until November 2028. |
| Database | done | PostgreSQL 17 ([DATABASE_DESIGN §1](../database/DATABASE_DESIGN.md#1-choice-of-the-dbms)) | Own compliance matrix for the database rubric. |
| ORM | done | EF Core 10 + Npgsql, one DbContext per module, Fluent API configurations ([ADR 0004](../adr/0004-hybrid-ef-core-code-first.md), [ADR 0011](../adr/0011-database-ownership-per-module.md), [DATABASE_DESIGN §2.1](../database/DATABASE_DESIGN.md#21-data-access-layer)) | Triggers and functions stay SQL, applied by migrations (hybrid code-first). |
| Layered architecture | done | Domain / Features / Infrastructure folders per module, Contracts project, host ([ARCHITECTURE_DECISIONS §3](ARCHITECTURE_DECISIONS.md#3-inside-a-module)). Enforced by [`ModuleBoundaryTests`](../../tests/SmartSpare.ArchitectureTests/ModuleBoundaryTests.cs) (Domain has no EF Core, ASP.NET Core or Npgsql dependency; no module references another's implementation) | `Domain/` folders get their first aggregates in stage 3 (Identity, ADR 0013). |
| SOLID | gap | Applied in code: one handler per use case, decorators around handlers ([`Messaging/Decorators`](../../src/BuildingBlocks/SmartSpare.Infrastructure/Messaging/Decorators)), small interfaces ([`Messaging/Abstractions.cs`](../../src/BuildingBlocks/SmartSpare.Infrastructure/Messaging/Abstractions.cs), [`IModuleInstaller`](../../src/BuildingBlocks/SmartSpare.Infrastructure/Modules/IModuleInstaller.cs)), dependency inversion through Contracts | No document explains it with examples for the defence: **#62** (stage 10). |
| API description (OpenAPI/Swagger) | gap | `AddOpenApi()` / `MapOpenApi()` in [`Program.cs`](../../src/Bootstrapper/SmartSpare.Host/Program.cs) | **Mapped only in Development**, so a deployed instance has no API docs. No summaries, response codes or examples. **#54** (stage 4). |
| Global error handling | done | `AddProblemDetails`, `UseExceptionHandler`, `UseStatusCodePages` in [`Program.cs`](../../src/Bootstrapper/SmartSpare.Host/Program.cs). `Result` → RFC 9457 ProblemDetails in [`ResultHttpExtensions`](../../src/BuildingBlocks/SmartSpare.Infrastructure/Http/ResultHttpExtensions.cs) (tested in `ResultHttpExtensionsTests`). SQLSTATE SS1xx–SS4xx → errors in [`DatabaseErrorMapper`](../../src/BuildingBlocks/SmartSpare.Infrastructure/Persistence/DatabaseErrorMapper.cs) (tested in `PersistenceTests`) | `DatabaseErrorMapper` is not called from a handler yet. The first business endpoints (stage 4) wire it in. |
| Structured logging | done | `ILogger` with source-generated `[LoggerMessage]` templates ([`LoggingDecorator.cs`](../../src/BuildingBlocks/SmartSpare.Infrastructure/Messaging/Decorators/LoggingDecorator.cs)). OpenTelemetry log export with scopes and OTLP ([`ServiceDefaultsExtensions`](../../src/BuildingBlocks/SmartSpare.ServiceDefaults/ServiceDefaultsExtensions.cs)) | Locally the logs are searchable in the Aspire dashboard. Central production logs: see section 2. |
| Production deployment reachable by external users | planned (stage 9) | [ADR 0009](../adr/0009-azure-hosting-and-delivery.md), [ARCHITECTURE_DECISIONS §8](ARCHITECTURE_DECISIONS.md#8-azure-and-delivery), #16 | Nothing is deployed yet. #48 (forwarded headers) must land before the first deployment. |
| Unit-test coverage ≥ 70 % of business logic, measured by a tool | gap | Unit tests in `tests/*.UnitTests`; CI collects coverage with coverlet (`--collect "XPlat Code Coverage"`, [`ci.yml`](../../.github/workflows/ci.yml)) | **Collected but not reported or gated.** No coverage figure exists. **#63** (stage 4). |
| No paid libraries | done | All packages are free/OSS ([`Directory.Packages.props`](../../Directory.Packages.props)). Own handlers are used instead of MediatR, which is commercially licensed ([ADR 0005](../adr/0005-own-handlers-instead-of-mediatr.md)); commercial UI suites rejected ([ADR 0006](../adr/0006-frontend-blazor-wasm-mudblazor-bff.md)) | MudBlazor (MIT) arrives in stage 3. |
| Vulnerable-package checks | done | NuGet Audit runs on every restore. For `net10.0` it covers direct and transitive packages (`NuGetAuditMode=all` by default), and `TreatWarningsAsErrors` turns NU1901–NU1904 into restore failures ([NuGet docs](https://learn.microsoft.com/nuget/concepts/auditing-packages)). CI restores in locked mode. [Dependabot](../../.github/dependabot.yml) runs weekly for NuGet and Actions | **Implicit:** no CI step names the check, nothing reports "0 vulnerable", and new advisories surface only on the next push. **#64** (stage 4) adds an explicit step and a weekly run. |
| Version control | done | Git + GitHub, GitHub Flow, Conventional Commits, PR template ([CONTRIBUTING](../../.github/CONTRIBUTING.md)) | |
| JSON | done | Minimal APIs with System.Text.Json; errors as `application/problem+json` | |
| No secrets in the repository | planned (stage 3) | `.env` (git-ignored), user-secrets locally, Key Vault in Azure ([ARCHITECTURE_DECISIONS §7](ARCHITECTURE_DECISIONS.md#7-security), [`.gitignore`](../../.gitignore)); DB logins SCRAM-SHA-256 | **One finding:** `.env.example` still contains the demo users' password `SMARTSPARE_DEMO_USER_PASSWORD` (synthetic data). Removed by **#7** in PR 3a. |
| Automatic deployment from the repository | planned (stage 9) | [ADR 0009](../adr/0009-azure-hosting-and-delivery.md): GitHub Actions → image to ACR → EF migration bundle → deploy, OIDC sign-in | CI (build, tests, checks) is done; CD is not. |
| No home-made crypto/JWT | done | No cryptographic code in the repository; an HttpOnly cookie is used instead of self-issued JWTs. Sign-in uses ASP.NET Core Identity, its V3 password hasher and Data Protection ([ADR 0014](../adr/0014-browser-sign-in-through-bff-endpoints.md)) | Identity is implemented in stage 3. |
| No outdated/educational libraries | done | .NET 10 LTS packages, Dependabot weekly, lock files ([`ci.yml`](../../.github/workflows/ci.yml) `--locked-mode`) | |
| No stub APIs | gap | `GET /api/<module>/ping` in every module (`src/Modules/*/…/Features/Ping`) | Skeleton endpoints from stage 1, to be removed before the defence: **#55** (stage 10). |
| Modularity | done | Six modules + BuildingBlocks ([ADR 0002](../adr/0002-modular-monolith.md), [ADR 0003](../adr/0003-module-boundaries-and-integration-events.md)), schema per module ([ADR 0011](../adr/0011-database-ownership-per-module.md)). Enforced by `ModuleBoundaryTests`, `EvolvabilityTests` and `ModuleCatalogTests` | |

## 2. Maximum requirements

| Requirement | Status | Evidence | Notes |
|---|---|---|---|
| CI | done | [`ci.yml`](../../.github/workflows/ci.yml): locked restore, `-warnaserror` build, format check, model-drift check, unit tests, integration tests (Testcontainers), SQL suite on the EF-built database, stale-terms check | |
| CD: Docker image, registry, automatic deployment, secrets injected | planned (stage 9) | [ADR 0009](../adr/0009-azure-hosting-and-delivery.md): container from day one, ACR, Bicep, GitHub Actions with OIDC, Key Vault + Managed Identity; #16 | No Dockerfile, `infra/` or deploy workflow yet. |
| Microservices deployable independently | gap | The modular monolith is microservice-ready ([ADR 0002](../adr/0002-modular-monolith.md)): schema per module, Contracts, Outbox/Inbox | **Decision pending:** scope, gateway, independent deployment. **#56** (stage 9). |
| Asynchronous messaging (broker) | gap | Transactional Outbox/Inbox with `FOR UPDATE SKIP LOCKED` dispatcher, in-process transport ([ADR 0003](../adr/0003-module-boundaries-and-integration-events.md), `Messaging/Outbox`) | **Decision pending:** RabbitMQ or Azure Service Bus. **#57** (stage 9). |
| Caching (e.g. Redis) | gap | — | Stock is deliberately never cached ([DATABASE_DESIGN §1.2](../database/DATABASE_DESIGN.md#12-why-one-dbms-and-not-postgresql--clickhouseredismongodb)). HybridCache + Redis for reference data, catalogue and dashboards: **#58** (stage 8). |
| Monitoring (metrics + dashboards) | gap | OpenTelemetry metrics (ASP.NET Core, HttpClient, runtime) exported over OTLP; Aspire dashboard locally ([`ServiceDefaultsExtensions`](../../src/BuildingBlocks/SmartSpare.ServiceDefaults/ServiceDefaultsExtensions.cs)) | No production dashboards. **Decision pending:** Azure Monitor/App Insights or Grafana stack. **#59** (stage 9). |
| Centralized structured logs with search | gap | OTLP log export; local search in the Aspire dashboard | No central store outside development. **#59** (decision pending). |
| Request tracing with a correlation id | gap | OpenTelemetry tracing (ASP.NET Core + HttpClient, W3C `traceparent`); ASP.NET Core adds `traceId` to the ProblemDetails of the exception handler | Not verified for our own `Result` errors; no end-to-end trace is shown. **#60** (stage 3). |
| Alerting to messenger/e-mail | gap | — | **#59** (decision pending). |
| Quality gate: static analysis | gap | Roslyn/.NET analyzers at `latest-recommended` with `TreatWarningsAsErrors`; `dotnet format --verify-no-changes` in CI | No dedicated analysis with duplication and maintainability metrics. SonarQube Cloud Free was checked: private repositories up to 50k LOC, main branch only, no custom gates. **#61** (stage 4). |
| Quality gate: tests | done | Unit, integration, architecture and SQL tests must pass in CI | |
| Quality gate: coverage ~80 % | gap | Coverage collected, not gated | **#63**: 70 % gate first, 80 % by stage 8. |
| Quality gate: deploy only when green | planned (stage 9) | The deploy job will depend on the CI jobs (ADR 0009) | |

## 3. Point deductions to avoid

| Mistake (−1 each) | Status | Evidence | Notes |
|---|---|---|---|
| Insecure password hashing | planned (stage 3) | ASP.NET Core Identity V3: PBKDF2-HMAC-SHA512, 128-bit salt, format enforced by a CHECK ([DATABASE_DESIGN §8.4](../database/DATABASE_DESIGN.md#84-passwords-and-secrets)) | **Finding:** 100,000 iterations is below the current OWASP value of 220,000 for PBKDF2-HMAC-SHA512 ([OWASP Password Storage Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html#pbkdf2)). Fixed in PR 3a: `IterationCount = 220_000`, old hashes rehashed on the next sign-in ([STAGE_3_PLAN §2.3](../stage-3/STAGE_3_PLAN.md#23-password-hash-compatibility-7)). |
| Plain-text secrets | planned (stage 3) | Secrets in `.env` (ignored), user-secrets, Key Vault in Azure | The demo password in `.env.example` (see section 1) is removed by **#7**. |
| Missing input validation | done | FluentValidation validators run for every command and query by [`ValidationDecorator`](../../src/BuildingBlocks/SmartSpare.Infrastructure/Messaging/Decorators/ValidationDecorator.cs) and short-circuit with a `ValidationError` → 400 (tested in `HandlerPipelineTests`). The database adds 146 CHECK constraints and trigger validation as a second line of defence | The same validators are shared with the Blazor forms through ApiContracts (stage 3). |
| Missing error handling | done | See "Global error handling" in section 1 | |
| Wrong HTTP status codes | done | `ErrorType` → 400/403/404/409/422, otherwise 500, in one place ([`ResultHttpExtensions.StatusCodeFor`](../../src/BuildingBlocks/SmartSpare.Infrastructure/Http/ResultHttpExtensions.cs)), unit-tested | 401/403/429 for authentication are specified in [STAGE_3_PLAN §1.2, §9.2](../stage-3/STAGE_3_PLAN.md). |
| Database/docs mismatch | done | DDL, data dictionary and diagrams are generated from the EF-built database (`tools/export_ddl.sh`, `tools/generate_db_docs.py`). CI checks model drift, and `tools/schema_diff.sh` diffs against the V016 baseline | |
| Missing API docs | gap | — | **#54** (stage 4). |
| Poor code quality | done | Nullable reference types, analyzers at `latest-recommended`, `TreatWarningsAsErrors`, `.editorconfig` + format check, architecture tests, ADRs | A dedicated static-analysis gate is **#61**. |
| Inefficient queries | done | Every index justified with its query ([DATABASE_DESIGN §7](../database/DATABASE_DESIGN.md#7-indexes)); plans checked by `db/tests/T04`; timings at the BA scale (§7.3); CQRS-lite projections with `AsNoTracking` ([ARCHITECTURE_DECISIONS §3](ARCHITECTURE_DECISIONS.md#3-inside-a-module)) | Application queries arrive with stage 4. Each query PR checks its plan. |
| Hardcoded configuration | done | Connection strings, OTLP endpoint and secrets come from configuration, user-secrets or Aspire. Nothing environment-specific is in code or `appsettings*.json` | |
| No modularity | done | See "Modularity" in section 1 | |
| Magic strings | planned (stage 3) | Error codes are constants per error. SQLSTATEs are mapped in one place ([`DatabaseErrorMapper`](../../src/BuildingBlocks/SmartSpare.Infrastructure/Persistence/DatabaseErrorMapper.cs)) | Role, claim, policy and problem-code names become shared constants in `SmartSpare.ApiContracts` ([STAGE_3_PLAN §3, §8.4](../stage-3/STAGE_3_PLAN.md)). Policy roles are checked against the SQL role lists by a test (§3.2). |
| Code duplication | gap | Shared pipeline (decorators, `ResultHttpExtensions`, `ModuleDatabase`) avoids per-module copies | Not measured. Duplication metrics are part of **#61**. |
| No test coverage | gap | Tests exist at every level (unit, integration, architecture, 97 SQL assertions); coverage is not measured | **#63**. |
| Not following REST | planned (stage 4) | ADR 0012: versioned `/api/v1/...` from the first public business endpoint | **Convention:** plural resources, and state transitions as **POST sub-resources** (`POST /api/v1/inventory/transfers/{id}/approval`, `.../cancellation`), never `PUT status` or verbs in the path; 201 + `Location` on create; ProblemDetails on errors. Documented in OpenAPI by **#54**. The current `/api/<module>/ping` stubs are unversioned and go away with **#55**. |

---

## 4. Gap issues

All gaps carry the `rubric` label: [open rubric issues](https://github.com/sgorgun/SmartSpare/issues?q=is%3Aopen+label%3Arubric).

| Issue | Topic | Stage | Note |
|---|---|---|---|
| #54 | API documentation outside Development (OpenAPI + Scalar), REST conventions | 4 | |
| #55 | Remove the stub `/ping` endpoints | 10 | |
| #56 | Microservice topology and independent deployment | 9 | **decision pending** |
| #57 | Message broker (RabbitMQ or Azure Service Bus) | 9 | **decision pending** |
| #58 | Caching with HybridCache + Redis | 8 | |
| #59 | Monitoring, dashboards, centralized logs, alerting | 9 | **decision pending** |
| #60 | Trace id in every ProblemDetails, end-to-end trace | 3 | |
| #61 | Static code analysis quality gate | 4 | |
| #62 | SOLID and layering explanation for the defence | 10 | |
| #63 | Coverage report and gate | 4 | |
| #64 | Explicit vulnerable-package check in CI | 4 | |
