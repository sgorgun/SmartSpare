-- =============================================================================
-- Inventory 0013 — responsible employees of brands and warehouses (ADR 0026 decision 3, STAGE_7_PLAN 3.7). The
-- tables are created by EF (migration ResponsibleEmployees); this script adds the audit trail and the privileges.
-- The application writes them as app_write; the rights-bearing ones reach Identity as ResponsibilitiesChanged.
-- =============================================================================

CREATE TRIGGER trg_audit AFTER INSERT OR UPDATE OR DELETE ON catalog.brand_responsible
    FOR EACH ROW EXECUTE FUNCTION audit.trg_row_audit();
CREATE TRIGGER trg_audit AFTER INSERT OR UPDATE OR DELETE ON inventory.warehouse_responsible
    FOR EACH ROW EXECUTE FUNCTION audit.trg_row_audit();

GRANT SELECT ON catalog.brand_responsible, inventory.warehouse_responsible TO app_read;
GRANT INSERT, DELETE ON catalog.brand_responsible, inventory.warehouse_responsible TO app_write;
GRANT USAGE ON SEQUENCE catalog.brand_responsible_id_seq, inventory.warehouse_responsible_id_seq TO app_write;
