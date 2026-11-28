-- =============================================================================
-- Inventory 0016 — the brand move (STAGE_7_PLAN 3.2, decision C1, PR 7h1). ref.brand becomes Inventory's catalogue
-- catalog.brand: the table is moved, not copied, so its ids, rows, grants and every foreign key that points at it are
-- kept (parts, brand responsibles; Fleet drops its own in its migration of this PR). It gets the master-data columns
-- (ADR 0025), its triggers and grants; its unique constraints become the unique indexes of the shared mapping.
--
-- Names stay varchar(80): three views select brand.name and PostgreSQL refuses to change the type of a column a view
-- uses. Views follow the table by itself; the two staging functions that name ref.brand in their text are replaced.
-- Fleet and Identity read brands only from their own copies, fed by BrandCatalogChanged.
-- =============================================================================

DO $$
BEGIN
    IF to_regclass('catalog.brand') IS NULL THEN
        ALTER TABLE ref.brand SET SCHEMA catalog;
    END IF;
END $$;

ALTER TABLE catalog.brand
    ADD COLUMN is_archived boolean NOT NULL DEFAULT false,
    ADD COLUMN archived_at timestamp with time zone,
    ADD COLUMN archived_by uuid,
    ADD COLUMN created_at timestamp with time zone NOT NULL DEFAULT now(),
    ADD COLUMN updated_at timestamp with time zone NOT NULL DEFAULT now(),
    ADD CONSTRAINT ck_brand_archived CHECK ((is_archived AND archived_at IS NOT NULL) OR (NOT is_archived AND archived_at IS NULL));

ALTER TABLE catalog.brand DROP CONSTRAINT uq_brand_code, DROP CONSTRAINT uq_brand_name;
CREATE UNIQUE INDEX uq_brand_code ON catalog.brand (code);
CREATE UNIQUE INDEX uq_brand_name ON catalog.brand (name);
CREATE INDEX ix_brand_code_trgm ON catalog.brand USING gin (code gin_trgm_ops);
CREATE INDEX ix_brand_name_trgm ON catalog.brand USING gin (name gin_trgm_ops);

COMMENT ON COLUMN catalog.brand.id IS 'Surrogate primary key.';
COMMENT ON COLUMN catalog.brand.code IS 'Business key, e.g. CLAAS; never changes after creation.';
COMMENT ON COLUMN catalog.brand.name IS 'Display name.';
COMMENT ON COLUMN catalog.brand.is_archived IS 'Archived instead of deleted: kept on history, cannot be chosen (SS500).';
COMMENT ON COLUMN catalog.brand.archived_by IS 'User who archived the record (iam.app_user).';

CREATE TRIGGER trg_brand_updated_at BEFORE UPDATE ON catalog.brand
    FOR EACH ROW EXECUTE FUNCTION util.trg_set_updated_at();
CREATE TRIGGER trg_audit AFTER INSERT OR UPDATE OR DELETE ON catalog.brand
    FOR EACH ROW EXECUTE FUNCTION audit.trg_row_audit();

INSERT INTO audit.history_table (table_name) VALUES ('catalog.brand') ON CONFLICT DO NOTHING;

GRANT SELECT ON catalog.brand TO app_read;
GRANT INSERT, UPDATE ON catalog.brand TO app_write;

-- The staging validation of the parts and demand-history imports, as in 0002 with catalog.brand instead of ref.brand.

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
           r.brand_code, r.category_code, r.uom_code, r.weight_kg AS weight_raw, r.unit_cost AS cost_raw
    FROM raw.part_catalog_row r
    LEFT JOIN catalog.brand b            ON b.code = upper(btrim(r.brand_code))
    LEFT JOIN ref.part_category c    ON c.code = upper(btrim(r.category_code))
    LEFT JOIN ref.unit_of_measure u  ON u.code = upper(btrim(r.uom_code))
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
                            format('Invalid unit cost "%s"', t.cost_raw))
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
                                      uom_id, weight_kg, unit_cost, is_condition_based, is_valid)
    SELECT t.import_batch_id, t.line_no, t.brand_id, left(nullif(t.oem, ''), 40), nullif(t.description, ''),
           t.category_id, t.uom_id,
           CASE WHEN t.weight_kg BETWEEN 0 AND 999999 THEN round(t.weight_kg, 3) END,
           CASE WHEN t.unit_cost BETWEEN 0 AND 9999999999 THEN round(t.unit_cost, 2) END,
           t.condition_based,
           NOT EXISTS (SELECT 1 FROM staging.import_error e WHERE e.import_batch_id = t.import_batch_id AND e.line_no = t.line_no)
    FROM tmp_pc t;

    SELECT count(DISTINCT line_no) INTO v_errors FROM staging.import_error WHERE import_batch_id = p_batch_id;
    RETURN v_errors;
END $$;

CREATE OR REPLACE FUNCTION staging.validate_demand_history(p_batch_id bigint) RETURNS integer
LANGUAGE plpgsql AS $$
DECLARE
    v_errors integer;
