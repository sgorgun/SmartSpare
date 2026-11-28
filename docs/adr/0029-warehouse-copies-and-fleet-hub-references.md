# ADR 0029: Warehouse copies and Fleet hub references

- Status: Accepted; amended by PRs 7j1b and 7j1c (see the amendments)
- Date: 2026-11-28
- Relates to: [STAGE_7_PLAN.md](../stage-7/STAGE_7_PLAN.md) sections 3.1 and 12.2 (PR 7j), [ADR 0003](0003-module-boundaries-and-integration-events.md), [ADR 0024](0024-acting-user-of-integration-events-and-jobs.md), [ADR 0025](0025-generic-master-data-mechanism.md), [ADR 0026](0026-employee-derived-authorization.md)

## Context

Warehouses become an editable Inventory catalogue in stage 7 (PR 7j). Two other modules use them:

- **Fleet** stores hub ids on machines (`home_warehouse_id`) and service orders (`warehouse_id`) and shows hub codes;
- **Identity** shows hub codes in the user list and offers hubs in the user form.

Until now both asked Inventory through `IWarehouseQueries`: in process in the single host, over `/internal/v1` between services. That was enough while the seven hubs were static reference data. It is not enough once hubs can be renamed and archived.

The product owner's requirements for 7j (28 November 2026) add two needs that cross the module boundary:

1. **Archiving a hub is refused** (SS501 with the reasons, in the API and in the database) while Fleet has open service orders at it or machines based there. Inventory's database cannot read Fleet's schema, and Inventory's API cannot either (ADR 0003). A synchronous call to Fleet would make an Inventory write depend on Fleet's availability and break the rule that Contracts queries serve reads during a user request only.
2. **Fleet's and Identity's warehouse copies are read-only and event-fed**, the same pattern as brands (PR 7h1).

Option (b), checking the two Fleet conditions in the API only, was rejected by the product owner for the reason above: the API has no legitimate way to read them either.

## Decision

1. **Warehouse copies in Fleet and Identity.** `fleet.warehouse_snapshot` and `iam.warehouse_snapshot` (id, code, name, central hub flag, archived flag) keep Inventory's ids.
   - They are fed by the new event `WarehouseCatalogChanged` from Inventory, newest event wins per row.
   - No login may write them. `fleet.apply_warehouses` / `iam.apply_warehouses` (SECURITY DEFINER) is the only write path, executable only by the logins that host the module.
   - The job `inventory.warehouse-catalog-publish` fills them the first time and repairs them.
   - Fleet and Identity read hub codes and the archived flag from their copy. `IWarehouseQueries`, its HTTP client and its internal endpoint are removed (amendment).
2. **Fleet hub references in Inventory.** `inventory.fleet_hub_reference` holds one row per Fleet record that holds a hub: `(record_kind, record_id) → warehouse_id, is_open`.
   - `record_kind` is `service_order` (open = draft, parts reserved or in progress) or `machine` (open = active).
   - It is fed by the new event `HubReferencesChanged` from Fleet: a state transfer of the records a write touched, newest event wins per record.
   - Fleet writes it in the same transaction as the change: a drafted, completed or cancelled service order, and from PR 7l a created, moved or retired machine.
   - `inventory.apply_fleet_hub_references` (SECURITY DEFINER) is its only write path. The job `fleet.hub-reference-publish` sends every machine and service order for the first fill and for repair.
   - **Per record, not per-hub counts.** A count computed inside a Fleet transaction cannot see a concurrent uncommitted order at the same hub, so the newer of two events could carry a lower count. A record's own state has a single writer, so the newest event per record is always right, and redelivery is harmless.
