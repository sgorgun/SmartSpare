# ADR 0024: The acting user of integration events and background jobs

- Status: Accepted
- Date: 2026-11-27
- Amends: [ADR 0003](0003-module-boundaries-and-integration-events.md), [ADR 0017](0017-rabbitmq-integration-event-transport.md)
- Relates to: [ADR 0016](0016-authentication-behind-the-gateway.md), MODULE_MAPPING 3.6 (trust model of the session context)

## Context

Every database transaction of a module starts with a session context: `SessionContextInterceptor` writes `app.user_id`, `app.reason`, `app.user_roles` and `app.home_warehouse_id` from the signed-in user's claims (MODULE_MAPPING 3.6). The audit trigger and the actor columns (`created_by`, `recorded_by`, …) read `app.user_id`; `util.require_role` reads `app.user_roles`.

Two kinds of work run outside a user's request:

- **Integration event consumers** (ADR 0003, in process or over RabbitMQ, ADR 0017). Stage 6 brought the first consumers that write business data on behalf of a person: Inventory reserves a service kit when Fleet drafts an order (`ServiceOrderDrafted`), and Fleet applies the answer (`ServiceKitReserved`). Without a user, the holds and the audit rows would name nobody.
- **Background jobs** (`IJob`, run by `JobRunner` with `--job <name>`; a Quartz scheduler in the single host follows with #30). The first that changes business data is the expiry of service-kit holds (`inventory.reservation-expiry`, stage 6).

The question is whose identity this work carries, and whether a message can give a consumer more rights than the code that published it.

## Decision

1. **Events carry the acting user for attribution only.** An integration event caused by a person implements `IActedByUser` (SharedKernel) with `ActorId`, the user who acted. `IntegrationEventDelivery` sets `ActingUser` to that id with the reason `event:<EventName>` **before** the consumer's transaction starts, so the session context, the audit trail and the actor columns name that person.
2. **Authorization happens at publish time, never from the message.** The user's permissions were checked when the user acted: the endpoint's policy and the SQL function's `util.require_role` in the publishing module. An acting user has **no roles and no home warehouse** in the session (`SessionContextInterceptor`), so a consumer can never pass `util.require_role` or a warehouse check because of what a message says. The SQL a consumer calls checks at most that a session user exists (`inventory.reserve_service_kit` uses `util.require_app_user`), and it is granted only to the module's own roles.
3. **Background jobs act as a named system user.** `JobRunner` sets `ActingUser` to `SystemUser.Id` (`00000000-0000-4000-8000-00000000f001`, shown as "System") with the reason `job:<name>`, without roles. The id is well known and not a row in `iam.app_user`: actor columns carry no foreign keys (MODULE_MAPPING 2.1). The scheduler of #30 must run jobs through the same entry point.
4. **Events that are not caused by a person** (an expiry, a mart refresh) do not implement `IActedByUser`; their consumers run as the session they have (no user, or the job's system user when a job publishes and consumes in one process).

## Verification (code as of stage 6, PR 6g4)

| Rule | Where | Test |
|---|---|---|
| Consumers act as `ActorId` | `IntegrationEventDelivery.DeliverAsync` | `KitReservationFlowTests` (holds `created_by` = the engineer who logged the reading) |
| Acting user has no roles or warehouse | `SessionContextInterceptor.CreateCommand` | `SessionContextTests.An_acting_user_during_sign_in_has_no_roles` (a request with Administrator role, acting user without roles) |
| Consumer SQL needs a user, not a role | `db/sql/inventory/0007_service_kit_reservation.sql` | T02 kit assertions; `PolicySqlRoleParityTests` (no role check, so no policy) |
| Jobs act as the system user | `JobRunner.RunAsync` | `JobRunnerTests.A_job_acts_as_the_named_system_user_without_roles`; `KitExpiryTests` (audit rows of expired holds: `user_id` = `SystemUser.Id`, reason `job:inventory.reservation-expiry`) |

Before this ADR the expiry job ran without any user (`app.user_id` empty); PR "event attribution" added rule 3 to `JobRunner`. The consumers already followed rules 1 and 2.

## Consequences

- The audit trail answers "who" for every change, including the asynchronous halves of a workflow; "why" is in `reason` (`event:ServiceOrderDrafted`, `job:inventory.reservation-expiry`).
- A forged or replayed message can at most attribute work to the wrong person, never widen what the consumer may do. The inbox deduplicates by message id (ADR 0021), and RabbitMQ is reachable only from inside the environment.
- Consumers whose work needs a decision with a permission check (for example accepting a recommendation on someone's behalf, stage 8) must carry the decision itself in the event, checked at publish time, not re-check roles from the message.
- Stage 10's per-module database roles (MODULE_MAPPING 4.2) further limit what any consumer or job can touch, whatever its session says.
