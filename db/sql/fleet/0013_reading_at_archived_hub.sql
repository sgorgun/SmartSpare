-- =============================================================================
-- Fleet 0013 — a reading is never lost at an archived hub (ADR 0029 amendment, PR 7j1b; product owner, 28 November 2026).
-- An engine-hours reading is a field fact: only the reading rules SS100–SS104 may reject it. When the machine's home hub
-- is archived in Fleet's warehouse copy, fleet.log_engine_hours stores the reading and drafts nothing, instead of letting
-- trg_service_order_hub_not_archived refuse the whole reading; the API returns a warning and the due list flags the
-- machine. Otherwise unchanged from fleet/0010_require_permission.sql.
-- =============================================================================

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

    IF EXISTS (SELECT 1 FROM fleet.machine m JOIN fleet.warehouse_snapshot w ON w.warehouse_id = m.home_warehouse_id
               WHERE m.id = p_machine_id AND w.is_archived) THEN
        RETURN;   -- the reading stays; no order can be drafted at an archived hub (ADR 0029)
    END IF;

    FOR v_due IN SELECT d.maintenance_plan_id AS plan_id, d.due_engine_hours AS due
                 FROM fleet.fn_maintenance_due(p_machine_id, 50) d
    LOOP
        v_so := fleet.create_service_order_draft(p_machine_id, v_due.plan_id, v_due.due);
        RETURN QUERY
            SELECT so.id, so.number, so.due_engine_hours, so.status = 'parts_reserved'
            FROM fleet.service_order so WHERE so.id = v_so;
    END LOOP;
END $$;
COMMENT ON FUNCTION fleet.log_engine_hours(bigint, numeric, timestamptz, fleet.reading_source) IS
    'UC02: validates and stores an engine-hours reading and drafts every plan due within 50 h; at a home hub archived in the warehouse copy it stores the reading and drafts nothing (ADR 0029).';
