-- =============================================================================
-- Reporting 9000 — TRANSITIONAL cross-module views and LANGUAGE sql functions.
-- MODULE_MAPPING 3.2: these read other modules' schemas and are resolved when created, so the last
-- migration of the Reporting context creates them, after every other context. Bodies copied verbatim
-- from V011, V012 and V014 (supplier tables renamed, ADR 0011 D2); names, grants and callers are
-- unchanged. Each object names the stage and issue that replaces it; that stage removes it from this
-- script and adds the module-local version to its own module.
-- =============================================================================

-- TRANSITIONAL: replaced by a read of inventory.expected_receipt in stage 7 (reads procurement; X13) (#13)
-- ---------------------------------------------------------------------------
-- In-transit quantity: dispatched transfers + open purchase orders
-- ---------------------------------------------------------------------------
CREATE FUNCTION inventory.fn_in_transit_qty(p_warehouse_id smallint, p_part_id bigint) RETURNS numeric
LANGUAGE sql STABLE AS $$
    SELECT coalesce((SELECT sum(l.quantity)
                     FROM inventory.transfer_order_line l
                     JOIN inventory.transfer_order o ON o.id = l.transfer_order_id
                     WHERE o.to_warehouse_id = p_warehouse_id AND l.part_id = p_part_id
                       AND o.status = 'dispatched'), 0)
         + coalesce((SELECT sum(pl.quantity - pl.received_qty)
                     FROM procurement.purchase_order_line pl
                     JOIN procurement.purchase_order po ON po.id = pl.purchase_order_id
                     WHERE po.warehouse_id = p_warehouse_id AND pl.part_id = p_part_id
                       AND po.status IN ('approved', 'exported', 'partially_received')), 0)
$$;
COMMENT ON FUNCTION inventory.fn_in_transit_qty(smallint, bigint) IS 'Inbound quantity: dispatched transfers + not yet received approved purchase orders.';
GRANT EXECUTE ON FUNCTION inventory.fn_in_transit_qty(smallint, bigint) TO app_read;

-- TRANSITIONAL: replaced by inventory.expected_receipt in stage 7 (reads procurement; X13) (#13)
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
GRANT SELECT ON inventory.v_in_transit TO app_read;

-- TRANSITIONAL: replaced by the module-local in-transit view in stage 7 (depends on v_in_transit; X13) (#13)
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
GRANT SELECT ON inventory.v_stock_overview TO app_read;

-- TRANSITIONAL: replaced by a module-local view in stage 6 (reads inventory.warehouse; X10) (#12)
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
GRANT SELECT ON fleet.v_machine_maintenance_status TO app_read;

-- TRANSITIONAL: replaced by fleet.part_snapshot and qty_reserved in stage 6 (reads catalog and inventory; X9) (#12)
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
GRANT SELECT ON fleet.v_service_kit_shortage TO app_read;

-- TRANSITIONAL: replaced by a module-local view in stage 7 (reads inventory.warehouse and iam.app_user; X14) (#13)
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
JOIN procurement.supplier s   ON s.id = po.supplier_id
JOIN inventory.warehouse w    ON w.id = po.warehouse_id
JOIN iam.app_user cu          ON cu.id = po.created_by
LEFT JOIN iam.app_user au     ON au.id = po.approved_by
LEFT JOIN procurement.purchase_order_line l ON l.purchase_order_id = po.id
GROUP BY po.id, s.code, s.name, w.code, cu.user_name, au.user_name;
COMMENT ON VIEW procurement.v_purchase_order_overview IS 'WF04: purchase order list with totals and approval state.';
GRANT SELECT ON procurement.v_purchase_order_overview TO app_read;

-- TRANSITIONAL: replaced by the C# recommendation engine in stage 8 (reads inventory, fleet and procurement; X15) (#14)
-- UC01 step 5 / NFR08: decomposed stock position of one SKU at one hub.
CREATE FUNCTION forecast.fn_stock_position(p_warehouse_id smallint, p_part_id bigint, p_forecast_run_id bigint,
                                           p_lead_time_days numeric DEFAULT NULL)
RETURNS TABLE (available_qty numeric, in_transit_qty numeric, base_demand_qty numeric, maintenance_demand_qty numeric,
               lead_time_buffer_qty numeric, safety_stock_qty numeric, net_position_qty numeric)
LANGUAGE sql STABLE AS $$
    WITH f AS (
        SELECT coalesce(sum(df.forecast_qty), 0) AS total, coalesce(avg(df.forecast_qty), 0) AS weekly
        FROM forecast.demand_forecast df
        WHERE df.forecast_run_id = p_forecast_run_id AND df.warehouse_id = p_warehouse_id AND df.part_id = p_part_id
    ), maint AS (            -- planned demand of open service orders not yet covered by a reservation
        SELECT coalesce(sum(greatest(l.qty_required - l.qty_issued - coalesce(r.held, 0), 0)), 0) AS qty
        FROM fleet.service_order_line l
        JOIN fleet.service_order s ON s.id = l.service_order_id
        LEFT JOIN LATERAL (SELECT sum(sr.quantity) AS held FROM inventory.stock_reservation sr
                           WHERE sr.service_order_line_id = l.id AND sr.status IN ('soft', 'firm')) r ON true
        WHERE s.warehouse_id = p_warehouse_id AND l.part_id = p_part_id
          AND s.status IN ('draft', 'parts_reserved', 'in_progress')
    ), lt AS (
        SELECT coalesce(p_lead_time_days,
                        (SELECT coalesce(min(sp.lead_time_days) FILTER (WHERE sp.is_preferred), min(sp.lead_time_days))
                         FROM procurement.supplier_part sp WHERE sp.part_id = p_part_id), 0) AS days
    ), pos AS (
        SELECT coalesce((SELECT b.available_qty FROM inventory.stock_balance b
                         WHERE b.warehouse_id = p_warehouse_id AND b.part_id = p_part_id), 0)            AS available,
               inventory.fn_in_transit_qty(p_warehouse_id, p_part_id)                                    AS in_transit,
               round(f.total, 3)                                                                         AS base,
               round(maint.qty, 3)                                                                       AS maintenance,
               round(f.weekly * lt.days / 7.0, 3)                                                        AS lead_time_buffer,
               coalesce((SELECT wp.safety_stock_qty FROM inventory.warehouse_part wp
                         WHERE wp.warehouse_id = p_warehouse_id AND wp.part_id = p_part_id), 0)          AS safety
        FROM f, maint, lt
    )
    SELECT available, in_transit, base, maintenance, lead_time_buffer, safety,
           available + in_transit - (base + maintenance + lead_time_buffer + safety)
    FROM pos
$$;
COMMENT ON FUNCTION forecast.fn_stock_position(smallint, bigint, bigint, numeric) IS 'Net = Available + InTransit - (ML forecast + unreserved maintenance demand + lead-time buffer + safety stock). Used by the recommendation engine.';
GRANT EXECUTE ON FUNCTION forecast.fn_stock_position(smallint, bigint, bigint, numeric) TO app_read;

-- TRANSITIONAL: replaced by forecast.part_snapshot in stage 8 (reads catalog, inventory, iam and suppliers; X17) (#14)
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
LEFT JOIN procurement.supplier s       ON s.id = r.supplier_id
LEFT JOIN iam.app_user du          ON du.id = r.decided_by
JOIN forecast.forecast_run fr      ON fr.id = r.forecast_run_id
JOIN forecast.ml_model_version mv  ON mv.id = fr.model_version_id;
COMMENT ON VIEW forecast.v_recommendation_explained IS 'NFR08: decomposition (a) base demand, (b) maintenance, (c) lead-time buffer, (d) safety stock, stock position and economics of each proposal.';
GRANT SELECT ON forecast.v_recommendation_explained TO app_read;

-- TRANSITIONAL: replaced by a module-local view in stage 3 (reads inventory.warehouse; X18) (#11)
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
GRANT SELECT ON iam.v_app_user_masked TO app_read;
