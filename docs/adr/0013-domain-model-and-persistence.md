# ADR 0013: Domain model and persistence

- Status: Accepted
- Date: 2026-11-17

## Context

Stage 2 ported the V016 schema to EF Core code-first per module (ADR 0004, ADR 0011). To do that without designing the domain at the same time, each module got persistence classes in `Infrastructure/Persistence/PersistenceModel.cs`: plain classes with public setters that mirror the tables, so the Fluent API configurations could reproduce the schema exactly. They carry no behaviour, and the `Domain/` folders are still empty.

From stage 3 on, the modules get real domain models (aggregates, invariants, domain events; ARCHITECTURE_DECISIONS section 3). A choice is needed between keeping the table mirrors as a separate persistence model with a mapping layer to the domain, and mapping the domain directly.

## Decision

1. **The stage 2 persistence classes are temporary.** They are table mirrors used to port the schema, not a layer the application builds on.
2. **Aggregates live in `Domain/` and EF Core maps them directly.** In each module stage, aggregates and entities derive from `AggregateRoot<TId>` / `Entity<TId>` of SharedKernel, keep their state private, change it only through behaviour methods and raise domain events. The module's EF configurations map them directly: private constructors for materialisation, private setters or backing fields, owned or complex types for value objects. The matching persistence class is then deleted. There is **one model** and no mapping layer between domain and persistence classes.
3. **Tables without behaviour stay plain classes:** the `raw` / `staging` import rows, `ref` reference data and the Reporting marts. They are data, not aggregates.
4. **No generic repository** (ADR 0012). Command handlers use the module's internal DbContext and `IUnitOfWork<TContext>`; queries project straight into DTOs with `AsNoTracking` or read views (CQRS-lite). An **aggregate-specific repository** is allowed only when loading an aggregate is complex (several includes, filters, locking) and the same loading is reused by more than one handler.
5. **Domain classes reference no EF Core types.** Mapping concerns stay in `Infrastructure/Persistence/Configurations`; the architecture tests already fail if a `Domain` namespace depends on EF Core, ASP.NET Core, Npgsql or any other framework package.

## Consequences

- One model per table: invariants live in the aggregate that owns the data, and there is no mapping code to keep in sync.
- The domain bends slightly to EF Core: private parameterless constructors and backing fields. This is accepted, since it needs no EF types in `Domain/`.
- Remapping a table from a persistence class to an aggregate must not change the schema: `MigrationRunner check-model` shows no pending model changes, or any migration it needs is deliberate and reviewed.
- Each module stage removes its own persistence classes. Identity starts in stage 3 (issue #38); stages 4–8 do the same for Inventory, Fleet, Procurement, Forecasting and Reporting.
- Handlers depend on EF Core directly. Replacing EF Core would mean rewriting a module's handlers, which ADR 0012 already accepts for code outside the volatile-technology seams.