3. **The warehouse archive guard reads the projection** (PR 7j2). The API guard and `inventory`'s database trigger both ask `inventory.fleet_hub_reference` for open rows at the hub, so the two refuse for the same reasons.
4. **Fleet refuses an archived hub** (SS500, in the API and in a Fleet trigger, reading `fleet.warehouse_snapshot`):
   - a new service order at an archived hub;
   - a machine created with, or moved to, an archived home hub.

   A hub with no row in the copy is not known to be archived and is allowed, so a database upgraded before the publish job ran keeps working.

   **A reading is never lost** (amendment). An engine-hours reading that makes a service due at an archived home hub is stored, and no order is drafted; the API answers with the warning `Fleet.HubArchived`, and the due list flags the machine "hub archived".
5. **Accepted limitation: the event-lag window.** Each side checks its own copy of the other side's facts, so a hub archive and a Fleet write that happen within the delivery time of the events can both succeed.
   - **Example:** Inventory archives hub X while no open record is known at it; at the same moment Fleet drafts a service order at X, whose copy does not yet say X is archived.
   - **Window:** the time from one side's commit until the other side applies the event. Over RabbitMQ that is milliseconds; in the single host one outbox dispatcher cycle. It grows while the consuming service is down or scaled to zero, because the outbox keeps the event until it is delivered.
   - **Effect:** an archived hub with an open service order or an active machine. No data is lost: the hub can be restored, and the order can be cancelled or completed.
   - **Detection:** PR 7j2 adds a view of archived hubs that still have open Fleet references, so the case is visible instead of silent.
   - Closing the window would need a distributed lock or a synchronous cross-module check in both directions, which ADR 0003 rules out. For seven hubs edited a few times a year, the window is accepted.

## Consequences

