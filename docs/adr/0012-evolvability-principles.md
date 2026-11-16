# ADR 0012: Evolvability principles

- Status: Accepted
- Date: 2026-11-17

## Context

SmartSpare should outlive its first technology choices: the forecasting framework, the cloud provider, the front-end and the clients calling the API may all change after the defence. Making everything replaceable up front would bury a one-person project in abstractions that are never used. This ADR records where the system keeps its options open and, just as importantly, where it deliberately does not.

## Decision

1. **Abstractions only at seams to volatile technology.** An interface is introduced where a technology is likely to be replaced: machine learning, file storage, external APIs, cloud services and the message transport. Everything else uses the framework directly: no generic repositories over EF Core (the DbContext is the unit of work and repository), no interface per class, no wrappers around the .NET base library.

2. **Stable contracts.** What other code depends on is kept stable and changed only deliberately:
   - module boundaries: `*.Contracts` projects and the module installer (ADR 0002, 0003);
   - the HTTP API, described by OpenAPI and versioned from the first public business endpoint (`/api/v1/...`); operational endpoints such as health probes and module pings are not versioned;
   - integration events, which are versioned rather than changed in place.

3. **Claims-based authorization.** Policies check claims only (role, brand, warehouse), never how the caller authenticated. The same endpoints can then accept the BFF cookie today and bearer tokens from mobile or third-party clients later without changing a policy.

4. **Anti-corruption layer for external services.** Each external system is an adapter in the owning module's `Infrastructure/` with its own DTOs. The adapter translates to domain types; external types (SDK models, generated clients, ML.NET types) never reach `Domain/`.

5. **The database is replaceable per module, not globally.** PostgreSQL-specific logic (triggers, functions, partitions, grants) stays in `db/sql/<module>/` and is applied by migrations (ADR 0004, 0011). Business rules are enforced in the domain as well, so the SQL is a second line of defence, not the only one. Replacing PostgreSQL is a decision for one module at a time, made when that module is extracted.

6. **Platform.** Follow every .NET LTS release (ADR 0007); no preview or prerelease packages in production code; keep third-party dependencies minimal and prefer the framework when it covers the need.

7. **Swaps are recorded.** Every technology replacement is a new ADR that supersedes the old one; accepted ADRs are not edited to describe a different decision.

## Enforcement

Architecture tests in `tests/SmartSpare.ArchitectureTests` check the rules that can be checked automatically:

- `Microsoft.ML` types are used only in `SmartSpare.Modules.Forecasting.Infrastructure`, and only the Forecasting project may reference ML.NET packages;
- module projects (implementation and Contracts) reference no cloud SDK (`Azure.*`, `Microsoft.Azure.*`, AWS, Google Cloud); cloud services are wired in the host or in BuildingBlocks adapters;
- `Domain` namespaces depend only on the .NET base library, SharedKernel and their own module's Domain.

The other principles are checked in code review.

## Consequences

- Replacing ML.NET, the file store, the cloud provider or the message transport touches one adapter and the host, not business code.
- New client types (mobile, third-party, another front-end) need new authentication and clients, not changes to endpoints or policies.
- Code outside the seams is coupled to EF Core and ASP.NET Core on purpose; swapping those is out of scope and would be a rewrite of the affected module.
- API and event versioning adds some ceremony to breaking changes.
- Follow-up work is tracked as GitHub issues labelled `future`.
