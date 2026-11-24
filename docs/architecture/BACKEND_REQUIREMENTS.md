# SmartSpare — Backend Requirements Compliance

Status as of **24 November 2026**: `main` after stage 2, the stage 3 plan and the product owner's topology decisions D1–D4 (ADRs 0015–0019). This matrix maps the backend grading rubric of the EHU diploma to evidence in the repository. The database part of the rubric is covered by [DATABASE_DESIGN.md §0](../database/DATABASE_DESIGN.md#0-requirements-compliance-matrix).

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
| Layered architecture | done | Domain / Features / Infrastructure folders per module, Contracts project, host ([ARCHITECTURE_DECISIONS §3](ARCHITECTURE_DECISIONS.md#3-inside-a-module)). Enforced by [`ModuleBoundaryTests`](../../tests/SmartSpare.ArchitectureTests/ModuleBoundaryTests.cs) (Domain has no EF Core, ASP.NET Core or Npgsql dependency; no module references another's implementation) | Identity maps its domain aggregates directly (`Domain/`, ADR 0013); the other modules follow in their stages. |
| SOLID | gap | Applied in code: one handler per use case, decorators around handlers ([`Messaging/Decorators`](../../src/BuildingBlocks/SmartSpare.Infrastructure/Messaging/Decorators)), small interfaces ([`Messaging/Abstractions.cs`](../../src/BuildingBlocks/SmartSpare.Infrastructure/Messaging/Abstractions.cs), [`IModuleInstaller`](../../src/BuildingBlocks/SmartSpare.Infrastructure/Modules/IModuleInstaller.cs)), dependency inversion through Contracts | No document explains it with examples for the defence: **#62** (stage 11). |
| API description (OpenAPI/Swagger) | planned (stage 4) | Today: `AddOpenApi()` / `MapOpenApi()` in [`Program.cs`](../../src/Bootstrapper/SmartSpare.Host/Program.cs), Development only. Decided: one OpenAPI document per module, Scalar UI at `/scalar`, published without sign-in, internal endpoints excluded ([ADR 0019](../adr/0019-public-api-documentation.md)) | Mapped only in Development until stage 4. Summaries, response codes and examples come with every endpoint: **#54** (stage 4). |
| Global error handling | done | `AddProblemDetails`, `UseExceptionHandler`, `UseStatusCodePages` in [`Program.cs`](../../src/Bootstrapper/SmartSpare.Host/Program.cs). `Result` → RFC 9457 ProblemDetails in [`ResultHttpExtensions`](../../src/BuildingBlocks/SmartSpare.Infrastructure/Http/ResultHttpExtensions.cs) (tested in `ResultHttpExtensionsTests`). SQLSTATE SS1xx–SS4xx → errors in [`DatabaseErrorMapper`](../../src/BuildingBlocks/SmartSpare.Infrastructure/Persistence/DatabaseErrorMapper.cs) (tested in `PersistenceTests`) | `DatabaseErrorMapper` is not called from a handler yet. The first business endpoints (stage 5) wire it in. |
| Structured logging | done | `ILogger` with source-generated `[LoggerMessage]` templates ([`LoggingDecorator.cs`](../../src/BuildingBlocks/SmartSpare.Infrastructure/Messaging/Decorators/LoggingDecorator.cs)). OpenTelemetry log export with scopes and OTLP ([`ServiceDefaultsExtensions`](../../src/BuildingBlocks/SmartSpare.ServiceDefaults/ServiceDefaultsExtensions.cs)) | Locally the logs are searchable in the Aspire dashboard. Central production logs: see section 2. |
| Production deployment reachable by external users | planned (stage 4) | [ADR 0015](../adr/0015-deployment-topology-microservices-behind-yarp-gateway.md), [ADR 0009](../adr/0009-azure-hosting-and-delivery.md) (Azure Container Apps), [ARCHITECTURE_DECISIONS §8](ARCHITECTURE_DECISIONS.md#8-azure-and-delivery) | Nothing is deployed yet. The walking skeleton is deployed to a dev environment in stage 4, and the production environment follows in stage 10 (#16). #48 (forwarded headers) must land before the first deployment. Cost: about €22–23/month within NFR07 (ADR 0009 amendment, #66 closed). Every app scales to zero; the warm-up workflow runs before the defence. |
| Unit-test coverage ≥ 70 % of business logic, measured by a tool | gap | Unit tests in `tests/*.UnitTests`; CI collects coverage with coverlet (`--collect "XPlat Code Coverage"`, [`ci.yml`](../../.github/workflows/ci.yml)) | **Collected but not reported or gated.** No coverage figure exists. **#63** (stage 5). |
| No paid libraries | done | All packages are free/OSS ([`Directory.Packages.props`](../../Directory.Packages.props)). Own handlers are used instead of MediatR, which is commercially licensed ([ADR 0005](../adr/0005-own-handlers-instead-of-mediatr.md)); commercial UI suites rejected ([ADR 0006](../adr/0006-frontend-blazor-wasm-mudblazor-bff.md)) | MudBlazor (MIT) arrives in stage 3. |
| Vulnerable-package checks | done | NuGet Audit runs on every restore. For `net10.0` it covers direct and transitive packages (`NuGetAuditMode=all` by default), and `TreatWarningsAsErrors` turns NU1901–NU1904 into restore failures ([NuGet docs](https://learn.microsoft.com/nuget/concepts/auditing-packages)). CI restores in locked mode. [Dependabot](../../.github/dependabot.yml) runs weekly for NuGet and Actions | **Implicit:** no CI step names the check, nothing reports "0 vulnerable", and new advisories surface only on the next push. **#64** (stage 5) adds an explicit step and a weekly run. |
| Version control | done | Git + GitHub, GitHub Flow, Conventional Commits, PR template ([CONTRIBUTING](../../.github/CONTRIBUTING.md)) | |
| JSON | done | Minimal APIs with System.Text.Json; errors as `application/problem+json` | |
| No secrets in the repository | done | `.env` (git-ignored), user-secrets locally, Key Vault in Azure ([ARCHITECTURE_DECISIONS §7](ARCHITECTURE_DECISIONS.md#7-security), [`.gitignore`](../../.gitignore)); DB logins SCRAM-SHA-256. [`.env.example`](../../.env.example) holds placeholders only; the demo users' password is chosen locally and hashed at load time ([`DemoPasswords`](../../src/Bootstrapper/SmartSpare.MigrationRunner/DemoPasswords.cs), #7) | T001 stores random placeholder hashes that no password verifies. |
| Automatic deployment from the repository | planned (stage 4) | [ADR 0015](../adr/0015-deployment-topology-microservices-behind-yarp-gateway.md) §6: GitHub Actions builds one image per changed service → ACR → that module's EF migration bundle → new Container Apps revision, OIDC sign-in | CI (build, tests, checks) is done; CD is not. |
| No home-made crypto/JWT | done | No cryptographic code in the repository. Sign-in uses ASP.NET Core Identity, its V3 password hasher and Data Protection ([ADR 0014](../adr/0014-browser-sign-in-through-bff-endpoints.md)). The internal gateway-to-service tokens are created with `JsonWebTokenHandler` and validated with JwtBearer (ES256, Microsoft libraries), rather than with custom code; there are no tokens in the browser ([ADR 0016](../adr/0016-authentication-behind-the-gateway.md)) | Identity is implemented in stage 3, internal tokens in stages 3–4. |
| No outdated/educational libraries | done | .NET 10 LTS packages, Dependabot weekly, lock files ([`ci.yml`](../../.github/workflows/ci.yml) `--locked-mode`) | |
| No stub APIs | gap | `GET /api/<module>/ping` in every module (`src/Modules/*/…/Features/Ping`) | Skeleton endpoints from stage 1, to be removed before the defence: **#55** (stage 11). |
| Modularity | done | Six modules + BuildingBlocks ([ADR 0002](../adr/0002-modular-monolith.md), [ADR 0003](../adr/0003-module-boundaries-and-integration-events.md)), schema per module ([ADR 0011](../adr/0011-database-ownership-per-module.md)). Enforced by `ModuleBoundaryTests`, `EvolvabilityTests` and `ModuleCatalogTests` | In production each module is its own service ([ADR 0015](../adr/0015-deployment-topology-microservices-behind-yarp-gateway.md)). |

## 2. Maximum requirements

| Requirement | Status | Evidence | Notes |
|---|---|---|---|
| CI | done | [`ci.yml`](../../.github/workflows/ci.yml): locked restore, `-warnaserror` build, format check, model-drift check, unit tests, integration tests (Testcontainers), SQL suite on the EF-built database, stale-terms check | |
| CD: Docker image, registry, automatic deployment, secrets injected | planned (stage 4) | [ADR 0015](../adr/0015-deployment-topology-microservices-behind-yarp-gateway.md) §6, [ADR 0009](../adr/0009-azure-hosting-and-delivery.md): one image per service via SDK container publishing, Azure Container Registry, per-service deployment to Container Apps, Key Vault secret references with Managed Identity, OIDC in GitHub Actions; #16 | No images, `infra/` or deploy workflow yet. |
| Microservices deployable independently | planned (stage 4) | [ADR 0015](../adr/0015-deployment-topology-microservices-behind-yarp-gateway.md): gateway + six services, one image and one Container Apps revision each, per-service pipeline with path filters, backward-compatible contracts and events; single host kept for development and tests | Implementation: **#56**. Topology test in CI. |
| Asynchronous messaging (broker) | planned (stage 4) | Today: transactional Outbox/Inbox with a `FOR UPDATE SKIP LOCKED` dispatcher and in-process transport ([ADR 0003](../adr/0003-module-boundaries-and-integration-events.md), `Messaging/Outbox`). Decided: RabbitMQ (CloudAMQP free plan in production, RabbitMQ 4.4 container locally) with the official `RabbitMQ.Client` 7.x, publisher confirms, quorum queues, delayed retry, dead-lettering, designed within the plan's limits ([ADR 0017](../adr/0017-rabbitmq-integration-event-transport.md)) | Implementation: **#57**. |
| Caching (e.g. Redis) | gap | — | Stock is deliberately never cached ([DATABASE_DESIGN §1.2](../database/DATABASE_DESIGN.md#12-why-one-dbms-and-not-postgresql--clickhouseredismongodb)). HybridCache + Redis for reference data, catalogue and dashboards: **#58** (stage 9). |
| Monitoring (metrics + dashboards) | planned (stage 4) | Today: OpenTelemetry metrics exported over OTLP, Aspire dashboard locally ([`ServiceDefaultsExtensions`](../../src/BuildingBlocks/SmartSpare.ServiceDefaults/ServiceDefaultsExtensions.cs)). Decided: OTLP → Grafana Cloud Free (Prometheus-compatible metrics), provisioned Grafana dashboards; the self-hosted stack runs locally ([ADR 0018](../adr/0018-observability-with-the-grafana-stack.md)) | Implementation: **#59**. |
| Centralized structured logs with search | planned (stage 4) | OTLP logs → Loki in Grafana Cloud Free (14-day retention, 50 GB/month), searched in Grafana by service and trace id; self-hosted Loki locally ([ADR 0018](../adr/0018-observability-with-the-grafana-stack.md)) | Implementation: **#59**. |
| Request tracing with a correlation id | planned (stage 3) | Today: OpenTelemetry tracing (ASP.NET Core + HttpClient, W3C `traceparent`); ASP.NET Core adds `traceId` to the exception handler's ProblemDetails. Decided: trace id in every ProblemDetails (#60, stage 3); traces across gateway, services and RabbitMQ in Tempo (stage 4, [ADR 0018](../adr/0018-observability-with-the-grafana-stack.md)) | **#60**, **#59**. |
| Alerting to messenger/e-mail | planned (stage 4) | Grafana Cloud alert rules as code (5xx rate, p95 latency, outbox age, dead letters, queued messages, broker quota, free-tier usage) to Telegram and e-mail contact points ([ADR 0018](../adr/0018-observability-with-the-grafana-stack.md)) | Implementation: **#59**; live in production in stage 10. |
| Quality gate: static analysis | gap | Roslyn/.NET analyzers at `latest-recommended` with `TreatWarningsAsErrors`; `dotnet format --verify-no-changes` in CI | No dedicated analysis with duplication and maintainability metrics. SonarQube Cloud Free was checked: private repositories up to 50k LOC, main branch only, no custom gates. **#61** (stage 5). |
| Quality gate: tests | done | Unit, integration, architecture and SQL tests must pass in CI | |
| Quality gate: coverage ~80 % | gap | Coverage collected, not gated | **#63**: 70 % gate first, 80 % by stage 9. |
| Quality gate: deploy only when green | planned (stage 4) | Each service's deploy job depends on the shared CI gate ([ADR 0015](../adr/0015-deployment-topology-microservices-behind-yarp-gateway.md) §6) | |

## 3. Point deductions to avoid

| Mistake (−1 each) | Status | Evidence | Notes |
|---|---|---|---|
| Insecure password hashing | done | ASP.NET Core Identity V3: PBKDF2-HMAC-SHA512 with **220,000 iterations**, the current OWASP value ([OWASP Password Storage Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html#pbkdf2)), 128-bit salt; one configuration in [`IdentityModuleInstaller.ConfigurePasswordHasher`](../../src/Modules/Identity/SmartSpare.Modules.Identity/IdentityModuleInstaller.cs), used by the host and the demo-password tool; format enforced by a CHECK ([DATABASE_DESIGN §8.4](../database/DATABASE_DESIGN.md#84-passwords-and-secrets)). Tests decode the V3 header (`PasswordHashingTests`, T03) | Hashes with fewer iterations keep verifying and are rehashed at sign-in (`SuccessRehashNeeded`, stage 3b). |
| Plain-text secrets | done | Secrets in `.env` (ignored), user-secrets, Key Vault in Azure | The demo password was removed from `.env.example` (#7). |
| Missing input validation | done | FluentValidation validators run for every command and query by [`ValidationDecorator`](../../src/BuildingBlocks/SmartSpare.Infrastructure/Messaging/Decorators/ValidationDecorator.cs) and short-circuit with a `ValidationError` → 400 (tested in `HandlerPipelineTests`). The database adds 146 CHECK constraints and trigger validation as a second line of defence | The same validators are shared with the Blazor forms through ApiContracts (stage 3). |
| Missing error handling | done | See "Global error handling" in section 1 | |
| Wrong HTTP status codes | done | `ErrorType` → 400/403/404/409/422, otherwise 500, in one place ([`ResultHttpExtensions.StatusCodeFor`](../../src/BuildingBlocks/SmartSpare.Infrastructure/Http/ResultHttpExtensions.cs)), unit-tested | 401/403/429 for authentication are specified in [STAGE_3_PLAN §1.2, §9.2](../stage-3/STAGE_3_PLAN.md). |
| Database/docs mismatch | done | DDL, data dictionary and diagrams are generated from the EF-built database (`tools/export_ddl.sh`, `tools/generate_db_docs.py`). CI checks model drift, and `tools/schema_diff.sh` diffs against the V016 baseline | |
| Missing API docs | planned (stage 4) | [ADR 0019](../adr/0019-public-api-documentation.md) | **#54** (stage 4). |
| Poor code quality | done | Nullable reference types, analyzers at `latest-recommended`, `TreatWarningsAsErrors`, `.editorconfig` + format check, architecture tests, ADRs | A dedicated static-analysis gate is **#61**. |
| Inefficient queries | done | Every index justified with its query ([DATABASE_DESIGN §7](../database/DATABASE_DESIGN.md#7-indexes)); plans checked by `db/tests/T04`; timings at the BA scale (§7.3); CQRS-lite projections with `AsNoTracking` ([ARCHITECTURE_DECISIONS §3](ARCHITECTURE_DECISIONS.md#3-inside-a-module)) | Application queries arrive with stage 5. Each query PR checks its plan. |
| Hardcoded configuration | done | Connection strings, OTLP endpoint and secrets come from configuration, user-secrets or Aspire. Nothing environment-specific is in code or `appsettings*.json` | |
| No modularity | done | See "Modularity" in section 1 | |
| Magic strings | planned (stage 3) | Error codes are constants per error. SQLSTATEs are mapped in one place ([`DatabaseErrorMapper`](../../src/BuildingBlocks/SmartSpare.Infrastructure/Persistence/DatabaseErrorMapper.cs)) | Role, claim, policy and problem-code names become shared constants in `SmartSpare.ApiContracts` ([STAGE_3_PLAN §3, §8.4](../stage-3/STAGE_3_PLAN.md)). Policy roles are checked against the SQL role lists by a test (§3.2). |
| Code duplication | gap | Shared pipeline (decorators, `ResultHttpExtensions`, `ModuleDatabase`) avoids per-module copies | Not measured. Duplication metrics are part of **#61**. |
| No test coverage | gap | Tests exist at every level (unit, integration, architecture, 97 SQL assertions); coverage is not measured | **#63**. |
| Not following REST | planned (stage 5) | ADR 0012: versioned `/api/v1/...` from the first public business endpoint | **Convention:** plural resources, and state transitions as **POST sub-resources** (`POST /api/v1/inventory/transfers/{id}/approval`, `.../cancellation`), never `PUT status` or verbs in the path; 201 + `Location` on create; ProblemDetails on errors. Documented in OpenAPI by **#54**. The current `/api/<module>/ping` stubs are unversioned and go away with **#55**. |

---

## 4. Rubric issues

All rubric items that are not done carry the `rubric` label: [open rubric issues](https://github.com/sgorgun/SmartSpare/issues?q=is%3Aopen+label%3Arubric).

| Issue | Topic | Stage | Decision |
|---|---|---|---|
| #54 | API documentation (OpenAPI + Scalar, public), REST conventions | 4 | ADR 0019 |
| #55 | Remove the stub `/ping` endpoints | 11 | |
| #56 | Service hosts, YARP gateway, independent deployment | 4 | ADR 0015, ADR 0016 |
| #57 | RabbitMQ transport for integration events | 4 | ADR 0017 |
| #58 | Caching with HybridCache + Redis | 9 | |
| #59 | Grafana stack: metrics, dashboards, logs, traces, alerting | 4 | ADR 0018 |
| #60 | Trace id in every ProblemDetails, end-to-end trace | 3 | |
| #61 | Static code analysis quality gate | 5 | |
| #62 | SOLID and layering explanation for the defence | 11 | |
| #63 | Coverage report and gate | 5 | |
| #64 | Explicit vulnerable-package check in CI | 5 | |

Stage 4 is planned in #67.
