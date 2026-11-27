-- =============================================================================
-- Fleet 0010 — the business functions check permissions, not role codes (ADR 0028, PR 7x2). Each function is redefined
-- as it was last defined, with util.require_role(...) replaced by util.require_permission(...) with the permission its
-- endpoint requires (PolicySqlPermissionParityTests). The signatures and grants are unchanged.
-- Generated from the latest definitions; applied scripts stay untouched (#152).
-- =============================================================================

-- fleet.cancel_service_order, as last defined in fleet/0001_fleet_logic.sql, with the permission check.
CREATE OR REPLACE FUNCTION fleet.cancel_service_order(p_service_order_id bigint, p_reason text) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_user uuid := util.require_permission('Fleet.ServiceOrder.Cancel');
BEGIN
    PERFORM 1 FROM fleet.service_order WHERE id = p_service_order_id FOR UPDATE;
    UPDATE fleet.service_order_line SET qty_reserved = 0 WHERE service_order_id = p_service_order_id;
    UPDATE fleet.service_order SET status = 'cancelled', cancel_reason = p_reason WHERE id = p_service_order_id;
END $$;

-- fleet.complete_service_order, as last defined in fleet/0001_fleet_logic.sql, with the permission check.
CREATE OR REPLACE FUNCTION fleet.complete_service_order(p_service_order_id bigint, p_engine_hours numeric) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_user  uuid := util.require_permission('Fleet.ServiceOrder.Complete');
    v_so    fleet.service_order;
BEGIN
    SELECT * INTO v_so FROM fleet.service_order WHERE id = p_service_order_id FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'Service order % not found', p_service_order_id USING ERRCODE = 'no_data_found'; END IF;
    IF v_so.status NOT IN ('draft', 'parts_reserved', 'in_progress') THEN
        RAISE EXCEPTION 'Service order % is %', v_so.number, v_so.status USING ERRCODE = 'SS300';
    END IF;

    UPDATE fleet.service_order
       SET status = 'completed', completed_at = now(), completed_engine_hours = p_engine_hours
     WHERE id = p_service_order_id;
END $$;

-- fleet.create_service_order_draft, as last defined in fleet/0001_fleet_logic.sql, with the permission check.
CREATE OR REPLACE FUNCTION fleet.create_service_order_draft(p_machine_id bigint, p_plan_id integer, p_due_engine_hours numeric)
RETURNS bigint
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_user    uuid := util.require_permission('Fleet.ServiceOrder.Create');
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

-- fleet.log_engine_hours, as last defined in fleet/0001_fleet_logic.sql, with the permission check.
CREATE OR REPLACE FUNCTION fleet.log_engine_hours(p_machine_id bigint, p_reading_hours numeric,
                                       p_read_at timestamptz DEFAULT now(),
                                       p_source fleet.reading_source DEFAULT 'manual')
RETURNS TABLE (service_order_id bigint, service_order_number varchar, due_engine_hours numeric, fully_reserved boolean)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
#variable_conflict use_column
DECLARE
    v_user uuid := util.require_permission('Fleet.EngineHours.Record');
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