- Two new integration events, two new consumers in Fleet and Identity and one in Inventory, each with its own quorum queue (three queues, within the CloudAMQP budget of ADR 0017 §5).
- Two new publish jobs, which run once after the deployment that introduces them (stage 10, #85) and whenever a copy needs repair.
- Hub renames reach Fleet and Identity with event latency instead of a 60-second cache, which replaces "`IWarehouseQueries` caches expire after 60 s" in STAGE_7_PLAN 3.1.
- Inventory's database checks every archive condition itself, including the two that Fleet owns.

## Amendment (PR 7j1b, product owner, 28 November 2026)

1. **A reading is never lost.** An engine-hours reading is a field fact; only the reading rules SS100–SS104 may reject it. Decision 4 first let the service order trigger refuse a reading that would draft an order at an archived hub, which refused the reading too. Now:
   - `fleet.log_engine_hours` stores the reading and drafts nothing when the copy marks the machine's home hub archived;
   - `POST /machines/{id}/readings` answers 201 with the warning `Fleet.HubArchived` when a service is due;
   - the due list flags the machine (`hubArchived`) and offers no new order there.

   The service order trigger stays and still refuses any other new order at an archived hub.
2. **`IWarehouseQueries` is removed** with its HTTP client and its internal endpoint `/internal/v1/inventory/warehouses`. No production path used them after decision 1, and no stage 8 reader needs them: Forecasting can keep its own copy from `WarehouseCatalogChanged` if it ever needs hubs. The tests of the cross-service read pattern moved to `HttpEmployeeDirectory`, the read Inventory makes to Identity.

## Amendment 2 (PR 7j1c, product owner, 28 November 2026): no synchronous read between modules

**Rule.** A module that needs another module's data keeps a read-only copy fed by integration events, written only by a SECURITY DEFINER apply function (newest event wins) and filled the first time by a publish job. Adding a synchronous read between modules needs a change to this ADR.

1. **The employee directory becomes a copy.** Inventory called Identity synchronously (`IEmployeeDirectory` over `/internal/v1/identity/employee-lookups` and `/account-owners`) for three things. None of them needs a strongly consistent answer:

   | Use | Why a copy is enough |
   |---|---|
   | Assigning responsibles: are the added employees active? | Identity grants no right to an employee who left (`EffectiveRightsCalculator`), so a copy that still says "active" for a moment cannot give anyone rights. Identity stays the single source of rights (ADR 0026). |
   | No self-granting: which employee owns the caller's account? | The account-to-employee link is required, unique and never changes. |
   | Names in the responsibles list and in record history | Display only. |

   Under scale-to-zero the synchronous call also made every assignment and history view wait for, or fail with, the Identity service (a 503 and up to the 30 s cold-start target). Now:
   - Inventory keeps `inventory.employee_snapshot` (employee id, code, display name, active, account id), fed by the new `EmployeeDirectoryChanged`, which Identity writes with every employee change: create, edit, leave, restore, account creation and anonymisation. It is written only by `inventory.apply_employees`, and the job `identity.employee-publish` fills it the first time.
   - The no-self-granting rule of ADR 0028 is now checked in the database too, by a row trigger on the rights-bearing responsibles that reads the copy (SS301). It closes an "API checked, database did not" gap.
   - `IEmployeeDirectory`, `HttpEmployeeDirectory` and both internal endpoints are removed. Fleet and Procurement get their own copy when their catalogues need history names (7k, 7l), never a synchronous call.

2. **Every remaining synchronous call between services.** All of them are gateway → Identity, the authentication path of ADR 0016; no module calls another module.

   | Call | When | Timeout and failure | Cold start |
   |---|---|---|---|
   | Credential check (`ICredentialVerifier.VerifyAsync`, `/internal/v1/identity/credential-checks`) | Sign-in | The standard resilience handler of every HTTP client (`AddStandardResilienceHandler` in ServiceDefaults: retries, attempt and total timeouts); a transport failure or timeout is 503, never "wrong password" | The first sign-in after idle waits for Identity to start (the cold-start target of #85) |
   | Session check (`ValidateSessionAsync`, `/session-checks`) | At most every 30 s per session (ADR 0026) | Same handler; when Identity cannot answer, that request gets 503 (`DependencyUnavailableException`) and the session is kept and checked again on the next request: never signed out by mistake | Identity stays warm while anyone is signed in |
   | Role permissions (`IRolePermissions`, `/role-permissions`) | When the gateway builds a user's permissions; the map is cached and aged on the registered clock | Same handler; failure is 503 | As above |

   The sign-in itself cannot be asynchronous, and the gateway is not a module. Out of scope: the CloudAMQP management API of the broker metrics job (an external service), and the browser composing a screen from several services through the gateway (no service-to-service call).

3. **Personal data in event payloads.** `EmployeeDirectoryChanged` carries an employee's display name. After an anonymisation the name could remain in these places:

   | Store | Holds the payload? | How long | Who can read it |
   |---|---|---|---|
   | `iam.outbox_message.payload` (Identity, the publisher) | yes | until delivered, then 7 days (`MessageRetention:OutboxDays`) | the Identity logins and the migrator; no application role of another module |
   | `<module>.inbox_message` (the consumers) | no: only the message id, the consumer name and the time | 30 days | — |
   | RabbitMQ queues | yes, while in flight | seconds; a dead-lettered message stays in the module's dead-letter queue until it is handled (ADR 0017), which raises an alert | the service's broker user |
   | `inventory.employee_snapshot` | the current name | as long as the employee exists; anonymisation replaces it with "Anonymised Employee" | the Inventory logins |
   | Logs and traces | no: payloads are not logged, and telemetry is filtered (ADR 0018) | — | — |

   **Purge on anonymisation (cheap and safe, so done).** `iam.anonymise_employee` now also redacts the employee in every `EmployeeDirectoryChanged` row of `iam.outbox_message`, delivered or pending: the display name becomes "Anonymised Employee" and the code stays. This is one UPDATE on the publisher's own table, in the same transaction:
   - a delivered row stays for tracing;
   - a pending row is delivered redacted, which is harmless, because the anonymisation's own event is newer and wins in the copy anyway.

   What remains is a message that is in flight at that moment or sits in a dead-letter queue. This is accepted: the time is bounded (seconds, or until the dead-letter alert is handled), and only the service's own broker user can read it.
