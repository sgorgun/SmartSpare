-- =============================================================================
-- V012 — Business logic: engine hours & maintenance (UC02, US05, US06),
--        purchase orders (US12-US14), model promotion (KPI-02),
--        stock position & recommendation decisions (UC01, NFR08, KPI-04)
-- =============================================================================

-- ===========================================================================
-- FLEET & MAINTENANCE
-- ===========================================================================

-- UC02 step 3 / A1: monotonic and physically plausible engine-hour readings.
CREATE FUNCTION fleet.trg_engine_hours_validate() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    v_status     fleet.machine_status;
    v_last       fleet.engine_hours_reading;
    v_elapsed_h  numeric;
BEGIN
    -- Lock the machine: concurrent readings of one machine are serialised.
    SELECT status INTO v_status FROM fleet.machine WHERE id = NEW.machine_id FOR UPDATE;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Machine % not found', NEW.machine_id USING ERRCODE = 'foreign_key_violation';
    END IF;
    IF v_status <> 'active' THEN
        RAISE EXCEPTION 'Machine % is not active (%)', NEW.machine_id, v_status USING ERRCODE = 'SS102';
    END IF;

    SELECT * INTO v_last FROM fleet.engine_hours_reading
    WHERE machine_id = NEW.machine_id ORDER BY read_at DESC LIMIT 1;

    IF FOUND THEN
        IF NEW.read_at <= v_last.read_at THEN
            RAISE EXCEPTION 'Reading time % is not after the last reading (%)', NEW.read_at, v_last.read_at USING ERRCODE = 'SS103';
        END IF;
        IF NEW.reading_hours < v_last.reading_hours THEN
            RAISE EXCEPTION 'Entered hours (%h) cannot be less than previous recorded reading (%h)',
                  NEW.reading_hours, v_last.reading_hours USING ERRCODE = 'SS100';
        END IF;
        -- An engine cannot run more hours than wall-clock time has passed (<= 24 h per day).
        v_elapsed_h := extract(epoch FROM (NEW.read_at - v_last.read_at)) / 3600.0;
        IF NEW.reading_hours - v_last.reading_hours > v_elapsed_h THEN
            RAISE EXCEPTION 'Implausible reading: +%h engine hours within %h elapsed time',
                  NEW.reading_hours - v_last.reading_hours, round(v_elapsed_h, 1) USING ERRCODE = 'SS101';
        END IF;
    END IF;
    RETURN NEW;
END $$;

CREATE FUNCTION fleet.trg_engine_hours_apply() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
BEGIN
    UPDATE fleet.machine
       SET current_engine_hours = NEW.reading_hours, engine_hours_read_at = NEW.read_at
     WHERE id = NEW.machine_id;
    RETURN NULL;
END $$;

CREATE TRIGGER trg_engine_hours_validate BEFORE INSERT ON fleet.engine_hours_reading
    FOR EACH ROW EXECUTE FUNCTION fleet.trg_engine_hours_validate();
CREATE TRIGGER trg_engine_hours_apply AFTER INSERT ON fleet.engine_hours_reading
    FOR EACH ROW EXECUTE FUNCTION fleet.trg_engine_hours_apply();
-- Readings are evidence for warranty / OEM compliance: never edited.
CREATE TRIGGER trg_engine_hours_immutable BEFORE UPDATE ON fleet.engine_hours_reading
    FOR EACH ROW EXECUTE FUNCTION util.trg_forbid_modification();

-- Which planned services are due within p_horizon_hours (US05: draft when <= 50 h remain)?
CREATE FUNCTION fleet.fn_maintenance_due(p_machine_id bigint, p_horizon_hours numeric DEFAULT 50)
RETURNS TABLE (maintenance_plan_id integer, plan_name varchar, interval_hours integer,
               effective_interval numeric, due_engine_hours numeric, current_engine_hours numeric,
               remaining_hours numeric, is_overdue boolean)
