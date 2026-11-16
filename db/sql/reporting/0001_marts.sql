-- =============================================================================
-- Reporting 0001 — data marts, KPI views, load monitoring, data quality.
-- Statements copied verbatim from V001 and V014 (mart.refresh_log is created by the EF model).
-- The Reporting exception of ADR 0002: these objects read every module's schema (MODULE_MAPPING 3.3).
-- v_data_quality names procurement.supplier_part (ADR 0011 D2).
-- =============================================================================

COMMENT ON SCHEMA mart        IS 'Layer 3 (data mart): materialised read models for ML training, KPIs and reports.';

-- Weekly demand per hub and SKU (ISO weeks, Europe/Vilnius), exceptional demand excluded (US08).
CREATE MATERIALIZED VIEW mart.mv_weekly_demand AS
SELECT (date_trunc('week', m.occurred_at AT TIME ZONE 'Europe/Vilnius'))::date AS week_start,
       m.warehouse_id,
       m.part_id,
       sum(m.quantity)                                                           AS demand_qty,
       count(*)                                                                  AS demand_lines,
       coalesce(sum(m.quantity) FILTER (WHERE m.movement_type_code = 'ISSUE_SERVICE'), 0) AS service_qty
FROM inventory.stock_movement m
JOIN ref.movement_type t ON t.code = m.movement_type_code AND t.is_demand
WHERE NOT EXISTS (SELECT 1 FROM inventory.demand_exception e
                  WHERE e.movement_id = m.id AND e.movement_occurred_at = m.occurred_at)
GROUP BY 1, 2, 3
WITH NO DATA;
CREATE UNIQUE INDEX uq_mv_weekly_demand ON mart.mv_weekly_demand (warehouse_id, part_id, week_start);
CREATE INDEX ix_mv_weekly_demand_week ON mart.mv_weekly_demand (week_start);
COMMENT ON MATERIALIZED VIEW mart.mv_weekly_demand IS 'Data mart: weekly demand (only non-zero weeks) per warehouse and part; source for ML training and accuracy.';

-- Demand profile of the last 104 complete weeks: ADI, CV², demand class (finding F1).
CREATE MATERIALIZED VIEW mart.mv_demand_profile AS
WITH bounds AS (
    SELECT (date_trunc('week', now() AT TIME ZONE 'Europe/Vilnius'))::date - 7 AS last_week
), s AS (
    SELECT d.warehouse_id, d.part_id,
           min(d.week_start) AS first_week, max(d.week_start) AS last_demand_week,
           count(*) AS nonzero_weeks, sum(d.demand_qty) AS total_qty,
           avg(d.demand_qty) AS mean_nonzero, stddev_pop(d.demand_qty) AS sd_nonzero
    FROM mart.mv_weekly_demand d CROSS JOIN bounds b
    WHERE d.week_start BETWEEN b.last_week - 7 * 103 AND b.last_week
    GROUP BY d.warehouse_id, d.part_id
), k AS (
    SELECT s.*, b.last_week,
           (b.last_week - s.first_week) / 7 + 1 AS periods,
           round(((b.last_week - s.first_week) / 7 + 1)::numeric / s.nonzero_weeks, 3) AS adi,
           round(coalesce(power(s.sd_nonzero / nullif(s.mean_nonzero, 0), 2), 0), 3) AS cv2
    FROM s CROSS JOIN bounds b
)
SELECT k.warehouse_id, k.part_id, k.first_week, k.last_week, k.last_demand_week,
       k.periods, k.nonzero_weeks, k.total_qty, k.adi, k.cv2,
       forecast.fn_classify_demand(k.adi, k.cv2) AS demand_class_code,
       now() AS computed_at
FROM k
WITH NO DATA;
CREATE UNIQUE INDEX uq_mv_demand_profile ON mart.mv_demand_profile (warehouse_id, part_id);
CREATE INDEX ix_mv_demand_profile_class ON mart.mv_demand_profile (demand_class_code);
COMMENT ON MATERIALIZED VIEW mart.mv_demand_profile IS 'Data mart: ADI / CV² demand classification per series (intermittent, lumpy, smooth, erratic).';

-- Dense weekly series (zero weeks included) with lag / rolling features for ML.NET.
CREATE VIEW mart.v_ml_training_set AS
SELECT g.warehouse_id, g.part_id, g.week_start,
       extract(week FROM g.week_start)::smallint                         AS iso_week,
       ac.season_phase_code, ac.seasonal_weight,
       p.brand_id, p.category_id, g.demand_class_code,
       coalesce(d.demand_qty, 0)                                          AS demand_qty,
       lag(coalesce(d.demand_qty, 0), 1)  OVER w                          AS lag_1,
       lag(coalesce(d.demand_qty, 0), 2)  OVER w                          AS lag_2,
       lag(coalesce(d.demand_qty, 0), 4)  OVER w                          AS lag_4,
       lag(coalesce(d.demand_qty, 0), 52) OVER w                          AS lag_52,
       avg(coalesce(d.demand_qty, 0)) OVER (w ROWS BETWEEN 4 PRECEDING AND 1 PRECEDING)  AS rolling_mean_4,
       avg(coalesce(d.demand_qty, 0)) OVER (w ROWS BETWEEN 13 PRECEDING AND 1 PRECEDING) AS rolling_mean_13,
       count(d.demand_qty)            OVER (w ROWS BETWEEN 13 PRECEDING AND 1 PRECEDING) AS nonzero_weeks_13
