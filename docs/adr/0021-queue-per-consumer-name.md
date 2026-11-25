# ADR 0021: One queue per consumer name

- Status: Accepted
- Date: 2026-11-25
- Amends: [ADR 0017](0017-rabbitmq-integration-event-transport.md) §2

## Context

ADR 0017 §2 names one durable quorum queue per consumer `<consuming-module>.<EventName>.v<N>` (for example `forecasting.TransferApproved.v1`) and says the consumer name equals the inbox `consumer` value (#32).

Since PR 4a, consumers have stable names `<module>.<purpose>` (for example `inventory.reserve-service-kit`, #32). A queue named only by module and event would be shared by every consumer of that event in the module. Messages on a shared queue are distributed between its consumers, so each consumer would see only some of the events, and the per-consumer inbox couldn't protect against that.

## Decision

The queue of a subscription is named after the consumer, not only its module:

`<consumer name>.<EventName>.v<N>`, for example `inventory.reserve-service-kit.ServiceOrderReleased.v1`.

- The name still starts with the consuming module, so per-module conventions keep working: the dead-letter queue `<module>.dead-letter`, the KEDA regular expression over a service's queues, and policies by prefix.
- Every consumer has its own queue and receives every event it subscribes to.
- The consumer name in the queue equals the inbox `consumer` value, as ADR 0017 intended.
- The rest of ADR 0017 §2 is unchanged: exchanges, routing keys, message properties and dead-lettering.

## Consequences

- Queue names are longer. CloudAMQP's limit of 100 queues counts subscriptions, not modules, which ADR 0017 §5 already budgets: ≤ 45 subscriptions.
- Renaming a consumer renames its queue. A consumer name is stable by design (#32), and a rename is an ordinary deployment: the new queue is declared at start-up, and the old one is deleted by an operator once it is empty, or expires after the broker's idle time.
- Implemented in `SmartSpare.Infrastructure.RabbitMq` (`IntegrationEventTopology.Queue`) and tested with a broker in Testcontainers.

## Alternatives considered

| Alternative | Why not |
|---|---|
| ADR 0017 as written, with an architecture test that forbids two consumers of one event in one module | Keeps the shorter names, but turns a naming convention into a design constraint on every future module. |
