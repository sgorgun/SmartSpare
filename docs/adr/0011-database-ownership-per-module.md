# ADR 0011: Database ownership per module

- Status: Proposed
- Date: 2026-11-16

## Context

ADR 0002–0004 require each module to own its schema, DbContext and migration history, with no cross-module foreign keys, JOINs or transactions. The current database (V001–V016) was designed as one unit: 33 foreign keys and about twenty functions and views cross module boundaries, and workflows such as UC01 (recommendation → transfer) and UC02 (engine hours → service order with reserved kit) run in one transaction across schemas. Stage 2 must port this database to EF Core code-first per module without losing the tested behaviour (97 SQL assertions, concurrency test, demo data). The full analysis is in [MODULE_MAPPING.md](../database/MODULE_MAPPING.md).

## Decision

1. **Ownership.** `iam` → Identity; `catalog`, `inventory`, `raw`, `staging` → Inventory; `fleet` → Fleet; `procurement` → Procurement; `forecast` → Forecasting; `mart` → Reporting; `ref`, `util`, `audit` → Platform. `meta` is retired.
2. **Suppliers move to Procurement.** `catalog.supplier`, `supplier_part` and `supplier_price` become `procurement.*`, so purchase-order validation and approval stay inside one module.
3. **Platform is a shared kernel.** Foreign keys from module tables to `ref.*` are kept; modules use `util.*` helpers and attach `audit.trg_row_audit` to their own tables. The Platform never references a module.
4. **Cross-module foreign keys are removed** (33). Actor columns become plain ids from the authenticated principal; warehouse and part references are validated through `*.Contracts` read interfaces or read-only copies updated by events; document lineage ids are set only by event handlers.
5. **Cross-module logic becomes event flows**, implemented in the stage of each module: service kits (`ServiceOrderDrafted` → `ServiceKitReserved`, `ServiceKitReservationExpired`, `ServiceOrderCompleted` → `ServiceKitIssued`, `ServiceOrderCancelled`), goods receipt and in-transit (`PurchaseOrderApproved`, `PurchaseOrderLineReceived`, `PurchaseOrderCancelled` with an `inventory.expected_receipt` copy), UC01 (`TransferRequested` → `TransferApproved` / `TransferRejected`, `PurchaseRequested` → `PurchaseRequestAccepted` / `PurchaseRequestRejected`). The stock position (NFR08) is computed in C# in Forecasting from set-based Contracts queries.
6. **Stage 2 is a structural port.** Cross-module functions and views are kept with unchanged bodies as marked transitional objects (PL/pgSQL in the caller's module, early-bound views and SQL functions in the last Reporting migration) until their module stage replaces them, so the existing tests stay green.
7. **Migrations.** One DbContext per module plus `PlatformDbContext`, each with `<schema>.__ef_migrations_history`; order Platform → Identity → Inventory → Fleet → Procurement → Forecasting → Reporting. SQL parts live in `db/sql/<context>/` and are embedded into the module assembly; partitioned tables are created in SQL and excluded from the EF model. Objects are owned by `smartspare_owner`; migrations run as `smartspare_migrator`, locally and in CI through a migration runner, in Azure as one EF bundle per context.
8. **Roles.** The current `app_*` roles are ported unchanged in stage 2. Per-module roles used with `SET ROLE` replace them in stage 9, after the transitional cross-module functions are gone.
9. **Verification.** Schema diff against the V016 database with an explicit allow-list, the 97 SQL assertions, the concurrency test and T001 run in CI against the EF-built database (Testcontainers).

## Consequences

- Every module can be moved to its own database by copying its schema(s) and the Platform SQL; no business code depends on another module's tables.
- Stage 2 keeps every database-rubric point and remains verifiable, at the price of transitional SQL that stages 3–7 must remove; each object is listed with its removal stage.
- Some invariants that were transactional across modules become eventually consistent (kit reservation, goods receipt, UC01 fulfilment) and need compensating actions and Inbox idempotency.
- Several read models gain deliberate copies (`part_snapshot` per consumer, `expected_receipt`, `qty_reserved`) that must be kept in sync by events.
- Module boundaries are enforced in PostgreSQL only from stage 9; until then architecture tests and code review enforce them.
- Section 2 of ARCHITECTURE_DECISIONS.md (schema list of Inventory and Procurement) and DATABASE_DESIGN.md §9 must be updated when this ADR is accepted.
