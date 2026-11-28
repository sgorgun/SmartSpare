-- =============================================================================
-- Fleet 0012 — the warehouse copy and the archived-hub guard (ADR 0029, STAGE_7_PLAN 12.2, PR 7j1).
-- Warehouses are an Inventory catalogue; Fleet keeps fleet.warehouse_snapshot, fed by WarehouseCatalogChanged with the
-- same ids, for hub codes and for its own guard: a new service order cannot be placed at an archived hub, and a machine
-- cannot be created with, or moved to, an archived home hub (SS500). A hub missing from the copy is not known to be
-- archived and is allowed, so a database upgraded before inventory.warehouse-catalog-publish ran keeps working.
-- The copy is read-only: fleet.apply_warehouses (SECURITY DEFINER), which the consumer calls, is its only write path,
-- executable only by the logins that host the Fleet module (as fleet.apply_brands).
-- =============================================================================

GRANT SELECT ON fleet.warehouse_snapshot TO app_read, app_ml;

CREATE FUNCTION fleet.apply_warehouses(
    p_occurred_at timestamptz, p_ids smallint[], p_codes text[], p_names text[], p_central boolean[], p_archived boolean[])
RETURNS integer
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_rows integer;
BEGIN
    INSERT INTO fleet.warehouse_snapshot AS s (warehouse_id, code, name, is_central_hub, is_archived, source_occurred_at, updated_at)
    SELECT w.warehouse_id, w.code, w.name, w.is_central_hub, w.is_archived, p_occurred_at, now()
    FROM unnest(p_ids, p_codes, p_names, p_central, p_archived) AS w (warehouse_id, code, name, is_central_hub, is_archived)
    ON CONFLICT (warehouse_id) DO UPDATE
       SET code = EXCLUDED.code, name = EXCLUDED.name, is_central_hub = EXCLUDED.is_central_hub, is_archived = EXCLUDED.is_archived,
           source_occurred_at = EXCLUDED.source_occurred_at, updated_at = now()
     WHERE s.source_occurred_at < EXCLUDED.source_occurred_at;   -- an older or repeated event never overwrites a newer one
    GET DIAGNOSTICS v_rows = ROW_COUNT;
    RETURN v_rows;
END $$;
COMMENT ON FUNCTION fleet.apply_warehouses(timestamptz, smallint[], text[], text[], boolean[], boolean[]) IS
    'Applies one WarehouseCatalogChanged to the warehouse copy, newest event wins; the only write path of fleet.warehouse_snapshot.';

REVOKE EXECUTE ON FUNCTION fleet.apply_warehouses(timestamptz, smallint[], text[], text[], boolean[], boolean[]) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION fleet.apply_warehouses(timestamptz, smallint[], text[], text[], boolean[], boolean[]) TO smartspare_api, smartspare_fleet;

-- The guard reads the copy as its owner: the helper and the trigger functions are SECURITY DEFINER, so every login that
-- writes service orders or machines gets the same answer without EXECUTE on the helper or SELECT on the copy.
CREATE FUNCTION fleet.require_hub_not_archived(p_warehouse_id smallint, p_what text) RETURNS void
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_code text;
BEGIN
    SELECT w.code INTO v_code FROM fleet.warehouse_snapshot w WHERE w.warehouse_id = p_warehouse_id AND w.is_archived;
    IF FOUND THEN
        RAISE EXCEPTION 'The % % is archived and cannot be chosen', p_what, v_code USING ERRCODE = 'SS500';
    END IF;
END $$;
COMMENT ON FUNCTION fleet.require_hub_not_archived(smallint, text) IS
    'Raises SS500 when the warehouse copy says the hub is archived; a hub missing from the copy is allowed.';

CREATE FUNCTION fleet.trg_service_order_hub_not_archived() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
BEGIN
    PERFORM fleet.require_hub_not_archived(NEW.warehouse_id, 'hub');
    RETURN NEW;
END $$;

CREATE TRIGGER trg_service_order_hub_not_archived
    BEFORE INSERT ON fleet.service_order
    FOR EACH ROW EXECUTE FUNCTION fleet.trg_service_order_hub_not_archived();
COMMENT ON TRIGGER trg_service_order_hub_not_archived ON fleet.service_order IS
    'A new service order cannot be placed at an archived hub (SS500, ADR 0029).';

CREATE FUNCTION fleet.trg_machine_home_hub_not_archived() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
BEGIN
    IF TG_OP = 'INSERT' OR NEW.home_warehouse_id IS DISTINCT FROM OLD.home_warehouse_id THEN
        PERFORM fleet.require_hub_not_archived(NEW.home_warehouse_id, 'home hub');
    END IF;
    RETURN NEW;
END $$;

CREATE TRIGGER trg_machine_home_hub_not_archived
    BEFORE INSERT OR UPDATE OF home_warehouse_id ON fleet.machine
    FOR EACH ROW EXECUTE FUNCTION fleet.trg_machine_home_hub_not_archived();
COMMENT ON TRIGGER trg_machine_home_hub_not_archived ON fleet.machine IS
    'A machine cannot be created with, or moved to, an archived home hub (SS500, ADR 0029).';
