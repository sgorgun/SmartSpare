-- =============================================================================
-- Fleet 0008 — privilege of the kit-issue consumer (migration ServiceKitIssueGrants, STAGE_6_PLAN 5.4, PR 6h2).
-- =============================================================================

-- The consumer of ServiceKitIssued stores the issued quantity per line and clears the held copy.
GRANT UPDATE (qty_issued) ON fleet.service_order_line TO app_write;
