# Changelog

All notable changes to SmartSpare are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). Versions follow the stages of the work plan
([ARCHITECTURE_DECISIONS.md, section 13](docs/architecture/ARCHITECTURE_DECISIONS.md#13-work-plan)).

## [Unreleased]

### Changed

- Schedule of 27 November 2026 (ARCHITECTURE_DECISIONS §13): application, thesis and presentation ready by 23 December,
  pre-defence on 3 January 2027. Stage 7: 29 Nov – 1 Dec; 8: 2–7 Dec; 9: 8–9 Dec; 10: 10–11 Dec. Code freeze on 12 December,
  then a full review on 12–13 December; stage 11 is limited to fixes from that review and the expert reviews.
- The architecture tests also run in Debug, where the compiler emits different IL (`tools/check.sh architecture-debug`,
  closes [#125]).
- The schema diff against the V016 baseline is a CI gate (`tools/check.sh schema-diff`, closes [#126]).
- An architecture test keeps Quartz in the single host only, so scheduled jobs never run twice in the services topology.

### Fixed

- The end-to-end suite failed with "Too many attempts" when it ran fast: its browsers sign in about 14 times from one
  loopback address, over the sign-in limit of 10 a minute per IP. The limit is now the setting `Bff:LoginRateLimit`
  (production default unchanged: 10 a minute), and the end-to-end host raises it.

## [0.6.0] - 2026-11-27

Stage 6: Fleet ([STAGE_6_PLAN.md](docs/stage-6/STAGE_6_PLAN.md)). The core of the diploma topic: customer machines,
engine hours, the maintenance due calculation and service orders, whose kits Inventory reserves, issues and releases
through integration events; the screens for the service engineer's tablet; retention of delivered messages and a
scheduler in the single host.

### Added

- The Fleet domain mapped directly by EF Core (ADR 0013): `Machine`, `EngineHoursReading`, `MaintenancePlan`,
  `ServiceOrder` with its state machine and kit lines, `EngineHours`; calendar intervals of the OEM plans ([#121]).
- The maintenance due calculation as a unit-tested domain service: effective interval by operating condition, next
  threshold, average daily usage, projected and calendar due dates and status, documented in
  [MAINTENANCE_PLANNING.md](docs/architecture/MAINTENANCE_PLANNING.md) and in parity with `fleet.fn_maintenance_due` on
  the demo fleet ([#122]).
- `fleet.part_snapshot`, Fleet's copy of the catalogue, fed idempotently by `PartCatalogChanged`, which Inventory now
  writes in the same transaction as the catalogue change; the job `inventory.part-catalog-publish` ([#123]).
- The Fleet read API: machines with search by partial VIN, the machine card, readings, due maintenance and maintenance
  plans, under the new policy `ReadFleet`; full OpenAPI descriptions ([#127]).
- Engine-hour readings: row lock per machine, idempotent retry, SS100–SS103 and the new SS104 (reading in the future,
  against `TimeProvider`), drafting the services due within 50 h; policy/SQL parity including EXECUTE grants ([#128]).
- The service-kit flow as integration events (#12): `ServiceOrderDrafted` → `inventory.reserve_service_kit` (partial,
  idempotent per line, deterministic lock order, deadlock-tested) → `ServiceKitReserved`; expiry after 7 days
  (`ServiceKitReservationExpired`); completion → `inventory.issue_service_kit` with the shortfall rule →
  `ServiceKitIssued`; cancellation → `inventory.release_service_kit`; a reservation reaching a closed order is released
  at once (`ServiceKitReleaseRequested`); the Fleet service in the topology test ([#129], [#130], [#131], [#132], [#135],
  [#136]).
- The service-order API: list, detail with kit status (including "reserving") and `allowedActions`, create (planned or
  repair), start, complete (an identical repeat is a safe retry) and cancel, each conflict a 409 with a code ([#134],
  [#135], [#136]).
- Editing the kit of an open service order: `PUT /service-orders/{id}/lines` with the whole kit, a changed quantity
  replaced by a new line, `ServiceOrderLinesChanged` → Inventory releases removed lines and reserves new ones; the
  "Edit parts" panel on the order detail (closes [#140]).
- [ADR 0024](docs/adr/0024-acting-user-of-integration-events-and-jobs.md): events carry the acting user for attribution
  only; jobs act as the named system user ([#133]).
- Screens: machine list and card, engine-hours entry (decimal keypad, rules checked before sending, SS100–SS104 as
  plain sentences, identical retry), due maintenance, service orders, order detail that follows the kit while Inventory
  answers, new order; bUnit tests and the Playwright scenario on desktop, tablet and phone ([#137], [#138], [#139]).
- Demo data for every Fleet state, an imported service history so that about 8 % of the fleet is calendar-overdue,
  and checks in T001 that fail the load otherwise ([#137], [#141]).
- Retention of delivered outbox and handled inbox rows per module schema (`purge_processed_messages`, job
  `outbox-retention`, settings validated against the redelivery window) and the Quartz scheduler of the single host,
  which now also expires service-kit holds ([#142], closes [#30]).

### Changed

- Fleet's SQL is module-local: no Fleet or Inventory object writes another module's schema any more, and both Fleet
  views read Fleet only (MODULE_MAPPING X5–X10; [#127], [#129], [#136], closes [#12]).
- The Fleet menu entry follows `ReadFleet`, so every role that may read the fleet sees it ([#137]).
- Focus set by navigation shows no ring, keyboard focus does, now also on MudBlazor buttons ([#141]).
- CONTRIBUTING.md records the decision to refresh lock files on Dependabot branches by hand, without an App or token
  ([#142], closes [#17]).

### Fixed

- The Inventory domain architecture test failed in Debug builds only ([#124]).

### Deferred

- `RabbitMQ.Client.OpenTelemetry` is still a prerelease: moved to v0.10.0 ([#82]).
- CI gates for architecture tests in Debug and for the schema diff: [#125], [#126].

## [0.5.0] - 2026-11-27

Stage 5: Inventory ([STAGE_5_PLAN.md](docs/stage-5/STAGE_5_PLAN.md)). The first business module: the Inventory domain,
the stock, transfer and import API with full OpenAPI descriptions, and the screens for stock, transfers and import,
plus user administration, static analysis and a coverage metric defined by layer.

### Added

- The Inventory domain mapped directly by EF Core (ADR 0013): `TransferOrder` with its state machine and line rules,
  `StockReservation`, `StockMovement`, `WarehousePart`, `StockBalance`, `StockPosition` and `Quantity`; transfer
  numbers stay database-assigned; the only migration maps `xmin` ([#106]).
- Expected database errors (SS100–SS402, constraint and concurrency conflicts) become failed results with their code
  in the command pipeline ([#106]).
- The stock API: overview with status and transfers in transit, position with reservations and recent movements,
  stock levels against the version read (`If-Match`), the ledger and stock count corrections; `IWarehouseQueries` for
  other modules, in process and over `/internal/v1/inventory/warehouses` with the caller's token ([#107], closes [#97]).
- The transfers API: propose, list, detail with the actions the user may take, approve, cancel, dispatch and receive;
  the SQL functions keep locks, reservations, the ledger and the role and hub checks; brand scope on approve and
  cancel ([#108]).
- The integration event contracts `TransferApproved`, `TransferCancelled`, `TransferRejected` and
  `PartCatalogChanged`, contract-tested; an event is published only once a consumer exists, so they are written from
  stages 6 and 8 ([#108], acceptance items on [#12] and [#14]).
- CSV import of the part catalogue and the demand history, raw → staging → core with the 5 % quality gate, duplicate
  detection, size, row and rate limits, and the `ImportData` policy; module rate limits in the service hosts ([#109]).
- Screens: stock overview and position, transfers list, detail and proposal, data import, Inventory tabs; statuses
  as icon and text, cards on small screens, errors with their reference; bUnit tests and Playwright scenarios,
  including the stage scenario (a planner proposes and approves a transfer and finds the reservation at the donor)
  ([#110], [#111], [#112]).
- User administration: directory, create, roles, brands and home hub, unlock, deactivate, GDPR anonymisation, and the
  own password change, with the Users and Change password screens; administrative writes switch to `app_admin` for one
  transaction ([#116], closes [#46]).
- Static analysis: the SonarQube C# rules in the build and a duplication gate (jscpd, 3 %) in every pipeline
  ([#114], closes [#61]).

### Changed

- Stages 5–7 moved a week earlier and stage 8 got the gain ([#102]).
- EF Core warning 20601 is an error; every store-generated bool and enum default has a sentinel ([#113], closes [#19]).
- The unit-test metric of business logic is defined by layer: Domain and Features count, Infrastructure does not; CI
  reports unit business logic (gated 70 %), unit all code and unit + integration all code; data access of Inventory
  moved to Infrastructure ([#115], [#63]).

### Moved to later stages

- Outbox and inbox retention ([#30]) and the Dependabot lock-file refresh ([#17]) to stage 6, with their findings;
  `RabbitMQ.Client.OpenTelemetry` ([#82]) to stage 6 (still a release candidate).
- Exceptional demand flagging (US08) to stage 8 ([#104]).

## [0.4.0] - 2026-11-26

Stage 4: the services topology ([STAGE_4_PLAN.md](docs/stage-4/STAGE_4_PLAN.md)). The same module code now runs as a
YARP gateway and six services that exchange integration events over RabbitMQ, with the Grafana stack locally and in
Grafana Cloud. Everything is built and tested locally and in CI; the first Azure deployment is in stage 10 ([#85]).

### Added

- Composition seams for both topologies: module installers, an integration-event transport seam, stable consumer
  names (`<module>.<purpose>`) and the topology architecture rules ([#86], closes [#32]).
- Outbox back-off with jitter, parking of poison events after the last attempt, the `smartspare.outbox.*` metrics, a
  health check and a data-quality row for parked events ([#88], closes [#31]).
- RabbitMQ transport on the official client: a topic exchange per publishing module, a quorum queue per consumer with
  native delayed retry, a dead-letter queue per module, publisher confirms, idempotent inbox, budget tests for queues
  and connections, and a diagnostics probe from Identity to Inventory (ADR 0017, ADR 0021) ([#89]).
- Six service hosts (`SmartSpare.Services.<Module>`) on their own database logins, accepting only internal tokens;
  Identity's credential and session checks over `/internal/v1`; `--job <name>` and `MigrationRunner --module`
  (ADR 0020) ([#90]).
- `SmartSpare.Gateway`, the production BFF: YARP routes to the services, the cookie and antiforgery of the BFF
  building block, ES256 internal tokens, rate limiting, `/warmup`, and a route-table test that `/internal` is never
  routed (ADR 0015, ADR 0016) ([#91]).
- Public OpenAPI 3.1 documents per module and the Scalar reference at `/scalar`, cached and rate-limited, with a strict
  CSP (ADR 0019) ([#92], closes [#54]).
- The services topology test in CI: gateway, Identity and Inventory with PostgreSQL and RabbitMQ in Testcontainers,
  sign-in through the gateway, the probe over the broker and its linked delivery trace ([#93], closes [#57]).
- `--topology services` in the Aspire AppHost: the gateway, six services, RabbitMQ (or CloudAMQP by configuration),
  and an OpenTelemetry Collector with Prometheus, Loki, Tempo and Grafana, provisioned with the SmartSpare dashboards
  and alert rules (ADR 0018) ([#94]).
- Container images for the gateway, the six services and the migration job; per-service deploy workflows with the
  shared quality gates and generated path filters; Bicep for the Azure environment, checked with what-if and not
  applied (ADR 0020, ADR 0022) ([#95]).
- The coverage gate on business logic (70 %; [#63] stays open for the 80 % step) and an explicit vulnerable-package
  check in every pipeline ([#87], closes [#64]).
- `/openapi/bff.json` for the session endpoints, examples and ProblemDetails with `code` and `traceId` in every
  document (ADR 0019 amendment 1); the read-only Inventory API (`GET /api/v1/inventory/parts`, `/parts/{id}`,
  `/warehouses`) under the `ReadInventory` policy and the parts catalogue page with a server-paged grid, pulled forward
  from stage 5 ([#98]).
- Grafana Cloud as code: Terraform in `infra/grafana` for the folder, five dashboards, seven alert rules, the e-mail
  contact point and the notification policy; the broker metrics job (`--job reporting.broker-metrics`); the local
  services topology exporting to the stack by configuration (ADR 0023) ([#99], closes [#59]).
- Diploma criteria tables in the README, FRONTEND.md, API_DOCUMENTATION.md and a mobile layout test ([#96]).
- Unit + integration coverage reported next to the unit-test gate ([#100]).

### Changed

- .NET Aspire 17 (the AppHost is a plain SDK project with `AddDotnetProject`) and Testcontainers 4.17 ([#86]).
- TRACING.md describes the request trace and the linked delivery trace in Grafana Tempo, with a Grafana Cloud
  example ([#93], [#99]).
- The monthly CloudAMQP message quota is watched by CloudAMQP's e-mails instead of an alert rule (ADR 0023) ([#99]).

### Security

- Services accept only the gateway's 2-minute ES256 internal tokens and run on one database login each (ADR 0016,
  ADR 0015 §5) ([#90], [#91]).
- No secrets in the repository: user-secrets locally, Key Vault references in Bicep; a gitleaks scan of the full
  history ([#96]).

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
  clients with antiforgery, en-US resources, and error messages that end with `Reference: <trace id>` ([#76]).
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

[Unreleased]: https://github.com/sgorgun/SmartSpare/compare/v0.6.0...HEAD
[0.6.0]: https://github.com/sgorgun/SmartSpare/compare/v0.5.0...v0.6.0
[0.5.0]: https://github.com/sgorgun/SmartSpare/compare/v0.4.0...v0.5.0
[0.4.0]: https://github.com/sgorgun/SmartSpare/compare/v0.3.0...v0.4.0
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
[#31]: https://github.com/sgorgun/SmartSpare/issues/31
[#32]: https://github.com/sgorgun/SmartSpare/issues/32
[#33]: https://github.com/sgorgun/SmartSpare/issues/33
[#34]: https://github.com/sgorgun/SmartSpare/issues/34
[#35]: https://github.com/sgorgun/SmartSpare/issues/35
[#36]: https://github.com/sgorgun/SmartSpare/issues/36
[#37]: https://github.com/sgorgun/SmartSpare/pull/37
[#38]: https://github.com/sgorgun/SmartSpare/issues/38
[#39]: https://github.com/sgorgun/SmartSpare/pull/39
[#40]: https://github.com/sgorgun/SmartSpare/pull/40
[#52]: https://github.com/sgorgun/SmartSpare/pull/52
[#54]: https://github.com/sgorgun/SmartSpare/issues/54
[#57]: https://github.com/sgorgun/SmartSpare/issues/57
[#59]: https://github.com/sgorgun/SmartSpare/issues/59
[#60]: https://github.com/sgorgun/SmartSpare/issues/60
[#63]: https://github.com/sgorgun/SmartSpare/issues/63
[#64]: https://github.com/sgorgun/SmartSpare/issues/64
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
[#85]: https://github.com/sgorgun/SmartSpare/issues/85
[#86]: https://github.com/sgorgun/SmartSpare/pull/86
[#87]: https://github.com/sgorgun/SmartSpare/pull/87
[#88]: https://github.com/sgorgun/SmartSpare/pull/88
[#89]: https://github.com/sgorgun/SmartSpare/pull/89
[#90]: https://github.com/sgorgun/SmartSpare/pull/90
[#91]: https://github.com/sgorgun/SmartSpare/pull/91
[#92]: https://github.com/sgorgun/SmartSpare/pull/92
[#93]: https://github.com/sgorgun/SmartSpare/pull/93
[#94]: https://github.com/sgorgun/SmartSpare/pull/94
[#95]: https://github.com/sgorgun/SmartSpare/pull/95
[#96]: https://github.com/sgorgun/SmartSpare/pull/96
[#98]: https://github.com/sgorgun/SmartSpare/pull/98
[#99]: https://github.com/sgorgun/SmartSpare/pull/99
[#100]: https://github.com/sgorgun/SmartSpare/pull/100
[#12]: https://github.com/sgorgun/SmartSpare/issues/12
[#14]: https://github.com/sgorgun/SmartSpare/issues/14
[#17]: https://github.com/sgorgun/SmartSpare/issues/17
[#19]: https://github.com/sgorgun/SmartSpare/issues/19
[#30]: https://github.com/sgorgun/SmartSpare/issues/30
[#46]: https://github.com/sgorgun/SmartSpare/issues/46
[#61]: https://github.com/sgorgun/SmartSpare/issues/61
[#82]: https://github.com/sgorgun/SmartSpare/issues/82
[#97]: https://github.com/sgorgun/SmartSpare/issues/97
[#102]: https://github.com/sgorgun/SmartSpare/pull/102
[#104]: https://github.com/sgorgun/SmartSpare/issues/104
[#106]: https://github.com/sgorgun/SmartSpare/pull/106
[#107]: https://github.com/sgorgun/SmartSpare/pull/107
[#108]: https://github.com/sgorgun/SmartSpare/pull/108
[#109]: https://github.com/sgorgun/SmartSpare/pull/109
[#110]: https://github.com/sgorgun/SmartSpare/pull/110
[#111]: https://github.com/sgorgun/SmartSpare/pull/111
[#112]: https://github.com/sgorgun/SmartSpare/pull/112
[#113]: https://github.com/sgorgun/SmartSpare/pull/113
[#114]: https://github.com/sgorgun/SmartSpare/pull/114
[#115]: https://github.com/sgorgun/SmartSpare/pull/115
[#116]: https://github.com/sgorgun/SmartSpare/pull/116
[#121]: https://github.com/sgorgun/SmartSpare/pull/121
[#122]: https://github.com/sgorgun/SmartSpare/pull/122
[#123]: https://github.com/sgorgun/SmartSpare/pull/123
[#124]: https://github.com/sgorgun/SmartSpare/pull/124
[#127]: https://github.com/sgorgun/SmartSpare/pull/127
[#128]: https://github.com/sgorgun/SmartSpare/pull/128
[#129]: https://github.com/sgorgun/SmartSpare/pull/129
[#130]: https://github.com/sgorgun/SmartSpare/pull/130
[#131]: https://github.com/sgorgun/SmartSpare/pull/131
[#132]: https://github.com/sgorgun/SmartSpare/pull/132
[#133]: https://github.com/sgorgun/SmartSpare/pull/133
[#134]: https://github.com/sgorgun/SmartSpare/pull/134
[#135]: https://github.com/sgorgun/SmartSpare/pull/135
[#136]: https://github.com/sgorgun/SmartSpare/pull/136
[#137]: https://github.com/sgorgun/SmartSpare/pull/137
[#138]: https://github.com/sgorgun/SmartSpare/pull/138
[#139]: https://github.com/sgorgun/SmartSpare/pull/139
[#141]: https://github.com/sgorgun/SmartSpare/pull/141
[#142]: https://github.com/sgorgun/SmartSpare/pull/142
[#125]: https://github.com/sgorgun/SmartSpare/issues/125
[#126]: https://github.com/sgorgun/SmartSpare/issues/126
[#140]: https://github.com/sgorgun/SmartSpare/issues/140
