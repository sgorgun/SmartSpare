# ADR 0003: Module boundaries and integration events

- Status: Accepted, amended by [ADR 0017](0017-rabbitmq-integration-event-transport.md) (2026-11-24)
- Date: 2026-11-16

## Context

The existing database has foreign keys across module schemas and runs cross-module workflows (e.g. accepting a recommendation creates a transfer) in one transaction. This blocks extracting modules into services.

## Decision

Remove cross-module FKs, JOINs and transactions. Modules change each other's state only through integration events delivered with the Outbox/Inbox pattern (`FOR UPDATE SKIP LOCKED` dispatcher, idempotent receivers). Synchronous cross-module calls are read-only and go through interfaces in `Contracts`: in-process in the single host, HTTP clients between services ([ADR 0015](0015-deployment-topology-microservices-behind-yarp-gateway.md) §4). Modules may keep read-only copies of foreign data, updated by events. UC01 runs as TransferRequested → TransferApproved / TransferRejected with a compensating action.

## Consequences

- Eventual consistency between modules; invariants inside a module stay strictly transactional.
- The single host uses the in-process transport. The services topology delivers the same outbox messages through RabbitMQ ([ADR 0017](0017-rabbitmq-integration-event-transport.md)), without changing business code.
- More moving parts (outbox tables, background dispatcher) than direct calls.

## Amendment (2026-11-24)

The transport between services is **RabbitMQ** ([ADR 0017](0017-rabbitmq-integration-event-transport.md), product-owner decision D2), not Azure Service Bus.

The Outbox/Inbox design of this ADR is unchanged. RabbitMQ replaces only the delivery step:
- publisher confirms are required before `processed_at` is set;
- the inbox stays the idempotency guard.

The in-process transport stays for the single-host topology.
