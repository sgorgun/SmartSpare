# SmartSpare — Stage 7 Plan: Master Data, Employee-Derived Authorization and Procurement

Status: **approved** (27 November 2026, with the review decisions and corrections of section 14; proposed the same day). Stage 7 of the work plan ([ARCHITECTURE_DECISIONS.md, section 13](../architecture/ARCHITECTURE_DECISIONS.md#13-work-plan)), tracked in #153. Release target: **v0.7.0**.

Stage 7 turns SmartSpare from a set of workflows over seeded data into a system a dealer can run:
- **people and rights from one source**: employees and positions are the only place where "who is who" is edited; user accounts are created from employees, and every permission is derived (section 1);
- **one master-data mechanism** for every catalogue: code and name, trigram search, paging, If-Match, archive instead of delete, change history, CSV template, import with a dry run, and export (section 2);
- **every catalogue** a dealer needs, each in its owning module, with the documents picking only active entries (sections 3 and 4);
- **the Procurement core**: purchase orders with the four-eyes rule and goods receipt reaching Inventory as an event, which removes the last transitional objects of Procurement (#13, section 5).

**Product owner's decisions, 27 November 2026** (final stage 7 scope, superseding every earlier stage 7 instruction):
- **Scope:** master data + employee-derived authorization + procurement. No catalogue is cut. The conventions follow mature inventory and ERP systems (SAP, Odoo, 1C): codes as business keys, archive instead of delete, validity-dated prices, a primary vendor per part, interchangeability and supersession of parts, reason codes on adjustments.
- **Schedule** (replaces the windows of 27 November): stage 7 **28 November – 6 December**; stage 8 (ML) 7–11 December; stage 9 (Reporting, minimal) 12 December; stage 10 (Azure, first deployment with the cron jobs) 13–14 December; **code freeze 15 December**; thesis and presentation outside the repository 15–23 December; reserve 24 December – 2 January; pre-defence 3 January 2027.
- **Order of work:** authorization rework → generic mechanism → catalogues → document integration → procurement → documentation.
- **Demo-ready `main`:** the expert reviews run 2–10 December. Every PR leaves `main` working and demonstrable; the authorization rework is behind complete tests and a migration.
- **Autonomous delivery** as in stages 4–6 once this plan is approved: implement per plan, CI as the full gate (locally only the changed steps, one at a time), merge on green with a rebase, log on #153; stop only on plan contradictions, decisions not covered here or by an ADR, money or accounts, or date risk.

Inputs, as on `main` (v0.6.0 + the schema-diff gate):
- [MODULE_MAPPING.md](../database/MODULE_MAPPING.md) sections 1.2, 1.3, 1.5, 2, 3.1 (X12–X14), 3.5, 3.6, 4.2;
- [DATABASE_DESIGN.md](../database/DATABASE_DESIGN.md) sections 4.1, 5, 6.3 (procurement functions, US12–US16, WF04), 8;
- [STAGE_3_PLAN.md](../stage-3/STAGE_3_PLAN.md) sections 3 and 9 (policies, brand scope, the parity rule), ADRs 0014 and 0016 (sign-in, claims, internal token), ADR 0024 (acting user of events);
- [STAGE_5_PLAN.md](../stage-5/STAGE_5_PLAN.md) (domain-model pattern, the SQL function as the invariant guard, `DatabaseErrorMapper`, the event publication rule, coverage by layer) and [STAGE_6_PLAN.md](../stage-6/STAGE_6_PLAN.md) (this document's format, the part snapshot, the kit flow);
- the existing CSV import (`Features/Imports`, `CsvImportFile`, `staging.process_batch` with the 5 % gate) and the stock-levels screen with `If-Match` on `xmin` (`StockCommands`, `Versions`, `StockPositionPage`), both reused;
- #152 (SQL resources of applied migrations must never change; the 42P07 on `v_service_kit_shortage`).

**What exists today (facts the plan builds on).** No employee or position table exists. `iam.app_user` carries name, e-mail, phone and one `home_warehouse_id`; roles (`iam.user_role`) and brand scope (`iam.user_brand`) are edited by hand on `/admin/users` (`PUT /users/{id}/access`). Claims: `role`, `ss:brand`, `ss:warehouse` (one value); the BFF re-validates the session every 5 minutes. Brands are Platform reference data (`ref.brand`, no archive, no audit); manufacturers, adjustment reasons, service works and responsible-employee assignments do not exist. `catalog.part_analogue` has no type or direction. Parts are written only by the CSV import; warehouses, suppliers, customers and machines have no write API. Procurement is still the stage 2 table port (`PersistenceModel.cs`, only `/ping`); `procurement.receive_purchase_order_line` writes `inventory.stock_movement` (X12), and the in-transit objects read `procurement` (X13, X14). Nothing reads the audit trail through the API.

---

## 0. Summary of decisions

| # | Decision | Section |
|---|---|---|
| A1 | **Organisation model in Identity:** `iam.position` with a role template (`iam.position_role`), `iam.employee` (the person: number, name, contacts, position, hire and leave dates), and `iam.app_user.employee_id` (`NOT NULL`, unique). An account is created from an employee and never exists without one; an employee needs no account. Name, e-mail and phone move from the account to the employee. | 1.1 |
| A2 | **Rights are derived, never edited:** roles = the position's role template; brand approval scope = "brand manager" assignments on brands; hub access = "keeper" or "service base" assignments on warehouses. One pure domain service (`EffectiveRightsCalculator`) computes them with their **origin**; the claims, the admin screen and the SQL session context all come from it. `PUT /users/{id}/access`, `iam.user_role`, `iam.user_brand` and `app_user.home_warehouse_id` are removed after a shadow period. | 1.2 |
| A3 | **Responsible-employee assignments live with the record they are about** (one table per record kind, in the owning module, with a real FK to the record) and follow one shared pattern. The rights-bearing ones (brand manager, warehouse keeper, service base) reach Identity as the event `ResponsibilitiesChanged`; Identity keeps a read-only projection and derives from it. Granting or revoking a rights-bearing assignment requires the `Administration` policy, whichever module owns the record. | 1.3, 3.7 |
| A4 | **Immediate effect:** every change that alters a user's effective rights (position, role template, assignment, deactivation) rotates the security stamp; the BFF checks the session every **30 seconds** (was 5 minutes), and sign-in checks the employee's status directly. A deactivated employee cannot sign in from the moment the change commits. | 1.4 |
| A5 | **Hub access becomes a list:** `ss:warehouse` may repeat, the session GUC `app.warehouse_ids` replaces `app.home_warehouse_id`, and `util.require_hub(warehouse)` (SS302) replaces the inline checks. Hub access guards **writes only**: reads stay unscoped, as decided in stage 3 (planners and brand managers see stock across every hub and brand). For writes, only `ADMINISTRATOR` is unrestricted; a keeper without a hub assignment has no hub access (least privilege; today a missing home hub meant "unrestricted"). | 1.2, 1.6 |
| M1 | **One master-data mechanism** in BuildingBlocks, ApiContracts and the web client: record base (code, name, archive, `xmin`), Fluent configuration helper (unique code, trigram indexes, audit trigger), an endpoint builder (list with search, filters and paging; detail with `ETag`; create; update and archive/restore with `If-Match`; history; CSV), archive guards, and shared MudBlazor components. New ADR 0025. | 2 |
| M2 | **Archive instead of delete** everywhere: archived records stay referenced by history, cannot be picked by new documents (SS500), and can be restored. Archiving runs the catalogue's guard (e.g. a warehouse with stock or open documents) and is refused with the reasons (SS501). | 2.4 |
| M3 | **Change history** from the existing audit trail: `audit.record_history(table, record_id)` (`SECURITY DEFINER`, personal-data columns already excluded by the trigger) behind `GET /{catalog}/{id}/history`; every catalogue table gets `trg_audit`. | 2.5 |
| M4 | **CSV for every catalogue:** template, export, and import with a **dry run that runs the real import in a transaction and rolls it back**, so the preview and the result can never differ; duplicates in the file are errors, existing codes are updated (upsert by code), unchanged rows are not written, and the existing **5 % error gate** applies. Parts keep their bulk SQL pipeline (`staging.process_batch`) behind the same API shape. | 2.6 |
| C1 | **Brands move from Platform to Inventory** (`ref.brand` → `catalog.brand` with `ALTER TABLE … SET SCHEMA`, ids kept) and become editable; Fleet drops its FK and keeps a brand snapshot fed by `BrandCatalogChanged`. Amends ADR 0011 D3 for this one table (recorded in ADR 0025). | 3.1 |
| C2 | **Part analogues** become typed links: `INTERCHANGEABLE` (symmetric, stored once) or `SUPERSEDES` (directed, new part → old part); no self-links, one link per pair, at most one successor per part, no cycles (trigger, serialised by an advisory lock). Interchangeable groups and the current successor are views for stage 8. New ADR 0027. | 3.3 |
| C3 | **Service works** (operation code, name, norm hours, applicable brand or model, standard kit) in Fleet. Maintenance plans reference works; the plan kit `fleet.maintenance_plan_part` becomes a **view with the same name and columns** over the works' kits, so the drafting function and the kit events stay unchanged. The changes to Fleet flows are listed exactly in 4.3. | 3.5, 4.3 |
| P1 | **Purchase orders** as a domain aggregate over the existing tables and SQL guards: create, lines from the part-supplier records (price valid on the order date, MOQ, pack size, currency), submit, approve, return to draft, export, cancel; **release by threshold**: approval is required above a configurable threshold (a database setting, default €5,000), and whenever it is required the author can never approve their own order (section 14, Q1); brand scope for brand managers. | 5.1 |
| P2 | **Goods receipt as a document** in Procurement (`GR-YYYY-NNNNNN`: supplier, warehouse, delivery note, lines against open PO lines). Posting updates the PO and publishes `GoodsReceiptPosted`; Inventory posts `RECEIPT_PO` movements. `PurchaseOrderApproved` / `PurchaseOrderCancelled` keep `inventory.expected_receipt`; the in-transit objects and the PO overview become module-local. Closes #13 (X12–X14). | 5.2, 5.3 |
| Q1 | **Quality:** employee anonymisation (GDPR), grants and SQL tests for every new table, OpenAPI with ProblemDetails examples and codes, mobile-first accessible screens, "Load demo data" fills every catalogue, E2E at three viewports, coverage by layer without mocks. | 7 |
| S1 | **Stage 8 input:** analogue groups pool demand across interchangeable parts and map superseded history onto the current part; lead times from the primary part-supplier give the reorder point. Recorded in section 8 and exposed through `ISupplierQueries` and the analogue views. | 8 |

---

## 1. People and rights (Identity)

### 1.1 Model

| Table | Columns (new unless noted) | Rules |
|---|---|---|
| `iam.position` | `id smallint`, `code` (`^[A-Z0-9_]{2,30}$`, unique), `name`, `description`, master-data columns (2.1) | Catalogue. Archiving is blocked while an active employee holds the position. |
| `iam.position_role` | `position_id` FK, `role_id` FK → `iam.role`; PK both | The role template. The six roles stay seeded and fixed (`iam.role`): a role is a code-defined set of permissions, not data. |
| `iam.employee` | `id uuid`, `code` = employee number (`^E[0-9]{4,6}$`, unique), `full_name`, `email` (citext, unique, nullable), `phone`, `position_id` FK (not null), `hired_on`, `left_on`, `anonymised_at`, master-data columns | The only place where a person's data and position are edited. Archived = left the company (sets `left_on`). Personal data: masked in `iam.v_employee_masked` for callers without access, excluded from the audit values. |
| `iam.app_user` (changed) | `employee_id uuid NOT NULL UNIQUE` FK → `iam.employee`; `full_name`, `email`, `phone` dropped (7e) | Login data only: user name, password hash, security stamp, lockout, last sign-in, an account-level `is_active` (an administrator may lock an account without changing the employee). |
| `iam.employee_responsibility` | `record_kind` (`brand` \| `warehouse`), `record_id`, `employee_id` FK, `assignment_role`, `source_occurred_at`; PK the first four | **Read-only projection** of the rights-bearing assignments (A3), written only by the `ResponsibilitiesChanged` consumer. |
| `iam.v_effective_right` | view: `user_id`, `kind` (`role` \| `brand` \| `warehouse`), `value`, `origin_kind`, `origin_ref` | What the claims factory and the admin screen read. |

`ref.assignment_role` (Platform, seeded, read-only; FKs to it are allowed by MODULE_MAPPING D3) lists the assignment roles per record kind and whether they grant scope:

| Record kind | Assignment roles (grants scope in bold) |
|---|---|
| brand | **BRAND_MANAGER**, PRODUCT_SPECIALIST |
| warehouse | **KEEPER**, **SERVICE_BASE**, HUB_MANAGER |
| manufacturer | ACCOUNT_MANAGER |
| part | PRODUCT_SPECIALIST, BUYER |
| supplier | BUYER, ACCOUNT_MANAGER |

### 1.2 Derivation rules

`Domain/EffectiveRightsCalculator` (pure; inputs: employee status, position template, projection rows; output: rights with origin):

| Right | Derived from | Origin shown on the admin screen |
|---|---|---|
| Role R | R in the role template of the employee's position | "Role BRAND_MANAGER — from position *Brand manager*" |
| Brand scope B | a BRAND_MANAGER assignment on brand B, **and** the employee holds the BRAND_MANAGER role | "Brand CLAAS — from assignment *Brand manager of CLAAS*"; an assignment without the role is listed as "no effect: the position has no BRAND_MANAGER role" |
| Hub access W | a KEEPER or SERVICE_BASE assignment on warehouse W | "Hub KLP — from assignment *Keeper of KLP*" |
| Nothing | employee archived or anonymised, or account locked | "Account inactive: employee left on 2026-11-30" |

**Claims** keep their types (`role`, `ss:brand`, `ss:warehouse`); `ss:warehouse` may now repeat. The internal token forwards the same claims (ADR 0016 table amended). `/bff/me` returns `HomeWarehouseIds`. **Session context:** `app.warehouse_ids` (sorted, comma-joined); `util.current_warehouse_ids()`; `util.require_hub(p_warehouse)` raises SS302 unless the session has `ADMINISTRATOR` or the warehouse is in the list. `inventory.dispatch_transfer`, `receive_transfer`, the stock adjustment and the new goods-receipt function use it (new migrations, `CREATE OR REPLACE`). The C# side has one `HubAccess` check with the same rule; the parity test covers it. **Hub access guards writes only:** stock movements, adjustments, dispatch and receipt of transfers, and goods receipt. Every read (stock, movements, transfers, service orders, purchase orders, catalogues) stays unscoped across hubs and brands, as decided in stage 3: a planner sees the whole network, and a brand manager sees every brand but approves only their own. `util.require_hub` is called only from write functions; an architecture test fails if a query handler or a read view calls it, and the golden matrix checks the reads.

**Safety rules:** the last active employee whose position grants ADMINISTRATOR cannot be archived or moved to another position, and ADMINISTRATOR cannot be removed from the last such template (409 `Identity.LastAdministrator`); an administrator cannot archive themselves (kept from stage 3).

### 1.3 Assignments reach Identity

- Assignments are edited on the record's page in the owning module (section 3.7). When a rights-bearing assignment of a brand or warehouse changes, Inventory writes **`ResponsibilitiesChanged`** (record kind, record id, the full set of rights-bearing assignments of that record, acting user) to its outbox in the same transaction.
- Identity's consumer `identity.apply-responsibilities` replaces the projection rows of that record when the event is newer than the stored `source_occurred_at` (state transfer: idempotent and order-safe), and rotates the stamps of every user whose rights changed (old ∪ new employees).
- `inventory.responsibility-publish` (a `--job`, like `part-catalog-publish`) republishes every record's set: initial fill and repair after an incident.
- Why not keep the assignments in Identity: "keeper of KLP" is a fact about the warehouse as much as about the person, and the warehouse page is where a dealer edits it (Odoo and SAP keep responsible persons on the object). Keeping them with the record gives one editable source per fact, real FKs to the record and one pattern for all five kinds; the cost is event latency on rights changes (milliseconds over RabbitMQ, one dispatcher cycle in the single host), which is below the 30-second session check and is tested.

### 1.4 Refresh and revocation

| Change | Effect |
|---|---|
| Employee's position changed, template changed, assignment added or removed | Stamp rotated in the same transaction (template: every holder); the next session check (≤ 30 s) rebuilds the claims or ends the session. |
| Employee archived (left) or anonymised | Stamp rotated; sign-in refused at once (`ActiveUserConfirmation` checks the employee); open sessions end at the next check. |
| Account locked by an administrator | As today. |

`BffDefaults.SessionCheckInterval` becomes the setting `Bff:SessionCheckInterval` (default 30 s; one indexed query per active session per interval, through `ValidateSessionAsync`). Tests move a `FakeTimeProvider`.

**Deliberate trade-off (recorded in ADR 0026):** sign-in for a deactivated employee is blocked immediately, but an open session keeps its old rights for at most 30 seconds (plus event delivery for assignment changes). Checking on every request would add a database or HTTP call to every proxied request; 30 seconds is the accepted window (product owner, 27 November 2026).

### 1.5 Admin screens and API

- `/admin/positions`: catalogue (section 2) with the role-template editor; `/admin/employees` and `/admin/employees/{id}`: catalogue, account panel (create account: user name and an initial password the user must change; lock, unlock), the employee's rights-bearing assignments (read from the projection, with links to the records).
- `/admin/users`: accounts with their **effective rights and origin**, read-only. No role or brand checkbox remains anywhere.
- API under `/api/v1/identity`: `positions`, `employees` (generic catalogue endpoints), `POST employees/{id}/account`, `GET users` (effective rights), `POST users/{id}/lock|unlock`, `GET employees/{id}/rights`. `PUT users/{id}/access` and `POST users` are removed. `Identity.Contracts.IEmployeeDirectory` (exists, active, display name; masked by the caller's rights) lets other modules validate and display employees (HTTP to `/internal/v1/identity/employees` in the services topology).

### 1.6 Migration of the existing data

In three steps so that nothing is destructive until the derived rights are proven equal:

1. **7c (Identity migration, module-local):** one employee per existing user (name, e-mail, phone copied; number `E0001`…); one position per distinct role set (BRAND_MANAGER → *Brand manager*; BRAND_MANAGER + SPONSOR → *Commercial director*; …), template = that set; `employee_id` filled and made `NOT NULL`. Roles are now derived; `iam.user_role` stays as a read-only shadow.
2. **7d (Reporting migration, one-time copy):** `iam.user_brand` → `catalog.brand_responsible` (BRAND_MANAGER) and `app_user.home_warehouse_id` → `inventory.warehouse_responsible` (KEEPER for keepers, SERVICE_BASE for others), then the projection is filled. The Reporting context is the one context allowed to read every schema and already carries the transitional cross-module script; this copy is the only cross-module data step of the stage, runs once before any production database exists (stage 10) and is recorded in ADR 0026 as the only exception, with the reason it goes through Reporting: the migrator applies Reporting last, after Identity and Inventory have created both sides, and Reporting is the one context already allowed to read every schema, so no module migration has to read another module's schema. The copy is **idempotent** (`INSERT … ON CONFLICT DO NOTHING`, skipped when the legacy objects are gone) and covered by `AccessMigrationTests`, which also runs it twice.
3. **7e (Identity migration):** after the drift view `iam.v_access_drift` (legacy vs derived rights) is empty on T001 in CI, `iam.user_role`, `iam.user_brand`, `app_user.home_warehouse_id` and the moved personal-data columns are dropped. The migration refuses to run if legacy rows were never copied (a local database that skips 7d must be reset, README).

**Superseded, marked in place (not rewritten):** STAGE_3_PLAN 3.x on role and brand administration; ADR 0016's claim table (`ss:warehouse` single); MODULE_MAPPING 2.2 (`iam.app_user.home_warehouse_id`) and 3.6 (`app.home_warehouse_id`); DATABASE_DESIGN 8.3 and 8.5 (employee data instead of user data). ADR 0026 lists each.

### 1.7 Tests

| What | Tests |
|---|---|
| Rights derivation | `EffectiveRightsCalculatorTests` (unit): every rule and origin of 1.2, brand assignment without the role, archived employee, empty template, several hubs. |
| Golden access matrix | `AccessMatrixTests` (integration, written first in 7c from `main`): for each T001 user, roles, brands, hubs and the allow/deny result of every policy, of the SS302 hub check for writes, and of the reads across every hub and brand (which must stay allowed for every role that has them today); asserted unchanged after 7c, 7d and 7e, except the listed intended difference (keeper without a hub). |
| Revocation | position change, template change, assignment removed (through the event), employee archived: the session is rebuilt or ended at the next check; sign-in refused at once; `FakeTimeProvider`. |
| Policy/SQL parity | `PolicySqlPermissionParityTests` extended with `util.require_hub` and the new functions; `AppPoliciesTests` for the new policies. |
| No direct editing path | Architecture test: no endpoint in any module writes `position_role`, the projection or account rights except the listed handlers; reflection test: no route under `/identity/users` accepts roles, brands or warehouses; SQL test (T03): `app_write` and `app_admin` cannot write `iam.employee_responsibility` except through the consumer's function; the removed endpoint returns 404. |
| Migration | `AccessMigrationTests`: a database built to the last stage 6 migration with T001 loaded, migrated to the end of 7e, gives the same golden matrix. |
| Events | `ResponsibilitiesChanged` contract test; redelivery and out-of-order delivery are no-ops; topology test: an assignment change in the Inventory service reaches the Identity service over RabbitMQ. |

### 1.8 Permissions, roles as data and guardrails (ADR 0028)

Added at the product owner's second checkpoint (27 November 2026). It follows the model of ASP.NET Core permission-based authorization, ABP and Odoo: permissions are fixed in code, roles are data, and data scope stays as it is. PRs 7x1–7x4, placed right after 7f2. That is before the first procurement catalogue (7k), as asked, and early enough that 7g–7j are built on permissions directly instead of being rewritten.

**Layer 1: permissions in code (the only fixed part).**
- `SmartSpare.ApiContracts.Authorization.Permissions`: a stable catalogue named `<Module>.<Resource>.<Action>`.
  - Actions: `View`, `Create`, `Edit`, `Archive`, plus special ones (`Approve`, `Cancel`, `Dispatch`, `Receive`, `Adjust`, `Import`, `Export`, `ManageAssignments`, `Decide`).
  - Every catalogue and document gets its own set; `ManageCatalog` and the other role policies of section 6 are replaced.
  - Each permission has a module, a group and a description for the UI.
- `iam.permission` mirrors the catalogue (seeded by migration; a test fails if code and table differ). Adding a permission means adding a constant and a seed row in a new migration.
- **API:** `.RequirePermission(Permissions.Inventory.Transfer.Approve)` through a `PermissionPolicyProvider`, which builds the policy `perm:<name>` on demand. No endpoint names a role.
- **SQL:** `util.require_permission('<name>')` reads `app.user_permissions`, which the `SessionContextInterceptor` sets beside `app.user_id`. It raises SS301 like `util.require_role`, which it replaces in all 12 scripts (about 25 calls), by new scripts that redefine the functions. Applied scripts stay untouched (#152).
- **Parity:** `PolicySqlPermissionParityTests` checks that every endpoint fronting a SQL function requires exactly the permission the function checks.

**Layer 2: roles are data.**
- `iam.role` gains `name`, `description`, `is_locked`, archiving and `xmin`; `iam.role_permission (role_id, permission)` holds the bundle.
- Positions keep their role template (`iam.position_role`, one or more roles). Effective permissions are the union of the roles of the employee's position: additive only, no deny rules.
- Administrators edit roles in the UI.
- **Seeded default roles reproduce today's rights exactly.** The six current roles keep their codes, and each gets the permissions of the policies section 6 gives it today.
- **Golden matrix:** the endpoint probes, roles, brands and hubs stay identical. The policy rows become permission rows (policies no longer exist); this is the one intended difference, listed in the test.
- **Transport:**
  - The cookie keeps id, name, stamp and role codes. The BFF adds the permissions per request from a server-side role → permissions map, cached and refreshed at the 30-second session check. Permissions never go into the cookie.
  - The 2-minute internal token to the services carries the permissions; a test measures its size against a budget of 8 KB (the platform limits are far above that). The size goes into ADR 0028.
  - The web client gets its permissions from `/bff/user`.

**Layer 3: data scope stays.** Brand and hub assignments limit writes (ADR 0026), unchanged.

**Guardrails.**
- **Separation of duties:** a short declarative list in code (`DutyConflicts`) of permission pairs no role and no position (the union of its roles) may hold together.
  - First pair: `Procurement.PurchaseOrder.Create` + `Procurement.Supplier.Edit`.
  - Saving a role or a position template that breaks a pair gives 409 `Organisation.DutyConflict` with the pair.
  - Self-approval stays forbidden (four-eyes, 5.1).
- **No self-granting:** this is the second rule asked for ("`Organisation.Role.Edit` + being able to grant yourself rights"). It is a rule rather than a pair, because rights flow from position to role. A user may not:
  - change a role they hold;
  - change the template of their own position;
  - change their own employee's position.

  Each refusal is 403 SS301. Interpretation to confirm, see 14.2.
- **Locks:**
  - the built-in Administrator role is locked: it cannot be edited, archived or deleted;
  - the last active administrator keeps the role (ADR 0026, kept);
  - nobody changes their own roles or position (above).
- **Audit and refresh:** every change to a role, its permissions or a position template is audited by the trigger. It rotates the security stamp of every account that holds the role or position, so sessions take the new rights within 30 seconds.
- **Effective rights screen:** each permission is shown with its origin, permission ← role ← position; scope ← assignment.

**Mapping: old policy → new permissions (PR 7x1).** The default roles hold exactly these. The golden matrix kept its endpoint probes, roles, brands and hubs identical; only its rows changed, from policies to permissions.

| Old policy | Roles | New permission(s) |
|---|---|---|
| `Administration` | ADMINISTRATOR | `Organisation.Employee.View/Create/Edit/Archive/Anonymise`, `Organisation.Position.View/Create/Edit/Archive`, `Organisation.Account.View/Create/Edit`, `Organisation.Role.View/Edit`, `Inventory.Brand.ManageAssignments`, `Inventory.Warehouse.ManageAssignments` |
| `PlanInventory` | BRAND_MANAGER, INVENTORY_PLANNER | `Inventory.Stock.EditLevels`, `Inventory.Demand.Flag`, `Inventory.Transfer.Create`, `Procurement.PurchaseOrder.Create` |
| `ApproveTransfer` | BRAND_MANAGER, INVENTORY_PLANNER | `Inventory.Transfer.Approve` |
| `CancelTransfer` | BRAND_MANAGER, INVENTORY_PLANNER, ADMINISTRATOR | `Inventory.Transfer.Cancel` |
| `MoveStock` | WAREHOUSE_KEEPER, ADMINISTRATOR | `Inventory.Stock.Adjust`, `Inventory.Transfer.Dispatch`, `Inventory.Transfer.Receive`, `Procurement.PurchaseOrder.Receive` |
| `RecordServiceWork` | SERVICE_ENGINEER, ADMINISTRATOR | `Fleet.EngineHours.Record`, `Fleet.ServiceOrder.Start`, `Fleet.ServiceOrder.Complete` |
| `ManageServiceOrders` | SERVICE_ENGINEER, BRAND_MANAGER, ADMINISTRATOR | `Fleet.ServiceOrder.Create`, `Fleet.ServiceOrder.Edit`, `Fleet.ServiceOrder.Cancel` |
| `ApprovePurchaseOrder` | BRAND_MANAGER, SPONSOR | `Procurement.PurchaseOrder.Approve` |
| `CancelPurchaseOrder` | BRAND_MANAGER, SPONSOR, ADMINISTRATOR | `Procurement.PurchaseOrder.Cancel` |
| `DecideRecommendation` | BRAND_MANAGER, INVENTORY_PLANNER | `Forecasting.Recommendation.Decide` |
| `ReadAudit` | SPONSOR, ADMINISTRATOR | `Audit.Log.View` |
| `ImportData` | ADMINISTRATOR, INVENTORY_PLANNER | `Inventory.Import.View`, `Inventory.Import.Run` |
| `ManageCatalog` | INVENTORY_PLANNER, ADMINISTRATOR | `Inventory.AdjustmentReason.Create/Edit/Archive` |
| `ReadInventory` | all six | `Inventory.Part.View`, `Inventory.Brand.View`, `Inventory.Warehouse.View`, `Inventory.Stock.View`, `Inventory.Transfer.View`, `Inventory.AdjustmentReason.View` |
| `ReadFleet` | all six | `Fleet.Machine.View`, `Fleet.ServiceOrder.View` |
| *(hub rule: ADMINISTRATOR unrestricted)* | ADMINISTRATOR | `Organisation.Scope.AllHubs` |
| *(brand scope: unscoped roles per action)* | INVENTORY_PLANNER (approve and cancel transfer, decide), ADMINISTRATOR (cancel transfer and purchase order), SPONSOR (approve and cancel purchase order) | `Inventory.Transfer.ApproveAnyBrand`, `Inventory.Transfer.CancelAnyBrand`, `Forecasting.Recommendation.DecideAnyBrand`, `Procurement.PurchaseOrder.ApproveAnyBrand`, `Procurement.PurchaseOrder.CancelAnyBrand` |

**Not in scope (deliberately; listed in ADR 0028 as considered and rejected):** role inheritance, field-level permissions, deny rules, time-limited grants, external policy engines (OPA, Cedar).

---

## 2. Generic master-data mechanism (ADR 0025)

### 2.1 Record shape and database conventions

Every catalogue row has `code` (business key, format per catalogue, unique; immutable after creation except by an administrator, because codes are what CSV files and other systems use), `name`, `is_archived`, `archived_at`, `archived_by`, `created_at`, `updated_at`, `xmin` as the concurrency token, and `trg_audit`. Existing columns are reused where they exist (`is_active` becomes `NOT is_archived` through a rename migration, so there is one meaning). `MasterDataConfiguration<T>` (Fluent helper) declares the unique code, the trigram GIN indexes on `code` and `name` (and `oem_part_number` for parts) and the archive columns. Real FKs inside a module everywhere; one table per catalogue and per assignment kind; no polymorphic tables.

### 2.2 API shape (identical for every catalogue)

| Method and path | Purpose |
|---|---|
| `GET /{catalog}?search&archived=false&<filters>&sort&page&pageSize` | Search by code, name (and OEM number for parts) with `ILIKE` on the trigram indexes; filters per catalogue; `PagedResponse<T>`. |
| `GET /{catalog}/{id}` | Detail with `ETag: "<xmin>"`. |
| `GET /{catalog}/lookup?search&limit=20` | Active entries only, for autocomplete (code, name, a short description). |
| `POST /{catalog}` | Create; `201 Created` with `Location`. |
| `PUT /{catalog}/{id}` | Update with `If-Match` (missing: 400 `Catalog.VersionRequired`; stale: 409 `Database.ConcurrencyConflict`). |
| `POST /{catalog}/{id}/archive`, `/restore` | With `If-Match`; archive runs the guard (2.4). |
| `GET /{catalog}/{id}/history?page` | Change history (2.5). |
| `GET /{catalog}/csv/template`, `GET /{catalog}/csv?<filters>` | Template (header + one commented example row) and export (UTF-8 with BOM, `;` for spreadsheet compatibility, archived rows only on request). |
| `POST /{catalog}/csv?dryRun=true` | Import (2.6). |
| `GET/PUT /{catalog}/{id}/responsibles` | Only for the five kinds of 3.7. |

`MapCatalog<TRecord, TDto, TWrite>(group, CatalogDescriptor)` builds these endpoints with OpenAPI summaries, descriptions, parameter descriptions, response types and examples (ADR 0019 rule); a catalogue supplies its query, its validator, its archive guard and its CSV column map. The `Versions` helper of the stock-levels screen moves to BuildingBlocks.

### 2.3 Write path

Commands go through the catalogue's aggregate and EF (ADR 0013) with the transaction decorator; events go to the outbox in the same `SaveChanges`. Validators live in ApiContracts and run on the form and the server. Expected errors are `Result` errors mapped to ProblemDetails with `code` and `traceId`.

### 2.4 Archive and guards

| Code | Meaning | HTTP |
|---|---|---|
| **SS500** `Catalog.Archived` | a document or catalogue write references an archived record | 422 |
| **SS501** `Catalog.ArchiveBlocked` | the guard refused; `reasons` lists what blocks (e.g. "on hand 12 at KLP", "3 open transfers") | 409 |
| **SS502** `Catalog.AnalogueRule` | self-link, duplicate pair, second successor or cycle | 422 |
| **SS403** `Procurement.CurrencyMismatch` | part-supplier price currency differs from the purchase order | 422 |

Guards are C# (reading the module's own data and, for warehouses, `IFleetQueries` on the user's request) and, where the rule protects stock, SQL too. Restore re-checks uniqueness. `DatabaseErrorMapper` maps the SQL codes to the same errors.

### 2.5 History

`audit.record_history(p_table text, p_record_id text, p_page int, p_page_size int)` returns time, action, user id, changed columns and old/new values for one record (only tables on an allow-list; the trigger already omits personal-data columns). Granted to `app_read`. The endpoint resolves user names through `IEmployeeDirectory`. The panel shows "who, what, when" in plain words ("Rasa K. changed lead time 14 → 21 days").

### 2.6 CSV

- **One parser** (`CsvImportFile` moves to BuildingBlocks): UTF-8 with or without BOM, `,` or `;` from the header, RFC 4180 quoting, header = the catalogue's columns in any order, ≤ 10 MB and 50,000 rows, rate limit as imports today.
- **Rows → upserts by code:** references by code (brand `CLAAS`, unit `PCS`); the API validator per row; a code twice in the file is a `DUPLICATE` error on the later rows (as `validate_part_catalog` does); an archived code is an error (import never archives or restores); unchanged rows are counted, not written.
- **Gate:** errors / rows > 5 % → nothing is written (the rule of `staging.process_batch`).
- **Dry run = the real import rolled back:** the handler runs the whole import in the transaction and rolls back when `dryRun=true`, returning created, updated, unchanged and the errors (row, column, code, message). The preview therefore uses exactly the code that will commit.
- **Parts and demand history** keep the bulk SQL pipeline (`raw` → `staging` → `process_batch`, the import monitor, file deduplication); they gain the dry run (same rollback), the template, the export and the manufacturer column. The API and the screen are the same as for every catalogue.

### 2.7 Web components

`Features/MasterData/`: `CatalogPage<T>` (search as you type on the registered `TimeProvider`, filters slot, archived toggle, paging; cards below `sm`, grid above), `CatalogEditor<T>` (dialog on desktop, full page on phones; ApiContracts validators; a 409 reloads and says what changed), `ArchiveAction` (confirmation, SS501 reasons as a list), `HistoryPanel`, `CsvPanel` (template, export, import with the dry-run preview table, then "Apply"), `CatalogAutocomplete<T>` (lookup endpoint, active only, keyboard and screen-reader support), `ResponsiblesPanel`. Strings through `IStringLocalizer`; touch targets ≥ 44 px; status never by colour alone; errors as plain sentences with "Reference: <traceId>".

---

## 3. Catalogues

Every catalogue below uses section 2; the columns listed are additions or rules beyond the shared shape.

### 3.1 Inventory

| Catalogue | Table | Code / name | Rules, archive guard | Events | Policy (write) |
|---|---|---|---|---|---|
| Brands | `catalog.brand` (moved from `ref.brand`, C1) | `CLAAS` / "CLAAS" | `country_code`; guard: active parts of the brand | `BrandCatalogChanged` (Fleet snapshot) | `ManageCatalog` |
| Manufacturers | `catalog.manufacturer` (new) | `MANN` / "MANN+HUMMEL" | `country_code`, `website`; guard: active parts | — | `ManageCatalog` |
| Parts | `catalog.part` (+ `manufacturer_id` FK, nullable) | OEM number (unique per brand) / description | brand, manufacturer, category and unit (`ref`), weight, unit cost, condition-based; guard: on hand or reserved > 0 anywhere, open transfer lines, open kit holds, open expected receipts | `PartCatalogChanged` on **every** write (`PartCatalogPublication`) | `ManageCatalog` |
| Part analogues | `catalog.part_analogue` (reworked, 3.3) | — (a link) | 3.3 | — | `ManageCatalog` |
| Warehouses | `inventory.warehouse` (+ `updated_at`) | `KLP` / "Klaipėda hub" | city, address, coordinates, central hub (single, partial unique); guard: stock, open transfers, open expected receipts, kit holds, and open service orders or machines based there (`IFleetQueries`) | — (`IWarehouseQueries` caches expire after 60 s) | `ManageWarehouses` |
| Transport routes | `inventory.warehouse_route` (child of a warehouse) | from → to | distance, transit hours, cost; edited on the warehouse page; CSV by code pair | — | `ManageWarehouses` |
| Adjustment reasons | `inventory.adjustment_reason` (new) | `DAMAGED` / "Damaged in storage" | `direction` (in, out, both), `requires_note`; `stock_movement.adjustment_reason_id` FK, required for `ADJUSTMENT_IN/OUT` (CHECK `NOT VALID`, so the immutable ledger history keeps its notes); guard: none (archived reasons stay on history) | — | `ManageCatalog` |

Stock adjustments (`POST /stock-adjustments`) require a reason code of the right direction; the note is required only when the reason says so.

### 3.2 Brand move (C1)

One Inventory migration moves the table (`ALTER TABLE ref.brand SET SCHEMA catalog`, ids and FKs kept; adds the master-data columns and the audit trigger) and the Platform model drops the entity without SQL. Fleet's FK `machine_model → brand` is dropped (now cross-module) and Fleet gets `fleet.brand_snapshot` from `BrandCatalogChanged` (and `brand-catalog-publish` for the initial fill). PL/pgSQL that names `ref.brand` (`staging.validate_part_catalog` and the forecasting and reporting objects) is replaced through new scripts; views follow the table automatically. The 7h PR lists every object it touches; the schema diff allow-list records the move.

### 3.3 Part analogues (C2, ADR 0027)

| Column | Meaning |
|---|---|
| `id`, `part_id`, `analogue_part_id` | both FKs to `catalog.part` |
| `link_type` | enum `catalog.analogue_link_type`: `interchangeable` \| `supersedes` |
| `scaling_factor` | units of the analogue per unit of the part (US15), 0 < f ≤ 10 |
| `valid_from`, `note`, audit columns | — |

- **INTERCHANGEABLE** is symmetric and stored once with `part_id < analogue_part_id` (CHECK).
- **SUPERSEDES**: `part_id` (new) replaces `analogue_part_id` (old) — the direction of today's comment ("new SKU → predecessor"); the existing T001 row becomes a supersession.
- **No self-link** (CHECK), **one link per unordered pair** whatever its type (unique index on `least`, `greatest`), **at most one successor** per part (partial unique index on `analogue_part_id` where `supersedes`), **no cycles** in the supersession graph (`BEFORE INSERT OR UPDATE` trigger with a recursive check, serialised by `pg_advisory_xact_lock` on the table so that two concurrent inserts cannot close a cycle). Violations raise SS502. The C# `AnalogueRules` checks the same first for readable messages.
- Views for stage 8: `catalog.v_part_analogue_group(part_id, group_id)` (connected components of the interchangeable links; `group_id` = the smallest part id) and `catalog.v_part_successor(part_id, current_part_id, chain_length)`.
- Screen: the part page's "Analogues" tab (add by autocomplete, type, factor; the group and the supersession chain shown).

### 3.4 Procurement

| Catalogue | Table | Rules, archive guard | Policy |
|---|---|---|---|
| Suppliers | `procurement.supplier` (existing; + audit trigger, archive) | code / name, country, order e-mail (a shared mailbox), default lead time, currency; guard: open purchase orders | `ManageSuppliers` |
| Part suppliers | `procurement.supplier_part` (existing) + `currency_code`; prices in `procurement.supplier_price` (existing, `daterange` with the EXCLUDE constraint) | several suppliers per part; supplier part number, MOQ, pack size (MOQ a multiple), lead time days, **at most one primary** (`is_preferred`, partial unique index; shown as "primary"); a new price takes a **valid-from date** and closes the open period the day before (`procurement.set_supplier_price`); archive instead of delete; edited on the part's "Suppliers" tab and on the supplier page; CSV by supplier code + part (brand, OEM number) | `ManageSuppliers` |

`procurement.part_snapshot` (from `PartCatalogChanged`, like Fleet's) validates parts and gives the brand for the brand scope. `Procurement.Contracts.ISupplierQueries`: `GetLeadTimes(partIds)` (primary supplier's lead time, else the supplier default, else none) and `GetPrimarySuppliers(partIds)`, set-based, for stage 8.

### 3.5 Fleet

| Catalogue | Table | Rules, archive guard | Policy |
|---|---|---|---|
| Customers | `fleet.customer` (+ `name`, `kind` company \| individual, archive, audit with `name` excluded) | code `CUST-NNNNNN` / name; many farmers are private persons, so the name is personal data: masked for roles without access, removed by the new `fleet.anonymise_customer` (decided, section 14, Q2); guard: active machines | `ManageFleetData` |
| Machine models | `fleet.machine_model` (+ `code`, archive) | `CL-LEX-8900` / "Lexion 8900"; brand (snapshot), machine type (`ref`); guard: active machines, active service works | `ManageFleetData` |
| Machines | `fleet.machine` (existing) | code = VIN / name = model and year; model, customer, operating condition, home hub (`IWarehouseQueries`), year, commissioning date; engine hours are never edited here (readings only); archive = out of service; guard: open service orders | `ManageFleetData` |
| Service works | `fleet.service_work` (new), `fleet.service_work_part` (new) | operation code (`^[A-Z0-9-]{2,20}$`) / name, norm hours (0 < h ≤ 999.99), applicable model or brand (none = any), standard kit (part from the snapshot, quantity > 0, active parts only); guard: active maintenance plans using it | `ManageFleetData` |

### 3.6 Identity / organisation

Positions and employees: section 1. Both use the generic mechanism (CSV included; employee CSV never contains account data).

### 3.7 Responsible-employee assignments (shared pattern)

One table per record kind, each with a real FK to its record and to `ref.assignment_role`: `catalog.brand_responsible`, `catalog.manufacturer_responsible`, `catalog.part_responsible`, `inventory.warehouse_responsible`, `procurement.supplier_responsible`. Columns: `id`, record FK, `employee_id uuid` (plain: the employee is another module's; validated through `IEmployeeDirectory`), `assignment_role`, `valid_from`, `note`, audit columns; unique (record, employee, role). BuildingBlocks supplies the entity base, the Fluent helper, the `GET/PUT /{catalog}/{id}/responsibles` endpoints and the `ResponsiblesPanel`; a module supplies only the record type. Several employees per record, several roles per employee. Rights-bearing roles (1.1) need `Administration`; the others the catalogue's write policy. Archived employees stay listed as "left".

---

## 4. Documents use the catalogues

### 4.1 Pickers and the active-entry rule

Every document field that references a catalogue uses `CatalogAutocomplete` (active entries only). The server refuses archived references with SS500 in the handler, and the SQL guards that already validate a reference check it too. Documents in scope:

| Document | References |
|---|---|
| Transfer order | part, from and to warehouse (route must exist) |
| Stock adjustment | part, warehouse, adjustment reason |
| Service order | machine, service works, parts (snapshot `is_active`) |
| Purchase order | supplier, warehouse, part supplier (active, of that supplier) |
| Goods receipt | supplier, warehouse, open PO lines (part shown from the line) |

### 4.2 Goods receipt picks part, supplier and warehouse

The receipt form starts from the supplier and the warehouse (the keeper's hubs first), lists the open lines of that supplier's approved POs for that warehouse, and lets the keeper pick parts by autocomplete among them; the quantity defaults to the open quantity.

### 4.3 Fleet: exactly what changes

1. `fleet.maintenance_plan_work` (new): plan ↔ service work.
2. `fleet.maintenance_plan_part` **becomes a view** with the same name and columns, summing the kits of the plan's works. Data migration: every existing plan kit becomes one service work ("<plan name>", norm hours from T001), linked to its plan; then the table is replaced by the view. `fleet.create_service_order_draft` reads it unchanged; the `MaintenancePlan` aggregate maps the kit read-only.
3. `fleet.service_order_work` (new): the works of an order with their norm hours at drafting time. `create_service_order_draft` also inserts the plan's works (new script, `CREATE OR REPLACE`).
4. `POST /service-orders` for a repair accepts `serviceWorkIds`; the lines are the sum of their kits unless lines are given; lines stay editable (6i).
5. Order list and detail, and the plans endpoint, gain works and total norm hours (additive fields).
6. Plans stay OEM reference data maintained by migrations and T001 (6b decision); a changed service-work kit applies to the next drafts, never to existing orders.

Unchanged: readings, the due calculator and its parity test, `ServiceOrderDrafted` and the whole kit-reservation flow, the expiry job, the screens of 6j–6k except the works shown on the order.

---

## 5. Procurement core

### 5.1 Purchase orders (P1)

`Domain/`: `PurchaseOrder` (aggregate; states of `trg_po_status_guard`: draft → pending_approval → approved → exported → partially_received → received; draft, pending_approval, approved, exported → cancelled; pending_approval → draft), `PurchaseOrderLine`, `Supplier`, `SupplierPart` with its prices, `GoodsReceipt`, `ProcurementErrors`. The SQL functions stay the invariant guards (STAGE_5_PLAN I2): the handler asks the aggregate first, then calls the function in the same transaction. `PersistenceModel.cs` is deleted.

| Command | Written by | Policy |
|---|---|---|
| Create draft (supplier, warehouse, urgent with reason — US16) | EF | `PlanInventory` |
| Replace lines (part supplier of the PO's supplier; price valid on the order date unless overridden with a reason; MOQ and pack size with the nearest valid quantity in the message — SS400/SS401/SS402/SS403) | EF; `trg_po_line_validate` guards | `PlanInventory` |
| Submit | `procurement.submit_purchase_order`: sets `approval_required` from the threshold at that moment (total > the threshold of `procurement.approval_policy`); an order at or below the threshold is released at once (→ approved, `approved_by` = the submitter, expected arrival set), as in an ERP release strategy | `PlanInventory` |
| Approve | `procurement.approve_purchase_order` (orders with `approval_required` only): **approver ≠ author** whenever approval is required (CHECK `ck_purchase_order_four_eyes`: `NOT approval_required OR approved_by IS NULL OR approved_by <> created_by`, and SS301), brand scope for brand managers (all line brands from the part snapshot), expected arrival = approval date + the longest lead time of the lines | `ApprovePurchaseOrder` |
| Return to draft (with a reason) | EF (status guard) | `ApprovePurchaseOrder` |
| Export (sent to the supplier; CSV of the lines for the supplier) | `procurement.export_purchase_order` | `PlanInventory` |
| Cancel (reason; refused once anything was received) | `procurement.cancel_purchase_order` | `CancelPurchaseOrder` |

`allowedActions` per order for the current user (four-eyes included: the author never sees "Approve").

**Approval threshold (Q1).** `procurement.approval_policy` (one row: `threshold_amount numeric(14,2)`, `currency_code`, audited) holds the setting, default **€5,000** (the current constant). It lives in the database because the SQL guard must enforce the same value as the handler. `GET/PUT /api/v1/procurement/settings/approval` (`Administration`, If-Match). `purchase_order.approval_required` is stored at submission, so a later change of the threshold never changes the rule for an order already submitted. The constant `5000` disappears from the CHECK, `approve_purchase_order` and `v_purchase_order_overview` (new scripts, #152). `procurement.v_purchase_order_overview` becomes module-local and returns ids (X14); codes and names are resolved in C#.

### 5.2 Goods receipt (P2, X12)

`procurement.goods_receipt` (number `GR-YYYY-NNNNNN` from `util.trg_assign_document_number('GR')`, supplier, warehouse, delivery note number, received at, posted by) and `procurement.goods_receipt_line` (PO line FK, quantity > 0). `procurement.post_goods_receipt(p_receipt)` (`SECURITY DEFINER`, roles of `MoveStock`, `util.require_hub(warehouse)`): locks the PO lines in id order, refuses more than the open quantity (CHECK), updates `received_qty` and the PO status. The handler writes **`GoodsReceiptPosted`** in the same transaction. The transitional `procurement.receive_purchase_order_line` is dropped.

### 5.3 Events and Inventory's copies (X13)

| Publisher | Event | Payload | Consumer |
|---|---|---|---|
| Procurement | `PurchaseOrderApproved` | PO id, number, supplier id, warehouse id, expected date, lines (line id, part id, open quantity), actor | Inventory: `inventory.expected_receipt` rows |
| Procurement | `PurchaseOrderCancelled` | PO id, line ids, reason, actor | Inventory: remove the rows |
| Procurement | `GoodsReceiptPosted` | receipt id, number, warehouse id, PO id, lines (receipt line id, PO line id, part id, quantity, unit cost), actor | Inventory: `inventory.post_goods_receipt` posts `RECEIPT_PO` (lineage `purchase_order_line_id`, idempotent per receipt line), lowers the expected receipt |
| Inventory | `ResponsibilitiesChanged`, `BrandCatalogChanged`, `PartCatalogChanged` | sections 1.3, 3.2, 3.1 | Identity; Fleet; Fleet and Procurement |

All carry the acting user (`IActedByUser`, ADR 0024); consumers check at most `util.require_app_user`. A receipt never fails in Inventory (stock only grows). `inventory.fn_in_transit_qty`, `v_in_transit` and `v_stock_overview` move from the Reporting transitional script into Inventory SQL reading `expected_receipt` (new scripts and migrations, never edits of applied ones, #152). The T02 receipt assertions move to C# integration tests; T001 posts its receipts through the functions as the consumers would. Two new consumer queues per direction, within the CloudAMQP budget (ADR 0017 §5). The topology test gains `GoodsReceiptPosted` over RabbitMQ.

### 5.4 Screens

`/procurement/orders` (status tabs, supplier and hub filters, urgent chip), `/procurement/orders/{id}` (lines with price source, MOQ hints, totals; timeline; actions from `allowedActions`; four-eyes explained in plain words when the creator looks at their own order), `/procurement/orders/new`, `/procurement/receipts` and `/procurement/receipts/new` (4.2), `/procurement/suppliers` and the supplier page (part suppliers and prices with validity). Mobile-first: the keeper receives on a phone at the dock.

---

## 6. Policies

**Superseded by permissions (1.8, PR 7x1).** The table stays as the definition of the seeded default roles: each role gets the permissions of every policy listed for it. `ManageSuppliers` is the exception (14.2).

| Policy | Roles | Fronts (SQL parity) | New? |
|---|---|---|---|
| `Administration` | ADMINISTRATOR | employees, positions, accounts, rights-bearing assignments, `iam.anonymise_employee`, the approval threshold | existing, wider |
| `ManageCatalog` | INVENTORY_PLANNER, ADMINISTRATOR | brands, manufacturers, parts, analogues, adjustment reasons, other Inventory assignments | new |
| `ManageWarehouses` | ADMINISTRATOR | warehouses, routes | new |
| `ManageSuppliers` | BRAND_MANAGER, INVENTORY_PLANNER, ADMINISTRATOR | suppliers, part suppliers, `procurement.set_supplier_price` | new |
| `ManageFleetData` | SERVICE_ENGINEER, ADMINISTRATOR | customers, machine models, machines, service works, `fleet.anonymise_customer` (ADMINISTRATOR only) | new |
| `PlanInventory` | BRAND_MANAGER, INVENTORY_PLANNER | PO create, lines, submit, export | existing |
| `ApprovePurchaseOrder` / `CancelPurchaseOrder` | as today (brand-scoped for BRAND_MANAGER) | approve, return to draft / cancel | existing |
| `MoveStock` | WAREHOUSE_KEEPER, ADMINISTRATOR, plus hub access | goods receipt, adjustments, dispatch, receive | existing |
| `ReadProcurement` | all six roles | PO, receipt, supplier reads | new |
| `ReadMasterData` | all six roles | catalogue reads and lookups (employee personal data only through `Administration`) | new |

---

## 7. Quality and security

- **GDPR:** `iam.anonymise_employee(id)` replaces `iam.anonymise_user`: overwrites the employee's personal data, ends the account (stamp, hash, lockout), keeps ids and history; refuses the acting administrator. `fleet.anonymise_customer` for individuals (Q2). Audit values exclude personal-data columns; telemetry stays free of personal data (ADR 0018).
- **Database:** every new table, view and function has grants in its module's grants script (`app_read` read, `app_write` through functions or column grants, no `DELETE` on catalogues: archive only) and a row in the stage 10 per-module role plan (MODULE_MAPPING 4.2). New SQL suite file **T05_master_data.sql** (analogue rules incl. a concurrent cycle attempt, archive guards for stock, the reason requirement, the four-eyes CHECK, the hub check, the history function's allow-list, employee anonymisation, grants); T03 extended (no direct writes to the rights projection). Schema diff allow-list entries per PR.
- **OpenAPI:** every new endpoint with summary, description, parameter descriptions, response types, examples for 200/201 and the main ProblemDetails (400, 403, 404, 409 SS300/SS501, 400 `Catalog.VersionRequired`, 422 SS400–SS403/SS500/SS502); `ApiDocumentationTests` cover them; API_DOCUMENTATION lists the error codes.
- **Screens:** mobile-first (390 px phone, 820 px tablet, desktop), plain-language errors with the Reference, keyboard operation (autocomplete, dialogs, tabs), labels and live regions for screen readers, status never by colour alone, focus moved to the first error.
- **Demo data:** "Load demo data" (T001) fills every catalogue: about 9 positions (two without roles), 20 employees (the 12 demo users and 8 without an account), brand and warehouse assignments reproducing today's rights, 10 manufacturers, 60 analogue links (interchangeable groups across brands, supersession chains up to three steps), 6 adjustment reasons, part suppliers with currency and price history, customers with names (companies and individuals), machine models with codes, 20 service works with kits (the plan kits), purchase orders in every state with goods receipts, **on both sides of the approval threshold** (released at once below it, approved by a second person above it); every demo keeper has the hub assignment matching today's home hub. The T001 guards and `mart.v_data_quality` gain checks: every catalogue non-empty, every active part has a primary supplier, analogue rules hold, every demo user's effective rights equal the README table.
- **E2E (three viewports: 1280 × 800, 820 × 1180, 390 × 844):** (A) rights — an administrator moves an employee to the brand-manager position and assigns a brand on the brand page; the user's effective rights show both with their origin; after archiving the employee the user is signed out within the check interval. (B) procurement — a planner creates a PO from part-supplier data (price and MOQ prefilled), submits it and cannot approve it; a brand manager of that brand approves; a keeper receives part of it at their hub; the stock position shows the receipt and less in transit. (C) master data — a CSV import with a dry-run preview, apply, an archive refused with reasons, a restore.
- **Coverage** by layer (STAGE_5_PLAN 7): the domain of Identity (rights calculator), Procurement (`PurchaseOrder`, `GoodsReceipt`, price selection), Inventory (`AnalogueRules`, archive guards) and Fleet (service-work kit sum) is pure and unit-tested; data access stays in `Infrastructure`. No mocks are written to raise the number. Each PR states its unit coverage.
- **Infrastructure:** no new Container App; Bicep gets the gateway's `Bff__SessionCheckInterval` and the new consumer settings; Grafana: a procurement panel (POs created, approved, received per day) on the business dashboard and an "authorization" panel (stamp rotations by cause, session checks, rejected sessions); metrics `smartspare.identity.rights_refreshed`, `smartspare.procurement.purchase_orders`, `smartspare.masterdata.imports`. No new alert rule; no new cron job (the publish jobs are manual `--job`s).

---

## 8. Stage 8 input: analogue groups and lead times

**Demand pooling across analogues.**
- **History is never rewritten.** `inventory.stock_movement` stays append-only (`trg_stock_movement_immutable`); no row is moved, re-keyed or copied to another part. Pooling happens **at forecast time**, in the query that builds the training series: a mapping view joins each movement's part to its current successor and analogue group, and the series are aggregated over that mapping. Removing a link changes the next forecast, never the ledger.
- *Supersession:* when the training series are built, the demand of a superseded part is attributed to its current successor through `v_part_successor`, multiplied by the link's scaling factor, so a new part number starts with the history of the parts it replaces (US15). Forecasts and recommendations are produced for current parts only.
- *Interchangeable groups:* intermittent demand (most spare parts, ADI > 1.32) is forecast more reliably at group level. For a group from `v_part_analogue_group`, stage 8 forecasts the pooled series (sum of the members' demand in the base unit, scaled), classifies it with `fn_classify_demand`, and splits the forecast back to the members by their share of the last 52 weeks (the stocked member gets the rest when a share is zero). The benchmark compares pooled and per-part forecasts on the holdout; pooling is used where it is better (thesis material).
- *Recommendations:* an interchangeable part in stock at the same hub covers a shortage before a transfer or a purchase is proposed.

**Lead times and the reorder point.**
- Lead time *L* of a part: the primary part-supplier's `lead_time_days`, else the supplier's default, else the recommendation is flagged "no supplier" (`ISupplierQueries.GetLeadTimes`, set-based, one call per run; replaces the transitional read of `supplier_part` in `fn_stock_position`, X15).
- Reorder point per part and hub: **ROP = d̄ · L + SS** with d̄ the forecast daily demand over the lead time and **SS = z · σ_d · √L** (z from the target service level, default 95 % → 1.65; σ_d from the forecast error on the holdout). Order quantity rounded up to the primary supplier's MOQ and pack size. The recommendation's NFR08 explanation keeps its components (base, maintenance, lead-time buffer, safety stock).
- Expected receipts (`inventory.expected_receipt`, 5.3) feed the in-transit term of the net position.

---

## 9. Documentation and diagrams

Docs are updated **in the PR that changes the behaviour** (ADRs with their implementation, API_DOCUMENTATION, FRONTEND and MODULE_MAPPING rows per PR); the last PRs only consolidate and regenerate.

| Document | Content | PR |
|---|---|---|
| ADR 0026 Employee-derived authorization | A1–A5; superseded parts of ADR 0014/0016, STAGE_3_PLAN, MODULE_MAPPING, DATABASE_DESIGN marked "superseded by ADR 0026" in place | 7c |
| ADR 0025 Generic master-data mechanism | M1–M4, C1 (amends ADR 0011 D3 for brands) | 7f |
| ADR 0027 Part analogue model | C2 and the stage 8 use | 7i |
| `docs/requirements/REQUIREMENTS.md` + `TRACEABILITY.md` | the BA use cases and user stories as recorded (UC01, UC02, US01–US18, NFR01–NFR08), the stage 7 requirements (people and rights, master data, procurement), and a matrix requirement → code → test | 7t |
| ERD, DDL, data dictionary | regenerated from a database migrated to the end of stage 7 (`db/ddl/smartspare_schema.sql`, `DATA_DICTIONARY.md`, `docs/database/diagrams`) | 7u |
| MODULE_MAPPING, architecture and module context diagrams, event flow diagrams (Mermaid: rights, PO → receipt, catalogue events) | X12–X14 done, new tables and events, new copies | 7u |
| API_DOCUMENTATION, FRONTEND.md user flows (Mermaid) | every new endpoint and error code; flows for rights, catalogues, PO and receipt | per PR, consolidated in 7w |
| README | demo users table (user, employee, position, roles, brands, hubs; the password is the AppHost parameter `demo-user-password`, never a value) and one line: stage 7 in progress in December | 7e (table), 7w |
| CHANGELOG, BACKEND_REQUIREMENTS, ARCHITECTURE_DECISIONS §3/§13, CLAUDE.md (local, not committed) | release notes, evidence | 7w |

---

## 10. GitHub

- **Stage issue #153** with the running log comment.
- **One issue per PR** in milestone v0.7.0, created when this plan is approved (the breakdown may change in review).
- **Issues rewritten or closed** on approval: #13 rewritten to the receipt-document design (5.2, 5.3) and closed by 7q; #14 (stage 8) gets section 8; #53 (database-level brand scope) and #51 (security event log) stay `future`, with a note that rights changes are now audited on the source records.
- **#152** (migration checksums) exists, in v0.11.0. Its first part, the PR-time manifest guard, moves into this stage as **7b**, because stage 7 adds many migrations and replaces several SQL objects: an edited applied script must fail CI with a clear message, not a raw 42P07 on a developer's database. The runtime check (part 2) stays in #152.
- **Milestones moved when this plan merges:** v0.7.0 → 6 Dec, v0.8.0 → 11 Dec, v0.9.0 → 12 Dec, v0.10.0 → 14 Dec; v0.11.0 (review fixes) stays 23 Dec.

---

## 11. Schedule

| Day | PRs | State of `main` at the end of the day |
|---|---|---|
| Sat 28 Nov | 7a, 7b, 7c | plan merged; employees and positions; roles derived |
| Sun 29 Nov | 7d, 7e | all rights derived; legacy access removed |
| Mon 30 Nov | 7f, 7g | generic mechanism with adjustment reasons; CSV |
| Tue 1 Dec | 7h, 7i | brands, manufacturers, parts, analogues |
| Wed 2 Dec | 7j, 7k | warehouses, routes, suppliers, part suppliers — **expert reviews start** |
| Thu 3 Dec | 7l, 7m, 7n | Fleet master data and service works; documents use the catalogues |
| Fri 4 Dec | 7o, 7p, 7q | purchase orders, expected receipts, goods receipt |
| Sat 5 Dec | 7r, 7s, 7v | procurement screens, E2E, infrastructure |
| Sun 6 Dec | 7t, 7u, 7w | documentation, release v0.7.0 |

*Revised at the second checkpoint (27 November 2026, evening).* 7a–7f1 were merged on 27 November, a day and a half ahead of the table above. The permissions PRs go right after 7f2:

| Day | PRs | State of `main` at the end of the day |
|---|---|---|
| Sat 28 Nov | 7f2, 7x1, 7x2 | catalogue screens; endpoints and SQL check permissions |
| Sun 29 Nov | 7x3, 7x4, 7g | roles as data with guardrails and the matrix screen; CSV |
| Mon 30 Nov | 7h, 7i | brands, manufacturers, parts, analogues |
| Tue 1 Dec | 7j, 7k | warehouses, routes, suppliers, part suppliers |
| Wed 2 Dec | 7l, 7m | Fleet master data and service works — **expert reviews start** |
| Thu 3 Dec | 7n, 7o | documents use the catalogues; purchase orders |
| Fri 4 Dec | 7p, 7q, 7r | expected receipts, goods receipt, procurement screens |
| Sat 5 Dec | 7s, 7v, 7t | E2E, infrastructure, requirements |
| Sun 6 Dec | 7u, 7w | documentation, release v0.7.0 |

---

## 12. Pull requests

Effort: **S** ≈ a quarter day, **M** ≈ half a day, **L** ≈ a full day, at the pace of stage 6. Each PR: CI green (full gate), rebase-merge, 3-line log entry on #153; locally only the changed steps of `tools/check.sh`, one at a time.

| PR | Scope | Effort | Depends on | Acceptance |
|---|---|---|---|---|
| **7a** | This plan; stage issue; ARCHITECTURE_DECISIONS §13 schedule | S | — | merged; on approval: per-PR issues, issue rewrites, milestone dates |
| **7b** (#155) | #152 part 1: `db/sql/applied.sha256` manifest of every SQL resource a migration references, a test that fails with "<file> belongs to the applied migration <name>; add a new migration", the rule in CONTRIBUTING | S | 7a | an edited applied script fails CI with the message |
| **7c** (#156) | A1, A2 (roles): positions, role templates, employees, account link, `EffectiveRightsCalculator`, claims from it, the golden matrix (first), migration step 1, `/admin/positions`, `/admin/employees`, `/admin/users` read-only rights; role editing removed; `anonymise_employee`; ADR 0026 | L | 7b | golden matrix unchanged; role derivation and revocation tests; no role editing path |
| **7d** (#157) | A2 (scope), A3, A5: shared assignment pattern (entity, helper, endpoints, panel), `catalog.brand_responsible` (FK to `ref.brand` until 7h), `inventory.warehouse_responsible`, `ResponsibilitiesChanged`, Identity projection and consumer, stamp rotation, `app.warehouse_ids`, `util.require_hub`, migration step 2, publish job; topology round trip | L | 7c | golden matrix equal except the listed keeper case; event redelivery and order tests; parity incl. hub |
| **7e** (#158) | A4: 30-second session check setting; drift view must be empty; migration step 3 (drops); "no direct path" architecture and SQL tests; README demo users table | M | 7d | legacy tables gone; revocation within the interval; drift empty in CI |
| **7f** (#159) | M1–M3: record base, Fluent helper, `MapCatalog`, guards, history function and panel, `Versions` in BuildingBlocks, web components; first catalogue **adjustment reasons** + required reason on adjustments; ADR 0025 | L | 7e | generic endpoints tested once on the real catalogue; SS500/SS501; history shows who, what, when |
| **7x1** (#189) | 1.8 layer 1 and 2 read side: `Permissions` catalogue, `iam.permission`, `iam.role_permission` with the default bundles, `PermissionPolicyProvider` and `RequirePermission`, every endpoint and the web client switched from role policies to permissions, server-side permission resolution in the BFF, internal token size measured; ADR 0028 | L | 7f2 | golden matrix: probes, roles, brands, hubs identical; policy rows → permission rows; code/table parity test |
| **7x1b** | Compact permission set in the internal token: stable codes, versioned bitset, expansion on validation, pinned code list (ADR 0028) | S | 7x1 | worst case far under 8 KB; services with an older catalogue ignore unknown bits |
| **7x2** (#190) | `util.require_permission`, `app.user_permissions`, every SQL function switched (new scripts), `PolicySqlPermissionParityTests`, T03 | M | 7x1 | parity test; SQL suite green; no `util.require_role` call left outside the frozen scripts |
| **7x3** (#191), split into 7x3a and 7x3b | Roles as data: role API (create, edit permissions, archive; Administrator locked), `DutyConflicts` (409 with the pair), no self-granting (SS301), stamp rotation on role and template changes, audit | M | 7x2 | SoD, lock, self-grant and refresh tests (fake clock); matrix unchanged |
| **7x4** (#192) | Roles × permissions matrix screen (grouped by module, search), effective rights with origin (permission ← role ← position, scope ← assignment), demo data docs | M | 7x3 | bUnit; a role edit refreshes a session within 30 s (integration) |
| **7g** (#160) | M4: shared CSV parser, template, export, import with rolled-back dry run, duplicates, gate, upsert; parts and demand-history pipeline gain dry run, template, export | M | 7f | dry run = commit result (test); 5 % gate; parts import unchanged otherwise |
| **7h** (#161) | C1 brand move, manufacturers, `part.manufacturer_id`, `BrandCatalogChanged` + Fleet snapshot, brand and manufacturer responsibles | M | 7g | schema diff with the move recorded; Fleet machine list shows brands from the snapshot |
| **7i** (#162) | Parts CRUD (create, edit, archive with guard, `PartCatalogChanged` on every write), part responsibles, analogue rework (C2) with views and the part page tabs; ADR 0027 | L | 7h | SS502 rules incl. a concurrent cycle; snapshots updated on edit; archive guard |
| **7j** (#163) | Warehouses and routes CRUD, archive guard incl. `IFleetQueries`, `IWarehouseQueries` cache expiry, warehouse responsibles screen | M | 7i | guard reasons; a renamed hub appears in Fleet within the cache time |
| **7k** (#164) | Suppliers, part suppliers, prices with valid-from, primary, currency, `procurement.part_snapshot`, supplier responsibles, `ISupplierQueries` | L | 7i | one primary; price periods never overlap; CSV round trip |
| **7l** (#165) | Customers (names, masking, anonymisation), machine models, machines CRUD | M | 7h | personal data masked; machine archive guard |
| **7m** (#166) | Service works and kits; Fleet changes 4.3 (plan works, kit view, order works) | L | 7l | parity test and the kit flow unchanged; drafts carry works; T001 kits identical |
| **7n** (#167) | 4.1: autocomplete in every document; SS500 on archived references in every document write | M | 7m, 7k | each document refuses an archived reference |
| **7o** (#168) | P1: Procurement domain, PO API with `allowedActions`, four-eyes, brand scope, module-local overview (X14) | L | 7n | role × action matrix; four-eyes in C# and SQL; MOQ/price/currency errors |
| **7p** (#169) | `PurchaseOrderApproved`/`Cancelled`, `inventory.expected_receipt`, in-transit objects module-local (X13) | M | 7o | in transit from the copy; redelivery no-op |
| **7q** (#170) | P2: goods receipt document, `post_goods_receipt`, `GoodsReceiptPosted` → `RECEIPT_PO`, transitional function removed, T02 → C# tests, topology round trip; closes #13 | M | 7p | partial and full receipt; over-receipt refused; hub check; no Procurement object writes another schema |
| **7r** (#171) | Procurement screens (5.4) | L | 7q | bUnit states and actions; phone layout |
| **7s** (#172) | E2E scenarios A–C at three viewports | M | 7r | green in CI |
| **7t** (#173) | REQUIREMENTS.md, TRACEABILITY.md | M | 7s | every stage 7 requirement traced to code and a test |
| **7u** (#174) | ERD, DDL, data dictionary regenerated; MODULE_MAPPING; architecture, context and event-flow diagrams | M | 7q | generated from the end-of-stage database |
| **7v** (#175) | Bicep settings, metrics, dashboard panels | S | 7q | `what-if` clean (read-only, the owner's terminal if needed); dashboards provisioned locally |
| **7w** (#176) | API_DOCUMENTATION, FRONTEND flows, README, CHANGELOG, BACKEND_REQUIREMENTS, exit-criteria evidence; tag v0.7.0, release, close the milestone | M | all | released |

**7c is split into three PRs (28 November 2026),** each green and merged before the next, as 6g and 6h were: **7c1** the golden access matrix only, recorded on `main` before any authorization code changes (`AccessMatrixTests`, `AccessMatrix/access-matrix.json`); **7c2** the organisation model, migration step 1 and derived roles, with `/admin/users` read-only so `main` stays demo-ready; **7c3** the position and employee screens and ADR 0026. All three belong to #156.

**As built, 7c2 (#179):** `iam.position` with `iam.position_role`, `iam.employee`, `app_user.employee_id` (required, unique); `EffectiveRightsCalculator` feeds the claims, `GetRolesAsync` and the users list, each right with its origin; `POST /users` and role editing removed, accounts only through `POST /employees/{id}/account`; a new position, a changed template (every holder), leaving and restoring rotate the stamp, and sign-in checks the employee. Migration step 1 (`identity/0007_organisation.sql`) seeds seven standard positions and turns every user into an employee whose position's template equals the old roles (a position of its own for an unusual role set; anonymised users become anonymised employees who left); `iam.anonymise_employee` erases employee and account. A template-only change also touches the position row, so every write checks `If-Match`. Raw SQL through `SqlQueryRaw` must not contain `{}` (format placeholders): empty arrays are written `ARRAY[]::text[]`. Tests: the golden matrix unchanged, `AccessMigrationTests` (legacy users, second run a no-op), `OrganisationRulesTests` on T001 (last administrator, leaving blocks sign-in at once), `OrganisationTests`, the reworked `UserAdministrationTests`.

**As built, 7c3:** ADR 0026 (with the 30-second trade-off and the reason the one-time copy of 7d goes through Reporting); superseded parts marked in STAGE_3_PLAN 3.1, ADR 0016, MODULE_MAPPING 2.2 and 3.6, DATABASE_DESIGN 8.3 and 8.5. The Organisation area (`/admin/employees`, `/admin/positions`, `/admin/users` as tabs; menu entry Organisation): employees with position filter, leaving date, restore, account creation and anonymisation; positions with the role-template editor; every write sends the version read. bUnit: `OrganisationPagesTests`.
**7d is split into two PRs (28 November 2026),** each green and merged before the next: **7d1** hub access as a list for writes only (`app.warehouse_ids`, `util.require_hub`, `HubRights`/`HubAccess` with C#/SQL parity, a keeper without a hub writes nowhere), still fed by the legacy home hub; **7d2** the shared assignment pattern, `catalog.brand_responsible`, `inventory.warehouse_responsible`, `ResponsibilitiesChanged`, the Identity projection, migration step 2 and the topology round trip. Both belong to #157.

**7d2 is split again (28 November 2026):** **7d2a** the backend (assignment roles in `ref.assignment_role`, `catalog.brand_responsible`, `inventory.warehouse_responsible`, `IEmployeeDirectory`, `ResponsibilitiesChanged` with Identity's projection and watermark, the `inventory.responsibility-publish` job, migration step 2 in Reporting, T001, the topology round trip; the legacy brand and home-hub editing on users is removed because it no longer has any effect); **7d2b** the screen for brand and hub responsibilities.

**As built, 7e:** the drift guard of migration step 3 refuses to run unless every account's legacy roles equal its position's template and every legacy brand and home hub has a projected assignment (tested: a database that skipped the copy of step 2 is refused and keeps its tables). Then `iam.user_role`, `iam.user_brand` and `app_user.home_warehouse_id` are dropped. The account's `full_name`, `email` and `phone` were emptied in 7e and are **dropped** by the follow-up Reporting migration `DropAccountPersonalColumns`, which first redefines `audit.v_audit_log_masked` over the employee (the applied InitialReporting script reads `app_user.full_name`, and on a fresh database only Reporting runs after it; no applied script is edited, #152). `AccessMigrationTests` covers the path from the stage 6 schema. **Projection writes are enforced by grants:** `iam.apply_responsibilities` (SECURITY DEFINER) is the only write path, `app_write` has no direct INSERT, UPDATE or DELETE, and EXECUTE goes only to `smartspare_api` and `smartspare_identity` (T03); `RightsSourceTests` is an extra layer. The session check is the setting `Bff:Session:CheckInterval` (default 30 s, at most 5 minutes). `RightsSourceTests` prove that only the consumer writes the projection and no code writes roles, brands or hubs onto an account; the README has the demo users table.

**As built, 7f (split into 7f1 and 7f2, as stage 6 split its large PRs):** 7f1 (#159) is the mechanism without the screens:
- **Shared pieces:** `MasterRecord<TId>`, `MapMasterRecord`, `CatalogDefinition` and `MapCatalog`, the shared errors, and the record history (`audit.record_history` with its allow-list, names through `IEmployeeDirectory.FindAccountOwnersAsync`). `Versions` was already in BuildingBlocks since 7c2.
- **First catalogue: adjustment reasons**, five seeded. Every new adjustment names a reason that allows its direction; a note is needed only when the reason says so. The ledger's insert trigger enforces the reason, and `ck_stock_movement_source_document` accepts the reason in place of the note. Old rows keep their notes; the ledger is never rewritten.
- **Screens in 7f1:** only a reason picker on the adjustment form. The generic web components (`CatalogPage`, `CatalogEditor`, `ArchiveAction`, `HistoryPanel`, `CatalogAutocomplete`) and the reasons screen are 7f2.
- **Deviation from 3.1:** the reason rule is an insert trigger rather than a `CHECK NOT VALID`, because the ledger is append-only and partitioned; the effect is the same.
- **New policy:** `ManageCatalog` (planners, administrators). It only adds rows to the golden matrix; no existing right changed.

**As built, 7x3a (part of #191; 7x3 is split as stage 6 split its large PRs, and 7x3b adds the superadministrator with its guards):**
- **Roles as data:** `Role` is an aggregate holding its permissions, with lock and archive flags. The API under `/api/v1/identity/roles` lists, reads (ETag), creates, edits permissions (If-Match), archives and restores roles; `/permissions` serves the catalogue. Permissions `Organisation.Role.View` and `.Edit`.
- **Locked Administrator:** the built-in Administrator role cannot be edited or archived (422).
- **Separation of duties:** the `DutyConflicts` list (first pair: `Procurement.PurchaseOrder.Create` + `Procurement.Supplier.Edit`) is checked for a role and for every position whose template holds it, and for position templates (409 `Identity.DutyConflict` with the pair). `Procurement.Supplier.Edit` joins the catalogue now, held only by Administrator by default; the golden matrix gains this one row.
- **No self-granting (403 SS301):** editing a role you hold; editing your own position's template; moving your own employee to another position; changing your own rights-bearing brand or hub assignments.
- **Archive rules:** a role in a template cannot be archived (409 SS501), and an archived role cannot be put in a template (422 SS500).
- **Refresh and audit:** a permission change rotates the stamps of every account whose position holds the role; `iam.role` and `iam.role_permission` are audited. Position templates accept any well-formed role code, because roles are data.

**As built, 7x2 (#190):**
- **SQL checks:** `util.require_permission` and `util.current_app_permissions` read `app.user_permissions`, which the interceptor now sets with the request's permissions. The 17 business functions were redefined from their latest definitions with the permission their endpoint requires, generated by script into one new file per module (applied scripts untouched).
- **Hub check:** `util.require_hub` opens every hub for `Organisation.Scope.AllHubs`.
- **Tests:** `PolicySqlPermissionParityTests` (the renamed parity test) compares exact permission names. T03 adds two assertions: the ADMINISTRATOR role code alone opens no hub, and no business function calls `util.require_role` any more. The SQL test helpers derive permissions from the role bundles.

**As built, 7x1 (#189):**
- **Permissions:** 55 permissions in code (`Permissions`), `iam.permission` and `iam.role_permission` seeded from them, `PermissionPolicyProvider`, and every endpoint, page and menu entry on permissions. Brand and hub scope are permissions too. Brand scope uses an any-brand variant per action, not one global "all brands" permission, so a brand manager who is also a sponsor stays brand-scoped for transfers, as before.
- **Transport:** permissions are added server-side per request from the role codes (`PermissionClaimsTransformation`, 30-second cache, `IRolePermissions` in process or over HTTP for the gateway). They are never in the cookie. They reach the internal token (worst case 3,062 bytes, budget 8 KB, tested), the client's authentication state and `/bff/me`.
- **Golden matrix:** the endpoint probes, roles, brands and hubs are identical; the rows are now permissions. The superadministrator follows in 7x3 together with its guards, so that no window lets an administrator move into that position.

**As built, 7f2 (#187):**
- **Components in `Features/MasterData`:** `CatalogClient` (any catalogue route), `CatalogPage<T>`, `CatalogEditor`, `ArchiveAction`, `HistoryPanel` and `CatalogAutocomplete`, used by the adjustment reasons screen (`/inventory/adjustment-reasons`, a new Inventory tab).
  - `CatalogPage<T>` has search as you type, the archived switch, paging, and cards below `sm`.
  - `ArchiveAction` asks for confirmation and lists the SS501 reasons, which the client now reads from the problem details.
  - `HistoryPanel` shows "Rasa K. changed it", each field old → new, with older changes on request.
  - `CatalogAutocomplete` offers active entries only and is a combobox for keyboard and screen readers.
- **Deviation from 2.7:** the editor is an **inline panel** above the list, as on the existing organisation screens, not a dialog that turns into a full page on phones. It is full width on a phone and needs no viewport detection; a 409 shows the current version with a notice.

Total: 3 S, 15 M, 9 L (7x1–7x4 added at the second checkpoint) ≈ 14.75 PR-days of nominal effort in 9 calendar days.

---

### 12.1 Found during the stage

| PR | Finding | Kind | Fix |
|---|---|---|---|
| #186 | **Security fix.** The rights projection (`iam.employee_responsibility`, `iam.responsibility_source`) was writable by `app_write`, so every application login, including the Inventory and Fleet service logins, could change anyone's brand scope and hubs directly. 1.7 asked for exactly this T03 test, and 7d did not deliver it. | security | Direct privileges revoked; `iam.apply_responsibilities` (SECURITY DEFINER) is the only write path, executable only by the logins that host Identity; T03 +4; the privilege self-check flags direct writes. |
| #186 | The account kept emptied copies of the employee's name, e-mail and phone (7e deviation). | privacy | Dropped by a forward-only Reporting migration after the masked audit view moved to the employee. |
| #188 | A one-statement save outside the command pipeline runs without a transaction, so the session context is never set and the audit row has no user. | audit | Catalogue writes run in an explicit unit-of-work transaction. |
| #183 | A sign-in that met a concurrent stamp rotation failed with 500. | correctness | The sign-in retries on the fresh row. |
| #195 | **Authorization design.** A single global "all brands" permission would have widened rights: a brand manager who is also a sponsor would have approved transfers of every brand, because the sponsor's unscoped right on purchase orders leaked onto transfers. Found by an existing unit test before it shipped. | security | One "any brand" permission per brand-scoped action (`…ApproveAnyBrand`, `…CancelAnyBrand`, `…DecideAnyBrand`), which reproduces the old per-role rules exactly. |
| 7x1b | **Token growth.** As names, permissions cost about 40 bytes each in the internal token: 3,062 bytes at 55 permissions, an estimated 6.9 KB at 150 (end of stage 7) and over the 8 KB budget near 200. | capacity | Decided before the test failed: stable permission codes and a versioned bitset in the token (ADR 0028); 886 bytes now. |

## 13. Exit criteria, risk, dates and the cut order

### 13.1 Exit criteria for v0.7.0

1. No user exists without an employee; hub scoping guards writes only, reads stay unscoped; roles, brand scope and hub access are derived only from positions and assignments, shown with their origin; no endpoint or screen edits rights directly (architecture, reflection and SQL tests).
2. A change of position, template or assignment, or a deactivation, takes effect within the 30-second session check; sign-in is refused at once for a deactivated employee (tests with a fake clock).
3. Endpoints and SQL functions check permissions, never role names, and permission/SQL parity holds; roles are data with the Administrator role locked, separation of duties and no self-granting enforced, and every role or template change audited and effective within 30 seconds; the golden access matrix keeps its probes identical (permission rows instead of policy rows, and the documented keeper case).
4. Every catalogue of section 3 offers search (trigram), filters, paging, If-Match, archive with guards, history, CSV template, dry-run import with the 5 % gate, and export; each lives in its owning module and reaches other modules only by events or Contracts queries.
5. Part analogues enforce type, direction, uniqueness and acyclicity in the database; the group and successor views exist.
6. Documents pick only active entries and refuse archived references; service works drive plan and order kits without changing the reservation flow.
7. Purchase orders released by a configurable threshold, with four-eyes approval above it and brand scope; goods receipt reaches Inventory as `RECEIPT_PO` through `GoodsReceiptPosted` in both topologies; MODULE_MAPPING X12–X14 done; #13 closed.
8. Employee anonymisation; grants and SQL tests for every new object; schema diff and SQL suite green; OpenAPI complete with examples and codes.
9. "Load demo data" fills every catalogue; the three E2E scenarios pass at three viewports; unit coverage of business logic ≥ 70 % with the three figures reported.
10. ADRs 0025–0028, requirements and traceability, regenerated database docs, diagrams, README, CHANGELOG done.

### 13.2 The riskiest PR and how it is protected

**7d** (with 7c): it changes who may do what, the claim shape (several hubs), the SQL hub check and the data, and it makes rights depend on an event. Protection:
- the **golden access matrix** is written first, from `main`, and must stay equal after 7c, 7d and 7e (intended differences listed in the test, not hidden);
- **no destructive step before proof:** 7c and 7d are additive; the legacy tables stay as a read-only shadow, and 7e drops them only when the drift view is empty in CI;
- a **migration test** from the stage 6 schema with T001 loaded;
- **revocation tests** with a fake clock, event redelivery and out-of-order tests, a topology round trip, and the publish job as the repair path for a lost event;
- every PR can be reverted on its own until 7e.

### 13.3 Does 6 December hold?

Honestly: **only at the pace of stage 6, with no slack for surprises.** The nominal effort is about 14.75 PR-days for 9 calendar days; stage 6 delivered 20 PRs in two days, so the plan fits at that pace with roughly two days of margin. The risks are breadth (about 16 catalogue screens), the authorization rework needing a second iteration, CI time for three E2E scenarios at three widths, and fixes from the expert reviews from 2 December, which take priority over new scope. The checkpoint is **30 November**: if 7c–7e are not merged by then, the cut order applies from that day and the owner is told the same day.

*Second checkpoint (27 November 2026):* the permissions work adds about **2.5 PR-days** (7x1 L, 7x2–7x4 M), so the total goes from 14.75 to about 17.25 PR-days. 7a–7f1 are done a day and a half early, so **6 December still holds at the current pace, but the margin falls from about two days to about half a day.** The expert-review fixes from 2 December are the main threat. The next checkpoint is **1 December**: if 7x1–7x4 and 7g are not merged by then, the cut order applies. In the cut order, the matrix screen's search and grouping come after the history panel (item 1b); permission enforcement, separation of duties, the locks and the stamp rotation are never cut.

**Cut order** (no catalogue is cut; each cut becomes an issue, `future`, because the later stages have their own scope):
1. The history **panel** in the screens (the endpoint and the audit stay).
   1b. Search and module grouping on the roles × permissions screen (a plain sorted grid stays).
2. CSV **import** for the low-volume catalogues (positions, adjustment reasons, manufacturers, machine models, routes); template and export stay; parts, part suppliers, suppliers, analogues and employees keep import.
3. E2E scenario C at phone width.
4. Service works on **repair** orders (plans and drafts with works stay).
5. The supplier CSV export of a PO (export as a status stays).

**Never cut:** the authorization rework with its tests and migration, the generic mechanism core, every catalogue of section 3, documents using active entries, purchase orders with four-eyes, goods receipt as an event, the stage 8 inputs, the ADRs and the requirements traceability.

---

## 14. Review decisions

### 14.1 Plan approval (product owner, 27 November 2026)

| # | Question | Decision |
|---|---|---|
| Q1 | Four-eyes on every purchase order? | **No: release by threshold**, as in an ERP release strategy. Approval is required above a configurable threshold (a setting, default €5,000); whenever approval is required, the author can never approve their own order. Demo data has orders on both sides of the threshold (5.1). |
| Q2 | Customer names | **Yes**, masked for roles without access and removed by the new anonymisation function (many farmers are private persons). |
| Q3 | 30-second session check | **Accepted**, recorded in ADR 0026 as a deliberate trade-off; sign-in for a deactivated employee is blocked immediately (1.4). |
| Q4 | Keeper without a hub assignment | **No hub access** (least privilege). Demo keepers get their hub assignments. |
| Q5 | #152 part 1 as 7b | **Yes.** |
| Q6 | One-time cross-module copy through the Reporting migration | **Accepted** if idempotent, covered by the migration test and recorded in ADR 0026 as the only exception, with the reason it goes through Reporting (1.6). |

**Corrections:**
1. Stage 8 pooling never rewrites history: `inventory.stock_movement` stays append-only, and demand is pooled at forecast time through a mapping view (section 8).
2. Hub scoping applies to writes only; reads stay unscoped across hubs and brands (stage 3 decision), and the golden access matrix covers the reads (1.2, 1.7).

### 14.2 Second checkpoint (product owner, 27 November 2026)

- 7c–7e and 7f1 accepted, including the T03 finding, recorded as a security fix in 12.1.
- **Permissions model** (1.8, ADR 0028, PRs 7x1–7x4) added before the first procurement catalogue.

**Interpretations made in this plan, to be confirmed or corrected:**
1. "`Organisation.Role.Edit` + being able to grant yourself rights" is a no-self-granting rule (1.8), not a permission pair. Rights flow position → role, so the dangerous act is editing a role you hold or your own position.
2. `Procurement.Supplier.Edit` goes to the Administrator role only by default, instead of `ManageSuppliers` for brand managers, planners and administrators (section 6). Brand managers and planners create purchase orders, and the first conflict pair forbids holding both. Supplier editing does not exist yet (7k), so no current right changes.
3. The locked Administrator role holds today's administrator rights plus the organisation permissions, not every permission: "reproduce today's rights exactly" rules out giving it purchase order creation, and the first pair would forbid it anyway.
4. The golden matrix's policy rows become permission rows; the endpoint probes, roles, brands and hubs must stay identical.
5. The internal token carries the permissions (measured, budget 8 KB). The cookie does not.

**Owner's answers (27 November 2026, evening):**
1. Approved: editing a role you hold, or the template or assignments of your own position, is refused with 403.
2. Approved: supplier editing goes to Administrator only by default; ADR 0028 notes that a dealer can build a "procurement master data" role later.
3. **Changed.** Administrator stays system administration under normal rules. In addition there is a **Superadministrator**: a locked position whose locked role template holds every permission, with no scope limit and no SoD validation. Superadministrators are ordinary employees in that position. Only another superadministrator moves someone into or out of it, never themselves, and the last one cannot be removed (409). It does not bypass database invariants or the self-approval ban. Demo data has one, with the password from the new parameter `superadmin-password`. Built in 7x1/7x3 (no separate PR).
4. Approved, with the mapping table "old policy → new permission(s)" in the 7x1 PR and above.
5. Approved: permissions are computed server-side, the internal token carries them, there is an 8 KB budget test, and the figure is in ADR 0028.