LANGUAGE sql STABLE AS $$
    WITH m AS (
        SELECT m.id, m.model_id, m.current_engine_hours, oc.interval_factor,
               (SELECT r.reading_hours FROM fleet.engine_hours_reading r
                WHERE r.machine_id = m.id ORDER BY r.read_at LIMIT 1) AS first_reading
        FROM fleet.machine m
        JOIN ref.operating_condition oc ON oc.id = m.operating_condition_id
        WHERE m.id = p_machine_id AND m.status = 'active'
    ), p AS (
        SELECT mp.id, mp.name, mp.interval_hours,
               round(mp.interval_hours * m.interval_factor, 1) AS eff,   -- F5: condition shortens interval
               m.current_engine_hours,
               coalesce(
                   (SELECT max(so.due_engine_hours) FROM fleet.service_order so
                    WHERE so.machine_id = m.id AND so.maintenance_plan_id = mp.id AND so.status <> 'cancelled'),
                   -- thresholds before the first known reading are unknown history: not drafted
                   floor(coalesce(m.first_reading, 0) / round(mp.interval_hours * m.interval_factor, 1))
                       * round(mp.interval_hours * m.interval_factor, 1)) AS last_due
        FROM m JOIN fleet.maintenance_plan mp ON mp.model_id = m.model_id AND mp.is_active
    )
    SELECT p.id, p.name, p.interval_hours, p.eff, d.due, p.current_engine_hours,
           d.due - p.current_engine_hours, p.current_engine_hours >= d.due
    FROM p
    -- next threshold after the last one served; if several were skipped, only the latest crossed one
    CROSS JOIN LATERAL (SELECT greatest((floor(p.last_due / p.eff) + 1) * p.eff,
                                        floor(p.current_engine_hours / p.eff) * p.eff) AS due) d
    WHERE d.due > 0 AND d.due - p.current_engine_hours <= p_horizon_hours
    ORDER BY d.due - p.current_engine_hours, p.interval_hours DESC
$$;
COMMENT ON FUNCTION fleet.fn_maintenance_due(bigint, numeric) IS 'Planned services of a machine due within the horizon (effective interval = OEM interval x condition factor).';

-- US06 / UC02 steps 5-7: service order draft with kit and 7-day soft reservations.
CREATE FUNCTION fleet.create_service_order_draft(p_machine_id bigint, p_plan_id integer, p_due_engine_hours numeric)
RETURNS bigint
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_user    uuid := util.require_role('SERVICE_ENGINEER', 'BRAND_MANAGER', 'ADMINISTRATOR');
    v_wh      smallint;
    v_hours   numeric;
    v_so      bigint;
    v_line    record;
    v_avail   numeric;
    v_qty     numeric;
    v_all     boolean := true;
    v_lines   integer := 0;
BEGIN
    SELECT home_warehouse_id, current_engine_hours INTO v_wh, v_hours
    FROM fleet.machine WHERE id = p_machine_id;

    INSERT INTO fleet.service_order (machine_id, maintenance_plan_id, warehouse_id, due_engine_hours, trigger_engine_hours, created_by)
    VALUES (p_machine_id, p_plan_id, v_wh, p_due_engine_hours, v_hours, v_user)
    RETURNING id INTO v_so;

    INSERT INTO fleet.service_order_line (service_order_id, part_id, qty_required)
    SELECT v_so, k.part_id, k.quantity
    FROM fleet.maintenance_plan_part k WHERE k.maintenance_plan_id = p_plan_id;

    FOR v_line IN
        SELECT l.id, l.part_id, l.qty_required FROM fleet.service_order_line l
        WHERE l.service_order_id = v_so ORDER BY l.part_id
    LOOP
        v_lines := v_lines + 1;
        SELECT b.available_qty INTO v_avail FROM inventory.stock_balance b
        WHERE b.warehouse_id = v_wh AND b.part_id = v_line.part_id FOR UPDATE;
        v_qty := least(coalesce(v_avail, 0), v_line.qty_required);
        IF v_qty > 0 THEN
            INSERT INTO inventory.stock_reservation (warehouse_id, part_id, quantity, status, expires_at, service_order_line_id, created_by)
            VALUES (v_wh, v_line.part_id, v_qty, 'soft', now() + interval '7 days', v_line.id, v_user);
        END IF;
        IF v_qty < v_line.qty_required THEN v_all := false; END IF;   -- shortage -> visible in fleet.v_service_kit_shortage
    END LOOP;

    IF v_lines > 0 AND v_all THEN
        UPDATE fleet.service_order SET status = 'parts_reserved' WHERE id = v_so;
    END IF;
    RETURN v_so;
END $$;

-- US05 entry point: log a reading and draft all services that became due.
CREATE FUNCTION fleet.log_engine_hours(p_machine_id bigint, p_reading_hours numeric,
                                       p_read_at timestamptz DEFAULT now(),
                                       p_source fleet.reading_source DEFAULT 'manual')
