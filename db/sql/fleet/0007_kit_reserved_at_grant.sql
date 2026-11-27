-- =============================================================================
-- Fleet 0007 — privilege of the kit-reservation consumer on kit_reserved_at (migration ServiceOrderKitReservedAt,
-- STAGE_6_PLAN 6, PR 6h1).
-- =============================================================================

-- The consumer of ServiceKitReserved records when Inventory answered, so a pending kit shows as "reserving".
GRANT UPDATE (kit_reserved_at) ON fleet.service_order TO app_write;
