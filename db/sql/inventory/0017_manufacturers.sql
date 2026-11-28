-- =============================================================================
-- Inventory 0017 — manufacturers (STAGE_7_PLAN 3.1, PR 7h3, ADR 0025). catalog.manufacturer is a master-data catalogue
-- (tables, the part's manufacturer_id and the raw and staging columns come from EF); this script adds its triggers,
-- history registration and grants, and teaches the parts import the optional manufacturer_code column:
--   * a code that is unknown or archived is a row error (UNKNOWN_REF), counted by the 5 % gate like every other;
--   * an empty or missing code keeps the part's stored manufacturer, so files without the column change nothing.
-- =============================================================================

CREATE TRIGGER trg_manufacturer_updated_at BEFORE UPDATE ON catalog.manufacturer
    FOR EACH ROW EXECUTE FUNCTION util.trg_set_updated_at();
CREATE TRIGGER trg_audit AFTER INSERT OR UPDATE OR DELETE ON catalog.manufacturer
    FOR EACH ROW EXECUTE FUNCTION audit.trg_row_audit();

INSERT INTO audit.history_table (table_name) VALUES ('catalog.manufacturer') ON CONFLICT DO NOTHING;

GRANT SELECT ON catalog.manufacturer TO app_read;
GRANT INSERT, UPDATE ON catalog.manufacturer TO app_write;

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
    LEFT JOIN catalog.brand b            ON b.code = upper(btrim(r.brand_code))
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
                            format('Unknown or missing brand "%s"', t.brand_code)),
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

CREATE OR REPLACE FUNCTION staging.load_part_catalog(p_batch_id bigint) RETURNS integer
LANGUAGE plpgsql AS $$
DECLARE
    v_rows integer;
BEGIN
    -- Upsert by natural key; only rows whose values actually change are updated.
    INSERT INTO catalog.part AS p (brand_id, oem_part_number, description, category_id, uom_id, weight_kg, unit_cost, is_condition_based, manufacturer_id)
    SELECT s.brand_id, s.oem_part_number, s.description, s.category_id, s.uom_id, s.weight_kg, s.unit_cost, s.is_condition_based, s.manufacturer_id
    FROM staging.part_catalog s
    WHERE s.import_batch_id = p_batch_id AND s.is_valid
    ON CONFLICT (brand_id, oem_part_number) DO UPDATE
        SET description = EXCLUDED.description, category_id = EXCLUDED.category_id, uom_id = EXCLUDED.uom_id,
            weight_kg = EXCLUDED.weight_kg, unit_cost = EXCLUDED.unit_cost, is_condition_based = EXCLUDED.is_condition_based,
            manufacturer_id = coalesce(EXCLUDED.manufacturer_id, p.manufacturer_id)
        WHERE (p.description, p.category_id, p.uom_id, p.weight_kg, p.unit_cost, p.is_condition_based, p.manufacturer_id)
              IS DISTINCT FROM
              (EXCLUDED.description, EXCLUDED.category_id, EXCLUDED.uom_id, EXCLUDED.weight_kg, EXCLUDED.unit_cost, EXCLUDED.is_condition_based,
               coalesce(EXCLUDED.manufacturer_id, p.manufacturer_id));
    GET DIAGNOSTICS v_rows = ROW_COUNT;
    RETURN v_rows;
END $$;
