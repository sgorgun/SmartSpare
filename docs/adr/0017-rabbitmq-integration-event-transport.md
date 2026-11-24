# ADR 0017: RabbitMQ as the integration-event transport

- Status: Accepted
- Date: 2026-11-24
- Amends: [ADR 0003](0003-module-boundaries-and-integration-events.md)

## Context

Integration events are written to each module's `outbox_message` in the same transaction as the business change. A dispatcher (`FOR UPDATE SKIP LOCKED`) delivers them, and receivers record handled ids in `inbox_message` (ADR 0003). Today the delivery transport is in-process. With services in separate processes ([ADR 0015](0015-deployment-topology-microservices-behind-yarp-gateway.md)), events need a broker. On 24 November 2026 the product owner decided (D2) on RabbitMQ, with no commercial messaging library.

Facts checked on 24 November 2026:

| Item | Fact | Source |
|---|---|---|
| RabbitMQ server | Current release line 4.4 (4.4.1), MPL 2.0 | [release information](https://www.rabbitmq.com/release-information), [licence](https://github.com/rabbitmq/rabbitmq-server/blob/main/LICENSE) |
| Official .NET client | `RabbitMQ.Client` 7.3.0, Apache-2.0 OR MPL-2.0, async API (`IChannel`, `BasicPublishAsync`), publisher confirms enabled per channel via `CreateChannelOptions` | [NuGet](https://www.nuget.org/packages/RabbitMQ.Client), [.NET API guide](https://www.rabbitmq.com/client-libraries/dotnet-api-guide), [publisher confirms tutorial](https://www.rabbitmq.com/tutorials/tutorial-seven-dotnet) |
| MassTransit | v9 and later require a commercial licence; v8 support ends 31 December 2026 | [massient.com](https://massient.com/) |
| Quorum queues | Recommended default for durable, replicated queues. Delivery limit defaults to 20 since 4.0. Native delayed retry since 4.3 | [quorum queues](https://www.rabbitmq.com/docs/quorum-queues) |
| Dead-lettering | DLX configured preferably by **policy**, not hard-coded `x-arguments` | [dead letter exchanges](https://www.rabbitmq.com/docs/dlx) |
| Delayed-message exchange plugin | Archived and no longer maintained | [plugin repository](https://github.com/rabbitmq/rabbitmq-delayed-message-exchange) |
| Storage | Prefer local disks. NAS only with low, stable latency. Avoid distributed filesystems | [production checklist](https://www.rabbitmq.com/docs/production-checklist) |
| Aspire | `Aspire.Hosting.RabbitMQ` 17.0.0 (`WithManagementPlugin`, `WithDataVolume`); `Aspire.RabbitMQ.Client` 17.0.0 on RabbitMQ.Client 7 | [Aspire RabbitMQ hosting](https://aspire.dev/integrations/messaging/rabbitmq/rabbitmq-host/) |
| Tests | `Testcontainers.RabbitMq` 4.17.0 | [NuGet](https://www.nuget.org/packages/Testcontainers.RabbitMq) |

## Decision

### 1. Transport abstraction

BuildingBlocks gets two small interfaces in `SmartSpare.Infrastructure.Messaging`:
- **`IIntegrationEventTransport`:** publish a serialized outbox message.
- **`IIntegrationEventSubscriber`:** start the consumers of a module.

They have two implementations:
- **In-process** (current behaviour), registered by `SmartSpare.Host`.
- **RabbitMQ**, written on the official `RabbitMQ.Client` 7.x, registered by the service hosts. It lives in a separate project, `SmartSpare.Infrastructure.RabbitMq`, so modules never reference the client (architecture test).

The rest of the event path is unchanged in both topologies:
- the outbox write and dispatcher loop;
- the inbox check;
- the consumer handlers and their registration.

No messaging framework is used: MassTransit v9 is commercial, and wrapping the client is about the same size as ADR 0005's handlers. Rebus and Wolverine (both MIT) were considered and rejected because they bring their own outbox and conventions, which would duplicate ours.

### 2. Topology

| Element | Convention |
|---|---|
| Exchange | One durable **topic** exchange per publishing module: `smartspare.<module>.events` (e.g. `smartspare.inventory.events`) |
| Routing key | Event name and version: `TransferApproved.v1` |
| Queue | One durable **quorum** queue per consumer: `<consuming-module>.<EventName>.v<N>` (e.g. `forecasting.TransferApproved.v1`), bound to the publisher's exchange with the routing key. The consumer name equals the inbox `consumer` value (#32). |
| Message | JSON body as stored in `outbox_message.payload`. Properties: `message_id` = outbox id, `type`, `content_type=application/json`, `timestamp`, `persistent` delivery mode, W3C `traceparent` header. |
| Dead-letter | One dead-letter exchange and **one dead-letter queue per consuming module** (`smartspare.<module>.dlx` → `<module>.dead-letter`), configured by a **policy** where the broker allows it (§5). The original queue is kept in the `x-death` header. |

Exchanges, queues and bindings are declared by the consuming service at start-up (idempotent declarations). Policies come from the deployment.

### 3. Delivery guarantees

- **Publish:** the dispatcher publishes with **publisher confirms**. It sets `processed_at` only after the broker confirmed. A nack, a return (unroutable, `mandatory=true`) or a timeout counts as a failed attempt: `attempts` and `error` are updated, and the message is retried by the next dispatcher cycle with back-off and poison handling (#31). This is at-least-once.
- **Consume:** manual acknowledgements and a prefetch per consumer. The handler runs inside the module's transaction together with the inbox insert, and the message is acked after commit. A duplicate (inbox hit) is acked without effect, which makes consumers idempotent through the existing inbox.
- **Retry:** quorum-queue **native delayed retry** with linear back-off (for example 5 s, growing to 2 min), set by policy. After the delivery limit (default 20, set to 10) the message is dead-lettered with reason `delivery_limit`. Dead-lettered messages raise an alert (ADR 0018) and can be re-published from the dead-letter queue after a fix. The fallback without quorum queues is in §5.
- **Ordering:** not guaranteed across messages. Consumers already tolerate reordering through state checks: compensating actions in UC01, and version and status guards in the aggregates.

### 4. Environments

| Environment | RabbitMQ |
|---|---|
| Single host (development, unit and integration tests) | None. In-process transport. |
| Local services topology | Aspire resource `AddRabbitMQ("messaging").WithManagementPlugin().WithDataVolume()`, RabbitMQ 4.4 in a container. This is the self-hosted setup, shown and tested locally. |
| CI topology test | `Testcontainers.RabbitMq` |
| Azure | **CloudAMQP "Little Lemur"**, the free shared RabbitMQ plan (product owner decision, 24 November 2026; [ADR 0009](0009-azure-hosting-and-delivery.md) amendment). AMQPS over the internet with TLS. The connection URL (one user per service where the plan allows, otherwise one) is in Key Vault, read through Key Vault references. |

### 5. Designing within the Little Lemur limits

Limits from the [CloudAMQP plans page](https://www.cloudamqp.com/plans.html), read 24 November 2026: "Message quota/month 1 M", "Connection limit 20", "Max 100 queues", "Max 10 000 queued messages", "Max idle queue time 28 days". The page labels the plan "For Development" and as a "Shared RabbitMQ broker". It doesn't state a channel limit or what happens when a limit is exceeded.

| Limit | Design |
|---|---|
| **20 connections** | **One AMQP connection per service replica**, opened at start-up and reused, with **two channels**: one for publishing (publisher confirms) and one for consuming (manual acks). Services run `maxReplicas: 1` in the dev/demo environment, so 6 connections are used. The gateway doesn't connect. The KEDA scale rule polls queue lengths through the **management HTTP API** (`protocol: http`), which uses no AMQP connection. Budget: ≤ 12 connections, leaving room for a second replica per service or for diagnostics. |
| **100 queues** | Queues are **durable**, never exclusive or temporary. One queue per subscription (consumer × event). Dead-lettering uses **one dead-letter queue per consuming module** (`<module>.dead-letter`, 6 queues), not one per queue, and the dead-letter queues count against the limit. Budget: ≤ 45 subscriptions. That leaves room for one retry queue per subscription if the fallback retry pattern below is needed. A unit test counts the declared queues against the budget. |
| **10,000 queued messages** | Consumers that are scaled to zero are woken by the KEDA scale rule when their queue length is above 0 (30 s polling), so queues drain within a cold start. Alert when the total queued count exceeds 5,000. |
| **1 M messages a month** | The demo volume is hundreds to a few thousand events a day, orders of magnitude below. A counter of published messages per month alerts at 70 % of the quota. |
| **28-day idle queue expiry** | Each service declares its queues and bindings idempotently at start-up, so an expired queue comes back at the next start. Publishing uses `mandatory=true` with confirms: while a queue is missing, the message is returned and stays unprocessed in the outbox, and it is delivered once the consumer has redeclared its queue. The weekly warm-up run (ADR 0009 amendment) also keeps queues from going idle. |
| **Inactive instances** | The plans page summary describes free instances being deleted after two months without traffic (not confirmed verbatim). The weekly warm-up prevents that. |

**Features of the shared broker to verify at set-up (#57).** On a shared broker, the RabbitMQ version, the queue types, and whether users may set policies are chosen by the provider.
- If quorum queues, policies and native delayed retry (RabbitMQ 4.3+) are available, the design of §2–§3 applies as is.
- Otherwise the transport uses **classic durable queues** with the documented TTL + dead-letter-exchange retry cycle, an attempt counter in a message header, and queue arguments instead of policies. The publisher and consumer code is the same; only the declarations differ, behind one option.

**Fallback if a limit is hit:** the paid shared plan "Tough Tiger" ($19/month: 10 M messages, 100 connections, 1,000 queues; same page). Only the connection URL changes.

## Consequences

- The rubric's asynchronous-messaging item is met with a real broker and without a commercial library.
- Business code and the outbox/inbox tables don't change. The in-process path stays for fast tests.
- We own about 300 lines of transport code: publisher with confirms, consumer host, declarations. It is tested against a RabbitMQ container.
- Production depends on a free, shared, development-grade plan with no SLA. If the broker is unavailable, events are delayed but none is lost: they stay in the outbox until confirmed. The limits are designed for (§5), and the paid plan is a configuration change away.
- Running the broker is the provider's job: no VM, disk or backups of our own. The self-hosted broker is still exercised locally (Aspire) and in CI (Testcontainers).
- ADR 0003's statement that the transport becomes Azure Service Bus is replaced by RabbitMQ.

## Alternatives considered for hosting

| Alternative | Why not |
|---|---|
| RabbitMQ on a self-hosted VM with a managed disk | Local-disk semantics as RabbitMQ prefers ([production checklist](https://www.rabbitmq.com/docs/production-checklist)), but rejected for **cost against the €35/month target**: a B2s VM alone is about €31.46/month ([ADR 0009](0009-azure-hosting-and-delivery.md) amendment), plus patching and backups. |
| RabbitMQ in a Container App | Container Apps is designed for stateless workloads and persists data only on Azure Files mounts ([reliability](https://learn.microsoft.com/azure/reliability/reliability-container-apps), [storage mounts](https://learn.microsoft.com/azure/container-apps/storage-mounts)). RabbitMQ accepts network storage only with low, stable latency. |
| Azure Service Bus | Managed, but it isn't RabbitMQ (D2), and topics need the paid Standard tier. |