RETURNS TABLE (service_order_id bigint, service_order_number varchar, due_engine_hours numeric, fully_reserved boolean)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
#variable_conflict use_column
DECLARE
    v_user uuid := util.require_role('SERVICE_ENGINEER', 'ADMINISTRATOR');
    v_due  record;
    v_so   bigint;
BEGIN
    INSERT INTO fleet.engine_hours_reading (machine_id, reading_hours, read_at, source, recorded_by)
    VALUES (p_machine_id, p_reading_hours, p_read_at, p_source, v_user);

    FOR v_due IN SELECT d.maintenance_plan_id AS plan_id, d.due_engine_hours AS due
                 FROM fleet.fn_maintenance_due(p_machine_id, 50) d
    LOOP
        v_so := fleet.create_service_order_draft(p_machine_id, v_due.plan_id, v_due.due);
        RETURN QUERY
            SELECT so.id, so.number, so.due_engine_hours, so.status = 'parts_reserved'
            FROM fleet.service_order so WHERE so.id = v_so;
    END LOOP;
END $$;
COMMENT ON FUNCTION fleet.log_engine_hours(bigint, numeric, timestamptz, fleet.reading_source) IS 'UC02: validates and stores a reading, then drafts service orders with soft-reserved kits for every plan due within 50 h. Atomic.';

CREATE FUNCTION fleet.trg_service_order_status_guard() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.status IS DISTINCT FROM OLD.status AND NOT (
          (OLD.status = 'draft'          AND NEW.status IN ('parts_reserved', 'in_progress', 'completed', 'cancelled'))
       OR (OLD.status = 'parts_reserved' AND NEW.status IN ('draft', 'in_progress', 'completed', 'cancelled'))
       OR (OLD.status = 'in_progress'    AND NEW.status IN ('completed', 'cancelled'))) THEN
        RAISE EXCEPTION 'Invalid service order transition % -> % (order %)', OLD.status, NEW.status, OLD.number USING ERRCODE = 'SS300';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER trg_service_order_status_guard BEFORE UPDATE ON fleet.service_order
    FOR EACH ROW EXECUTE FUNCTION fleet.trg_service_order_status_guard();

CREATE FUNCTION fleet.complete_service_order(p_service_order_id bigint, p_engine_hours numeric) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_user  uuid := util.require_role('SERVICE_ENGINEER', 'ADMINISTRATOR');
    v_so    fleet.service_order;
    v_line  record;
BEGIN
    SELECT * INTO v_so FROM fleet.service_order WHERE id = p_service_order_id FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'Service order % not found', p_service_order_id USING ERRCODE = 'no_data_found'; END IF;
    IF v_so.status NOT IN ('draft', 'parts_reserved', 'in_progress') THEN
        RAISE EXCEPTION 'Service order % is %', v_so.number, v_so.status USING ERRCODE = 'SS300';
    END IF;

    FOR v_line IN
        SELECT l.id, l.part_id, l.qty_required - l.qty_issued AS qty, p.unit_cost
        FROM fleet.service_order_line l JOIN catalog.part p ON p.id = l.part_id
        WHERE l.service_order_id = p_service_order_id AND l.qty_required > l.qty_issued
        ORDER BY l.part_id
    LOOP
        UPDATE inventory.stock_reservation SET status = 'consumed'
         WHERE service_order_line_id = v_line.id AND status IN ('soft', 'firm');
        INSERT INTO inventory.stock_movement (occurred_at, warehouse_id, part_id, movement_type_code, quantity,
                                              unit_cost, service_order_line_id, created_by)
        VALUES (now(), v_so.warehouse_id, v_line.part_id, 'ISSUE_SERVICE', v_line.qty, v_line.unit_cost, v_line.id, v_user);
        UPDATE fleet.service_order_line SET qty_issued = qty_required WHERE id = v_line.id;
    END LOOP;

    UPDATE fleet.service_order
       SET status = 'completed', completed_at = now(), completed_engine_hours = p_engine_hours
     WHERE id = p_service_order_id;
END $$;
COMMENT ON FUNCTION fleet.complete_service_order(bigint, numeric) IS 'Consumes the kit reservations, issues parts (ISSUE_SERVICE = demand) and closes the order.';

