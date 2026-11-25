# Broker: what the CloudAMQP free plan offers

The production broker is CloudAMQP's free **Little Lemur** plan ([ADR 0017](../adr/0017-rabbitmq-integration-event-transport.md) §4–§5). CloudAMQP documents its limits but not the RabbitMQ features of shared plans, so the transport's needs were checked on a real instance before relying on them ([STAGE_4_PLAN.md](../stage-4/STAGE_4_PLAN.md) 5.4).

## Result (25 November 2026)

Checked with `tools/SmartSpare.BrokerCheck` against a new Little Lemur instance (AMQPS, port 5671). The instance's host and vhost are left out on purpose: on CloudAMQP the vhost is also the user name.

| Check | Result | Detail |
|---|---|---|
| AMQPS connection | yes | connected |
| RabbitMQ version | yes | **4.3.6** |
| Quorum queue with delivery limit and at-least-once dead-lettering (queue arguments) | yes | declared |
| Confirmed publish (`mandatory`) and consume on the quorum queue | yes | confirmed and consumed |
| Native delayed retry arguments (`x-delayed-retry-*`, RabbitMQ 4.3+) | yes | declared |
| Management HTTP API `GET /api/overview` | yes | RabbitMQ 4.3.6, management 4.3.6 |
| Management HTTP API `GET /api/queues/<vhost>/<queue>` (what KEDA's http mode calls) | yes | type quorum |
| User policy `PUT` and `DELETE /api/policies` | yes | created (201) and deleted (204) |

## Consequences for the transport

- **ADR 0017 §2–§3 apply as written.** The classic-queue fallback of ADR 0017 §5 isn't needed: quorum queues, the delivery limit and at-least-once dead-lettering into one dead-letter queue per module all work on the free plan.
- **Delayed retry:** RabbitMQ 4.3.6 supports native delayed retry, and CloudAMQP accepts user policies. So the back-off between redeliveries (ADR 0017 §3: linear, from 5 s up to 2 min) can be set by a policy at deployment, without code. Until then, a failing message is redelivered immediately, up to the delivery limit (10), and then dead-lettered.
- **KEDA queue scaling without AMQP connections** (STAGE_4_PLAN 5.6): the management HTTP API answers the queue endpoint the scaler's http mode calls, with the instance's own credentials. Whether Container Apps' KEDA reaches it over `https://` is checked with the first deployment (stage 10, #85).
- **Version difference:** locally and in CI the broker is RabbitMQ 4.4 (Aspire, Testcontainers). Everything the transport uses exists in both 4.3 and 4.4.

## Limits that still apply

Messages per month, connections, queues, queued messages and idle queue time are as on the plans page; their design is in ADR 0017 §5. CloudAMQP deletes free instances with no traffic for two months, so the weekly warm-up (stage 10) keeps this one alive.

## Run the check again

```
dotnet user-secrets set ConnectionStrings:messaging "amqps://<user>:<password>@<host>/<vhost>" --id smartspare-brokercheck
dotnet run --project tools/SmartSpare.BrokerCheck
```

It prints the table above and removes every queue, exchange and policy it creates (`smartspare-check.*`). A local broker can be checked too: set `Management:Url` to `http://localhost:15672/`.
