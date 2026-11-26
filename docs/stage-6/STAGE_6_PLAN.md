# SmartSpare — Stage 6 Plan: Fleet (Machines, Engine Hours, Maintenance Planning, Service Orders)

Status: **proposed** (26 November 2026). Stage 6 of the work plan ([ARCHITECTURE_DECISIONS.md, section 13](../architecture/ARCHITECTURE_DECISIONS.md#13-work-plan)), tracked in #118. Stage 6 is the core of the diploma topic, maintenance planning for agricultural machinery dealers (UC02). It turns the Fleet module from the stage 2 table port into a business module:
- the Fleet domain model of [ADR 0013](../adr/0013-domain-model-and-persistence.md): customer machines, engine-hour readings, maintenance plans by engine hours and by calendar, service orders with their state machine and kits;
- the **maintenance due calculation** as a documented, unit-tested domain service (section 2);
- the service-kit flow between Fleet and Inventory as integration events, which removes the last cross-module writes of Fleet (#12), with Fleet as the first consumer of `PartCatalogChanged`;
- the versioned Fleet API with full OpenAPI descriptions;
- the Blazor screens for the service engineer's tablet;
- the stage 6 housekeeping issues (#30, #17, #82).

Release target: **v0.6.0**.

**Product owner's decisions, 26 November 2026:**
- **Schedule:** stage 5 closed early (v0.5.0), so stage 6 starts now. Window **26 November – 4 December 2026** (milestone v0.6.0, due 4 December). Stage 6 has priority over the housekeeping items: it is the core of the topic.
- **Timebox:** if the stage runs late, scope is cut in the order of section 10; the later stages do not move.
- **Demo-ready `main`:** the experts review from **2 December 2026**. Every PR leaves `main` deployable and demonstrable: no half-finished screen is linked from the navigation, and the Inventory screens keep working throughout.
- **Autonomous delivery** as in stages 4 and 5: implement per this plan, full check set, PR, merge on green, log entry on the stage issue; stop only on plan contradictions, decisions not covered here or by an ADR, money or accounts, or date risk.

Inputs, as on `main` (v0.5.0):
- BA use case UC02 (engine hours → due service → service order with a reserved kit, alternative A1 "odometer rollback"), the user stories US01–US06 and the wireframe WF03 (machine card on the tablet), as recorded in [DATABASE_DESIGN.md](../database/DATABASE_DESIGN.md) sections 4.1, 6.3, 6.4 and 10;
- [MODULE_MAPPING.md](../database/MODULE_MAPPING.md) sections 1.4, 2.2, 2.3, 2.5, 3.1 (X5–X10), 3.2 and 3.5;
- #12 with its acceptance items (product owner, 26 November 2026);
- [STAGE_3_PLAN.md](../stage-3/STAGE_3_PLAN.md) section 3.2 (policies `RecordServiceWork`, `ManageServiceOrders` and the parity rule);
- [STAGE_5_PLAN.md](../stage-5/STAGE_5_PLAN.md) sections 1–4 and 7: the domain-model pattern, "the SQL function stays the invariant guard", `DatabaseErrorMapper`, the event publication rule and the coverage definition by layer;
- the SQL in `db/sql/fleet/0001_fleet_logic.sql`, the transitional views in `db/sql/reporting/9000_transitional_cross_module.sql`, T001 section 7–8 and T02 "UC02".

No new ADR is needed: every decision below applies ADRs 0003, 0004, 0005, 0012, 0013, 0016, 0017 and 0019. Two decisions that refine earlier documents are marked as such (F3, F9).

---

## 0. Summary of decisions

| # | Decision | Section |
|---|---|---|
| F1 | **One model per table (ADR 0013).** `Domain/` gets the aggregates `Machine` (with its readings), `MaintenancePlan` (with its kit) and `ServiceOrder` (with `ServiceOrderLine`), the value objects `EngineHours` and `UsageRate`, and the domain service `MaintenanceDueCalculator`. `Customer`, `MachineModel`, `PartSnapshot` and the `ref` principals stay plain data classes in named files. `PersistenceModel.cs` is deleted. | 1 |
| F2 | **Maintenance due calculation in C#**, a pure domain service with a documented formula (section 2): next due threshold from the effective interval (OEM interval × operating-condition factor), the remaining hours, the **average daily usage** from the reading history, the projected due date, the calendar interval, and a status `Overdue` / `DueSoon` / `Planned` / `Ok`. It agrees with `fleet.fn_maintenance_due` on every threshold (parity integration test); SQL keeps deciding the automatic drafts. | 2 |
| F3 | **Calendar intervals** (refines the V016 schema): `fleet.maintenance_plan.interval_months` (nullable, 1–60). A plan is due by engine hours or by calendar, **whichever comes first**. The calendar side drives the due list and manual orders; automatic drafting on a reading stays hours-only (US05). | 2.4 |
| F4 | **The SQL functions stay the invariant guard** (STAGE_5_PLAN I2): readings through `fleet.log_engine_hours` (odometer triggers SS100–SS103), drafts through `fleet.create_service_order_draft`, completion and cancellation through module-local `fleet.complete_service_order` / `fleet.cancel_service_order`. The handler asks the aggregate first (fast, readable, unit-tested), then calls the function in the same transaction. | 3 |
| F5 | **Service kits as integration events (#12).** Fleet publishes `ServiceOrderDrafted`, `ServiceOrderCompleted`, `ServiceOrderCancelled` (and `ServiceOrderLinesChanged` with line editing); Inventory reserves (`inventory.reserve_service_kit`), issues (`inventory.issue_service_kit`) and expires kits and publishes `ServiceKitReserved`, `ServiceKitIssued`, `ServiceKitReservationExpired`. Fleet keeps `service_order_line.qty_reserved`. The four transitional functions lose every cross-module statement; both Fleet views become module-local (X9, X10). | 5 |
| F6 | **`fleet.part_snapshot`** (part id, brand, OEM number, description, unit of measure, active, version) is fed by **`PartCatalogChanged`**, idempotent through the inbox. Fleet is the first consumer, so **Inventory now writes `PartCatalogChanged` to the outbox in the same transaction** as every catalogue write (the import), with an integration test (#12 acceptance). Kit lines are validated against the snapshot. | 5.2 |
| F7 | **Q2 of #12 decided** (the proposal on the issue): completing an order issues every reserved quantity, and the unreserved remainder **up to the available stock**; the shortfall is reported in `ServiceKitIssued` and shown on the order. Completion never fails on stock. | 5.4 |
| F8 | **API under `/api/v1/fleet/...`**: machines (search by partial VIN, card, readings), engine-hour readings, due maintenance, maintenance plans, service orders (list, detail, create, start, complete, cancel). Every endpoint has a summary, a description, parameter descriptions, response types and examples (ADR 0019, the stage 5 rule). | 4 |
| F9 | **Policies:** `RecordServiceWork` and `ManageServiceOrders` keep their stage 3 role lists and their SQL parity (the rewritten functions keep their `util.require_role` lists). New read policy **`ReadFleet`** (all six roles, never brand-scoped). Fleet has no approval action, so no brand scope. Integration-event consumers act as the event's user with no roles (`ActingUser`); the Inventory kit functions check that a user is set, not a role (refines STAGE_5_PLAN 4.1, which left the consumer session context to its first consumer). | 4.2, 5.5 |
| F10 | **Screens** in `Features/Fleet`, mobile-first for the engineer's tablet (WF03): machine list and card, engine-hours entry, due-maintenance list, service orders (list, detail, create, start, complete, cancel). bUnit per screen; one Playwright scenario: an engineer logs engine hours, the machine becomes due, a service order is drafted and completed. | 6 |
| F11 | **Coverage by layer** (STAGE_5_PLAN section 7): the Fleet domain and the due calculator carry the unit tests; data access lives in `Infrastructure`. Gate 70 %, three figures reported. | 7 |

---

## 1. Domain model (ADR 0013)

`src/Modules/Fleet/SmartSpare.Modules.Fleet/Domain/`. Domain classes reference no EF Core or ASP.NET Core types (architecture tests).

| Type | Kind | Behaviour and invariants |
|---|---|---|
| `Machine : AggregateRoot<long>` | aggregate | VIN, model, customer, operating condition, home hub, `Status`, `CurrentEngineHours`, `EngineHoursReadAt` (written by `trg_engine_hours_apply`, private setters). `RecordReading(hours, readAt, source, lastReading, now)` returns a `Result` with the odometer rules of `trg_engine_hours_validate`: machine active (SS102 parity), time after the last reading (SS103), hours not below the last reading (SS100, UC02 A1), increase not above the elapsed wall-clock hours (SS101), not in the future (5 minutes clock tolerance), within 0–100,000 h (CHECK). The reading is written by `fleet.log_engine_hours`, never by EF (F4). `xmin` concurrency. |
| `EngineHoursReading : Entity<long>` | entity (append-only) | Hours, time, `ReadingSource`, recorded by. No update method (`trg_engine_hours_immutable`). |
| `EngineHours` | value object | `numeric(8,1)`: 0–100,000, one decimal; validates input before the CHECKs would. |
| `MaintenancePlan : AggregateRoot<int>` | aggregate (read-mostly) | Model, name, `IntervalHours` (10–20,000, CHECK), `IntervalMonths` (F3), active, kit `MaintenancePlanPart` (part id, quantity > 0). `EffectiveInterval(conditionFactor)` = round(interval × factor, 1) (F5 of the BA findings). Plans are reference data from the OEM: maintained by migrations and T001 in this stage, not through the API. |
| `ServiceOrder : AggregateRoot<long>` | aggregate | Machine, optional plan (null = unplanned repair), hub, due and trigger engine hours, status, lines. State machine of `trg_service_order_status_guard`: draft → parts_reserved \| in_progress \| completed \| cancelled; parts_reserved → draft \| in_progress \| completed \| cancelled; in_progress → completed \| cancelled. Methods: `DraftFromPlan(machine, plan, due, trigger, kit)`, `DraftRepair(machine, lines)`, `ApplyKitReserved(lines, fullyReserved)` (→ parts_reserved when every line is covered), `ApplyKitReservationExpired(lineIds)` (→ draft), `Start(now)`, `Complete(engineHours, machineHours, now)` (hours ≥ trigger hours and ≥ the machine's current hours), `ApplyKitIssued(lines)`, `Cancel(reason, now)` (reason required), `AddLine` / `ChangeLineQuantity` / `RemoveLine` only while draft or parts_reserved. Kit events on a terminal order are ignored (5.5). Raises `ServiceOrderDraftedDomainEvent`, `ServiceOrderCompletedDomainEvent`, `ServiceOrderCancelledDomainEvent`. |
| `ServiceOrderLine : Entity<long>` | child entity | Part, `QtyRequired` (> 0), `QtyReserved` (new copy, 0 ≤ reserved ≤ required), `QtyIssued` (≤ required, CHECK), `Shortfall` = required − issued after completion. |
| `Number` | string | `SO-YYYY-NNNNNN`, assigned by `util.trg_assign_document_number('SO')`; read back with the stage 5 `TemporaryDocumentNumberGenerator`. |
| `UsageRate` | value object | Average engine hours per calendar day from a reading history (section 2.2); `Unknown` when the history is too short. |
| `MaintenanceDueCalculator` | domain service | Section 2. Pure: inputs are values, no I/O, no clock (the caller passes `today`). |
| `PartSnapshot` | data class | Read-only copy of an Inventory part (F6); `Apply(snapshot)` keeps the newer `version` only. |
| `FleetErrors` | static class | Expected errors with stable codes (`Fleet.ReadingDecreased` ↔ SS100, `Fleet.ReadingImplausible` ↔ SS101, `Fleet.MachineNotActive` ↔ SS102, `Fleet.ReadingOutOfOrder` ↔ SS103, `Fleet.ServiceOrderNotOpen` ↔ SS300, …). |

**The remap must not change the schema**, except the reviewed changes of this stage: the `xmin` tokens of `Machine` and `ServiceOrder` (schema-free), `maintenance_plan.interval_months` (F3), `service_order_line.qty_reserved` and `fleet.part_snapshot` (F5, F6). Each is listed in `tools/schema_diff.allow` with its PR. `check-model` stays clean. Until stage 10 no database is upgraded in place (MODULE_MAPPING 3.2), so the transitional objects are replaced by editing the Fleet and Inventory SQL parts and deleting them from the Reporting transitional script; the new columns and table come as ordinary migrations.

---

## 2. Maintenance due calculation (diploma material)

`Domain/Maintenance/MaintenanceDueCalculator.cs`, documented for the thesis in a new `docs/architecture/MAINTENANCE_PLANNING.md` (formula, worked example from the demo data, parity with SQL, limits). Every rule below has a unit test with hand-computed numbers.

### 2.1 Engine-hour threshold (parity with `fleet.fn_maintenance_due`)

For a machine with current engine hours *h* (latest reading), first known reading *h₀*, and a plan with OEM interval *H* under an operating condition with factor *f* (1.0 normal, < 1 for severe dust, high load, non-OEM oil):

- effective interval **E = round(H · f, 1)**;
- last served threshold **L** = the highest `due_engine_hours` of the machine's non-cancelled orders for the plan; without any, **L = ⌊h₀ / E⌋ · E** (thresholds before the first known reading are unknown history and are never drafted);
- next due threshold **D = max((⌊L / E⌋ + 1) · E, ⌊h / E⌋ · E)** — if several thresholds were skipped, only the latest crossed one is due;
- remaining hours **R = D − h** (R ≤ 0 means overdue by −R hours).

`fleet.fn_maintenance_due` computes exactly this; `log_engine_hours` drafts every plan with **R ≤ 50 h** (US05). An integration test runs the calculator and the SQL function over all 500 demo machines and asserts the same D and R for every machine and plan.

### 2.2 Average daily usage

From the readings in a **look-back window of 365 days** before the latest reading:

- **u = (h_last − h_first) / days(t_first, t_last)**, in hours per calendar day;
- known only if the window holds at least two readings that are **at least 14 days apart**; otherwise the whole history is used under the same rule; otherwise *u* is unknown;
- the window spans whole seasons on purpose: agricultural machines run in campaigns (sowing, harvest), and a short window would project a harvest-week rate over the winter. Seasonal profiles per machine type are a future refinement (#119).

### 2.3 Projected due date by hours

**t_h = t_last + R / u** (days), when *u* > 0. With R ≤ 0 the date is the last reading (already due). With *u* unknown or 0 there is no projected date; the status then follows the hours alone.

### 2.4 Calendar interval (F3)

With an interval of *M* months, the anchor *A* is the completion date of the last completed order of the plan; without one, the machine's commissioning date; without that, the first reading. Calendar due date **t_c = A + M months**. The plan's due date is **t_due = min(t_h, t_c)** with the trigger named (`EngineHours` or `Calendar`) — whichever comes first.

### 2.5 Status

With the planning horizon *P* (default **14 days**, query parameter) and the drafting horizon of 50 h:

| Status | Rule |
|---|---|
| `Overdue` | R ≤ 0, or t_c ≤ today |
| `DueSoon` | R ≤ 50 h, or t_due ≤ today + P |
| `Planned` | an open service order (draft, parts_reserved, in_progress) of the plan exists; it counts as served in L, so D is already the threshold after it |
| `Ok` | otherwise |

`Planned` takes precedence, so the due list shows what still needs an order. The due list (`GET /maintenance/due`) returns every active machine and plan with status `Overdue` or `DueSoon` by default, ordered by due date, with D, R, *u*, t_h, t_c, the trigger, and the open order if any.

### 2.6 Data access

`Infrastructure/Queries/MaintenanceDueInputs` reads the inputs set-based in one round trip per page of machines (machine, factor, plans, last served threshold, last completion, first and last reading in the window), so the calculator runs in memory over the 500 demo machines well within NFR01. No calculation runs in the query.

---

## 3. Handlers and the database

### 3.1 Record an engine-hour reading (UC02 steps 1–7)

1. Load the machine and its last reading, untracked (404 if missing).
2. `Machine.RecordReading(...)` — a failed `Result` (SS100–SS103 rules) returns 422 with the code without touching the database.
3. `SELECT * FROM fleet.log_engine_hours(@machine, @hours, @readAt, @source)` in the same transaction. The function inserts the reading (triggers repeat every check and lock the machine row), updates the odometer and drafts every plan due within 50 h through `fleet.create_service_order_draft`.
4. From PR 6g: for each drafted order the handler adds `ServiceOrderDrafted` to the unit of work, so the outbox row commits with the reading (F5).
5. The response returns the reading, the new engine hours and the drafted orders (number, due threshold, kit status `reserving`).

### 3.2 Service order commands

| Command | Written by | Policy |
|---|---|---|
| Create from plan (due list "Create order", calendar-due plans) | `fleet.create_service_order_draft(machine, plan, D)`; D from the calculator, so the partial unique index `uq_service_order_machine_plan_due` and the next automatic draft never duplicate it | `ManageServiceOrders` |
| Create unplanned repair (lines from the part snapshot) | EF; triggers guard | `ManageServiceOrders` |
| Change kit lines (draft or parts_reserved) | EF; publishes `ServiceOrderLinesChanged` | `ManageServiceOrders` |
| Start work | EF (status guard trigger) | `RecordServiceWork` |
| Complete (engine hours at completion) | `fleet.complete_service_order` (module-local: open quantities, status, completion data); a completion with more hours than the machine's records a `service_visit` reading through `log_engine_hours` first; publishes `ServiceOrderCompleted` | `RecordServiceWork` |
| Cancel (reason) | `fleet.cancel_service_order` (module-local); publishes `ServiceOrderCancelled` | `ManageServiceOrders` |

The hub of a new order is the machine's home hub (validated through `IWarehouseQueries`, MODULE_MAPPING 2.2). `DatabaseErrorMapper` (STAGE_5_PLAN 2.3) maps SS100–SS103 to 422 and SS300 to 409.

### 3.3 Queries

`AsNoTracking` projections or raw SQL in `Infrastructure/Queries` (the stage 5 rule). `fleet.v_machine_maintenance_status` becomes module-local (X10: `home_warehouse_id` instead of the code) and stays for reports; the machine card computes the full due calculation in C# instead. Hub codes are resolved in C# through `IWarehouseQueries`. Lists filter and page the due status in memory after the calculation, which suits the dealer fleet (hundreds of machines, NFR01); a larger fleet would persist the calculated status. Partial-VIN search uses the trigram index `ix_machine_vin_trgm`. Customers are shown by their pseudonymous `customer_code` only (DATABASE_DESIGN 8.5).

---

## 4. HTTP API

### 4.1 Endpoints

All under `/api/v1/fleet`, JSON, ProblemDetails with `code` and `traceId`, paged lists as `PagedResponse<T>`, antiforgery on unsafe methods through the BFF.

| Method and path | Policy | Purpose |
|---|---|---|
| `GET /machines?search&warehouseId&status&dueStatus&page&pageSize` | `ReadFleet` | Machine list: VIN (partial), model, brand, customer code, hub, engine hours, last reading, next service and due status. |
| `GET /machines/{id}` | `ReadFleet` | Machine card (WF03): data, engine hours, usage rate, every plan with its due calculation, open and recent service orders. |
| `GET /machines/{id}/readings?page&pageSize` | `ReadFleet` | Reading history, newest first. |
| `POST /machines/{id}/readings` | `RecordServiceWork` | Record a reading (3.1); returns drafted orders. `201 Created`. |
| `GET /maintenance/due?warehouseId&horizonDays&status&page&pageSize` | `ReadFleet` | Due-maintenance list (2.5). |
| `GET /maintenance-plans?modelId` | `ReadFleet` | Plans with effective intervals per operating condition and kits (part snapshot data). |
| `GET /service-orders?status&warehouseId&machineId&page&pageSize` | `ReadFleet` | Service orders, newest first. |
| `GET /service-orders/{id}` | `ReadFleet` | Detail: lines (part, required, reserved, issued, shortfall), kit status, timeline, `allowedActions` for the current user. |
| `POST /service-orders` | `ManageServiceOrders` | Create from a plan or as an unplanned repair. `201 Created` with `Location`. |
| `PUT /service-orders/{id}/lines` | `ManageServiceOrders` | Replace the kit lines of an open order (cut item, section 10). |
| `POST /service-orders/{id}/start` | `RecordServiceWork` | Work started. |
| `POST /service-orders/{id}/complete` | `RecordServiceWork` | Complete with engine hours. |
| `POST /service-orders/{id}/cancel` | `ManageServiceOrders` | Cancel with a reason. |

### 4.2 Authorization

- `RecordServiceWork` (`SERVICE_ENGINEER`, `ADMINISTRATOR`) fronts `fleet.log_engine_hours` and `fleet.complete_service_order`; `ManageServiceOrders` (`SERVICE_ENGINEER`, `BRAND_MANAGER`, `ADMINISTRATOR`) fronts `fleet.create_service_order_draft` and `fleet.cancel_service_order`. The rewritten functions keep their role lists, so the parity test of STAGE_3_PLAN 9.1 stays green unchanged. Start and line changes are EF writes outside the parity rule; they use the policy of the same actor.
- New **`ReadFleet`** = all six roles (read only, never brand-scoped, like `ReadInventory`): planners see open kit demand, keepers prepare kits, the sponsor sees the fleet.
- `allowedActions` is computed with the same policies and the aggregate's state rules.

### 4.3 OpenAPI

The stage 5 rule (STAGE_5_PLAN 3.3, #97): `WithName`, `WithSummary`, `WithDescription`, `[Description]` on parameters, `Produces`/`ProducesProblem` for every status, `WithResponseExample` for 200/201 and the main errors (400, 403, 404, 409 SS300, 422 SS100/SS101). `ApiDocumentationTests` cover every Fleet operation and schema.

---

## 5. Integration events and the service-kit flow (#12)

### 5.1 Events

Routing key `<Event>.v1` (ADR 0017). Payload ids are plain values (no cross-module FKs, MODULE_MAPPING 2.5).

| Publisher | Event | When | Payload | Consumer |
|---|---|---|---|---|
| Fleet | `ServiceOrderDrafted` | draft committed (reading or manual) | serviceOrderId, number, warehouseId, lines (lineId, partId, quantity), actorId, draftedAt | Inventory: reserve |
| Fleet | `ServiceOrderLinesChanged` | kit lines changed on an open order | serviceOrderId, warehouseId, added/changed (lineId, partId, quantity), removed lineIds, actorId | Inventory: adjust holds |
| Fleet | `ServiceOrderCompleted` | completion committed | serviceOrderId, warehouseId, lines (lineId, partId, open quantity), actorId, completedAt | Inventory: issue |
| Fleet | `ServiceOrderCancelled` | cancellation committed | serviceOrderId, lineIds, reason, actorId | Inventory: release |
| Inventory | `ServiceKitReserved` | holds written | serviceOrderId, lines (lineId, reservedQty), fullyReserved, expiresAt | Fleet: `qty_reserved`, status |
| Inventory | `ServiceKitReservationExpired` | expiry job released holds | serviceOrderId, lineIds | Fleet: back to draft, `qty_reserved = 0` |
| Inventory | `ServiceKitIssued` | `ISSUE_SERVICE` movements posted | serviceOrderId, lines (lineId, issuedQty, shortfallQty) | Fleet: `qty_issued`, shortfall |
| Inventory | `PartCatalogChanged` (stage 5 contract) | catalogue write committed | chunks of ≤ 500 part snapshots | Fleet: `part_snapshot` |

Every event gets a contract test (outbox type name, JSON shape, round trip). By the publication rule (STAGE_5_PLAN 4.1) each event is written to the outbox in the PR that adds its consumer, never earlier.

### 5.2 Part snapshot (F6)

- **Fleet:** `fleet.part_snapshot` (EF table; `part_id` primary key, brand code, OEM number, description, unit of measure, `is_active`, `source_occurred_at`, `updated_at`), no FK to `catalog`. The consumer upserts each chunk in one statement and keeps the row of the newer event (`source_occurred_at` = the event's `OccurredAt`; the stage 5 contract needed no change), so a late or replayed chunk never overwrites newer data; the inbox records the message id (idempotency). Kit lines of repair orders must reference an active snapshot part; plan kits are reference data.
- **Inventory:** the import handler writes `PartCatalogChanged` in the same transaction as `staging.process_batch` when the batch loaded part rows (chunks of 500 parts the load inserted or changed: `created_at` or `updated_at` equals the transaction time). It is the only catalogue write in the API today; a later catalogue write must queue the event through `PartCatalogPublication` too. Integration test: an import writes the expected outbox rows in the same transaction; a failed quality gate writes none.
- **Initial fill:** databases built before this PR, and the T001 dataset, already have parts. The job `inventory.part-catalog-publish` (`--job` in the services topology; the single host has no scheduler until #30) publishes the whole catalogue in chunks; T001 fills `fleet.part_snapshot` directly, as if the events had been delivered (test data writes every schema already).

### 5.3 Drafting and reservation (X6)

`fleet.create_service_order_draft` keeps creating the order and its lines from the plan kit, without touching `inventory`; the order stays `draft`. `ServiceOrderDrafted` goes to the outbox. Inventory's consumer calls the new **`inventory.reserve_service_kit`**: per line in part order, lock the balance row, soft-reserve `least(available, quantity)` for 7 days with `service_order_line_id`, and return the reserved quantity per line — the same locking order and rules as today's transitional body. It publishes `ServiceKitReserved`; Fleet stores `qty_reserved` and moves a fully reserved order to `parts_reserved`. In the single host the in-process transport delivers both hops within the dispatcher interval, so the screen shows "Reserving kit…" until the order refreshes.

### 5.4 Completion and issue (X7, F7)

`fleet.complete_service_order` (module-local) checks the state, stores completion hours and time and sets `completed`. `ServiceOrderCompleted` carries the open quantity per line. Inventory's consumer calls **`inventory.issue_service_kit`**: consume the line's holds, post `ISSUE_SERVICE` for the reserved quantity plus the unreserved remainder **up to the available stock** (with `unit_cost` from `catalog.part`), and return issued and shortfall per line. `ServiceKitIssued` updates `qty_issued`; the order shows the shortfall ("2 of 3 issued, 1 short"). The order stays completed: the work is done, the shortfall is a stock problem for the keeper.

### 5.5 Expiry, cancellation, ordering and the consumer's user

- **Expiry (X5):** `inventory.expire_soft_reservations` becomes module-local (expires holds, no `fleet` update) and returns the affected service orders; the job publishes `ServiceKitReservationExpired` per order. The job runs on Quartz in the single host (hourly) and as `--job reservation-expiry` in the services topology.
- **Cancellation (X8):** `ServiceOrderCancelled` releases every open hold of the order's lines.
- **Ordering:** kit events for an order that is already completed or cancelled are ignored by Fleet (logged at information level). Holds created for an order cancelled in between (a `ServiceOrderDrafted` redelivered after `ServiceOrderCancelled`) are released by the 7-day expiry at the latest; this bounded case is documented and tested.
- **Consumer's user (F9):** a consumer runs outside a request. It sets `ActingUser` to the event's `actorId` with the reason `event:<Event>`, so the audit and `created_by` name the engineer who acted, with **no roles**. `reserve_service_kit` and `issue_service_kit` therefore check `util.require_app_user()` (SS301 without a user) instead of a role: the role gate was applied in Fleet when the user acted, and the functions are granted to `app_write` only. Stage 10's per-module roles then limit them to the Inventory role (MODULE_MAPPING 4.2).

### 5.6 Views and the transitional objects

- `fleet.v_machine_maintenance_status` and `fleet.v_service_kit_shortage` move from the Reporting transitional script into the Fleet SQL, reading only `fleet` (`part_snapshot`, `qty_reserved`) and returning hub ids (X9, X10).
- `fleet.create_service_order_draft`, `fleet.complete_service_order`, `fleet.cancel_service_order` and `inventory.expire_soft_reservations` keep their names and role lists but lose every cross-module statement; their `TRANSITIONAL` headers are removed. (#12 says "remove the four transitional functions": what is removed is their cross-module part; keeping the names keeps the policy parity and the callers. #12 is updated accordingly.)
- The T02 assertions on kit reservation and expiry move to C# integration tests of the event flow (MODULE_MAPPING 6, item 6); T02 keeps the odometer, idempotency and status-guard assertions. T001 reproduces the demo state (the Lexion order with its soft-reserved kit, completed orders with their issues) by calling the Inventory kit functions after the drafts, as the consumers would.
- RabbitMQ: two new consumer queues per direction within the CloudAMQP budget (ADR 0017 §5: ≤ 100 queues); the topology test gains a `ServiceOrderDrafted` → `ServiceKitReserved` round trip.

### 5.7 Reservation policy (product owner's questions, 27 November 2026)

- **Partial, not all-or-nothing.** `inventory.reserve_service_kit` holds, per line, the smaller of the required and the available quantity, and reports what it held. A kit with a short line is still worth holding: the parts that are there are kept for the engineer, the shortage shows in `fleet.v_service_kit_shortage` (Fleet's `qty_reserved` copy) as input for a transfer or a purchase, and the order moves to `parts_reserved` only when every line is fully held. All-or-nothing would release good stock to other demand because one part is missing, and would turn a temporary shortage into an empty kit. Transfers stay all-or-nothing (`approve_transfer`): a transfer is one physical shipment.
- **Stock invariant.** `stock_balance` keeps `0 <= reserved_qty <= on_hand_qty` (CHECKs, T01 and T02), and every allocation, transfers included, uses `available = on_hand − reserved`; a unit held for a kit cannot be transferred (SS200, T02).
- **Lock order.** The kit's balance rows are locked in one statement in `(part_id, warehouse_id)` order before any hold is written, the order `approve_transfer` uses too; an integration test reserves overlapping kits in opposite orders 25 times concurrently (and fails with a deadlock when the order is removed).
- **Idempotency.** A line that already has a hold is not reserved again, on top of the inbox deduplication of the consumer (6g2).
- **What releases a hold.** Expiry after 7 days (soft holds, 6g3, `ServiceKitReservationExpired`); completion consumes the holds and issues the parts (6h, `ServiceOrderCompleted` → `ServiceKitIssued`); **cancellation releases them** (6h, `ServiceOrderCancelled` → Inventory releases every open hold of the order's lines). Cancellation is in scope of this stage (PR 6h); until 6h the transitional `fleet.cancel_service_order` releases the holds directly, as it does today.

---

## 6. Web client

In `src/Web/SmartSpare.Web.Client/Features/Fleet`, typed `FleetClient`, DTOs and validators in `SmartSpare.ApiContracts.Fleet`.

| Screen | Route | Content |
|---|---|---|
| Machines | `/fleet/machines` | Search by partial VIN (search-as-you-type, UC02 step 1), hub and due-status filters; cards on phone and tablet, grid on desktop; due chip with icon and text. |
| Machine card (WF03) | `/fleet/machines/{id}` | Engine hours with last reading, usage rate, the plans with next threshold, remaining hours, projected and calendar due dates and status; open and recent orders; big "Log engine hours" button. |
| Engine-hours entry | dialog on the card | Hours, time (default now), source; client validation from ApiContracts (not below the last reading, not in the future); the server errors SS100/SS101/SS103 as readable messages; the drafted orders shown after saving with a link. |
| Due maintenance | `/fleet/maintenance` | Hub, horizon and status filters; list ordered by due date with trigger (hours or calendar); "Create order" for rows without an open order. |
| Service orders | `/fleet/service-orders` | Status tabs, hub filter; number, machine, plan, status, kit status, age. |
| Service order detail | `/fleet/service-orders/{id}` | Lines with required / reserved / issued / shortfall; timeline; buttons from `allowedActions` (start, complete with engine hours, cancel with reason); kit status refreshes while "reserving" or "issuing". |
| New service order | `/fleet/service-orders/new` | From a machine and plan (prefilled from the due list), or an unplanned repair with lines from a part search. |

Common rules as in stage 5: **mobile-first** (the engineer's tablet at 768–1024 px is the primary layout, touch targets ≥ 44 px, one-hand actions at the bottom), skeletons and explicit empty states, ProblemDetails → snackbar with "Reference: <traceId>", strings through `IStringLocalizer`, WCAG 2.1 AA (status never by colour alone). Navigation: the Fleet area (already present for the two policies) gets Machines, Due maintenance, Service orders; it is linked only once its screens are merged (demo-ready rule).

**Tests.** bUnit per screen: loading, empty, data and error states; actions from `allowedActions`; reading validation; card layout at tablet width. **Playwright (one scenario, desktop and tablet):** a service engineer signs in, opens a demo machine whose next engine service is a few hours away, logs engine hours that cross the threshold, sees the machine become due and the drafted service order with its kit reserved, starts and completes it with the engine hours, and sees the status "Completed" with the kit issued.

---

## 7. Tests and coverage

| Level | New in stage 6 |
|---|---|
| Unit (`SmartSpare.Modules.Fleet.UnitTests`, new) | Odometer rules, every `ServiceOrder` transition (allowed and refused), kit-event application and ignoring, line rules, `EngineHours`, `UsageRate`, **every formula and status rule of section 2** with hand-computed cases (skipped thresholds, condition factor, no history, short history, calendar first, hours first, open order), event contracts, endpoint table, validators, `allowedActions`. |
| Integration (`SmartSpare.Host.IntegrationTests`) | Each endpoint: success, 400, 401, 403, 404, 409, 422 with codes; **calculator ↔ `fn_maintenance_due` parity** over the demo fleet; the kit flow end to end in the single host (draft → reserved → completed → issued; expiry → draft; cancel → released; shortfall); `PartCatalogChanged` written by the import in the same transaction and applied by Fleet once (redelivery is a no-op). |
| Database | T02 adjusted (5.6); the SQL suite and schema diff green with the allow-list entries of section 1. |
| Architecture | Fleet `Domain` free of framework types; no Fleet reference to Inventory except `Inventory.Contracts`; Contracts public, rest internal. |
| Topology | A Fleet endpoint through the gateway; `ServiceOrderDrafted` → `ServiceKitReserved` over RabbitMQ. |
| bUnit, Playwright | Section 6. |

**Coverage:** the stage 5 definition by layer (Domain + Features gated at 70 %; three figures reported). The due calculator and the `ServiceOrder` state machine are pure and fully unit-tested, which is where the module's business logic is. Each PR states its unit coverage; a drop of more than 2 points needs a reason.

---

## 8. Housekeeping issues in this stage

| Issue | What | PR |
|---|---|---|
| #30 | Outbox/inbox retention. Resolves the finding of stage 5 without DELETE grants to the application roles: each module's SQL gets `<schema>.purge_processed_messages(p_outbox_before, p_inbox_before, p_batch)`, `SECURITY DEFINER`, executable by `app_write` (functions as the write API, DATABASE_DESIGN 8.2); a Quartz job in the single host (daily; outbox 7 days, inbox 30 days, batches of 5,000) and `--job outbox-retention` for the services topology. | 6m |
| #82 | `RabbitMQ.Client.OpenTelemetry`: adopted if a stable version is out; otherwise moved to v0.10.0. | 6m |
| #17 | Lock-file refresh on Dependabot PRs: needs the owner's decision on a GitHub App or a fine-grained token (stage 5 finding). Asked in the stage report; without a decision it moves to v0.10.0. | — |

---

## 9. Pull requests

Each PR passes the full `tools/check.sh` and `tools/check.sh restore build e2e topology`, is merged with a rebase when every CI check is green, and gets a 3-line entry in the log comment of the stage issue. Closing keywords appear only in PR descriptions.

### 6a: Stage 6 plan
**Scope:** this document; the stage issue; #12 updated (5.6, F7); new issues of section 11. **Acceptance:** merged and linked from ARCHITECTURE_DECISIONS §13.

### 6b: Fleet domain and remap
**Scope:** section 1 (aggregates, value objects, errors, data classes in named files, `PersistenceModel.cs` deleted); `xmin` migration; `interval_months` migration with T001 values (engine service 12 months, transmission service 24 months); new `SmartSpare.Modules.Fleet.UnitTests`.
**Acceptance:** `check-model` clean; schema diff and SQL suite green; every transition and odometer rule unit-tested.

### 6c: Maintenance due calculator
**Scope:** section 2; `Infrastructure/Queries/MaintenanceDueInputs`; `docs/architecture/MAINTENANCE_PLANNING.md` with the formula and a worked example.
**Acceptance:** unit tests for every rule of section 2; the parity integration test over the demo fleet.

### 6d: Part snapshot and `PartCatalogChanged`
**Scope:** 5.2: `fleet.part_snapshot`, the Fleet consumer with inbox, Inventory writes `PartCatalogChanged` from the import, the `part-catalog-publish` job, T001 snapshot fill, RabbitMQ binding.
**Acceptance:** the #12 publication items: outbox row in the same transaction (integration test), idempotent consumer (redelivery test), topology binding present.

### 6e: Fleet read API
**Scope:** machines list and card, readings history, maintenance plans, the due list; `ReadFleet`; module-local `v_machine_maintenance_status` (X10); OpenAPI.
**Acceptance:** integration tests per endpoint; documentation test; the gateway route-table test covers `/api/v1/fleet/...`.
**Moved to 6g (26 November 2026):** the topology test's check that the gateway reaches `/api/v1/fleet/machines`. The topology test starts only the Identity and Inventory services today, so the check would need a Fleet service of its own in 6e; 6g has to add that service anyway for the `ServiceOrderDrafted` → `ServiceKitReserved` round trip over RabbitMQ. Doing both in 6g starts the service once, which also keeps the topology job's memory use down (local checks were stopped twice for low memory). Until then the gateway route-table test covers the Fleet routes.

### 6f: Engine-hour readings
**Scope:** 3.1 steps 1–3 and 5: `POST /machines/{id}/readings`; error mapping SS100–SS103. (Drafts still reserve through the transitional body until 6g.)
**Acceptance:** UC02 and A1 as integration tests (440 h → nothing; 312 h → 422 SS100; +460 h in a day → 422 SS101; 512 h → one draft of the 500 h service).
**As built (product owner's additions, 27 November 2026):** the handler locks the machine row (`SELECT … FOR UPDATE`) before its checks, so concurrent readings of one machine are handled one after the other and a race ends in a 422 with its code, never a 500 (database SS1xx errors map to the same 422 as the C# check). An identical reading posted again (same machine, time and hours) is a safe retry: 200 with the stored reading, no second row; a different value at the same time is 409 `Fleet.ReadingConflict`. A reading more than five minutes ahead of the server clock (`TimeProvider`) is the new code **SS104**, also mapped from `ck_engine_hours_reading_not_future`. The policy/SQL parity test now also requires that the API's database role may execute every fronted function. No brand or home-hub scope: engineers record readings for any machine they service (visits, telematics), and the home hub only decides where kits are reserved. No integration event is written (drafts publish from 6g).

### 6g: Service-kit reservation as events
**Split into four PRs (product owner, 27 November 2026),** each green and merged before the next: **6g1** SQL and Inventory (`qty_reserved`, `inventory.reserve_service_kit`, module-local `v_service_kit_shortage`, SQL and concurrency tests); **6g2** events (`ServiceOrderDrafted` → Inventory consumer → `ServiceKitReserved` → Fleet, outbox/inbox, redelivery test); **6g3** expiry (`ServiceKitReservationExpired`, expiry against consumption with `TimeProvider`); **6g4** topology (the Fleet service in the topology test and the gateway→Fleet check moved from 6e).
**As built, 6g2:** `fleet.create_service_order_draft` is module-local (the draft has no holds); the reading handler writes `ServiceOrderDrafted` per drafted order with the reading. Inventory's consumer `inventory.reserve-service-kit` calls `reserve_service_kit` (holds until now + 7 days by `TimeProvider`) and writes `ServiceKitReserved`; Fleet's consumer `fleet.apply-kit-reserved` stores `qty_reserved` and moves a fully held draft to `parts_reserved` (`ServiceOrder.ApplyKitReserved`; a late answer for a closed order is ignored). Both events carry the acting user (`IActedByUser`): the delivery pipeline sets `ActingUser` before the consumer's transaction, so holds and audit name the engineer (5.5). A redelivered `ServiceOrderDrafted` is skipped by the inbox, and a replay under a new message id finds the line already held (both tested). The draft response's `fullyReserved` is therefore always false; the order shows the held quantities once Inventory answered. T02 and T001 reserve the drafted kits as the consumers do.
**As built, 6g3:** the transitional procedure is replaced by the module-local function `inventory.expire_soft_reservations(p_now)`: it expires the soft holds overdue at the job's clock (`TimeProvider`, so tests move time instead of editing data), locks them first in `(part_id, warehouse_id)` order (the order completions and kit reservations lock in) and returns their lines. The job `inventory.reservation-expiry` writes `ServiceKitReservationExpired` in the same transaction (`--job` in the services topology; the single host gets its scheduler with #30). **Deviation from 5.1:** Inventory's holds know the service order line, not the order, so the event carries line ids (chunks of 500) instead of an order id; Fleet's consumer `fleet.apply-kit-expired` finds the open orders of those lines, sets their `qty_reserved` to 0 and returns a parts-reserved order to draft (`ServiceOrder.ApplyKitReservationExpired`). **Expiry against consumption:** both lock the hold row and the second re-checks its status, so a hold ends expired or consumed, never both, and `reserved_qty` drops once; an integration test runs one expiry against four completions at once at a clock where every hold is overdue.
**As built, 6g4:** the topology test starts the Fleet service on its own login (`smartspare_fleet`); its `IWarehouseQueries` client calls the in-memory Inventory service's `/internal/v1` endpoint with the caller's token. `FleetTopologyTests` proves the gateway → Fleet route (the check moved from 6e: the machine list through the gateway, with the hub code read from Inventory) and the kit round trip over RabbitMQ: a reading through the gateway drafts an order in Fleet, `ServiceOrderDrafted` reaches the Inventory service, which reserves the kit, and `ServiceKitReserved` returns the order to Fleet as `parts_reserved`. The gateway sign-in helper moved into the fixture.
**Scope:** 5.3, 5.5 (expiry, ordering, consumer's user), `qty_reserved`, module-local `create_service_order_draft` and `expire_soft_reservations`, `inventory.reserve_service_kit`, `v_service_kit_shortage` (X9); T02/T001 changes; topology round trip.
**Acceptance:** draft → `ServiceKitReserved` → `parts_reserved` and partial kits in the single host and over RabbitMQ (the topology test starts the Fleet service and reaches `/api/v1/fleet/machines` through the gateway); expiry → draft; T02 kit assertions replaced by C# tests.

### 6h: Service order API
**Scope:** list, detail with `allowedActions`, create (from plan, unplanned repair), start, complete (5.4, F7), cancel (5.5); module-local `complete_service_order` / `cancel_service_order`; `inventory.issue_service_kit`. After 6h no Fleet or Inventory object writes across modules.
**Acceptance:** the role × action matrix (engineer, brand manager, administrator, planner, keeper, sponsor); completion with full and short kits; cancellation releases holds; MODULE_MAPPING X5–X10 marked done. **Closes #12** (with 6d and 6g).

### 6i: Kit line editing
**Scope:** `PUT /service-orders/{id}/lines`, `ServiceOrderLinesChanged` and its consumer. **Acceptance:** added, changed and removed lines adjust the holds. First cut (section 10).

### 6j: Machine screens
**Scope:** machines list, machine card, engine-hours dialog; navigation entries; tablet layout.
**Acceptance:** bUnit states, validation and tablet rendering; manual check in the single host with the demo dataset.

### 6k: Maintenance and service-order screens
**Scope:** due-maintenance list, service orders list, detail with actions, new-order form.
**Acceptance:** bUnit for actions, kit status and the form.

### 6l: End-to-end scenario
**Scope:** the Playwright scenario of section 6, desktop and tablet. **Acceptance:** green in CI.

### 6m: Housekeeping
**Scope:** #30 and #82 (section 8). **Acceptance:** retention job tested per schema; #82 adopted or moved.

### 6n: Documentation and release
**Scope:** ARCHITECTURE_DECISIONS (§3, §13), API_DOCUMENTATION, FRONTEND, BACKEND_REQUIREMENTS, MODULE_MAPPING (Fleet status, X5–X10), DATABASE_DESIGN (6.3, 6.4), CHANGELOG; exit-criteria evidence (section 10.1); tag `v0.6.0`, release, close the milestone.

**Order and dates:** 26–27 Nov 6a–6c; 28 Nov 6d–6e; 29 Nov 6f–6g; 30 Nov 6h–6i; 1 Dec 6j–6k; 2 Dec 6l–6m; 3–4 Dec 6n and buffer. The demo for 2 December needs 6b–6h and 6j; 6k–6l follow the same day.

---

## 10. Exit criteria for v0.6.0 and the timebox

1. The Fleet aggregates replace their persistence classes; `check-model` clean; schema diff (with the reviewed allow-list entries) and the SQL suite green on the EF-built database.
2. An engineer records engine hours through the API and the tablet screens; implausible, decreasing and out-of-order readings are refused with readable messages (SS100–SS103); a reading that crosses a threshold drafts the service order.
3. The due calculation of section 2 is a unit-tested domain service, documented in MAINTENANCE_PLANNING.md, and agrees with `fleet.fn_maintenance_due` on the whole demo fleet; the due list shows hours- and calendar-due machines with projected dates.
4. The service-kit flow runs as integration events in both topologies: draft → reserved, completed → issued (shortfall reported), cancelled → released, expired → draft; no Fleet or Inventory object writes another module's schema; both Fleet views are module-local.
5. `PartCatalogChanged` is written by Inventory in the same transaction as the catalogue change and applied by Fleet idempotently (#12 acceptance).
6. Every Fleet endpoint is documented with summaries, descriptions and examples; `RecordServiceWork` / `ManageServiceOrders` keep their SQL parity; `ReadFleet` is in place.
7. Screens are mobile-first, show loading and error states with the Reference, and are covered by bUnit; the Playwright scenario passes in CI on desktop and tablet.
8. Unit coverage of business logic at or above the 70 % gate, with all three figures reported.
9. #12 closed; #30, #82, #17 closed or explicitly moved by the timebox.

**Known limitation (27 November 2026).** The expiry of service-kit holds runs only in the services topology, as `--job inventory.reservation-expiry` (a Container Apps cron job from stage 10): the single host has no scheduler until #30 adds Quartz. In the single host a soft hold therefore stays until the order is completed or cancelled. #30 is in milestone v0.6.0 (PR 6m); if 6m is cut by the timebox, #30 and this limitation move with it to v0.10.0. The job acts as the named system user (ADR 0024).

**Cut order if the stage runs late** (each cut becomes an issue in a later milestone; the later stages are not moved):
1. 6i kit line editing (and unplanned repair orders in 6h) → v0.11.0.
2. 6m housekeeping (#30, #82) → v0.10.0.
3. The readings-history page and the maintenance-plans endpoint → v0.11.0.
4. Calendar intervals (F3): the due list stays hours-based; the column and the formula section remain → v0.11.0.

Never cut: the domain, the due calculator with its documentation, the reading → draft → reserve → complete → issue flow with its events, the part snapshot, the machine card and service-order screens, and the E2E scenario — they are the core of the diploma topic and the demo.

### 10.1 Evidence

Filled in by 6n.

---

## 11. Stage issue and new issues

- **Stage issue:** #118 — the plan, the PR list and the running log comment.
- **#12** updated: the four functions keep their names and role lists without cross-module statements (5.6); Q2 decided as F7.
- **New: #119** seasonal usage profiles per machine type for the projected due date (2.2) — a refinement of the formula, `enhancement`, `future`.
- **Stage 8 (#14), extended:** `Fleet.Contracts.IFleetQueries.GetOpenMaintenanceDemand` for the recommendation engine (MODULE_MAPPING X15) comes with its first consumer in stage 8; recorded on #14.