CREATE FUNCTION fleet.cancel_service_order(p_service_order_id bigint, p_reason text) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_user uuid := util.require_role('SERVICE_ENGINEER', 'BRAND_MANAGER', 'ADMINISTRATOR');
BEGIN
    PERFORM 1 FROM fleet.service_order WHERE id = p_service_order_id FOR UPDATE;
    UPDATE inventory.stock_reservation r SET status = 'released'
      FROM fleet.service_order_line l
     WHERE l.service_order_id = p_service_order_id AND r.service_order_line_id = l.id AND r.status IN ('soft', 'firm');
    UPDATE fleet.service_order SET status = 'cancelled', cancel_reason = p_reason WHERE id = p_service_order_id;
END $$;

-- ===========================================================================
-- PROCUREMENT
-- ===========================================================================

-- US12: supplier conditions are hard constraints of every PO line.
CREATE FUNCTION procurement.trg_po_line_validate() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    v_po    procurement.purchase_order;
    v_sp    catalog.supplier_part;
    v_price numeric;
BEGIN
    SELECT * INTO v_po FROM procurement.purchase_order
    WHERE id = coalesce(NEW.purchase_order_id, OLD.purchase_order_id);

    IF v_po.status NOT IN ('draft', 'pending_approval') THEN
        RAISE EXCEPTION 'Lines of purchase order % (%) cannot be changed', v_po.number, v_po.status USING ERRCODE = 'SS300';
    END IF;
    IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;

    SELECT * INTO v_sp FROM catalog.supplier_part WHERE supplier_id = v_po.supplier_id AND part_id = NEW.part_id;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Part % is not offered by supplier %', NEW.part_id, v_po.supplier_id USING ERRCODE = 'SS401';
    END IF;
    IF NEW.quantity < v_sp.moq OR mod(NEW.quantity, v_sp.pack_size) <> 0 THEN
        RAISE EXCEPTION 'Quantity % violates supplier conditions (MOQ %, pack size %)', NEW.quantity, v_sp.moq, v_sp.pack_size
              USING ERRCODE = 'SS400',
                    HINT = format('Nearest valid quantity: %s', ceil(greatest(NEW.quantity, v_sp.moq) / v_sp.pack_size) * v_sp.pack_size);
    END IF;
    IF NEW.unit_price IS NULL THEN   -- default price from the price list valid today
        SELECT sp.unit_price INTO v_price FROM catalog.supplier_price sp
        WHERE sp.supplier_part_id = v_sp.id AND sp.valid_during @> current_date;
        IF v_price IS NULL THEN
            RAISE EXCEPTION 'No valid price of supplier % for part % on %', v_po.supplier_id, NEW.part_id, current_date USING ERRCODE = 'SS402';
        END IF;
        NEW.unit_price := v_price;
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER trg_po_line_validate BEFORE INSERT OR DELETE OR UPDATE OF part_id, quantity, unit_price
    ON procurement.purchase_order_line FOR EACH ROW EXECUTE FUNCTION procurement.trg_po_line_validate();

CREATE FUNCTION procurement.trg_po_line_total() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_po bigint := coalesce(NEW.purchase_order_id, OLD.purchase_order_id);
BEGIN
    UPDATE procurement.purchase_order po
       SET total_amount = (SELECT coalesce(sum(l.line_amount), 0)
                           FROM procurement.purchase_order_line l WHERE l.purchase_order_id = v_po)
     WHERE po.id = v_po;
    RETURN NULL;
END $$;
CREATE TRIGGER trg_po_line_total AFTER INSERT OR DELETE OR UPDATE OF quantity, unit_price
    ON procurement.purchase_order_line FOR EACH ROW EXECUTE FUNCTION procurement.trg_po_line_total();

CREATE FUNCTION procurement.trg_po_status_guard() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.status IS DISTINCT FROM OLD.status AND NOT (
          (OLD.status = 'draft'              AND NEW.status IN ('pending_approval', 'cancelled'))
       OR (OLD.status = 'pending_approval'   AND NEW.status IN ('approved', 'draft', 'cancelled'))
       OR (OLD.status = 'approved'           AND NEW.status IN ('exported', 'partially_received', 'received', 'cancelled'))
       OR (OLD.status = 'exported'           AND NEW.status IN ('partially_received', 'received', 'cancelled'))
       OR (OLD.status = 'partially_received' AND NEW.status = 'received')) THEN
        RAISE EXCEPTION 'Invalid purchase order transition % -> % (order %)', OLD.status, NEW.status, OLD.number USING ERRCODE = 'SS300';
    END IF;
    IF OLD.status NOT IN ('draft', 'pending_approval') AND NEW.supplier_id IS DISTINCT FROM OLD.supplier_id THEN
        RAISE EXCEPTION 'Supplier of an approved purchase order cannot change' USING ERRCODE = 'SS300';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER trg_po_status_guard BEFORE UPDATE ON procurement.purchase_order
    FOR EACH ROW EXECUTE FUNCTION procurement.trg_po_status_guard();

