-- =============================================================================
-- Inventory 0018 — responsible employees of manufacturers (STAGE_7_PLAN 3.7, PR 7h4), the third record kind of the
-- shared pattern after brands and warehouses: audited like them; its one role, ACCOUNT_MANAGER, grants no rights.
-- =============================================================================

CREATE TRIGGER trg_audit AFTER INSERT OR UPDATE OR DELETE ON catalog.manufacturer_responsible
    FOR EACH ROW EXECUTE FUNCTION audit.trg_row_audit();

GRANT SELECT ON catalog.manufacturer_responsible TO app_read;
GRANT INSERT, DELETE ON catalog.manufacturer_responsible TO app_write;
GRANT USAGE ON SEQUENCE catalog.manufacturer_responsible_id_seq TO app_write;
