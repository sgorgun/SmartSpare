# ADR 0023: Message quota watched by CloudAMQP

- Status: Accepted
- Date: 2026-11-26
- Amends: [ADR 0017](0017-rabbitmq-integration-event-transport.md) §5 (1 M messages a month), [ADR 0018](0018-observability-with-the-grafana-stack.md) §4 (the "Message quota" rule and the monthly message count on the messaging dashboard)

## Context

ADR 0017 §5 and ADR 0018 §4 planned a counter of published messages per month with an alert at 70 % of the CloudAMQP Little Lemur quota of 1 M messages. STAGE_4_PLAN 6.2 expected the broker metrics job to read that monthly count from the RabbitMQ management API.

The broker metrics job of PR 4j showed that the API has no monthly count. `message_stats.publish` of a vhost counts the messages published since the broker last reset its statistics, which happens on a broker restart and is outside our control on a shared plan. A monthly figure would need a baseline stored once a month and reset when the counter drops, that is, a table of state. The job runs from the Reporting image, and Reporting is read-only by design (ARCHITECTURE_DECISIONS §2).

CloudAMQP e-mails the account owner at 70 % and 100 % of the monthly quota ([FAQ](https://www.cloudamqp.com/docs/faq.html)). STAGE_4_PLAN 6.2 already named these e-mails as the fallback.

## Decision

Product owner, 26 November 2026:

1. **The monthly quota is watched by CloudAMQP's e-mails**, not by an alert rule of our own. The "Message quota" rule is removed; the rule set is 7 rules in 3 groups.
2. **The broker metrics job exports the count it really has:** `smartspare_broker_published_messages`, cumulative since the broker's last statistics reset. The messaging dashboard shows it as such. There is no monthly gauge.
3. **Reporting stays read-only:** no table and no migration for a monthly baseline.

The other limits of ADR 0017 §5 are unchanged and keep their rules: dead letters, queued messages > 5,000, and the connection count on the dashboard.

## Consequences

- One alert path less to build and to explain; the quota warning reaches the account owner's mailbox, not the SmartSpare contact point.
- The quota warning doesn't show in Grafana. With the demo volume orders of magnitude below 1 M (ADR 0017 §5), this is accepted.
- If the volume ever approaches the quota, the cumulative gauge still shows the publish rate (`increase()` over a window), and a monthly baseline can be added in a module that owns state.

## Alternatives considered

| Alternative | Why not |
|---|---|
| A monthly baseline in a Reporting table, reset when the counter drops | Gives the read-only Reporting module state and a migration for one gauge. |
| Count published messages in each service's outbox dispatcher | Every service would export a counter that resets when it scales to zero; the sum over a month needs long-range queries on 14-day retention. |