CREATE FUNCTION procurement.submit_purchase_order(p_po_id bigint) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_user uuid := util.require_role('BRAND_MANAGER', 'INVENTORY_PLANNER');
BEGIN
    PERFORM 1 FROM procurement.purchase_order WHERE id = p_po_id FOR UPDATE;
    IF NOT EXISTS (SELECT 1 FROM procurement.purchase_order_line WHERE purchase_order_id = p_po_id) THEN
        RAISE EXCEPTION 'Purchase order % has no lines', p_po_id USING ERRCODE = 'SS300';
    END IF;
    UPDATE procurement.purchase_order SET status = 'pending_approval', submitted_at = now() WHERE id = p_po_id;
END $$;

CREATE FUNCTION procurement.approve_purchase_order(p_po_id bigint) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_user uuid := util.require_role('BRAND_MANAGER', 'SPONSOR');
    v_po   procurement.purchase_order;
    v_lead integer;
BEGIN
    SELECT * INTO v_po FROM procurement.purchase_order WHERE id = p_po_id FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'Purchase order % not found', p_po_id USING ERRCODE = 'no_data_found'; END IF;
    IF v_po.status <> 'pending_approval' THEN
        RAISE EXCEPTION 'Purchase order % is %, expected pending_approval', v_po.number, v_po.status USING ERRCODE = 'SS300';
    END IF;
    IF v_po.total_amount > 5000 AND v_po.created_by = v_user THEN
        RAISE EXCEPTION 'Four-eyes rule: % (EUR %) must be approved by another manager', v_po.number, v_po.total_amount USING ERRCODE = 'SS301';
    END IF;

    SELECT max(sp.lead_time_days) INTO v_lead
    FROM procurement.purchase_order_line l
    JOIN catalog.supplier_part sp ON sp.part_id = l.part_id AND sp.supplier_id = v_po.supplier_id
    WHERE l.purchase_order_id = p_po_id;

    UPDATE procurement.purchase_order
       SET status = 'approved', approved_by = v_user, approved_at = now(),
           expected_arrival_date = current_date + coalesce(v_lead, 0)
     WHERE id = p_po_id;
END $$;
COMMENT ON FUNCTION procurement.approve_purchase_order(bigint) IS 'US14: approves a submitted PO; > EUR 5,000 requires an approver other than the author (also a CHECK constraint).';

CREATE FUNCTION procurement.export_purchase_order(p_po_id bigint) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_user uuid := util.require_role('BRAND_MANAGER', 'INVENTORY_PLANNER');
BEGIN
    UPDATE procurement.purchase_order SET status = 'exported', exported_at = now() WHERE id = p_po_id;
END $$;

CREATE FUNCTION procurement.receive_purchase_order_line(p_line_id bigint, p_quantity numeric) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_user uuid := util.require_role('WAREHOUSE_KEEPER', 'ADMINISTRATOR');
    v_line procurement.purchase_order_line;
    v_po   procurement.purchase_order;
BEGIN
    SELECT * INTO v_line FROM procurement.purchase_order_line WHERE id = p_line_id;
    IF NOT FOUND THEN RAISE EXCEPTION 'PO line % not found', p_line_id USING ERRCODE = 'no_data_found'; END IF;
    SELECT * INTO v_po FROM procurement.purchase_order WHERE id = v_line.purchase_order_id FOR UPDATE;
    IF v_po.status NOT IN ('approved', 'exported', 'partially_received') THEN
        RAISE EXCEPTION 'Purchase order % is % and cannot be received', v_po.number, v_po.status USING ERRCODE = 'SS300';
    END IF;
    IF p_quantity <= 0 OR v_line.received_qty + p_quantity > v_line.quantity THEN
        RAISE EXCEPTION 'Receipt of % exceeds the open quantity % of line %', p_quantity, v_line.quantity - v_line.received_qty, p_line_id
              USING ERRCODE = 'check_violation';
    END IF;

    INSERT INTO inventory.stock_movement (occurred_at, warehouse_id, part_id, movement_type_code, quantity,
                                          unit_cost, purchase_order_line_id, created_by)
    VALUES (now(), v_po.warehouse_id, v_line.part_id, 'RECEIPT_PO', p_quantity, v_line.unit_price, p_line_id, v_user);
    UPDATE procurement.purchase_order_line SET received_qty = received_qty + p_quantity WHERE id = p_line_id;

    UPDATE procurement.purchase_order
       SET status = CASE WHEN EXISTS (SELECT 1 FROM procurement.purchase_order_line
                                      WHERE purchase_order_id = v_po.id AND received_qty < quantity)
                         THEN 'partially_received'::procurement.po_status
                         ELSE 'received'::procurement.po_status END
     WHERE id = v_po.id;
