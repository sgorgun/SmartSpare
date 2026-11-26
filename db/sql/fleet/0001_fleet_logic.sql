-- =============================================================================
-- Fleet 0001 — engine hours, maintenance due, service orders.
-- Statements copied verbatim from V001 (schema comment), V006, V011, V012 and V015.
-- uq_customer_erp_ref_hash is created here: it is a UNIQUE constraint on a nullable column, which
-- EF cannot model as an alternate key. fleet.v_machine_maintenance_status is module-local since stage 6 (X10);
-- fleet.v_service_kit_shortage too (X9), created in 0005 after its column. The transitional draft, completion and
-- cancellation keep Fleet's copy service_order_line.qty_reserved current until the event flow replaces them (6h);
-- drafting is module-local since 6g2.
-- =============================================================================

COMMENT ON SCHEMA fleet       IS 'Core layer / Fleet & Maintenance module: machines, engine hours, maintenance plans, service orders.';

-- V006: inline constraint of fleet.customer.
ALTER TABLE fleet.customer
    ADD CONSTRAINT uq_customer_erp_ref_hash UNIQUE (erp_ref_hash);

-- updated_at and document-number triggers (V006, V011)
CREATE TRIGGER trg_machine_updated_at BEFORE UPDATE ON fleet.machine
    FOR EACH ROW EXECUTE FUNCTION util.trg_set_updated_at();
CREATE TRIGGER trg_service_order_updated_at BEFORE UPDATE ON fleet.service_order
    FOR EACH ROW EXECUTE FUNCTION util.trg_set_updated_at();
CREATE TRIGGER trg_service_order_number BEFORE INSERT ON fleet.service_order
    FOR EACH ROW EXECUTE FUNCTION util.trg_assign_document_number('SO');

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

-- Machine card (WF03), module-local since stage 6 (MODULE_MAPPING X10): the home hub as an id, its code is resolved in
-- C# through Inventory.Contracts.IWarehouseQueries. The API computes the full due calculation in C#
-- (MaintenanceDueCalculator); this view keeps the SQL answer for reports and ad-hoc queries.
CREATE VIEW fleet.v_machine_maintenance_status AS
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
JOIN ref.brand b                 ON b.id = mm.brand_id
JOIN ref.machine_type mt         ON mt.id = mm.machine_type_id
JOIN fleet.customer c            ON c.id = m.customer_id
JOIN ref.operating_condition oc  ON oc.id = m.operating_condition_id
LEFT JOIN LATERAL (SELECT * FROM fleet.fn_maintenance_due(m.id, 1000000) LIMIT 1) nd ON true
WHERE m.status = 'active';
COMMENT ON VIEW fleet.v_machine_maintenance_status IS 'Machine card (WF03): current engine hours and the next not yet planned OEM service.';

-- US06 / UC02 steps 5-6: service order draft with the plan's kit. Module-local since stage 6 (PR 6g2, MODULE_MAPPING
-- X6): the caller publishes ServiceOrderDrafted, Inventory reserves the kit (inventory.reserve_service_kit) and answers
-- with ServiceKitReserved, which sets qty_reserved and parts_reserved. The order starts as a draft without holds.
CREATE FUNCTION fleet.create_service_order_draft(p_machine_id bigint, p_plan_id integer, p_due_engine_hours numeric)
RETURNS bigint
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_user    uuid := util.require_role('SERVICE_ENGINEER', 'BRAND_MANAGER', 'ADMINISTRATOR');
    v_wh      smallint;
    v_hours   numeric;
    v_so      bigint;
BEGIN
    SELECT home_warehouse_id, current_engine_hours INTO v_wh, v_hours
    FROM fleet.machine WHERE id = p_machine_id;

    INSERT INTO fleet.service_order (machine_id, maintenance_plan_id, warehouse_id, due_engine_hours, trigger_engine_hours, created_by)
    VALUES (p_machine_id, p_plan_id, v_wh, p_due_engine_hours, v_hours, v_user)
    RETURNING id INTO v_so;

    INSERT INTO fleet.service_order_line (service_order_id, part_id, qty_required)
    SELECT v_so, k.part_id, k.quantity
    FROM fleet.maintenance_plan_part k WHERE k.maintenance_plan_id = p_plan_id;

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
COMMENT ON FUNCTION fleet.log_engine_hours(bigint, numeric, timestamptz, fleet.reading_source) IS 'UC02: validates and stores a reading, then drafts service orders with their kits for every plan due within 50 h (Inventory reserves the kits on ServiceOrderDrafted). Atomic.';

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

-- TRANSITIONAL: replaced by ServiceOrderCompleted -> ServiceKitIssued in stage 6, #12 (writes inventory; MODULE_MAPPING 3.1 X7)
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
        UPDATE fleet.service_order_line SET qty_issued = qty_required, qty_reserved = 0 WHERE id = v_line.id;
    END LOOP;

    UPDATE fleet.service_order
       SET status = 'completed', completed_at = now(), completed_engine_hours = p_engine_hours
     WHERE id = p_service_order_id;
END $$;
COMMENT ON FUNCTION fleet.complete_service_order(bigint, numeric) IS 'Consumes the kit reservations, issues parts (ISSUE_SERVICE = demand) and closes the order.';

-- TRANSITIONAL: replaced by ServiceOrderCancelled in stage 6, #12 (releases inventory.stock_reservation; MODULE_MAPPING 3.1 X8)
CREATE FUNCTION fleet.cancel_service_order(p_service_order_id bigint, p_reason text) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_user uuid := util.require_role('SERVICE_ENGINEER', 'BRAND_MANAGER', 'ADMINISTRATOR');
BEGIN
    PERFORM 1 FROM fleet.service_order WHERE id = p_service_order_id FOR UPDATE;
    UPDATE inventory.stock_reservation r SET status = 'released'
      FROM fleet.service_order_line l
     WHERE l.service_order_id = p_service_order_id AND r.service_order_line_id = l.id AND r.status IN ('soft', 'firm');
    UPDATE fleet.service_order_line SET qty_reserved = 0 WHERE service_order_id = p_service_order_id;
    UPDATE fleet.service_order SET status = 'cancelled', cancel_reason = p_reason WHERE id = p_service_order_id;
END $$;

-- Audit triggers (US18), V015.
CREATE TRIGGER trg_audit AFTER INSERT OR UPDATE OR DELETE ON fleet.machine
    FOR EACH ROW EXECUTE FUNCTION audit.trg_row_audit('current_engine_hours', 'engine_hours_read_at');
CREATE TRIGGER trg_audit AFTER INSERT OR UPDATE OR DELETE ON fleet.maintenance_plan
    FOR EACH ROW EXECUTE FUNCTION audit.trg_row_audit();
CREATE TRIGGER trg_audit AFTER INSERT OR UPDATE OR DELETE ON fleet.maintenance_plan_part
    FOR EACH ROW EXECUTE FUNCTION audit.trg_row_audit();
CREATE TRIGGER trg_audit AFTER INSERT OR UPDATE OR DELETE ON fleet.service_order
    FOR EACH ROW EXECUTE FUNCTION audit.trg_row_audit();
