-- =============================================================================
-- V014 — Views (simplify the API's read side, CQRS queries) and the data mart
--        layer: materialised weekly demand, demand profiles (ADI/CV²), dense
--        ML training set with lag features, KPI views, data-quality checks.
-- =============================================================================

-- ===========================================================================
-- Core read views (used by MediatR query handlers)
-- ===========================================================================

CREATE VIEW inventory.v_in_transit AS
SELECT warehouse_id, part_id,
       sum(qty)                                    AS in_transit_qty,
       coalesce(sum(qty) FILTER (WHERE src = 'transfer'), 0) AS transfer_qty,
       coalesce(sum(qty) FILTER (WHERE src = 'purchase'), 0) AS purchase_qty
FROM (
    SELECT o.to_warehouse_id AS warehouse_id, l.part_id, l.quantity AS qty, 'transfer' AS src
    FROM inventory.transfer_order o
    JOIN inventory.transfer_order_line l ON l.transfer_order_id = o.id
    WHERE o.status = 'dispatched'
    UNION ALL
    SELECT po.warehouse_id, pl.part_id, pl.quantity - pl.received_qty, 'purchase'
    FROM procurement.purchase_order po
    JOIN procurement.purchase_order_line pl ON pl.purchase_order_id = po.id
    WHERE po.status IN ('approved', 'exported', 'partially_received') AND pl.quantity > pl.received_qty
) x
GROUP BY warehouse_id, part_id;
COMMENT ON VIEW inventory.v_in_transit IS 'Inbound quantity per hub and part: dispatched transfers + open approved purchase orders.';

-- US04: 7-warehouse stock breakdown
CREATE VIEW inventory.v_stock_overview AS
SELECT p.id                               AS part_id,
       b.code                             AS brand_code,
       p.oem_part_number,
       p.description,
       pc.code                            AS category_code,
       w.id                               AS warehouse_id,
       w.code                             AS warehouse_code,
       w.name                             AS warehouse_name,
       wp.bin_location,
       coalesce(sb.on_hand_qty, 0)        AS on_hand_qty,
       coalesce(sb.reserved_qty, 0)       AS reserved_qty,
       coalesce(sb.available_qty, 0)      AS available_qty,
       coalesce(it.in_transit_qty, 0)     AS in_transit_qty,
       wp.safety_stock_qty,
       coalesce(sb.available_qty, 0) < wp.safety_stock_qty AS below_safety_stock,
       round(coalesce(sb.on_hand_qty, 0) * p.unit_cost, 2)  AS stock_value_eur,
       sb.last_movement_at,
       sb.updated_at                      AS balance_updated_at
FROM inventory.warehouse_part wp
JOIN catalog.part p          ON p.id = wp.part_id
JOIN ref.brand b             ON b.id = p.brand_id
JOIN ref.part_category pc    ON pc.id = p.category_id
JOIN inventory.warehouse w   ON w.id = wp.warehouse_id
LEFT JOIN inventory.stock_balance sb ON sb.warehouse_id = wp.warehouse_id AND sb.part_id = wp.part_id
LEFT JOIN inventory.v_in_transit it  ON it.warehouse_id = wp.warehouse_id AND it.part_id = wp.part_id;
COMMENT ON VIEW inventory.v_stock_overview IS 'US04: on-hand, reserved, available and in-transit stock per part and warehouse with last-updated timestamp.';

CREATE VIEW fleet.v_machine_maintenance_status AS
SELECT m.id                      AS machine_id,
       m.vin,
       b.name                    AS brand_name,
       mm.model_name,
       mt.code                   AS machine_type_code,
       c.customer_code,
       w.code                    AS home_warehouse_code,
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
JOIN ref.brand b                 ON b.id = mm.brand_id
JOIN ref.machine_type mt         ON mt.id = mm.machine_type_id
JOIN fleet.customer c            ON c.id = m.customer_id
JOIN inventory.warehouse w       ON w.id = m.home_warehouse_id
JOIN ref.operating_condition oc  ON oc.id = m.operating_condition_id
LEFT JOIN LATERAL (SELECT * FROM fleet.fn_maintenance_due(m.id, 1000000) LIMIT 1) nd ON true
WHERE m.status = 'active';
COMMENT ON VIEW fleet.v_machine_maintenance_status IS 'Machine card (WF03): current engine hours and the next not yet planned OEM service.';

CREATE VIEW fleet.v_service_kit_shortage AS
SELECT s.id AS service_order_id, s.number, s.status, w.code AS warehouse_code,
       l.part_id, p.oem_part_number, p.description,
       l.qty_required, l.qty_issued, coalesce(r.held, 0) AS qty_reserved,
       l.qty_required - l.qty_issued - coalesce(r.held, 0) AS qty_missing
FROM fleet.service_order s
JOIN fleet.service_order_line l ON l.service_order_id = s.id
JOIN catalog.part p             ON p.id = l.part_id
JOIN inventory.warehouse w      ON w.id = s.warehouse_id
LEFT JOIN LATERAL (SELECT sum(sr.quantity) AS held FROM inventory.stock_reservation sr
                   WHERE sr.service_order_line_id = l.id AND sr.status IN ('soft', 'firm')) r ON true
WHERE s.status IN ('draft', 'parts_reserved', 'in_progress')
  AND l.qty_required - l.qty_issued - coalesce(r.held, 0) > 0;
COMMENT ON VIEW fleet.v_service_kit_shortage IS 'Open service orders whose kit could not be fully reserved (input for transfers / purchasing).';

CREATE VIEW procurement.v_purchase_order_overview AS
SELECT po.id, po.number, s.code AS supplier_code, s.name AS supplier_name, w.code AS warehouse_code,
       po.status, po.is_urgent, po.total_amount, po.currency_code,
       count(l.id)                                       AS line_count,
       coalesce(sum(l.received_qty), 0)                  AS received_qty,
       coalesce(sum(l.quantity), 0)                      AS ordered_qty,
       cu.user_name                                      AS created_by_user,
       au.user_name                                      AS approved_by_user,
       po.total_amount > 5000                            AS requires_four_eyes,
       po.created_at, po.approved_at, po.expected_arrival_date
FROM procurement.purchase_order po
JOIN catalog.supplier s       ON s.id = po.supplier_id
JOIN inventory.warehouse w    ON w.id = po.warehouse_id
JOIN iam.app_user cu          ON cu.id = po.created_by
LEFT JOIN iam.app_user au     ON au.id = po.approved_by
LEFT JOIN procurement.purchase_order_line l ON l.purchase_order_id = po.id
GROUP BY po.id, s.code, s.name, w.code, cu.user_name, au.user_name;
COMMENT ON VIEW procurement.v_purchase_order_overview IS 'WF04: purchase order list with totals and approval state.';

-- NFR08 / US09 / WF02 "Explain calculation"
CREATE VIEW forecast.v_recommendation_explained AS
SELECT r.id, r.kind, r.status, r.created_at,
       p.oem_part_number, p.description, b.code AS brand_code,
       w.code AS warehouse_code, dw.code AS donor_warehouse_code, s.name AS supplier_name,
       r.base_demand_qty        AS a_base_demand,
       r.maintenance_demand_qty AS b_maintenance_demand,
       r.lead_time_buffer_qty   AS c_lead_time_buffer,
       r.safety_stock_qty       AS d_safety_stock,
       r.base_demand_qty + r.maintenance_demand_qty + r.lead_time_buffer_qty + r.safety_stock_qty AS total_requirement,
       r.available_qty, r.in_transit_qty, r.net_position_qty, r.recommended_qty,
       round(100 * r.base_demand_qty        / nullif(r.base_demand_qty + r.maintenance_demand_qty + r.lead_time_buffer_qty + r.safety_stock_qty, 0), 1) AS a_share_pct,
       round(100 * r.maintenance_demand_qty / nullif(r.base_demand_qty + r.maintenance_demand_qty + r.lead_time_buffer_qty + r.safety_stock_qty, 0), 1) AS b_share_pct,
       r.estimated_cost_eur, r.alternative_cost_eur,
       r.alternative_cost_eur - r.estimated_cost_eur AS saving_vs_alternative_eur,
       r.final_qty, r.decision_reason, du.user_name AS decided_by_user, r.decided_at,
       mv.algorithm, mv.version_label AS model_version
FROM forecast.recommendation r
JOIN catalog.part p                ON p.id = r.part_id
JOIN ref.brand b                   ON b.id = p.brand_id
JOIN inventory.warehouse w         ON w.id = r.warehouse_id
LEFT JOIN inventory.warehouse dw   ON dw.id = r.donor_warehouse_id
LEFT JOIN catalog.supplier s       ON s.id = r.supplier_id
LEFT JOIN iam.app_user du          ON du.id = r.decided_by
JOIN forecast.forecast_run fr      ON fr.id = r.forecast_run_id
JOIN forecast.ml_model_version mv  ON mv.id = fr.model_version_id;
COMMENT ON VIEW forecast.v_recommendation_explained IS 'NFR08: decomposition (a) base demand, (b) maintenance, (c) lead-time buffer, (d) safety stock, stock position and economics of each proposal.';

-- ===========================================================================
-- Personal data: masked views (granted to app_read / app_audit instead of
-- the base table columns)
-- ===========================================================================
CREATE VIEW iam.v_app_user_masked AS
SELECT u.id, u.user_name,
       util.mask_name(u.full_name) AS full_name_masked,
       util.mask_email(u.email)    AS email_masked,
       util.mask_phone(u.phone)    AS phone_masked,
       u.is_active, w.code AS home_warehouse_code,
       (SELECT array_agg(r.code ORDER BY r.code) FROM iam.user_role ur JOIN iam.role r ON r.id = ur.role_id
        WHERE ur.user_id = u.id) AS roles,
       u.last_login_at, u.created_at, u.anonymised_at IS NOT NULL AS is_anonymised
FROM iam.app_user u
LEFT JOIN inventory.warehouse w ON w.id = u.home_warehouse_id;
COMMENT ON VIEW iam.v_app_user_masked IS 'User directory with masked personal data (name -> initials, e-mail and phone partially hidden).';

CREATE VIEW audit.v_audit_log_masked AS
SELECT a.id, a.occurred_at, a.action, a.table_name, a.record_id, a.changed_columns,
       a.old_values, a.new_values, a.reason,
       u.user_name, util.mask_name(u.full_name) AS actor_masked, a.db_role
FROM audit.audit_log a
LEFT JOIN iam.app_user u ON u.id = a.user_id;
COMMENT ON VIEW audit.v_audit_log_masked IS 'Audit trail for the sponsor / auditors with the actor''s name masked.';

-- ===========================================================================
-- DATA MART LAYER
-- ===========================================================================

CREATE FUNCTION forecast.fn_classify_demand(p_adi numeric, p_cv2 numeric) RETURNS varchar
LANGUAGE sql STABLE AS $$
    SELECT dc.code FROM ref.demand_class dc
    WHERE p_adi >= dc.adi_lower AND p_adi < dc.adi_upper AND p_cv2 >= dc.cv2_lower AND p_cv2 < dc.cv2_upper
    LIMIT 1
$$;
COMMENT ON FUNCTION forecast.fn_classify_demand(numeric, numeric) IS 'Syntetos-Boylan class from ADI and CV² using the ranges in ref.demand_class.';

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
-- Unique index: required for REFRESH ... CONCURRENTLY (reads are not blocked during refresh).
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

-- ---------------------------------------------------------------------------
-- Load monitoring and data-quality checks
-- ---------------------------------------------------------------------------
CREATE TABLE mart.refresh_log (
    id           bigint       GENERATED BY DEFAULT AS IDENTITY,
    view_name    varchar(80)  NOT NULL,
    started_at   timestamptz  NOT NULL,
    finished_at  timestamptz  NOT NULL,
    row_count    bigint,
    status       varchar(10)  NOT NULL,
    message      text,
    CONSTRAINT pk_refresh_log PRIMARY KEY (id),
    CONSTRAINT ck_refresh_log_status CHECK (status IN ('ok', 'failed')),
    CONSTRAINT ck_refresh_log_time CHECK (finished_at >= started_at)
);
CREATE INDEX ix_refresh_log_view_time ON mart.refresh_log (view_name, started_at DESC);
COMMENT ON TABLE mart.refresh_log IS 'Monitoring of data mart refreshes (duration, row count, failures).';

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
        AND NOT EXISTS (SELECT 1 FROM catalog.supplier_part sp WHERE sp.part_id = p.id))
UNION ALL
SELECT 'duplicate demand rows in staging (loaded)',
       (SELECT count(*) FROM (SELECT row_hash FROM staging.demand_history WHERE is_valid AND NOT is_duplicate
                              GROUP BY row_hash HAVING count(*) > 1) d)
UNION ALL
SELECT 'expired soft reservations still active',
       (SELECT count(*) FROM inventory.stock_reservation WHERE status = 'soft' AND expires_at < now() - interval '1 hour');
COMMENT ON VIEW mart.v_data_quality IS 'Automated reconciliation checks (ledger vs. balance, reservations, PO totals, odometer, duplicates). Expected: all failed_rows = 0.';
