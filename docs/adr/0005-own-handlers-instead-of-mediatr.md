# ADR 0005: Own handlers instead of MediatR

- Status: Accepted
- Date: 2026-11-16

## Context

CQRS handlers need a dispatch mechanism with cross-cutting behaviours (validation, logging, transactions). MediatR is commercially licensed from version 13; 12.5.0 is the last Apache 2.0 release.

## Decision

Implement `ICommandHandler<TCommand, TResult>` and `IQueryHandler<TQuery, TResult>` with decorators for validation, logging and transactions, registered through DI. No MediatR.

## Consequences

- No licensing risk and no dependency on a frozen package version.
- About 100 lines of explicit, easily explained code.
- No built-in notification pipeline; integration events use the Outbox (ADR 0003).
