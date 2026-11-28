# ADR 0029: Warehouse copies and Fleet hub references

- Status: Accepted; amended by PR 7j1b (see the amendment)
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
