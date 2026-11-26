# SmartSpare — Stage 5 Plan: Inventory (Stock, Transfers, Import)

Status: **proposed** (26 November 2026). Stage 5 of the work plan ([ARCHITECTURE_DECISIONS.md, section 13](../architecture/ARCHITECTURE_DECISIONS.md#13-work-plan)), tracked in #103. It turns the Inventory module from the stage 2 table port and the read-only stage 4 catalogue into the first business module:
- the Inventory domain model of [ADR 0013](../adr/0013-domain-model-and-persistence.md) (stock, reservations, movements, the `TransferOrder` state machine);
- the versioned stock, transfer and import API with full OpenAPI descriptions;
- integration events through the outbox;
- the Blazor screens for stock, transfers and import;
- the stage 5 issues (#46, #61, #17, #19, #30, #97, #82).

Release target: **v0.5.0**.

**Product owner's decisions, 26 November 2026:**
- **Schedule:** stage 4 finished a week early. Stage 5 runs **26–30 November 2026** (milestone v0.5.0, due 30 November); stages 6–9 moved accordingly and stage 8 got the gain (ARCHITECTURE_DECISIONS §13).
- **Timebox:** if the stage runs late, scope is cut in the order of section 12, not the later stages moved.
- **Demo-ready `main`:** the experts review from **2 December 2026**. Every PR leaves `main` deployable and demonstrable: no half-finished screen is linked from the navigation, and the stage 4 catalogue page keeps working throughout.
- **Autonomous delivery** as in stage 4: implement per this plan, full check set, PR, merge on green, log entry on the stage issue; stop only on plan contradictions, decisions not covered here or by an ADR, money or accounts, or date risk.

Inputs, as on `main` (v0.4.0):
- BA use cases UC01 (transfer from a recommendation) and the user stories US08, US10, US11 and US18 as recorded in [DATABASE_DESIGN.md](../database/DATABASE_DESIGN.md) sections 6.3 and 8;
- [MODULE_MAPPING.md](../database/MODULE_MAPPING.md) sections 1.3, 2, 3.1 (X5, X13, X16, X22), 3.4 and 3.5;
- [STAGE_3_PLAN.md](../stage-3/STAGE_3_PLAN.md) sections 3.2–3.3 (policies and brand scope) and 7 (the domain-model pattern of Identity);
- [API_DOCUMENTATION.md](../architecture/API_DOCUMENTATION.md) and the stage 4 Inventory endpoints (#98);
- the SQL in `db/sql/inventory/0002_inventory_logic.sql` and `0006_session_context.sql`.

No new ADR is needed: every decision below applies ADRs 0003, 0004, 0005, 0012, 0013, 0016 and 0019.

---

## 0. Summary of decisions

| # | Decision | Section |
|---|---|---|
| I1 | **One model per table (ADR 0013).** `Domain/` gets the aggregates `TransferOrder` (with `TransferOrderLine`), `StockReservation`, `StockMovement`, `WarehousePart` and the read-mostly `StockBalance`, plus the value objects `Quantity` and `TransferNumber`. The EF configurations map them directly; their classes in `PersistenceModel.cs` are deleted. `Part`, `Warehouse`, `WarehouseRoute`, the `raw`/`staging` rows and the `ref` principals stay plain classes (data without behaviour, ADR 0013 rule 3) and move out of `PersistenceModel.cs` into named files. | 1 |
| I2 | **The SQL business functions stay the invariant guard.** `approve_transfer`, `cancel_transfer`, `dispatch_transfer`, `receive_transfer` and `staging.process_batch` do the writes, locking and role checks. The handler first asks the aggregate (a fast, unit-tested answer with a readable error), then calls the function in the same transaction. EF never saves an aggregate whose transition a function owns. | 2 |
| I3 | **`DatabaseErrorMapper` is wired into the transaction decorator.** An expected PostgreSQL error (SS200–SS302, constraint violations, serialization failures) becomes a failed `Result` and a ProblemDetails with the SS code; anything else stays an exception. | 2.3 |
| I4 | **API under `/api/v1/inventory/...`**: stock overview and position, movement ledger, stock adjustments, transfers (create, list, detail, approve, cancel, dispatch, receive), imports (upload, list, detail). Every endpoint has a summary, a description, parameter descriptions, response types and examples (ADR 0019 §1). | 3 |
| I5 | **Policies and brand scope from stage 3.** Approve and cancel run the `ApproveTransferScope` / `CancelTransferScope` resource check over the brands of all lines (read from `catalog.part`) before any state change. Dispatch, receive and adjustments use `MoveStock`; the database repeats the role and home-warehouse checks (SS301, SS302). One new policy, `ImportData`. | 3.2 |
| I6 | **Integration events** in `Inventory.Contracts`: `TransferApproved`, `TransferCancelled`, `TransferRejected` and `PartCatalogChanged`, written to `inventory.outbox_message` in the same transaction as the change. `TransferRejected` is defined now and published by the `TransferRequested` consumer, which is built in stage 8 with its publisher (#14). | 4 |
| I7 | **`IWarehouseQueries`** is the first Inventory Contracts query: in process in the single host, over `/internal/v1/inventory/warehouses` between services (the stage 4 pattern of `ICredentialVerifier`). User administration (#46) validates the home warehouse with it. | 4.3 |
| I8 | **CSV import raw → staging → core** for the part catalogue and the demand history: the upload is stored row by row in `raw`, `staging.process_batch` validates and loads with its 5 % data-quality gate, and the report (counts and per-row errors) is shown on a screen. Size limit 10 MB, rate limit, one batch per file hash. | 5 |
| I9 | **Screens** in `Features/Inventory`: stock overview and position, transfers list and detail with the actions the user may take, a create-transfer form, import. Responsive (cards on small screens), loading and empty states, errors with the trace "Reference". bUnit tests per screen and one Playwright scenario (approve a transfer). | 6 |
| I10 | **Unit coverage toward 80 %**: the domain is unit-tested exhaustively (every transition, every invariant); the gate stays 70 % and the figure is recorded per PR. | 7 |
| I11 | **Static analysis (#61): `SonarAnalyzer.CSharp` in the build plus a duplication check in `tools/check.sh`** — the fallback of #61, because SonarQube Cloud needs an account the owner would create (CLAUDE.md: accounts are the owner's). The hosted service remains an option for the owner (section 10). | 10 |
| I12 | **User administration (#46)** is the last feature PR and the first cut if the stage runs late. | 9, 12 |

---

## 1. Domain model (ADR 0013)

`src/Modules/Inventory/SmartSpare.Modules.Inventory/Domain/`. Domain classes reference no EF Core or ASP.NET Core types (architecture tests).

| Type | Kind | Behaviour and invariants |
|---|---|---|
| `TransferOrder : AggregateRoot<long>` | aggregate | `Propose(from, to, isUrgent, requestedBy, lines)` (factory: different hubs, at least one line, distinct parts, positive quantities — the CHECKs and `trg_transfer_line_editable`); `AddLine` / `ChangeLineQuantity` / `RemoveLine` only while `Proposed`; `Approve(user, now)`, `Dispatch(user, homeWarehouse, now)`, `Receive(user, homeWarehouse, now)`, `Cancel(reason, now)` follow the state machine of `trg_transfer_order_status_guard` (proposed → approved \| cancelled; approved → dispatched \| cancelled; dispatched → received). The keeper's home warehouse must be the donor for dispatch and the recipient for receipt (SS302 parity). Raises `TransferApprovedDomainEvent` and `TransferCancelledDomainEvent`. Exposes `BrandScope` input: the part ids of all lines. `xmin` concurrency. |
| `TransferOrderLine : Entity<long>` | child entity | `PartId`, `Quantity`, `ProposedQty`, `OverrideReason` (required when the quantity differs from the proposal, `ck_transfer_order_line_override`). |
| `TransferNumber` | value object | `TR-YYYY-NNNNNN`, assigned by the database trigger (`util.trg_assign_document_number`); parsed and formatted in C#. |
| `TransferStatus` | enum | Moves from `PersistenceModel.cs` to `Domain/`. |
| `StockReservation : AggregateRoot<long>` | aggregate | Created `Soft` (with expiry) or `Firm`; `Consume`, `Release`, `Expire` only from an open state (`trg_stock_reservation_guard`). In stage 5 reservations are written by `approve_transfer` / `dispatch_transfer` / `cancel_transfer`; the C# type exists for reads and for stage 6 (service kits, #12). |
| `StockMovement : Entity<long>` | entity (append-only) | `Adjust(warehouse, part, signedQuantity, note, user, now)` creates `ADJUSTMENT_IN` / `ADJUSTMENT_OUT` (note required, quantity non-zero); every other movement type is posted by SQL. No update or delete method (`trg_stock_movement_immutable`). `MovementType` is a small value type with `BalanceSign` and `IsDemand` (mirrors `ref.movement_type`). |
| `StockBalance` | entity (read-mostly) | `OnHand`, `Reserved`, `Available` (generated column), `LastMovementAt`. Only the trigger writes it. Domain logic: `CanIssue(quantity)`, `CanGive(quantity, safetyStock, isUrgent)` (US10 parity with `approve_transfer`). |
| `WarehousePart : Entity<(short, long)>` | entity | `SafetyStock`, `MaxStock`, `BinLocation`, `IsStocked`; `ChangeStockLevels(safety, max)` with `max ≥ safety` (CHECK parity). |
| `StockPosition` | value object | Read model for the overview: on hand, reserved, available, safety stock, in transit (dispatched transfers to the hub), `IsBelowSafety`, `Coverage` category (out, below safety, ok, above max). Pure and fully unit-tested. |
| `Quantity` | value object | Non-negative `decimal` with 3 decimals (`numeric(12,3)`), arithmetic that never goes negative. |
| `InventoryErrors` | static class | Expected errors with stable codes (`Inventory.TransferNotProposed`, `Inventory.SameWarehouse`, …) used by both the domain and the handlers. |

**Mapping.** Private parameterless constructors, private setters, backing fields for the line collection, value converters for `Quantity` and `TransferNumber`. Store-generated values (`number`, `available_qty`, timestamps) are read-only properties.

**The remap must not change the schema.** `MigrationRunner check-model` reports no pending changes, except one reviewed schema-free migration for the `xmin` tokens of `TransferOrder` and `WarehousePart` (the stage 3 rule, STAGE_3_PLAN section 7). The Inventory part of #19 (store-generated `bool` defaults such as `is_active`, `is_stocked`, `is_urgent`) is fixed here with `HasSentinel` or nullable backing fields.

**Not modelled in stage 5:** soft reservations for service kits and their expiry (stage 6, #12), expected receipts from purchase orders (stage 7, #13), exceptional demand (US08; #104, stage 8).

---

## 2. Handlers and the database

### 2.1 Commands that a SQL function completes

Approve, cancel, dispatch and receive follow one pattern (I2):

1. Load the `TransferOrder` with its lines (tracked, so `xmin` is checked) — 404 if missing.
2. Approve and cancel only: build `IBrandScoped` from the brands of the lines (`catalog.part.brand_id`, module-local) and run `IAuthorizationService.AuthorizeAsync` with the scope requirement → 403 ProblemDetails before any change (STAGE_3_PLAN 3.3).
3. Call the aggregate method. A failed `Result` (wrong state, wrong hub) returns without touching the database.
4. Detach the aggregate (`ChangeTracker.Clear()` for that entry), then `SELECT inventory.<function>(@id[, @reason])` in the same transaction. The function locks rows in part order, writes reservations, movements and the status, and repeats the role and warehouse checks.
5. Add the integration event to the unit of work; the transaction decorator writes the outbox row and commits.

So the domain gives fast, unit-tested answers and the database stays the last line of defence (ARCHITECTURE_DECISIONS §5): concurrent requests that pass step 3 on stale data are stopped by the function's row locks and state checks (SS300) or by SS200/SS201 on stock.

### 2.2 Commands that EF writes

Create transfer, change lines and stock adjustments are written through EF; the triggers guard them (`trg_transfer_line_editable`, `trg_stock_movement_apply` with SS200 for an adjustment that would make stock negative). `WarehousePart` stock levels are written through EF with `xmin`.

### 2.3 `DatabaseErrorMapper` in the transaction decorator (I3)

`TransactionCommandDecorator` catches exceptions from the handler and from `SaveChanges`/commit, rolls back, and asks `DatabaseErrorMapper.ToError`. A mapped error becomes `Result.Failure(error)`; `null` rethrows. `ResultHttpExtensions` already maps `ErrorType` to the status (Conflict 409, Forbidden 403, BusinessRule 422) and puts the code (for example `SS201`) into the ProblemDetails `code` extension. Retries of the execution strategy still happen first, because the mapper only sees exceptions the strategy gave up on. Unit tests cover each SQLSTATE family; an integration test proves SS201 on an approval that would empty the donor's safety stock.

### 2.4 Queries

Queries read straight into DTOs with `AsNoTracking` projections or raw SQL as in stage 4 (CQRS-lite). The transitional view `inventory.v_stock_overview` is **not** used: it reads Procurement data and is replaced in stage 7 (MODULE_MAPPING 3.2, X13). The stage 5 overview reads `stock_balance`, `warehouse_part`, `catalog.part`, `ref.brand` and dispatched transfers only; in-transit quantities from purchase orders join in stage 7 through `inventory.expected_receipt`.

---

## 3. HTTP API

### 3.1 Endpoints

All under `/api/v1/inventory`, JSON, ProblemDetails on errors (`code` and `traceId` extensions, #60), paged lists as `PagedResponse<T>` (stage 4). Unsafe methods carry the antiforgery header through the BFF (ADR 0014).

| Method and path | Policy | Purpose |
|---|---|---|
| `GET /stock?warehouseId&search&status&page&pageSize` | `ReadInventory` | Stock overview: one row per hub and part with on hand, reserved, available, safety stock, in transit and coverage status; filter by hub, text and status (`out`, `belowSafety`, `ok`, `aboveMax`). |
| `GET /stock/{warehouseId}/{partId}` | `ReadInventory` | Position detail: balance, stock levels, open reservations, the last 20 movements. |
| `PUT /stock/{warehouseId}/{partId}/levels` | `PlanInventory` | Change safety and maximum stock (`WarehousePart`, `If-Match` with `xmin`). |
| `GET /movements?warehouseId&partId&from&to&type&page&pageSize` | `ReadInventory` | The ledger, newest first (partition pruning by `occurred_at`). |
| `POST /stock-adjustments` | `MoveStock` | Stock count correction (`ADJUSTMENT_IN` / `ADJUSTMENT_OUT`, note required). |
| `GET /transfers?status&warehouseId&page&pageSize` | `ReadInventory` | Transfers list, newest first. |
| `GET /transfers/{id}` | `ReadInventory` | Detail with lines (part, brand, quantity, proposal and override reason), timeline (requested, approved, dispatched, received by and when) and `allowedActions` for the current user. |
| `POST /transfers` | `PlanInventory` | Propose a transfer: route, urgency, lines. `201 Created` with `Location`. |
| `POST /transfers/{id}/approve` | `ApproveTransfer` + `ApproveTransferScope` | Approve; firm reservations at the donor (UC01 steps 10–11, US10). |
| `POST /transfers/{id}/cancel` | `CancelTransfer` + `CancelTransferScope` | Cancel with a reason; releases firm reservations. |
| `POST /transfers/{id}/dispatch` | `MoveStock` | Donor keeper dispatches (US11). |
| `POST /transfers/{id}/receive` | `MoveStock` | Recipient keeper receives (US11). |
| `POST /imports` (multipart: `source`, `file`) | `ImportData` | Upload a CSV; processed synchronously; returns the batch report. |
| `GET /imports?page&pageSize` | `ImportData` | Import history. |
| `GET /imports/{id}` | `ImportData` | Batch report with the first 200 row errors. |

Timelines return user ids; the screen shows "you" for the current user. Display names follow when Identity offers `IUserDirectory` (MODULE_MAPPING 3.5); no cross-module read is added for display alone in this stage.

### 3.2 Authorization

- Policies come from `AppPolicies` (STAGE_3_PLAN 3.2). New: **`ImportData` = `ADMINISTRATOR`, `INVENTORY_PLANNER`** — data import is a planning and support task; `staging.process_batch` has no role check of its own, so the API policy is the control, and the grant to `app_write` limits what the database lets it do.
- Brand scope on approve and cancel exactly as STAGE_3_PLAN 3.3: brand ids come from the lines, never from the request.
- `allowedActions` in the detail response is computed with the same policies and scope checks, so the screen never offers an action the API would refuse.

### 3.3 OpenAPI (ADR 0019, #97)

Every endpoint declares `WithName`, `WithSummary`, `WithDescription`, `[Description]` on parameters, `Produces`/`ProducesProblem` for every status it returns and `WithResponseExample` for 200/201 and the main errors (400, 403, 404, 409 with an SS code). PR 5c settles #97: either XML comments reach the documents (then the convention is documented) or the explicit declarations stay the rule and `src/Modules/Directory.Build.props` is corrected. `ApiDocumentationTests` assert that every Inventory operation has a summary, a description and at least one example.

---

## 4. Integration events and Contracts

### 4.1 Events published by Inventory (MODULE_MAPPING 3.5)

In `SmartSpare.Modules.Inventory.Contracts`, routing key `<Event>.v1` (ADR 0017):

| Event | When | Payload |
|---|---|---|
| `TransferApproved` | approval committed | transferOrderId, number, from, to, isUrgent, recommendationId (null for manual transfers), lines (partId, quantity), approvedBy, approvedAt |
| `TransferCancelled` | cancellation committed | transferOrderId, number, recommendationId, reason, cancelledAt |
| `TransferRejected` | a `TransferRequested` cannot be fulfilled (SS200/SS201) | recommendationId, error code, message |
| `PartCatalogChanged` | a part-catalogue import loaded rows | chunks of at most 500 part snapshots (partId, brand, OEM number, description, unit of measure, isActive) |

`TransferRequested` and its consumer belong to stage 8 (#14): the consumer approves on behalf of the planner outside an HTTP request, which needs the session context of a consumer to be designed together with its publisher. Stage 5 defines `TransferRejected` so the contract is reviewed with the others. `PartCatalogChanged` has no consumer until stage 6 (#12); it is published now because the import is where it originates (X22), and the chunking keeps a 20,000-part import at 40 messages, within the CloudAMQP budget (ADR 0017 §5).

`recommendationId` is a nullable column added to `inventory.transfer_order` in stage 8 with the consumer; in stage 5 the events carry `null`.

### 4.2 Tests

Integration tests assert that each command writes exactly one outbox row with the expected type and payload, in the same transaction (a failed approval writes none), and that the outbox dispatcher delivers it in process.

### 4.3 `IWarehouseQueries` (I7)

`Inventory.Contracts.IWarehouseQueries`: `GetAll()` and `Exists(id)` over the seven hubs, cached in memory for five minutes. In process in the single host; in the services topology an HTTP client to `GET /internal/v1/inventory/warehouses` with the caller's internal token (the stage 4 pattern: never routed by the gateway, covered by the route-table test). `IStockQueries` and `IPartQueries` come with their first consumers (stages 6 and 8).

---

## 5. CSV import (raw → staging → core)

- **Formats.** UTF-8 CSV with a header row, comma or semicolon separated, the columns of `raw.part_catalog_row` and `raw.demand_history_row` (all text, as the raw layer expects). The expected header is documented in the endpoint description and offered as a template download on the screen.
- **Flow.** One command in one transaction: reject a file whose SHA-256 is already in `raw.import_batch` (409, the unique index), insert the batch and its rows (binary `COPY` through Npgsql for speed), call `staging.process_batch(batch)` and return its report. A failed quality gate is a normal response (`status: failed`, message with the error ratio), not an error. Row errors come from `staging.import_error`.
- **Limits.** 10 MB request limit on the endpoint (`RequestSizeLimit`), at most 50,000 rows per file, a rate limit of 5 imports per minute per user (the stage 3 limiter, ARCHITECTURE_DECISIONS §7).
- **Events.** A loaded part-catalogue batch publishes `PartCatalogChanged` for the changed parts (4.1).
- **Tests.** Parser unit tests (separators, quoting, BOM, wrong header); integration tests for a good file, a file over the 5 % threshold, a duplicate upload, and a demand-history file with duplicates skipped.

---

## 6. Web client

In `src/Web/SmartSpare.Web.Client/Features/Inventory`, typed `InventoryClient` extended per endpoint, DTOs and validators from `SmartSpare.ApiContracts.Inventory`.

| Screen | Route | Content |
|---|---|---|
| Stock overview | `/inventory/stock` | Hub selector, search, status filter, `MudDataGrid` with server paging; status chips with icon and text (WCAG: not colour alone); row click → position. |
| Stock position | `/inventory/stock/{warehouseId}/{partId}` | Balance, levels (editable for planners), reservations, recent movements; adjustment dialog for keepers. |
| Transfers | `/inventory/transfers` | Status tabs, hub filter, list with number, route, status, lines, age. |
| Transfer detail | `/inventory/transfers/{id}` | Lines, timeline, buttons from `allowedActions`; cancel asks for a reason; confirmation dialogs for approve, dispatch, receive. |
| New transfer | `/inventory/transfers/new` | Route, urgency, lines with part search (stage 4 parts API) and available quantity at the donor; client validation from ApiContracts. |
| Import | `/inventory/imports` | Source, file picker with size check, upload progress, the report with row errors, history. |

Common rules:
- **Responsive (NFR05):** below the `md` breakpoint lists render as `MudCard`s instead of grids (the pattern of the stage 4 mobile test), actions stay reachable with one hand.
- **Loading and empty states:** skeletons while loading, an explicit empty state with the next action.
- **Errors:** ProblemDetails → `MudSnackbar` with the readable message and "Reference: <traceId>" (#60, #72); 403 and 409 messages come from the API (`SS201` text, brand scope).
- **Navigation:** the Inventory area gets Stock, Transfers, Import (by role); the stage 4 parts page stays.
- **Localisation:** strings through `IStringLocalizer` (English resources).

**Tests.** bUnit per screen: rendering of loading, empty, data and error states; actions shown only from `allowedActions`; form validation; mobile card rendering. **Playwright (one scenario):** a planner signs in, opens a proposed demo transfer, approves it and sees the status "Approved" and the reservation on the donor position — on desktop; the tablet viewport repeats the list step.

---

## 7. Tests and coverage

| Level | New in stage 5 |
|---|---|
| Unit (`SmartSpare.Modules.Inventory.UnitTests`) | Every `TransferOrder` transition (allowed and refused), line rules, `StockPosition` and `StockBalance` calculations, `Quantity`, `TransferNumber`, adjustment rules, CSV parser, `allowedActions`, validators, `DatabaseErrorMapper` in the decorator. |
| Integration (`SmartSpare.Host.IntegrationTests`, Testcontainers) | Each endpoint: success, 400, 401, 403 (role and brand scope), 404, 409 (SS200/SS201/SS300); outbox rows; import flows; SS302 on dispatch by the wrong hub. |
| Architecture | Inventory `Domain` has no framework references (existing rule); the new Contracts types are public and the rest internal. |
| Topology | The stock overview reached through the gateway; `/internal/v1/inventory/warehouses` not routed. |
| bUnit, Playwright | Section 6. |

**Coverage.** The gate stays at 70 % (#63). The target for v0.5.0 is **unit coverage ≥ 78 %**, on the way to 80 % by stage 9 (#63). Each PR states its unit coverage; a drop of more than 2 points needs a reason (CLAUDE.md).

---

## 8. Housekeeping issues in this stage

| Issue | What | PR |
|---|---|---|
| #19 | EF warning 20601: sentinels for store-generated `bool`/enum defaults — Inventory in 5b, the remaining modules' properties in 5i, so the warning is gone from start-up logs. | 5b, 5i |
| #30 | Outbox/inbox retention: a Quartz job in the single host purges processed outbox rows older than 7 days and inbox rows older than 30 days per module schema, in batches of 5,000; a `--job outbox-retention` entry point for the services topology (cron job in stage 10). | 5i |
| #17 | Dependabot NuGet PRs: a workflow on `pull_request_target` limited to `dependabot[bot]` that runs `dotnet restore --force-evaluate` and pushes the refreshed lock files, with `contents: write` only. | 5i |
| #97 | XML comments vs explicit OpenAPI declarations. | 5c |
| #82 | `RabbitMQ.Client.OpenTelemetry`: adopted only if a stable version is out at 5i; otherwise moved to the stage 6 milestone. | 5i |

---

## 9. User administration (#46)

As specified in #46: `/api/v1/identity/users` (policy `Administration`) — list from the masked view, create with an initial password (stage 3 password policy), assign and revoke roles and brands, set the home warehouse (validated through `IWarehouseQueries`), unlock, deactivate, GDPR anonymisation (`iam.anonymise_user`); self-service password change. Every role, brand, password or status change rotates the security stamp. Admin writes run under `app_admin` with `SET ROLE` per operation until stage 10 (#16). Screens in `Features/Identity` (users list, user detail dialog), bUnit and integration tests. `UserDeactivated` stays a domain event until a consumer needs it.

This PR comes last among the features because it is independent of the Inventory flow; it is the first cut (section 12).

---

## 10. Static analysis (#61)

- **`SonarAnalyzer.CSharp`** (free NuGet analyzer, the SonarQube C# rules) in `Directory.Build.props` for all projects except tests and EF `Migrations`, under the existing `TreatWarningsAsErrors`. Rules that conflict with the codebase's conventions are switched off in `.editorconfig`, each with a one-line reason.
- **Duplication:** `jscpd` over `src/` (excluding `Migrations`, generated files and `wwwroot/lib`) in a new `tools/check.sh duplication` step, failing above 3 % duplicated lines; CI calls the same step.
- Both run inside the existing CI jobs, so the gate is already part of the required checks; no repository setting changes.
- **Why not SonarQube Cloud now:** it needs an organisation and token created by the owner (accounts are the owner's), and its free plan offers no custom gates and only main-branch analysis (#61). The owner can add it later on top of this gate; the issue records the choice.
- Results: README section "Code quality" points to the CI logs and the coverage report.

---

## 11. Pull requests

Each PR passes the full `tools/check.sh` and `tools/check.sh restore build e2e topology`, is merged with a rebase when every CI check is green, and gets a 3-line entry in the log comment of the stage issue. Closing keywords appear only in PR descriptions.

### 5a: Stage 5 plan
**Scope:** this document; the stage issue #103; new issue #104 and the note on #14 (section 13). **Acceptance:** the plan is merged and linked from ARCHITECTURE_DECISIONS §13.

### 5b: Inventory domain and remap
**Scope:** section 1; `DatabaseErrorMapper` in the transaction decorator (2.3); the `xmin` migration; Inventory sentinels (#19, partly); `PersistenceModel.cs` reduced to the plain data classes, split into named files.
**Acceptance:** `check-model` shows no pending changes after the reviewed `xmin` migration; schema diff and SQL suite green; domain unit tests cover every transition; unit coverage reported. **Part of #19.**

### 5c: Stock API and warehouse queries
**Scope:** stock overview, position, levels, movements, adjustments (3.1); `IWarehouseQueries` with the internal endpoint and HTTP client (4.3); the OpenAPI rule of 3.3.
**Acceptance:** integration tests per endpoint and status; topology test reaches `/stock` through the gateway; `/internal/v1/inventory/warehouses` not routed. **Closes #97.**

### 5d: Transfers API and events
**Scope:** create, list, detail, approve, cancel, dispatch, receive; brand scope; `allowedActions`; `TransferApproved`, `TransferCancelled`, `TransferRejected` contracts; outbox rows.
**Acceptance:** the role × action matrix (planner, brand manager in and out of scope, keeper of donor and of another hub, administrator, sponsor) as integration tests; SS200/SS201/SS300/SS302 mapped to 409/403 with codes; one outbox row per successful command, none on failure.

### 5e: CSV import API
**Scope:** section 5; `ImportData` policy; `PartCatalogChanged`.
**Acceptance:** the four integration scenarios of section 5; parser unit tests; request limit and rate limit tested.

### 5f: Stock screens
**Scope:** stock overview and position, adjustment dialog, levels editing; navigation entries; shared list/card and error components.
**Acceptance:** bUnit states and mobile rendering; manual check in the single host with the demo dataset.

### 5g: Transfer screens and the end-to-end scenario
**Scope:** transfers list, detail with actions, new-transfer form; the Playwright scenario of section 6.
**Acceptance:** bUnit for actions and validation; Playwright green in CI.

### 5h: Import screen
**Scope:** upload, report, history, CSV templates.
**Acceptance:** bUnit for upload states and the report; manual import of a demo file.

### 5i: Housekeeping
**Scope:** #30 retention job, the remaining sentinels (#19), #17 lock-file workflow, #82 check.
**Acceptance:** retention job integration test; no EF warning 20601 at host start-up; the lock-file workflow validated by a dry run on a branch. **Closes #19, #30, #17** (and #82 if adopted).

### 5j: Static analysis gate
**Scope:** section 10.
**Acceptance:** a deliberate rule violation and a duplicated block fail `tools/check.sh` on a scratch branch; the choice is recorded in BACKEND_REQUIREMENTS. **Closes #61.**

### 5k: User administration
**Scope:** section 9. **Acceptance:** as in #46. **Closes #46.**

### 5l: Documentation and release
**Scope:** ARCHITECTURE_DECISIONS (§3, §13), API_DOCUMENTATION, FRONTEND, BACKEND_REQUIREMENTS (coverage, static analysis), MODULE_MAPPING (Inventory status), CHANGELOG; exit-criteria evidence (section 12.1); tag `v0.5.0`, release, close the milestone.

**Order and dates:** 26 Nov 5a–5b; 27 Nov 5c–5d; 28 Nov 5e–5g; 29 Nov 5h–5j; 30 Nov 5k–5l.

---

## 12. Exit criteria for v0.5.0 and the timebox

1. The Inventory aggregates replace their persistence classes; `check-model` clean; schema diff and the SQL suite green on the EF-built database.
2. A planner proposes and approves a transfer, a keeper of the donor dispatches it and a keeper of the recipient receives it, through the API and the screens; stock and reservations change exactly as the SQL functions define; brand scope and warehouse checks refuse the wrong users.
3. `TransferApproved` and `TransferCancelled` are written to the outbox in the same transaction and delivered; the contracts of section 4.1 are documented in `Inventory.Contracts`.
4. A part catalogue and a demand history CSV are imported through the screen, with the quality gate and row errors shown.
5. Every Inventory endpoint is documented with summaries, descriptions and examples in the public OpenAPI document.
6. Screens are responsive, show loading and error states with the Reference, and are covered by bUnit; the Playwright scenario passes in CI.
7. Unit coverage ≥ 78 % (gate 70 %); the static-analysis gate runs in CI.
8. #46, #61, #17, #19, #30 and #97 are closed or explicitly moved by the timebox.

**Cut order if the stage runs late** (each cut becomes an issue in a later milestone; the later stages are not moved):
1. 5k user administration (#46) → milestone v0.10.0 or v0.11.0, decided with the owner.
2. 5j static analysis (#61) → v0.6.0.
3. 5i housekeeping items one by one → v0.6.0.
4. Stock levels editing and the adjustment dialog → v0.6.0.
5. `PartCatalogChanged` → stage 6 with its first consumer (#12).

The stock overview, the transfer flow with its screens and the E2E scenario are never cut: they are the demo for 2 December.

### 12.1 Evidence

Filled in by PR 5l.

---

## 13. Stage issue and new issues

- **Stage issue:** #103 — the plan, the PR list and the running log comment.
- **New: #104** Exceptional demand flagging (US08): API and screen over `inventory.find_exceptional_demand_candidates` and `flag_exceptional_demand`; `stage-8`, `enhancement` (it feeds the forecasting data).
- **Stage 8 (#14), extended:** the `TransferRequested` consumer in Inventory publishes `TransferApproved` / `TransferRejected` and adds `inventory.transfer_order.recommendation_id`.