END $$;

CREATE FUNCTION procurement.cancel_purchase_order(p_po_id bigint, p_reason text) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_user uuid := util.require_role('BRAND_MANAGER', 'SPONSOR', 'ADMINISTRATOR');
BEGIN
    IF EXISTS (SELECT 1 FROM procurement.purchase_order_line WHERE purchase_order_id = p_po_id AND received_qty > 0) THEN
        RAISE EXCEPTION 'Purchase order % already has receipts and cannot be cancelled', p_po_id USING ERRCODE = 'SS300';
    END IF;
    PERFORM set_config('app.reason', p_reason, true);
    UPDATE procurement.purchase_order SET status = 'cancelled', cancel_reason = p_reason WHERE id = p_po_id;
END $$;

-- ===========================================================================
-- FORECASTING
-- ===========================================================================

-- Automatic model replacement: candidate must beat the best benchmark RMSSE by >= 10 % (KPI-02).
CREATE FUNCTION forecast.promote_model(p_candidate_id integer, p_min_improvement numeric DEFAULT 0.10) RETURNS boolean
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_cand  forecast.ml_model_version;
    v_score numeric;
    v_best  numeric;
BEGIN
    SELECT * INTO v_cand FROM forecast.ml_model_version WHERE id = p_candidate_id FOR UPDATE;
    IF NOT FOUND OR v_cand.status <> 'candidate' THEN
        RAISE EXCEPTION 'Model version % is not a candidate', p_candidate_id USING ERRCODE = 'SS300';
    END IF;

    -- series-weighted RMSSE over demand classes
    SELECT sum(rmsse * series_count) / sum(series_count) INTO v_score
    FROM forecast.benchmark_result WHERE model_version_id = p_candidate_id AND method = v_cand.algorithm;
    v_score := coalesce(v_score, v_cand.rmsse);

    SELECT min(w) INTO v_best FROM (
        SELECT sum(rmsse * series_count) / sum(series_count) AS w
        FROM forecast.benchmark_result
        WHERE model_version_id = p_candidate_id AND method <> v_cand.algorithm
        GROUP BY method) b;
    IF v_best IS NULL THEN
        RAISE EXCEPTION 'Model version % has no benchmark results', p_candidate_id USING ERRCODE = 'SS300';
    END IF;

    IF v_score <= v_best * (1 - p_min_improvement) THEN
        UPDATE forecast.ml_model_version SET status = 'retired', retired_at = now()
         WHERE status = 'active' AND coalesce(demand_class_code, '*') = coalesce(v_cand.demand_class_code, '*');
        UPDATE forecast.ml_model_version SET status = 'active', promoted_at = now() WHERE id = p_candidate_id;
        RETURN true;
    END IF;
    UPDATE forecast.ml_model_version SET status = 'rejected' WHERE id = p_candidate_id;
    RETURN false;
END $$;
COMMENT ON FUNCTION forecast.promote_model(integer, numeric) IS 'Promotes a candidate only if its weighted RMSSE is >= 10 % lower than the best of the 5 benchmarks; retires the previous active model atomically.';

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
                         FROM catalog.supplier_part sp WHERE sp.part_id = p_part_id), 0) AS days
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

-- UC01 steps 10-11 and A2 / US13: human decision on a recommendation.
CREATE FUNCTION forecast.decide_recommendation(p_recommendation_id bigint, p_decision text,
                                               p_final_qty numeric DEFAULT NULL, p_reason text DEFAULT NULL)
