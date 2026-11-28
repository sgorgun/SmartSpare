-- =============================================================================
-- Inventory 0020 — parts on the shared catalogue mechanism (STAGE_7_PLAN 2.1 and 3.1, PR 7i1). Migration
-- PartsAsCatalogue adds the archive columns (EF); this script:
--   * backfills them from is_active (an inactive part is archived as of its last change); is_active itself is dropped
--     by migration DropPartIsActive after Reporting's data-quality view stopped reading it (one meaning, plan 2.1);
--   * refuses an archived brand or manufacturer on a new part or a changed reference (SS500), the database side of the
--     active-entry rule (plan 4.1);
--   * refuses to archive a part that still has stock on hand or reserved at any hub (SS501), the stock-protecting half
--     of the archive guard; the API checks open transfers and kit holds too and lists every reason;
--   * the parts import treats an archived brand like an unknown one (UNKNOWN_REF), so a file cannot add parts to it.
-- =============================================================================

UPDATE catalog.part SET is_archived = true, archived_at = updated_at WHERE NOT is_active;

CREATE FUNCTION catalog.trg_part_catalogue_guard() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' OR NEW.brand_id IS DISTINCT FROM OLD.brand_id THEN
        IF EXISTS (SELECT 1 FROM catalog.brand b WHERE b.id = NEW.brand_id AND b.is_archived) THEN
            RAISE EXCEPTION 'Brand % is archived and cannot be chosen', NEW.brand_id USING ERRCODE = 'SS500';
        END IF;
    END IF;

    IF NEW.manufacturer_id IS NOT NULL AND (TG_OP = 'INSERT' OR NEW.manufacturer_id IS DISTINCT FROM OLD.manufacturer_id) THEN
        IF EXISTS (SELECT 1 FROM catalog.manufacturer m WHERE m.id = NEW.manufacturer_id AND m.is_archived) THEN
            RAISE EXCEPTION 'Manufacturer % is archived and cannot be chosen', NEW.manufacturer_id USING ERRCODE = 'SS500';
        END IF;
    END IF;

    IF TG_OP = 'UPDATE' AND NEW.is_archived AND NOT OLD.is_archived THEN
        IF EXISTS (SELECT 1 FROM inventory.stock_balance s WHERE s.part_id = NEW.id AND (s.on_hand_qty > 0 OR s.reserved_qty > 0)) THEN
            RAISE EXCEPTION 'Part % still has stock on hand or reserved', NEW.id USING ERRCODE = 'SS501';
        END IF;
    END IF;

    RETURN NEW;
END $$;
COMMENT ON FUNCTION catalog.trg_part_catalogue_guard() IS
    'Parts: an archived brand or manufacturer cannot be chosen (SS500); a part with stock on hand or reserved cannot be archived (SS501).';

CREATE TRIGGER trg_part_catalogue_guard BEFORE INSERT OR UPDATE OF brand_id, manufacturer_id, is_archived ON catalog.part
    FOR EACH ROW EXECUTE FUNCTION catalog.trg_part_catalogue_guard();

-- app_write already has INSERT and UPDATE on catalog.part (inventory/0004_grants.sql).

CREATE OR REPLACE FUNCTION staging.validate_part_catalog(p_batch_id bigint) RETURNS integer
LANGUAGE plpgsql AS $$
DECLARE
    v_errors integer;
