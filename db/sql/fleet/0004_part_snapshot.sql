-- =============================================================================
-- Fleet 0004 — privileges on the part snapshot (migration AddPartSnapshot, STAGE_6_PLAN section 5.2).
-- =============================================================================

-- fleet.part_snapshot is Fleet's read-only copy of the Inventory part catalogue, written only by the consumer of
-- PartCatalogChanged (MODULE_MAPPING 2.3). The API and the services (app_write) upsert it; the jobs read it. No DELETE:
-- parts are deactivated, never deleted.
GRANT SELECT, INSERT, UPDATE ON fleet.part_snapshot TO app_write;
GRANT SELECT ON fleet.part_snapshot TO app_read, app_ml;
