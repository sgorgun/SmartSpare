-- =============================================================================
-- Identity 0013 — roles are data (ADR 0028, PR 7x3a). iam.role gains is_locked and is_archived (EF); this script:
--   * locks the built-in Administrator role: it cannot be changed or archived (system administration under normal rules);
--   * adds the permission Procurement.Supplier.Edit to the catalogue, held only by Administrator by default (the first
--     separation-of-duties pair forbids holding it with purchase order creation);
--   * audits changes of roles (iam.role_permission is audited since 0011);
--   * lets the administration transaction (app_admin) write role bundles.
-- =============================================================================

UPDATE iam.role SET is_locked = true WHERE code = 'ADMINISTRATOR';

INSERT INTO iam.permission (code, module, description) VALUES
    ('Procurement.Supplier.Edit', 'Procurement', 'Maintain suppliers, their parts and prices (never together with creating purchase orders).');
INSERT INTO iam.role_permission (role_id, permission)
SELECT id, 'Procurement.Supplier.Edit' FROM iam.role WHERE code = 'ADMINISTRATOR';

CREATE TRIGGER trg_audit AFTER INSERT OR UPDATE OR DELETE ON iam.role
    FOR EACH ROW EXECUTE FUNCTION audit.trg_row_audit();

GRANT INSERT, DELETE ON iam.role_permission TO app_admin;