FROM (SELECT pr.warehouse_id, pr.part_id, pr.demand_class_code, gs::date AS week_start
      FROM mart.mv_demand_profile pr
      CROSS JOIN LATERAL generate_series(pr.first_week, pr.last_week, interval '1 week') AS gs) g
JOIN catalog.part p              ON p.id = g.part_id
JOIN ref.agro_calendar_week ac   ON ac.iso_week = extract(week FROM g.week_start)
LEFT JOIN mart.mv_weekly_demand d ON d.warehouse_id = g.warehouse_id AND d.part_id = g.part_id AND d.week_start = g.week_start
WINDOW w AS (PARTITION BY g.warehouse_id, g.part_id ORDER BY g.week_start);
COMMENT ON VIEW mart.v_ml_training_set IS 'ML.NET training/inference features: dense weekly series with season, lags (1,2,4,52) and rolling means (window functions).';

-- ---------------------------------------------------------------------------
-- KPI views (BA 2.10)
-- ---------------------------------------------------------------------------
CREATE VIEW mart.v_kpi_recommendation_acceptance AS
SELECT date_trunc('month', r.decided_at)::date AS month, r.kind,
       count(*)                                  AS decided,
       count(*) FILTER (WHERE r.status = 'accepted')   AS accepted,
       count(*) FILTER (WHERE r.status = 'overridden') AS overridden,
       count(*) FILTER (WHERE r.status = 'rejected')   AS rejected,
       round(100.0 * count(*) FILTER (WHERE r.status = 'accepted') / count(*), 1) AS acceptance_rate_pct,
       80.0 AS target_pct
FROM forecast.recommendation r
WHERE r.decided_at IS NOT NULL
GROUP BY ROLLUP (date_trunc('month', r.decided_at)::date, r.kind);
COMMENT ON VIEW mart.v_kpi_recommendation_acceptance IS 'KPI-04: share of recommendations accepted without change (target >= 80 %); ROLLUP gives subtotals.';

CREATE VIEW mart.v_kpi_dead_stock AS
SELECT coalesce(w.code, 'NETWORK')                               AS warehouse_code,
       round(sum(sb.on_hand_qty * p.unit_cost), 2)               AS stock_value_eur,
       round(sum(sb.on_hand_qty * p.unit_cost) FILTER (WHERE NOT x.has_demand), 2) AS dead_stock_value_eur,
       round(100 * coalesce(sum(sb.on_hand_qty * p.unit_cost) FILTER (WHERE NOT x.has_demand), 0)
                 / nullif(sum(sb.on_hand_qty * p.unit_cost), 0), 1) AS dead_stock_ratio_pct,
       10.0 AS target_pct
FROM inventory.stock_balance sb
JOIN catalog.part p        ON p.id = sb.part_id
JOIN inventory.warehouse w ON w.id = sb.warehouse_id
CROSS JOIN LATERAL (
    SELECT EXISTS (SELECT 1 FROM inventory.stock_movement m
                   JOIN ref.movement_type t ON t.code = m.movement_type_code AND t.is_demand
                   WHERE m.part_id = sb.part_id AND m.warehouse_id = sb.warehouse_id
                     AND m.occurred_at >= now() - interval '12 months') AS has_demand) x
WHERE sb.on_hand_qty > 0
GROUP BY ROLLUP (w.code);
COMMENT ON VIEW mart.v_kpi_dead_stock IS 'KPI-05: value share of stock without any demand in 12 months, per hub and for the network (target <= 10 %).';

CREATE VIEW mart.v_forecast_accuracy AS
WITH pairs AS (
    SELECT f.forecast_run_id, pr.demand_class_code, f.forecast_qty, coalesce(d.demand_qty, 0) AS actual_qty
    FROM forecast.demand_forecast f
    LEFT JOIN mart.mv_weekly_demand d
           ON d.warehouse_id = f.warehouse_id AND d.part_id = f.part_id AND d.week_start = f.week_start
    LEFT JOIN mart.mv_demand_profile pr ON pr.warehouse_id = f.warehouse_id AND pr.part_id = f.part_id
    WHERE f.week_start + 7 <= (date_trunc('week', now() AT TIME ZONE 'Europe/Vilnius'))::date  -- completed weeks only
)
SELECT forecast_run_id, coalesce(demand_class_code, 'ALL') AS demand_class_code,
       count(*)                                                   AS points,
       round(avg(abs(forecast_qty - actual_qty)), 4)              AS mae,
       round(sqrt(avg(power(forecast_qty - actual_qty, 2))), 4)   AS rmse,
       round(avg(forecast_qty - actual_qty), 4)                   AS bias
FROM pairs
GROUP BY GROUPING SETS ((forecast_run_id, demand_class_code), (forecast_run_id));
COMMENT ON VIEW mart.v_forecast_accuracy IS 'Forecast vs. actual per run and demand class (MAE, RMSE, bias) for completed weeks.';

