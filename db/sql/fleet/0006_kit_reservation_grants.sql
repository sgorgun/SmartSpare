-- =============================================================================
-- Fleet 0006 — privileges of the kit-reservation consumer (migration KitReservationGrants, STAGE_6_PLAN 5.3, #12).
-- =============================================================================

-- The consumer of ServiceKitReserved stores Fleet's copy of the held quantity and moves a fully reserved draft to
-- parts_reserved through EF; the status guard trigger keeps the state machine.
GRANT UPDATE (qty_reserved) ON fleet.service_order_line TO app_write;
GRANT UPDATE (status) ON fleet.service_order TO app_write;