RETURNS bigint
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_user   uuid := util.require_role('BRAND_MANAGER', 'INVENTORY_PLANNER');
    v_rec    forecast.recommendation;
    v_qty    numeric;
    v_status forecast.recommendation_status;
    v_doc    bigint;
BEGIN
    IF p_decision NOT IN ('accept', 'override', 'reject') THEN
        RAISE EXCEPTION 'Unknown decision %', p_decision USING ERRCODE = 'invalid_parameter_value';
    END IF;
    SELECT * INTO v_rec FROM forecast.recommendation WHERE id = p_recommendation_id FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'Recommendation % not found', p_recommendation_id USING ERRCODE = 'no_data_found'; END IF;
    IF v_rec.status <> 'open' THEN
        RAISE EXCEPTION 'Recommendation % is already %', p_recommendation_id, v_rec.status USING ERRCODE = 'SS300';
    END IF;
    PERFORM set_config('app.reason', coalesce(p_reason, ''), true);

    IF p_decision = 'reject' THEN
        UPDATE forecast.recommendation SET status = 'rejected', decided_by = v_user, decided_at = now(), decision_reason = p_reason
         WHERE id = p_recommendation_id;
        RETURN NULL;
    END IF;

    v_qty    := CASE WHEN p_decision = 'accept' THEN v_rec.recommended_qty ELSE p_final_qty END;
    v_status := CASE WHEN p_decision = 'accept' THEN 'accepted' ELSE 'overridden' END;

    IF v_rec.kind = 'transfer' THEN
        INSERT INTO inventory.transfer_order (from_warehouse_id, to_warehouse_id, requested_by)
        VALUES (v_rec.donor_warehouse_id, v_rec.warehouse_id, v_user) RETURNING id INTO v_doc;
        INSERT INTO inventory.transfer_order_line (transfer_order_id, part_id, quantity, proposed_qty, override_reason)
        VALUES (v_doc, v_rec.part_id, v_qty, v_rec.recommended_qty, CASE WHEN v_status = 'overridden' THEN p_reason END);
        PERFORM inventory.approve_transfer(v_doc);           -- reserves at the donor (UC01 step 11)
        UPDATE forecast.recommendation
           SET status = v_status, final_qty = v_qty, decided_by = v_user, decided_at = now(),
               decision_reason = p_reason, transfer_order_id = v_doc
         WHERE id = p_recommendation_id;
    ELSE
        -- consolidate into an open draft PO of the same supplier and hub (US13)
        SELECT id INTO v_doc FROM procurement.purchase_order
        WHERE supplier_id = v_rec.supplier_id AND warehouse_id = v_rec.warehouse_id
          AND status = 'draft' AND NOT is_urgent
        ORDER BY created_at LIMIT 1 FOR UPDATE;
        IF v_doc IS NULL THEN
            INSERT INTO procurement.purchase_order (supplier_id, warehouse_id, created_by)
            VALUES (v_rec.supplier_id, v_rec.warehouse_id, v_user) RETURNING id INTO v_doc;
        END IF;
        INSERT INTO procurement.purchase_order_line AS l (purchase_order_id, part_id, quantity, proposed_qty, override_reason)
        VALUES (v_doc, v_rec.part_id, v_qty, v_rec.recommended_qty, CASE WHEN v_status = 'overridden' THEN p_reason END)
        ON CONFLICT (purchase_order_id, part_id) DO UPDATE
            SET quantity        = l.quantity + EXCLUDED.quantity,
                proposed_qty    = coalesce(l.proposed_qty, 0) + EXCLUDED.proposed_qty,
                override_reason = coalesce(EXCLUDED.override_reason, l.override_reason);
        UPDATE forecast.recommendation
           SET status = v_status, final_qty = v_qty, decided_by = v_user, decided_at = now(),
               decision_reason = p_reason, purchase_order_id = v_doc
         WHERE id = p_recommendation_id;
    END IF;
    RETURN v_doc;
END $$;
COMMENT ON FUNCTION forecast.decide_recommendation(bigint, text, numeric, text) IS 'Accept / override (with reason) / reject a recommendation; creates and approves a transfer or adds a line to a consolidated draft PO, in one transaction.';

CREATE PROCEDURE forecast.expire_open_recommendations(p_older_than interval DEFAULT interval '14 days')
LANGUAGE sql AS $$
    UPDATE forecast.recommendation SET status = 'expired'
    WHERE status = 'open' AND created_at < now() - p_older_than;
$$;
