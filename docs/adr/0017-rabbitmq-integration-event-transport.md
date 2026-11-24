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
| Dead-letter | Per-module dead-letter exchange `smartspare.<module>.dlx` → queue `<queue>.dlq`, configured by a **policy** applied at deployment, not by queue arguments in code |

Exchanges, queues and bindings are declared by the consuming service at start-up (idempotent declarations). Policies come from the deployment.

### 3. Delivery guarantees

- **Publish:** the dispatcher publishes with **publisher confirms**. It sets `processed_at` only after the broker confirmed. A nack, a return (unroutable, `mandatory=true`) or a timeout counts as a failed attempt: `attempts` and `error` are updated, and the message is retried by the next dispatcher cycle with back-off and poison handling (#31). This is at-least-once.
- **Consume:** manual acknowledgements and a prefetch per consumer. The handler runs inside the module's transaction together with the inbox insert, and the message is acked after commit. A duplicate (inbox hit) is acked without effect, which makes consumers idempotent through the existing inbox.
- **Retry:** quorum-queue **native delayed retry** with linear back-off (for example 5 s, growing to 2 min), set by policy. After the delivery limit (default 20, set to 10) the message is dead-lettered with reason `delivery_limit`. Dead-lettered messages raise an alert (ADR 0018) and can be re-published from the DLQ after a fix.
- **Ordering:** not guaranteed across messages. Consumers already tolerate reordering through state checks: compensating actions in UC01, and version and status guards in the aggregates.

### 4. Environments

| Environment | RabbitMQ |
|---|---|
| Single host (development, unit and integration tests) | None. In-process transport. |
| Local services topology | Aspire resource `AddRabbitMQ("messaging").WithManagementPlugin().WithDataVolume()` |
| CI topology test | `Testcontainers.RabbitMq` |
| Azure | RabbitMQ 4.4 in a container on the **stateful host**, a small Linux VM with a managed disk in the Container Apps environment's virtual network, together with the observability backends (ADR 0018). The services reach it over AMQP on the private network. Credentials are per service, stored in Key Vault and read by Key Vault references. The management UI is not exposed publicly. |

**Why not on Container Apps.** Container Apps is designed for stateless workloads and persists data only on Azure Files mounts ([reliability](https://learn.microsoft.com/azure/reliability/reliability-container-apps), [storage mounts](https://learn.microsoft.com/azure/container-apps/storage-mounts)). RabbitMQ asks for local disks and accepts NAS only with low, stable latency. A managed disk on a VM satisfies that.

**Managed alternative.** CloudAMQP: the free "Little Lemur" plan is for development only (shared, no SLA, 1M messages a month); paid plans start at $19 a month ([plans](https://www.cloudamqp.com/plans.html)). It would replace the VM container without code changes, since it is the same AMQP endpoint. Azure has no managed RabbitMQ service.

## Consequences

- The rubric's asynchronous-messaging item is met with a real broker, without a commercial library.
- Business code and the outbox/inbox tables don't change. The in-process path stays for fast tests.
- We own about 300 lines of transport code: publisher with confirms, consumer host, declarations. It is tested against a RabbitMQ container.
- One stateful component to operate (updates, disk, backups of definitions). It shares the VM with the observability stack to keep cost down. A VM outage delays events but loses none, because they stay in the outbox until confirmed.
- ADR 0003's statement that the transport becomes Azure Service Bus is replaced by RabbitMQ.
