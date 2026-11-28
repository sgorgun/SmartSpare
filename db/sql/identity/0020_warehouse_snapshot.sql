-- =============================================================================
-- Identity 0020 — the warehouse copy (ADR 0029, STAGE_7_PLAN 12.2, PR 7j1). Warehouses are an Inventory catalogue;
-- Identity shows hub codes in the user list and offers the active hubs in the user form from iam.warehouse_snapshot,
-- fed by WarehouseCatalogChanged with the same ids. Hub access itself is by id and does not read it.
-- The copy is read-only: iam.apply_warehouses (SECURITY DEFINER) is its only write path, executable only by the logins
-- that host the Identity module.
-- =============================================================================

GRANT SELECT ON iam.warehouse_snapshot TO app_read;

CREATE FUNCTION iam.apply_warehouses(
    p_occurred_at timestamptz, p_ids smallint[], p_codes text[], p_names text[], p_central boolean[], p_archived boolean[])
RETURNS integer
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_rows integer;
BEGIN
    INSERT INTO iam.warehouse_snapshot AS s (warehouse_id, code, name, is_central_hub, is_archived, source_occurred_at, updated_at)
    SELECT w.warehouse_id, w.code, w.name, w.is_central_hub, w.is_archived, p_occurred_at, now()
    FROM unnest(p_ids, p_codes, p_names, p_central, p_archived) AS w (warehouse_id, code, name, is_central_hub, is_archived)
    ON CONFLICT (warehouse_id) DO UPDATE
       SET code = EXCLUDED.code, name = EXCLUDED.name, is_central_hub = EXCLUDED.is_central_hub, is_archived = EXCLUDED.is_archived,
           source_occurred_at = EXCLUDED.source_occurred_at, updated_at = now()
     WHERE s.source_occurred_at < EXCLUDED.source_occurred_at;   -- an older or repeated event never overwrites a newer one
    GET DIAGNOSTICS v_rows = ROW_COUNT;
    RETURN v_rows;
END $$;
COMMENT ON FUNCTION iam.apply_warehouses(timestamptz, smallint[], text[], text[], boolean[], boolean[]) IS
    'Applies one WarehouseCatalogChanged to the warehouse copy, newest event wins; the only write path of iam.warehouse_snapshot.';

REVOKE EXECUTE ON FUNCTION iam.apply_warehouses(timestamptz, smallint[], text[], text[], boolean[], boolean[]) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION iam.apply_warehouses(timestamptz, smallint[], text[], text[], boolean[], boolean[]) TO smartspare_api, smartspare_identity;