BEGIN
    DELETE FROM staging.import_error WHERE import_batch_id = p_batch_id;
    DELETE FROM staging.part_catalog WHERE import_batch_id = p_batch_id;

    -- typed + normalised projection of the raw rows
    DROP TABLE IF EXISTS tmp_pc;
    CREATE TEMP TABLE tmp_pc ON COMMIT DROP AS
    SELECT r.import_batch_id, r.line_no,
           b.id                                                    AS brand_id,
           upper(regexp_replace(btrim(r.oem_part_number), '\s+', '', 'g')) AS oem,
           left(regexp_replace(btrim(r.description), '\s+', ' ', 'g'), 300) AS description,
           c.id                                                    AS category_id,
           u.id                                                    AS uom_id,
           util.try_numeric(r.weight_kg)                           AS weight_kg,
           util.try_numeric(r.unit_cost)                           AS unit_cost,
           lower(btrim(coalesce(r.condition_based, ''))) IN ('1', 'true', 'yes', 'y') AS condition_based,
           r.brand_code, r.category_code, r.uom_code, r.weight_kg AS weight_raw, r.unit_cost AS cost_raw,
           nullif(btrim(r.manufacturer_code), '')                  AS manufacturer_code,
           m.id                                                    AS manufacturer_id
    FROM raw.part_catalog_row r
    LEFT JOIN catalog.brand b            ON b.code = upper(btrim(r.brand_code)) AND NOT b.is_archived
    LEFT JOIN ref.part_category c    ON c.code = upper(btrim(r.category_code))
    LEFT JOIN ref.unit_of_measure u  ON u.code = upper(btrim(r.uom_code))
    LEFT JOIN catalog.manufacturer m ON m.code = upper(btrim(r.manufacturer_code)) AND NOT m.is_archived
    WHERE r.import_batch_id = p_batch_id;
    ANALYZE tmp_pc;

    INSERT INTO staging.import_error (import_batch_id, line_no, column_name, error_code, message)
    SELECT t.import_batch_id, t.line_no, e.col, e.code, e.msg
    FROM tmp_pc t
    CROSS JOIN LATERAL (VALUES
        ('brand_code',      CASE WHEN nullif(btrim(t.brand_code), '') IS NULL THEN 'REQUIRED' WHEN t.brand_id IS NULL THEN 'UNKNOWN_REF' END,
                            format('Unknown, archived or missing brand "%s"', t.brand_code)),
        ('oem_part_number', CASE WHEN nullif(t.oem, '') IS NULL THEN 'REQUIRED' WHEN t.oem !~ '^[A-Z0-9][A-Z0-9.\-/]{2,39}$' THEN 'FORMAT' END,
                            format('Invalid OEM part number "%s"', t.oem)),
        ('description',     CASE WHEN length(coalesce(t.description, '')) < 3 THEN 'REQUIRED' END,
                            'Description is missing or shorter than 3 characters'),
        ('category_code',   CASE WHEN t.category_id IS NULL THEN 'UNKNOWN_REF' END,
                            format('Unknown category "%s"', t.category_code)),
        ('uom_code',        CASE WHEN t.uom_id IS NULL THEN 'UNKNOWN_REF' END,
                            format('Unknown unit of measure "%s"', t.uom_code)),
        ('weight_kg',       CASE WHEN nullif(btrim(t.weight_raw), '') IS NOT NULL AND t.weight_kg IS NULL THEN 'TYPE'
                                 WHEN t.weight_kg < 0 OR t.weight_kg > 999999 THEN 'RANGE' END,
                            format('Invalid weight "%s"', t.weight_raw)),
        ('unit_cost',       CASE WHEN nullif(btrim(t.cost_raw), '') IS NULL THEN 'REQUIRED'
                                 WHEN t.unit_cost IS NULL THEN 'TYPE'
                                 WHEN t.unit_cost < 0 OR t.unit_cost > 9999999999 THEN 'RANGE' END,
                            format('Invalid unit cost "%s"', t.cost_raw)),
        ('manufacturer_code', CASE WHEN t.manufacturer_code IS NOT NULL AND t.manufacturer_id IS NULL THEN 'UNKNOWN_REF' END,
                            format('Unknown or archived manufacturer "%s"', t.manufacturer_code))
    ) AS e(col, code, msg)
    WHERE e.code IS NOT NULL
    UNION ALL
    -- duplicates inside the file: the first occurrence wins
    SELECT d.import_batch_id, d.line_no, 'oem_part_number', 'DUPLICATE', format('Duplicate of line %s', d.first_line)
    FROM (SELECT t.import_batch_id, t.line_no, t.brand_id, t.oem,
                 min(t.line_no) OVER (PARTITION BY t.brand_id, t.oem) AS first_line
          FROM tmp_pc t) d
    WHERE d.brand_id IS NOT NULL AND d.oem <> '' AND d.line_no > d.first_line;

    INSERT INTO staging.part_catalog (import_batch_id, line_no, brand_id, oem_part_number, description, category_id,
                                      uom_id, weight_kg, unit_cost, is_condition_based, is_valid, manufacturer_id)
    SELECT t.import_batch_id, t.line_no, t.brand_id, left(nullif(t.oem, ''), 40), nullif(t.description, ''),
           t.category_id, t.uom_id,
           CASE WHEN t.weight_kg BETWEEN 0 AND 999999 THEN round(t.weight_kg, 3) END,
           CASE WHEN t.unit_cost BETWEEN 0 AND 9999999999 THEN round(t.unit_cost, 2) END,
           t.condition_based,
           NOT EXISTS (SELECT 1 FROM staging.import_error e WHERE e.import_batch_id = t.import_batch_id AND e.line_no = t.line_no),
           t.manufacturer_id
    FROM tmp_pc t;

    SELECT count(DISTINCT line_no) INTO v_errors FROM staging.import_error WHERE import_batch_id = p_batch_id;
    RETURN v_errors;
END $$;
