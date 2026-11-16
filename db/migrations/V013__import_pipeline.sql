-- =============================================================================
-- V013 — Import pipeline raw -> staging -> core (reproducible, no manual steps)
--   1. API stores the file metadata (raw.import_batch, SHA-256) and the rows as
--      text (raw.*_row) using COPY / Npgsql binary import.
--   2. staging.process_batch(batch) validates, types, normalises, de-duplicates
--      and — if the error ratio is acceptable — loads the core tables.
--   All in one transaction: a batch is loaded completely or not at all.
-- =============================================================================

-- ---------------------------------------------------------------------------
-- Part catalogue
-- ---------------------------------------------------------------------------
CREATE FUNCTION staging.validate_part_catalog(p_batch_id bigint) RETURNS integer
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
    LEFT JOIN ref.brand b            ON b.code = upper(btrim(r.brand_code))
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

CREATE FUNCTION staging.load_part_catalog(p_batch_id bigint) RETURNS integer
LANGUAGE plpgsql AS $$
DECLARE
    v_rows integer;
BEGIN
    -- Upsert by natural key; only rows whose values actually change are updated.
    INSERT INTO catalog.part AS p (brand_id, oem_part_number, description, category_id, uom_id, weight_kg, unit_cost, is_condition_based)
    SELECT s.brand_id, s.oem_part_number, s.description, s.category_id, s.uom_id, s.weight_kg, s.unit_cost, s.is_condition_based
    FROM staging.part_catalog s
    WHERE s.import_batch_id = p_batch_id AND s.is_valid
    ON CONFLICT (brand_id, oem_part_number) DO UPDATE
        SET description = EXCLUDED.description, category_id = EXCLUDED.category_id, uom_id = EXCLUDED.uom_id,
            weight_kg = EXCLUDED.weight_kg, unit_cost = EXCLUDED.unit_cost, is_condition_based = EXCLUDED.is_condition_based
        WHERE (p.description, p.category_id, p.uom_id, p.weight_kg, p.unit_cost, p.is_condition_based)
              IS DISTINCT FROM
              (EXCLUDED.description, EXCLUDED.category_id, EXCLUDED.uom_id, EXCLUDED.weight_kg, EXCLUDED.unit_cost, EXCLUDED.is_condition_based);
    GET DIAGNOSTICS v_rows = ROW_COUNT;
    RETURN v_rows;
END $$;

-- ---------------------------------------------------------------------------
-- Demand history (incremental: new rows only, deduplicated by business hash)
-- ---------------------------------------------------------------------------
CREATE FUNCTION staging.validate_demand_history(p_batch_id bigint) RETURNS integer
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
    LEFT JOIN ref.brand b           ON b.code = upper(btrim(r.brand_code))
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

CREATE FUNCTION staging.load_demand_history(p_batch_id bigint) RETURNS integer
LANGUAGE plpgsql AS $$
DECLARE
    v_rows integer;
BEGIN
    INSERT INTO inventory.stock_movement (occurred_at, warehouse_id, part_id, movement_type_code, quantity,
                                          import_batch_id, external_doc_ref)
    SELECT s.occurred_at, s.warehouse_id, s.part_id, 'HISTORICAL_DEMAND', s.quantity, p_batch_id, s.doc_ref
    FROM staging.demand_history s
    WHERE s.import_batch_id = p_batch_id AND s.is_valid AND NOT s.is_duplicate
    ORDER BY s.occurred_at;
    GET DIAGNOSTICS v_rows = ROW_COUNT;
    RETURN v_rows;
END $$;

-- ---------------------------------------------------------------------------
-- Orchestration with a data-quality gate and load report
-- ---------------------------------------------------------------------------
CREATE FUNCTION staging.process_batch(p_batch_id bigint, p_max_error_ratio numeric DEFAULT 0.05)
RETURNS raw.import_batch
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_batch   raw.import_batch;
    v_total   integer;
    v_errors  integer;
    v_dups    integer := 0;
    v_loaded  integer := 0;
BEGIN
    SELECT * INTO v_batch FROM raw.import_batch WHERE id = p_batch_id FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'Import batch % not found', p_batch_id USING ERRCODE = 'no_data_found'; END IF;
    IF v_batch.status NOT IN ('uploaded', 'failed') THEN
        RAISE EXCEPTION 'Import batch % is already %', p_batch_id, v_batch.status USING ERRCODE = 'SS300';
    END IF;

    IF v_batch.source = 'part_catalog' THEN
        SELECT count(*) INTO v_total FROM raw.part_catalog_row WHERE import_batch_id = p_batch_id;
        v_errors := staging.validate_part_catalog(p_batch_id);
    ELSE
        SELECT count(*) INTO v_total FROM raw.demand_history_row WHERE import_batch_id = p_batch_id;
        v_errors := staging.validate_demand_history(p_batch_id);
        SELECT count(*) INTO v_dups FROM staging.demand_history WHERE import_batch_id = p_batch_id AND is_duplicate;
    END IF;

    IF v_total = 0 OR v_errors::numeric / v_total > p_max_error_ratio THEN
        UPDATE raw.import_batch
           SET status = 'failed', total_rows = v_total, valid_rows = v_total - v_errors, error_rows = v_errors,
               loaded_rows = 0, processed_at = now(),
               message = CASE WHEN v_total = 0 THEN 'Empty file'
                              ELSE format('%s of %s rows invalid (%s%%) > threshold %s%%: nothing loaded',
                                          v_errors, v_total, round(100.0 * v_errors / v_total, 1), round(100 * p_max_error_ratio, 1)) END
         WHERE id = p_batch_id
        RETURNING * INTO v_batch;
        RETURN v_batch;
    END IF;

    v_loaded := CASE v_batch.source WHEN 'part_catalog' THEN staging.load_part_catalog(p_batch_id)
                                    ELSE staging.load_demand_history(p_batch_id) END;

    UPDATE raw.import_batch
       SET status = 'loaded', total_rows = v_total, valid_rows = v_total - v_errors, error_rows = v_errors,
           loaded_rows = v_loaded, processed_at = now(),
           message = format('%s rows: %s loaded/updated, %s invalid, %s duplicates skipped', v_total, v_loaded, v_errors, v_dups)
     WHERE id = p_batch_id
    RETURNING * INTO v_batch;
    RETURN v_batch;
END $$;
COMMENT ON FUNCTION staging.process_batch(bigint, numeric) IS 'Validates a raw batch into staging and loads core tables if the share of invalid rows <= threshold (default 5 %). Returns the load report.';