BEGIN
    DELETE FROM staging.import_error   WHERE import_batch_id = p_batch_id;
    DELETE FROM staging.demand_history WHERE import_batch_id = p_batch_id;

    DROP TABLE IF EXISTS tmp_dh;
    CREATE TEMP TABLE tmp_dh ON COMMIT DROP AS
    SELECT r.import_batch_id, r.line_no,
           util.try_date(r.doc_date)         AS doc_date,
           w.id                              AS warehouse_id,
           p.id                              AS part_id,
           util.try_numeric(r.quantity)      AS quantity,
           left(nullif(btrim(r.doc_ref), ''), 40) AS doc_ref,
           r.doc_date AS date_raw, r.warehouse_code, r.brand_code, r.oem_part_number, r.quantity AS qty_raw
    FROM raw.demand_history_row r
    LEFT JOIN inventory.warehouse w ON w.code = upper(btrim(r.warehouse_code))
    LEFT JOIN catalog.brand b           ON b.code = upper(btrim(r.brand_code))
    LEFT JOIN catalog.part p        ON p.brand_id = b.id
                                   AND p.oem_part_number = upper(regexp_replace(btrim(r.oem_part_number), '\s+', '', 'g'))
    WHERE r.import_batch_id = p_batch_id;
    ANALYZE tmp_dh;

    INSERT INTO staging.import_error (import_batch_id, line_no, column_name, error_code, message)
    SELECT t.import_batch_id, t.line_no, e.col, e.code, e.msg
    FROM tmp_dh t
    CROSS JOIN LATERAL (VALUES
        ('doc_date', CASE WHEN t.doc_date IS NULL THEN 'FORMAT'
                          WHEN t.doc_date > current_date OR t.doc_date < DATE '2000-01-01' THEN 'RANGE' END,
                     format('Invalid date "%s" (expected YYYY-MM-DD, not in the future)', t.date_raw)),
        ('warehouse_code', CASE WHEN t.warehouse_id IS NULL THEN 'UNKNOWN_REF' END,
                     format('Unknown warehouse "%s"', t.warehouse_code)),
        ('oem_part_number', CASE WHEN t.part_id IS NULL THEN 'UNKNOWN_REF' END,
                     format('Unknown part %s/%s (import the catalogue first)', t.brand_code, t.oem_part_number)),
        ('quantity', CASE WHEN t.quantity IS NULL THEN 'TYPE' WHEN t.quantity <= 0 OR t.quantity > 999999 THEN 'RANGE' END,
                     format('Invalid quantity "%s"', t.qty_raw))
    ) AS e(col, code, msg)
    WHERE e.code IS NOT NULL;

    INSERT INTO staging.demand_history (import_batch_id, line_no, occurred_at, warehouse_id, part_id, quantity, doc_ref, row_hash, is_valid)
    SELECT t.import_batch_id, t.line_no,
           (t.doc_date + time '12:00') AT TIME ZONE 'Europe/Vilnius',
           t.warehouse_id, t.part_id,
           CASE WHEN t.quantity > 0 AND t.quantity <= 999999 THEN round(t.quantity, 3) END,   -- out-of-range stays NULL (error logged)
           t.doc_ref,
           CASE WHEN t.doc_date IS NOT NULL AND t.warehouse_id IS NOT NULL AND t.part_id IS NOT NULL
                     AND t.quantity > 0 AND t.quantity <= 999999
                THEN public.digest(concat_ws('|', t.doc_date, t.warehouse_id, t.part_id, coalesce(t.doc_ref, ''), round(t.quantity, 3)), 'sha256') END,
           NOT EXISTS (SELECT 1 FROM staging.import_error e WHERE e.import_batch_id = t.import_batch_id AND e.line_no = t.line_no)
    FROM tmp_dh t;

    ANALYZE staging.demand_history;

    -- Deduplication: within the file (window function, first line wins) and
    -- against batches loaded earlier (partial index ix_staging_demand_history_hash).
    UPDATE staging.demand_history d SET is_duplicate = true
    FROM (
        SELECT x.line_no
        FROM (SELECT s.line_no, row_number() OVER (PARTITION BY s.row_hash ORDER BY s.line_no) AS rn
              FROM staging.demand_history s
              WHERE s.import_batch_id = p_batch_id AND s.is_valid) x
        WHERE x.rn > 1
        UNION
        SELECT s.line_no
        FROM staging.demand_history s
        WHERE s.import_batch_id = p_batch_id AND s.is_valid
          AND EXISTS (SELECT 1 FROM staging.demand_history d2
                      JOIN raw.import_batch b2 ON b2.id = d2.import_batch_id AND b2.status = 'loaded'
                      WHERE d2.row_hash = s.row_hash AND d2.is_valid AND NOT d2.is_duplicate)
    ) dup
    WHERE d.import_batch_id = p_batch_id AND d.line_no = dup.line_no;

    INSERT INTO staging.import_error (import_batch_id, line_no, column_name, error_code, message)
    SELECT import_batch_id, line_no, 'row', 'DUPLICATE', 'Record already imported (same date, hub, part, document, quantity)'
    FROM staging.demand_history WHERE import_batch_id = p_batch_id AND is_duplicate;

    SELECT count(DISTINCT line_no) INTO v_errors FROM staging.import_error
    WHERE import_batch_id = p_batch_id AND error_code <> 'DUPLICATE';
    RETURN v_errors;
END $$;
