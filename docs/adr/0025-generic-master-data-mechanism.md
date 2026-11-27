# ADR 0025: Generic master-data mechanism

- Status: Accepted
- Date: 2026-11-28
- Amends: [ADR 0011](0011-database-ownership-per-module.md) decision 3 for brands (catalogue `catalog.brand`, stage 7 PR 7h)
- Relates to: [STAGE_7_PLAN.md](../stage-7/STAGE_7_PLAN.md) sections 2 and 3, [ADR 0013](0013-domain-model-and-persistence.md), [ADR 0019](0019-public-api-documentation.md)

## Context

Stage 7 adds about a dozen catalogues: brands, manufacturers, parts, analogues, adjustment reasons, suppliers, customers and the warehouse catalogues. Each needs the same behaviour: search, paging, optimistic concurrency, archiving instead of deleting, a change history, CSV import and export, an autocomplete field. Written one by one, these drift apart and multiply the tests. The product owner's scope (M1–M4) asks for one mechanism that every catalogue uses.

## Decision

1. **One record shape.** Every catalogue row has a `code` (business key, unique, fixed after creation; only an administrator may change it, because files and other systems refer to it), a `name`, `is_archived`, `archived_at`, `archived_by`, `created_at`, `updated_at`, `xmin` as the concurrency token and the audit trigger. In code: `MasterRecord<TId>` (SharedKernel) is the aggregate base; `MapMasterRecord` (Infrastructure) maps the columns, the unique code and trigram indexes on code and name. One table per catalogue, real FKs inside the module, no polymorphic tables.
2. **One API shape, built from a definition.** A catalogue supplies a `CatalogDefinition` (route, policies, projection, how a write creates or changes a record, archive guard, field labels for the history, OpenAPI examples). `MapCatalog` builds the same endpoints for every catalogue: list with search, archived switch, sort and paging; detail with `ETag`; lookup of active entries; create; update, archive and restore with `If-Match`; and the history. Each endpoint is documented (summary, description, parameters, responses, examples; ADR 0019).
3. **Writes go through the record and EF** (ADR 0013) in one explicit transaction of the module's unit of work. The transaction's start sets the session context, so the audit trail names the user. A stale version gives 409, a missing one 400 `Catalog.VersionRequired`.
4. **Archive instead of delete.** An archived record stays on past documents, leaves the lookup and is refused by new documents and catalogue writes with **SS500** (422). Archiving runs the catalogue's guard; if something still uses the record, it is refused with **SS501** (409) and the list of reasons ("on hand 12 at KLP").
5. **History from the audit trail.** `audit.record_history(table, record id, page, size)` (SECURITY DEFINER) reads one record's entries from `audit.audit_log`, only for tables a module registered in `audit.history_table`; the trigger already leaves out personal data. The endpoint names the people through Identity's employee directory (`IEmployeeDirectory.FindAccountOwnersAsync`) and returns plain words: when, who ("Rasa K."), action, and each field with its old and new value.
6. **Errors are shared:** `Catalog.NotFound`, `Catalog.CodeTaken`, `Catalog.CodeFixed`, `Catalog.VersionRequired`, `Catalog.InvalidPaging`, SS500, SS501 (`reasons`), SS502 for the analogue rules (PR 7i).
7. **CSV and the web components** follow in their own PRs (7g, 7f2) on the same definition.

The first catalogue is the adjustment reasons: every new stock adjustment names an active reason that allows its direction. Some reasons also require a note. The ledger's insert trigger enforces the reason, and `ck_stock_movement_source_document` accepts the reason in place of the note. Older adjustments keep only their note; the ledger is never rewritten.

## Consequences

- A new catalogue is an aggregate, a Fluent configuration, a definition and a migration; its endpoints, behaviour and documentation come from the mechanism, and the mechanism's tests (once, on the adjustment reasons) cover them.
- The endpoint builder reads the request body itself, because the ASP.NET route-handler analyzer cannot inspect a body parameter of a generic type. A missing or malformed body gives 400 `Catalog.BodyRequired`.
- `app_read` has USAGE on the `audit` schema so it can call the history function; `audit.audit_log` itself stays closed to it (T03).
- Codes stay stable: a planner cannot rename one (422 `Catalog.CodeFixed`), an administrator can.
