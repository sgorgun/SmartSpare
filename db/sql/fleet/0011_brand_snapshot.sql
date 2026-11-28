-- =============================================================================
-- Fleet 0011 — the brand copy (STAGE_7_PLAN 3.2, PR 7h1). Brands moved to Inventory (catalog.brand); Fleet keeps
-- fleet.brand_snapshot, fed by BrandCatalogChanged with the same ids, and no longer has a foreign key to the brand table.
-- The machine card view reads the copy; a left join keeps every machine on the card while a brand is not yet copied.
-- The copy is read-only: no login may write the table; fleet.apply_brands (SECURITY DEFINER), which the consumer calls,
-- is its only write path, executable only by the logins that host the Fleet module (as iam.apply_responsibilities).
-- =============================================================================

GRANT SELECT ON fleet.brand_snapshot TO app_read, app_ml;

CREATE FUNCTION fleet.apply_brands(
    p_occurred_at timestamptz, p_ids smallint[], p_codes text[], p_names text[], p_archived boolean[]) RETURNS integer
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_rows integer;
BEGIN
    INSERT INTO fleet.brand_snapshot AS s (brand_id, code, name, is_archived, source_occurred_at, updated_at)
    SELECT b.brand_id, b.code, b.name, b.is_archived, p_occurred_at, now()
    FROM unnest(p_ids, p_codes, p_names, p_archived) AS b (brand_id, code, name, is_archived)
    ON CONFLICT (brand_id) DO UPDATE
       SET code = EXCLUDED.code, name = EXCLUDED.name, is_archived = EXCLUDED.is_archived,
           source_occurred_at = EXCLUDED.source_occurred_at, updated_at = now()
     WHERE s.source_occurred_at < EXCLUDED.source_occurred_at;   -- an older or repeated event never overwrites a newer one
    GET DIAGNOSTICS v_rows = ROW_COUNT;
    RETURN v_rows;
END $$;
COMMENT ON FUNCTION fleet.apply_brands(timestamptz, smallint[], text[], text[], boolean[]) IS
    'Applies one BrandCatalogChanged to the brand copy, newest event wins; the only write path of fleet.brand_snapshot.';

REVOKE EXECUTE ON FUNCTION fleet.apply_brands(timestamptz, smallint[], text[], text[], boolean[]) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION fleet.apply_brands(timestamptz, smallint[], text[], text[], boolean[]) TO smartspare_api, smartspare_fleet;

CREATE OR REPLACE VIEW fleet.v_machine_maintenance_status AS
SELECT m.id                      AS machine_id,
       m.vin,
       b.name                    AS brand_name,
       mm.model_name,
       mt.code                   AS machine_type_code,
       c.customer_code,
       m.home_warehouse_id,
       oc.code                   AS operating_condition_code,
       m.current_engine_hours,
       m.engine_hours_read_at,
       nd.plan_name              AS next_service,
       nd.due_engine_hours       AS next_service_due_hours,
       nd.remaining_hours        AS next_service_remaining_hours,
       coalesce(nd.is_overdue, false) AS is_overdue,
       (SELECT count(*) FROM fleet.service_order so
        WHERE so.machine_id = m.id AND so.status IN ('draft', 'parts_reserved', 'in_progress')) AS open_service_orders
FROM fleet.machine m
JOIN fleet.machine_model mm      ON mm.id = m.model_id
LEFT JOIN fleet.brand_snapshot b  ON b.brand_id = mm.brand_id
JOIN ref.machine_type mt         ON mt.id = mm.machine_type_id
JOIN fleet.customer c            ON c.id = m.customer_id
JOIN ref.operating_condition oc  ON oc.id = m.operating_condition_id
LEFT JOIN LATERAL (SELECT * FROM fleet.fn_maintenance_due(m.id, 1000000) LIMIT 1) nd ON true
WHERE m.status = 'active';
