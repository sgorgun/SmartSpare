# Parked integration events: inspect and replay

For operators. Modules change each other's state only through integration events in the publishing module's outbox (ADR 0003). If an event can't be delivered (the broker is down, or a consumer keeps failing), the dispatcher retries it with exponential back-off. It waits 5 s, 10 s, 20 s and so on, at most 10 minutes between attempts, with ±20 % jitter. After 10 failed attempts, about half an hour, the event is **parked**: its `exhausted_at` is set and nothing retries it (#31).

A parked event is a cross-module workflow that stopped halfway, for example a transfer that never reached the recommendation. It stays parked until an operator replays it.

## How you notice

- **Health check:** `/health/ready` reports the `outbox` check as **Degraded**, for example "Parked integration events: inventory 1". Degraded still answers 200, so the instance keeps serving.
- **Metric:** `smartspare.outbox.exhausted` (per `schema`) is above 0, and `smartspare.outbox.oldest_pending_age` shows how long the oldest undelivered event has waited (meter `SmartSpare.Messaging`).
- **Log:** a warning "Outbox {schema}: N integration event(s) ran out of delivery attempts".
- **Reconciliation:** `mart.v_data_quality` reports the row "integration events parked in an outbox" with `failed_rows` > 0.

## Inspect

The outboxes are `iam`, `inventory`, `fleet`, `procurement` and `forecast` `.outbox_message`. For one module:

```sql
SELECT id, occurred_at, type, attempts, exhausted_at, error
FROM inventory.outbox_message
WHERE exhausted_at IS NOT NULL AND processed_at IS NULL
ORDER BY occurred_at;
```

`error` holds the last delivery error, and `type` the event. Fix the cause first: start the broker, deploy the corrected consumer, or repair the data the consumer rejected.

## Replay

Make the parked events deliverable again. The dispatcher picks them up within its polling interval (2 s):

```sql
UPDATE inventory.outbox_message
SET attempts = 0, next_attempt_at = NULL, exhausted_at = NULL, error = NULL
WHERE exhausted_at IS NOT NULL AND processed_at IS NULL;   -- or: AND id = '<event id>'
```

Replaying is safe: every consumer records handled events in its inbox under a stable consumer name (#32), so an event that was already handled before it failed elsewhere isn't handled twice.

The statement needs UPDATE on the outbox table, which the capability roles `app_write` and `app_ml` have (their logins, such as `smartspare_api` locally). Replayed events then show up in the trace and logs like any other delivery.
