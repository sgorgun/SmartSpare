-- =============================================================================
-- Identity 0015 — the brand copy (STAGE_7_PLAN 3.2, PR 7h1; product owner, 28 November 2026). Brands moved to Inventory
-- (catalog.brand); Identity shows brand codes in the user list and offers the active brands in the user form from
-- iam.brand_snapshot, fed by BrandCatalogChanged with the same ids. Brand scope itself is by id and does not read it.
-- The copy is read-only: iam.apply_brands (SECURITY DEFINER) is its only write path, executable only by the logins
-- that host the Identity module.
-- =============================================================================

GRANT SELECT ON iam.brand_snapshot TO app_read;

CREATE FUNCTION iam.apply_brands(
    p_occurred_at timestamptz, p_ids smallint[], p_codes text[], p_names text[], p_archived boolean[]) RETURNS integer
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_rows integer;
BEGIN
    INSERT INTO iam.brand_snapshot AS s (brand_id, code, name, is_archived, source_occurred_at, updated_at)
    SELECT b.brand_id, b.code, b.name, b.is_archived, p_occurred_at, now()
    FROM unnest(p_ids, p_codes, p_names, p_archived) AS b (brand_id, code, name, is_archived)
    ON CONFLICT (brand_id) DO UPDATE
       SET code = EXCLUDED.code, name = EXCLUDED.name, is_archived = EXCLUDED.is_archived,
           source_occurred_at = EXCLUDED.source_occurred_at, updated_at = now()
     WHERE s.source_occurred_at < EXCLUDED.source_occurred_at;   -- an older or repeated event never overwrites a newer one
    GET DIAGNOSTICS v_rows = ROW_COUNT;
    RETURN v_rows;
END $$;
COMMENT ON FUNCTION iam.apply_brands(timestamptz, smallint[], text[], text[], boolean[]) IS
    'Applies one BrandCatalogChanged to the brand copy, newest event wins; the only write path of iam.brand_snapshot.';

REVOKE EXECUTE ON FUNCTION iam.apply_brands(timestamptz, smallint[], text[], text[], boolean[]) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION iam.apply_brands(timestamptz, smallint[], text[], text[], boolean[]) TO smartspare_api, smartspare_identity;
