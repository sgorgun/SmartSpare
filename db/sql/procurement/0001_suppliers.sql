-- =============================================================================
-- Procurement 0001 — suppliers, supplier conditions and prices (moved from catalog).
-- ADR 0011 D2: catalog.supplier, supplier_part and supplier_price now live in procurement. Statements
-- copied from V003 and V015 with the schema name changed; the EF model creates the tables. The FK
-- supplier_part.part_id -> catalog.part is not created (cross-module, MODULE_MAPPING 2.3).
-- =============================================================================

COMMENT ON SCHEMA procurement IS 'Core layer / Procurement module: purchase orders and their lines.';

-- EXCLUDE constraint (btree_gist), V003: non-overlapping price periods per supplier-part.
ALTER TABLE procurement.supplier_price
    ADD CONSTRAINT ex_supplier_price_no_overlap EXCLUDE USING gist (supplier_part_id WITH =, valid_during WITH &&);

-- updated_at triggers (V003)
CREATE TRIGGER trg_supplier_updated_at BEFORE UPDATE ON procurement.supplier
    FOR EACH ROW EXECUTE FUNCTION util.trg_set_updated_at();
CREATE TRIGGER trg_supplier_part_updated_at BEFORE UPDATE ON procurement.supplier_part
    FOR EACH ROW EXECUTE FUNCTION util.trg_set_updated_at();

-- Audit triggers (US18), V015.
CREATE TRIGGER trg_audit AFTER INSERT OR UPDATE OR DELETE ON procurement.supplier_part
    FOR EACH ROW EXECUTE FUNCTION audit.trg_row_audit();
CREATE TRIGGER trg_audit AFTER INSERT OR UPDATE OR DELETE ON procurement.supplier_price
    FOR EACH ROW EXECUTE FUNCTION audit.trg_row_audit();
