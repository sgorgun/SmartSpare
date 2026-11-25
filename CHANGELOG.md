# Changelog

All notable changes to SmartSpare are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). Versions follow the stages of the work plan
([ARCHITECTURE_DECISIONS.md, section 13](docs/architecture/ARCHITECTURE_DECISIONS.md#13-work-plan)).

## [Unreleased]

## [0.3.0] - 2026-11-25

Stage 3: Identity, BFF and the Blazor client skeleton ([STAGE_3_PLAN.md](docs/stage-3/STAGE_3_PLAN.md)). A demo user
signs in through the web app, sees a menu that matches their roles and signs out; no token ever reaches the browser.

### Added

- Identity domain model mapped directly onto the `iam` tables (`AppUser`, `UserName`, `EmailAddress`,
  `LockoutPolicy`; ADR 0013) ([#70], closes [#38]).
- Demo-user passwords chosen locally and hashed at load time by `MigrationRunner set-demo-passwords`, and a
  **Load demo data** command in the Aspire dashboard ([#70], closes [#7] and [#33]).
- Cookie sign-in through the BFF endpoints `/bff/login`, `/bff/logout`, `/bff/me` and `/bff/antiforgery` in the new
  `SmartSpare.Bff` building block, with ASP.NET Core Identity over `iam` behind `ICredentialVerifier` (ADR 0014,
  ADR 0016) ([#73]).
- Authorization policies by role, brand-scoped approval checks over all document lines, and a test that the policies
  match the role checks in the SQL functions ([#73]).
- Internal ES256 tokens for the gateway-to-service hop, validated in the single host by the same policy scheme as the
  cookie (ADR 0016) ([#73]).
- The W3C trace id in every ProblemDetails as `traceId` ([#73], closes [#60]).
- Session context: the signed-in user's id, roles and home warehouse reach PostgreSQL as `app.*` settings, and the
  database role and warehouse checks use them instead of reading `iam` ([#74], closes [#11]).
- Data Protection key ring shared through `iam.data_protection_keys`, so host instances accept each other's cookies;
  key protection at rest configurable per environment ([#74], closes [#22]).
- Blazor WebAssembly client with MudBlazor 9.14: sign-in page with the shared validator, menu by policy, typed HTTP
  clients with antiforgery, en-US resources, and error messages that end with "Reference: <trace id>" ([#76]).
- bUnit tests for the sign-in page, the menu and the HTTP handlers ([#76]).
- Playwright smoke test in Chromium against the real host and PostgreSQL 17: redirect to sign-in, the generic
  failure with its trace id, sign-in, role-based menu, no credentials visible to scripts, sign-out. It runs as its own
  CI job and as `tools/check.sh e2e` ([#77]).
- Database command spans (Npgsql) in every request's trace (ADR 0018) and a walkthrough from an error reference to its
  trace in the Aspire dashboard, [docs/operations/TRACING.md](docs/operations/TRACING.md) ([#77], closes [#72]).
- SQL assertion that only Identity and Reporting SQL reads `iam`; the suite now has 103 assertions ([#77]).
- `tools/check.sh`, the check set that CI runs step by step, so local and CI checks are identical ([#73]).
- Backend requirements compliance matrix ([#65]) and ADRs 0014–0019: browser sign-in, the services topology behind a
  YARP gateway, authentication behind the gateway, RabbitMQ, the Grafana stack and public API documentation ([#52],
  [#68], closes [#66]).

### Changed

- New passwords are hashed with PBKDF2-HMAC-SHA512 at 220,000 iterations (the OWASP value); older hashes are rehashed
  at sign-in ([#70]).
- User names are stored in canonical lower case, also enforced by a CHECK ([#70], [#74], closes [#69]).
- ASP.NET Core Identity drives the lockout through the user store mapping (five failures, 15 minutes) ([#73]).
- The host serves the web app with prerendering and a strict Content Security Policy (no `'unsafe-inline'` or
  `'unsafe-eval'` for scripts; the import map is allowed by its hash), `X-Content-Type-Options`, `Referrer-Policy`
  and HSTS outside Development ([#76]).
- `iam.v_app_user_masked` is module-local and returns `home_warehouse_id` ([#74]).
- Architecture rules fail when their type selection is empty, and the web client may reference only
  `SmartSpare.ApiContracts` ([#73], [#76]).
- Work plan: the topology stage was inserted as stage 4; later stages moved up by one ([#68]).

### Fixed

- Audit rows of UPDATEs record the changed row's id instead of `?` ([#74], closes [#71]).

### Security

- No tokens in the browser: an HttpOnly, Secure, SameSite=Strict `__Host-` cookie, antiforgery on every unsafe
  request, security-stamp checks every five minutes, and identical responses for every failed sign-in ([#73]).
- Rate limit of ten sign-in attempts per minute per client IP ([#73]).
- The authentication state handed to WebAssembly carries only allow-listed claims, never the security stamp ([#76]).

## [0.2.1] - 2026-11-24

Documentation refresh after stage 2.

### Added

- ADR 0013: domain model and persistence ([#39]).
- Data access layer map in DATABASE_DESIGN and the README ([#39]).
- CI check that fails on stale architecture terms ([#40]).

### Changed

- Solution architecture diagram redrawn for the approved architecture, marking what is implemented and what is planned
  ([#40], closes [#35]).
- DDL, data dictionary and diagrams regenerated; the superseded `ARCHITECTURE.md` removed ([#40]).

### Fixed

- Two stale database comments ([#40], closes [#34]).
- The draw.io export keeps the borders of planned nodes and text nodes ([#40]).
- The new Identity migration stays `internal` like the module's other types ([#40]).

## [0.2.0] - 2026-11-17

Stage 2: the data layer moves to EF Core code-first, with one DbContext and PostgreSQL schema per module
([MODULE_MAPPING.md](docs/database/MODULE_MAPPING.md)).

### Added

- DbContexts, Fluent API configurations and initial migrations for Platform, Identity, Inventory, Fleet, Procurement,
  Forecasting and Reporting; triggers, functions, views and grants stay SQL, applied by the migrations (ADR 0004,
  ADR 0011) ([#5], [#8], [#20]).
- `SmartSpare.MigrationRunner` with the shared module catalogue, and a CI check for pending model changes ([#5]).
- Unit of work, transactional outbox and inbox with a `FOR UPDATE SKIP LOCKED` dispatcher ([#20]).
- Schema diff of the EF-built database against the frozen V016 baseline ([#5], [#8], [#20], [#37], closes [#9] and
  [#36]).
- The 97 SQL assertions, the concurrency test and the T001 demo dataset run against the EF-built database in CI
  ([#37], closes [#10]).
- ADR 0012: evolvability principles ([#29]); GitHub Flow contribution guide and pull request template ([#3]); module
  mapping and stage 2 plan ([#4]).

### Changed

- The DDL, data dictionary and diagrams are generated from the EF-built database ([#37]).
- Supplier tables moved from `catalog` to `procurement`; cross-module foreign keys removed (ADR 0011) ([#20]).
- Local databases are rebuilt with the MigrationRunner ([#37]).
- GitHub Actions updated by Dependabot ([#1]).

### Security

- Passwords removed from the repository: local credentials come from a git-ignored `.env`, and tests generate
  throw-away passwords per run ([#6]).

### Removed

- The SQL V-scripts and their runner, replaced by the EF migrations ([#37]).

## [0.1.0] - 2026-11-16

Stage 1: solution skeleton.

### Added

- PostgreSQL OLTP schema with business logic, security, test data and documentation, plus editable diagrams.
- Approved architecture decisions and ADRs 0001–0010.
- .NET 10 solution with central package management, `SharedKernel` (Result, Error, Entity, ValueObject), the handler
  pipeline with decorators, ServiceDefaults (OpenTelemetry, health checks), six module skeletons with ping endpoints,
  and the host with explicit module registration.
- .NET Aspire AppHost with PostgreSQL 17 for local development.
- Architecture tests for module boundaries.
- CI for build, format check and tests, with locked-mode restore and Dependabot.

[Unreleased]: https://github.com/sgorgun/SmartSpare/compare/v0.3.0...HEAD
[0.3.0]: https://github.com/sgorgun/SmartSpare/compare/v0.2.1...v0.3.0
[0.2.1]: https://github.com/sgorgun/SmartSpare/compare/v0.2.0...v0.2.1
[0.2.0]: https://github.com/sgorgun/SmartSpare/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/sgorgun/SmartSpare/releases/tag/v0.1.0

[#1]: https://github.com/sgorgun/SmartSpare/pull/1
[#3]: https://github.com/sgorgun/SmartSpare/pull/3
[#4]: https://github.com/sgorgun/SmartSpare/pull/4
[#5]: https://github.com/sgorgun/SmartSpare/pull/5
[#6]: https://github.com/sgorgun/SmartSpare/pull/6
[#7]: https://github.com/sgorgun/SmartSpare/issues/7
[#8]: https://github.com/sgorgun/SmartSpare/pull/8
[#9]: https://github.com/sgorgun/SmartSpare/issues/9
[#10]: https://github.com/sgorgun/SmartSpare/issues/10
[#11]: https://github.com/sgorgun/SmartSpare/issues/11
[#20]: https://github.com/sgorgun/SmartSpare/pull/20
[#22]: https://github.com/sgorgun/SmartSpare/issues/22
[#29]: https://github.com/sgorgun/SmartSpare/pull/29
[#33]: https://github.com/sgorgun/SmartSpare/issues/33
[#34]: https://github.com/sgorgun/SmartSpare/issues/34
[#35]: https://github.com/sgorgun/SmartSpare/issues/35
[#36]: https://github.com/sgorgun/SmartSpare/issues/36
[#37]: https://github.com/sgorgun/SmartSpare/pull/37
[#38]: https://github.com/sgorgun/SmartSpare/issues/38
[#39]: https://github.com/sgorgun/SmartSpare/pull/39
[#40]: https://github.com/sgorgun/SmartSpare/pull/40
[#52]: https://github.com/sgorgun/SmartSpare/pull/52
[#60]: https://github.com/sgorgun/SmartSpare/issues/60
[#65]: https://github.com/sgorgun/SmartSpare/pull/65
[#66]: https://github.com/sgorgun/SmartSpare/issues/66
[#68]: https://github.com/sgorgun/SmartSpare/pull/68
[#69]: https://github.com/sgorgun/SmartSpare/issues/69
[#70]: https://github.com/sgorgun/SmartSpare/pull/70
[#71]: https://github.com/sgorgun/SmartSpare/issues/71
[#72]: https://github.com/sgorgun/SmartSpare/issues/72
[#73]: https://github.com/sgorgun/SmartSpare/pull/73
[#74]: https://github.com/sgorgun/SmartSpare/pull/74
[#76]: https://github.com/sgorgun/SmartSpare/pull/76
[#77]: https://github.com/sgorgun/SmartSpare/pull/77
