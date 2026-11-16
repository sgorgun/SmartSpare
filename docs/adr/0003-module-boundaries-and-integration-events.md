# ADR 0003: Module boundaries and integration events

- Status: Accepted
- Date: 2026-11-16

## Context

The existing database has foreign keys across module schemas and runs cross-module workflows (e.g. accepting a recommendation creates a transfer) in one transaction. This blocks extracting modules into services.

## Decision

Remove cross-module FKs, JOINs and transactions. Modules change each other's state only through integration events delivered with the Outbox/Inbox pattern (`FOR UPDATE SKIP LOCKED` dispatcher, idempotent receivers). Synchronous cross-module calls are read-only and go through interfaces in `Contracts`. Modules may keep read-only copies of foreign data, updated by events. UC01 runs as TransferRequested → TransferApproved / TransferRejected with a compensating action.

## Consequences

- Eventual consistency between modules; invariants inside a module stay strictly transactional.
- The in-process transport can be swapped for Azure Service Bus without changing business code.
- More moving parts (outbox tables, background dispatcher) than direct calls.
