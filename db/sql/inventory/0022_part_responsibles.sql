-- =============================================================================
-- Inventory 0022 — responsible employees of parts (STAGE_7_PLAN 3.7, PR 7i1b), the fourth record kind of the shared
-- pattern. The table is created by EF (migration PartResponsibles); this script adds:
--   * the audit trail and the privileges, as for the other kinds;
--   * the permission check of every change in the database (Inventory.Part.Edit; the roles give no rights, ADR 0028);
--   * a fourth reason in the part archive guard, so the database and the API list the same conditions (#215 rule).
-- =============================================================================

CREATE TRIGGER trg_audit AFTER INSERT OR UPDATE OR DELETE ON catalog.part_responsible
    FOR EACH ROW EXECUTE FUNCTION audit.trg_row_audit();
CREATE TRIGGER trg_require_permission BEFORE INSERT OR DELETE ON catalog.part_responsible
    FOR EACH STATEMENT EXECUTE FUNCTION inventory.trg_require_assignment_permission('Inventory.Part.Edit');

GRANT SELECT ON catalog.part_responsible TO app_read;
GRANT INSERT, DELETE ON catalog.part_responsible TO app_write;
GRANT USAGE ON SEQUENCE catalog.part_responsible_id_seq TO app_write;

CREATE OR REPLACE FUNCTION catalog.trg_part_catalogue_guard() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    v_reasons text[];
    v_count   bigint;
BEGIN
    IF TG_OP = 'INSERT' OR NEW.brand_id IS DISTINCT FROM OLD.brand_id THEN
        IF EXISTS (SELECT 1 FROM catalog.brand b WHERE b.id = NEW.brand_id AND b.is_archived) THEN
            RAISE EXCEPTION 'Brand % is archived and cannot be chosen', NEW.brand_id USING ERRCODE = 'SS500';
        END IF;
    END IF;

    IF NEW.manufacturer_id IS NOT NULL AND (TG_OP = 'INSERT' OR NEW.manufacturer_id IS DISTINCT FROM OLD.manufacturer_id) THEN
        IF EXISTS (SELECT 1 FROM catalog.manufacturer m WHERE m.id = NEW.manufacturer_id AND m.is_archived) THEN
            RAISE EXCEPTION 'Manufacturer % is archived and cannot be chosen', NEW.manufacturer_id USING ERRCODE = 'SS500';
        END IF;
    END IF;

    IF TG_OP = 'UPDATE' AND NEW.is_archived AND NOT OLD.is_archived THEN
        SELECT coalesce(array_agg(CASE WHEN s.reserved_qty > 0
                                       THEN format('on hand %s, reserved %s at %s', trim_scale(s.on_hand_qty), trim_scale(s.reserved_qty), w.code)
                                       ELSE format('on hand %s at %s', trim_scale(s.on_hand_qty), w.code) END ORDER BY w.code), '{}')
          INTO v_reasons
          FROM inventory.stock_balance s JOIN inventory.warehouse w ON w.id = s.warehouse_id
         WHERE s.part_id = NEW.id AND (s.on_hand_qty > 0 OR s.reserved_qty > 0);

        SELECT count(DISTINCT t.id) INTO v_count
          FROM inventory.transfer_order t JOIN inventory.transfer_order_line l ON l.transfer_order_id = t.id
         WHERE l.part_id = NEW.id AND t.status IN ('proposed', 'approved', 'dispatched');
        IF v_count > 0 THEN
            v_reasons := v_reasons || format('%s open transfer(s)', v_count);
        END IF;

        SELECT count(*) INTO v_count
          FROM inventory.stock_reservation r
         WHERE r.part_id = NEW.id AND r.service_order_line_id IS NOT NULL AND r.status IN ('soft', 'firm');
        IF v_count > 0 THEN
            v_reasons := v_reasons || format('%s kit hold(s) for service orders', v_count);
        END IF;

        SELECT count(*) INTO v_count FROM catalog.part_responsible r WHERE r.part_id = NEW.id;
        IF v_count > 0 THEN
            v_reasons := v_reasons || format('%s responsible employee(s); remove them first', v_count);
        END IF;

        IF cardinality(v_reasons) > 0 THEN
            RAISE EXCEPTION 'Part % cannot be archived: %', NEW.id, array_to_string(v_reasons, '; ') USING ERRCODE = 'SS501';
        END IF;
    END IF;

    RETURN NEW;
END $$;
COMMENT ON FUNCTION catalog.trg_part_catalogue_guard() IS
    'Parts: an archived brand or manufacturer cannot be chosen (SS500); a part with stock, open transfers, kit holds or responsible employees cannot be archived (SS501 with every reason).';
