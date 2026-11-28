-- =============================================================================
-- Inventory 0019 — the permission of every responsibles change checked in the database too (ADR 0028, defence in
-- depth behind the API). The endpoints require Inventory.Brand.ManageAssignments, Inventory.Warehouse.ManageAssignments
-- and Inventory.Manufacturer.Edit; until now the tables only had INSERT and DELETE grants to app_write, so a write
-- outside those endpoints was not checked. A statement trigger now requires the same permission from the session
-- (app.user_permissions, util.require_permission, SS301). A brand-manager assignment carries approval rights, and an
-- inventory planner may edit the brand record (Inventory.Brand.Edit) but not assign its responsibles.
-- The table owner (migrations, the demo seed, test setup) is exempt; every application login is checked.
-- =============================================================================

CREATE FUNCTION inventory.trg_require_assignment_permission() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF NOT pg_has_role(current_user, (SELECT c.relowner FROM pg_class c WHERE c.oid = TG_RELID), 'MEMBER') THEN
        PERFORM util.require_permission(TG_ARGV[0]);
    END IF;
    RETURN NULL;
END $$;
COMMENT ON FUNCTION inventory.trg_require_assignment_permission() IS
    'Statement trigger of the responsibles tables: the session must hold the permission given as argument (SS301); the table owner is exempt (ADR 0028).';

CREATE TRIGGER trg_require_permission BEFORE INSERT OR DELETE ON catalog.brand_responsible
    FOR EACH STATEMENT EXECUTE FUNCTION inventory.trg_require_assignment_permission('Inventory.Brand.ManageAssignments');
CREATE TRIGGER trg_require_permission BEFORE INSERT OR DELETE ON inventory.warehouse_responsible
    FOR EACH STATEMENT EXECUTE FUNCTION inventory.trg_require_assignment_permission('Inventory.Warehouse.ManageAssignments');
CREATE TRIGGER trg_require_permission BEFORE INSERT OR DELETE ON catalog.manufacturer_responsible
    FOR EACH STATEMENT EXECUTE FUNCTION inventory.trg_require_assignment_permission('Inventory.Manufacturer.Edit');