CREATE PROCEDURE mart.refresh_all()
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_view text;
    v_t0   timestamptz;
    v_rows bigint;
BEGIN
    FOREACH v_view IN ARRAY ARRAY['mart.mv_weekly_demand', 'mart.mv_demand_profile'] LOOP   -- dependency order
        v_t0 := clock_timestamp();
        BEGIN
            IF (SELECT ispopulated FROM pg_matviews WHERE schemaname || '.' || matviewname = v_view) THEN
                EXECUTE 'REFRESH MATERIALIZED VIEW CONCURRENTLY ' || v_view;
            ELSE
                EXECUTE 'REFRESH MATERIALIZED VIEW ' || v_view;
            END IF;
            EXECUTE 'SELECT count(*) FROM ' || v_view INTO v_rows;
            INSERT INTO mart.refresh_log (view_name, started_at, finished_at, row_count, status)
            VALUES (v_view, v_t0, clock_timestamp(), v_rows, 'ok');
        EXCEPTION WHEN others THEN
            INSERT INTO mart.refresh_log (view_name, started_at, finished_at, status, message)
            VALUES (v_view, v_t0, clock_timestamp(), 'failed', SQLERRM);
            RAISE WARNING 'Refresh of % failed: %', v_view, SQLERRM;
            RETURN;                                    -- dependent views are not refreshed
        END;
    END LOOP;
END $$;
COMMENT ON PROCEDURE mart.refresh_all() IS 'Refreshes the data marts (concurrently once populated) and logs duration/rows/failures. Nightly Quartz.NET job and after imports.';

CREATE VIEW mart.v_import_monitor AS
SELECT b.id, b.source, b.file_name, b.status, b.total_rows, b.valid_rows, b.error_rows, b.loaded_rows,
       round(100.0 * b.error_rows / nullif(b.total_rows, 0), 1) AS error_rate_pct,
       b.uploaded_at, b.processed_at, b.processed_at - b.uploaded_at AS processing_time, b.message
FROM raw.import_batch b;
COMMENT ON VIEW mart.v_import_monitor IS 'Load report of every CSV import (row counts, error rate, duration).';

-- Reconciliation checks: every row returned with failed_rows > 0 is an integrity defect.
CREATE VIEW mart.v_data_quality AS
WITH ledger AS (
    SELECT m.warehouse_id, m.part_id, sum(t.balance_sign * m.quantity) AS qty
    FROM inventory.stock_movement m JOIN ref.movement_type t ON t.code = m.movement_type_code
    WHERE t.balance_sign <> 0
    GROUP BY m.warehouse_id, m.part_id
), holds AS (
    SELECT warehouse_id, part_id, sum(quantity) AS qty
    FROM inventory.stock_reservation WHERE status IN ('soft', 'firm')
    GROUP BY warehouse_id, part_id
)
SELECT 'stock_balance = SUM(ledger)' AS check_name,
       (SELECT count(*) FROM inventory.stock_balance b FULL JOIN ledger l USING (warehouse_id, part_id)
        WHERE coalesce(b.on_hand_qty, 0) <> coalesce(l.qty, 0)) AS failed_rows
UNION ALL
SELECT 'reserved_qty = SUM(active reservations)',
       (SELECT count(*) FROM inventory.stock_balance b FULL JOIN holds h USING (warehouse_id, part_id)
        WHERE coalesce(b.reserved_qty, 0) <> coalesce(h.qty, 0))
UNION ALL
SELECT 'PO total = SUM(lines)',
       (SELECT count(*) FROM procurement.purchase_order po
        WHERE po.total_amount <> (SELECT coalesce(sum(line_amount), 0) FROM procurement.purchase_order_line l WHERE l.purchase_order_id = po.id))
UNION ALL
SELECT 'machine hours = last reading',
       (SELECT count(*) FROM fleet.machine m
        JOIN LATERAL (SELECT reading_hours FROM fleet.engine_hours_reading r WHERE r.machine_id = m.id ORDER BY read_at DESC LIMIT 1) r ON true
        WHERE m.current_engine_hours <> r.reading_hours)
UNION ALL
SELECT 'active parts without supplier',
       (SELECT count(*) FROM catalog.part p WHERE p.is_active
        AND NOT EXISTS (SELECT 1 FROM procurement.supplier_part sp WHERE sp.part_id = p.id))
UNION ALL
SELECT 'duplicate demand rows in staging (loaded)',
       (SELECT count(*) FROM (SELECT row_hash FROM staging.demand_history WHERE is_valid AND NOT is_duplicate
                              GROUP BY row_hash HAVING count(*) > 1) d)
UNION ALL
SELECT 'expired soft reservations still active',
       (SELECT count(*) FROM inventory.stock_reservation WHERE status = 'soft' AND expires_at < now() - interval '1 hour');
COMMENT ON VIEW mart.v_data_quality IS 'Automated reconciliation checks (ledger vs. balance, reservations, PO totals, odometer, duplicates). Expected: all failed_rows = 0.';
