-- =============================================================================
-- Inventory 0026 — the employee copy and the no-self-granting rule in the database (ADR 0029 amendment 2, PR 7j1c).
-- Inventory no longer asks Identity synchronously who an employee is. inventory.employee_snapshot is fed by
-- EmployeeDirectoryChanged (newest event wins per employee) and filled the first time by identity.employee-publish; it
-- holds the display name (personal data: Identity's anonymisation sends the anonymised name). It is read-only:
-- inventory.apply_employees (SECURITY DEFINER), which the consumer calls, is its only write path, executable only by
-- the logins that host the Inventory module.
-- ADR 0028 forbids anyone to change their own rights-bearing assignments. The API checked it; now a row trigger on the
-- rights-bearing responsibles tables refuses it too (SS301), reading the copy for the acting account's employee. The
-- table owner (migrations, the demo seed, test setup) is exempt, as in inventory/0019.
-- =============================================================================

GRANT SELECT ON inventory.employee_snapshot TO app_read, app_ml;

CREATE FUNCTION inventory.apply_employees(
    p_occurred_at timestamptz, p_ids uuid[], p_codes text[], p_names text[], p_active boolean[], p_accounts uuid[]) RETURNS integer
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_rows integer;
BEGIN
    INSERT INTO inventory.employee_snapshot AS s (employee_id, code, display_name, is_active, account_id, source_occurred_at, updated_at)
    SELECT x.employee_id, x.code, x.display_name, x.is_active, x.account_id, p_occurred_at, now()
    FROM unnest(p_ids, p_codes, p_names, p_active, p_accounts) AS x (employee_id, code, display_name, is_active, account_id)
    ON CONFLICT (employee_id) DO UPDATE
       SET code = EXCLUDED.code, display_name = EXCLUDED.display_name, is_active = EXCLUDED.is_active, account_id = EXCLUDED.account_id,
           source_occurred_at = EXCLUDED.source_occurred_at, updated_at = now()
     WHERE s.source_occurred_at < EXCLUDED.source_occurred_at;   -- an older or repeated event never overwrites a newer one
    GET DIAGNOSTICS v_rows = ROW_COUNT;
    RETURN v_rows;
END $$;
COMMENT ON FUNCTION inventory.apply_employees(timestamptz, uuid[], text[], text[], boolean[], uuid[]) IS
    'Applies one EmployeeDirectoryChanged to the employee copy, newest event wins; the only write path of inventory.employee_snapshot.';

REVOKE EXECUTE ON FUNCTION inventory.apply_employees(timestamptz, uuid[], text[], text[], boolean[], uuid[]) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION inventory.apply_employees(timestamptz, uuid[], text[], text[], boolean[], uuid[]) TO smartspare_api, smartspare_inventory;

CREATE FUNCTION inventory.trg_no_self_grant() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    v_employee uuid;
    v_kind     text;
    v_role     text;
    v_me       uuid := util.current_app_user();
BEGIN
    IF TG_OP = 'DELETE' THEN
        v_employee := OLD.employee_id; v_kind := OLD.record_kind; v_role := OLD.assignment_role;
    ELSE
        v_employee := NEW.employee_id; v_kind := NEW.record_kind; v_role := NEW.assignment_role;
    END IF;
    IF v_me IS NOT NULL
       AND NOT pg_has_role(current_user, (SELECT c.relowner FROM pg_class c WHERE c.oid = TG_RELID), 'MEMBER')
       AND EXISTS (SELECT 1 FROM ref.assignment_role ar WHERE ar.record_kind = v_kind AND ar.code = v_role AND ar.grants_scope)
       AND EXISTS (SELECT 1 FROM inventory.employee_snapshot e WHERE e.account_id = v_me AND e.employee_id = v_employee) THEN
        RAISE EXCEPTION 'Nobody changes their own rights-bearing assignments' USING ERRCODE = 'SS301';
    END IF;
    RETURN coalesce(NEW, OLD);
END $$;
COMMENT ON FUNCTION inventory.trg_no_self_grant() IS
    'Row trigger of the rights-bearing responsibles: the acting account''s employee (from the employee copy) cannot gain or lose a rights-bearing assignment (SS301, ADR 0028); the table owner is exempt.';

CREATE TRIGGER trg_no_self_grant BEFORE INSERT OR DELETE ON catalog.brand_responsible
    FOR EACH ROW EXECUTE FUNCTION inventory.trg_no_self_grant();
CREATE TRIGGER trg_no_self_grant BEFORE INSERT OR DELETE ON inventory.warehouse_responsible
    FOR EACH ROW EXECUTE FUNCTION inventory.trg_no_self_grant();
