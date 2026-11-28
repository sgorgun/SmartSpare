-- =============================================================================
-- Inventory 0025 — Fleet's hub references (ADR 0029, STAGE_7_PLAN 12.2, PR 7j1).
-- The warehouse archive guard (PR 7j2) refuses while Fleet has open service orders at a hub or machines based there.
-- Inventory cannot read Fleet's schema, so it keeps inventory.fleet_hub_reference: one row per Fleet record that holds a
-- hub, fed by HubReferencesChanged (the record's current state, newest event wins per record) and filled the first time
-- by fleet.hub-reference-publish. The API guard and the database trigger both read it.
-- The projection is read-only: inventory.apply_fleet_hub_references (SECURITY DEFINER), which the consumer calls, is its
-- only write path, executable only by the logins that host the Inventory module.
-- =============================================================================

GRANT SELECT ON inventory.fleet_hub_reference TO app_read, app_ml;

CREATE FUNCTION inventory.apply_fleet_hub_references(
    p_occurred_at timestamptz, p_kinds text[], p_ids bigint[], p_warehouses smallint[], p_open boolean[]) RETURNS integer
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_rows integer;
BEGIN
    INSERT INTO inventory.fleet_hub_reference AS r (record_kind, record_id, warehouse_id, is_open, source_occurred_at, updated_at)
    SELECT x.record_kind, x.record_id, x.warehouse_id, x.is_open, p_occurred_at, now()
    FROM unnest(p_kinds, p_ids, p_warehouses, p_open) AS x (record_kind, record_id, warehouse_id, is_open)
    ON CONFLICT (record_kind, record_id) DO UPDATE
       SET warehouse_id = EXCLUDED.warehouse_id, is_open = EXCLUDED.is_open,
           source_occurred_at = EXCLUDED.source_occurred_at, updated_at = now()
     WHERE r.source_occurred_at < EXCLUDED.source_occurred_at;   -- an older or repeated event never overwrites a newer one
    GET DIAGNOSTICS v_rows = ROW_COUNT;
    RETURN v_rows;
END $$;
COMMENT ON FUNCTION inventory.apply_fleet_hub_references(timestamptz, text[], bigint[], smallint[], boolean[]) IS
    'Applies one HubReferencesChanged to the projection, newest event wins per record; the only write path of inventory.fleet_hub_reference.';

REVOKE EXECUTE ON FUNCTION inventory.apply_fleet_hub_references(timestamptz, text[], bigint[], smallint[], boolean[]) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION inventory.apply_fleet_hub_references(timestamptz, text[], bigint[], smallint[], boolean[]) TO smartspare_api, smartspare_inventory;
