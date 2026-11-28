-- =============================================================================
-- Identity 0017 — the manufacturer catalogue's permissions (STAGE_7_PLAN 3.1, PR 7h3, ADR 0028). Every role that sees the
-- parts catalogue sees its manufacturers; the roles that maintain catalogues (Administrator, Inventory planner) and the
-- Superadministrator change them.
-- =============================================================================

INSERT INTO iam.permission (code, module, description) VALUES
    ('Inventory.Manufacturer.View', 'Inventory', 'See part manufacturers.'),
    ('Inventory.Manufacturer.Create', 'Inventory', 'Add part manufacturers.'),
    ('Inventory.Manufacturer.Edit', 'Inventory', 'Change part manufacturers.'),
    ('Inventory.Manufacturer.Archive', 'Inventory', 'Archive or restore part manufacturers.');

INSERT INTO iam.role_permission (role_id, permission)
SELECT rp.role_id, 'Inventory.Manufacturer.View' FROM iam.role_permission rp WHERE rp.permission = 'Inventory.Part.View';

INSERT INTO iam.role_permission (role_id, permission)
SELECT r.id, p.code
FROM iam.role r
CROSS JOIN (VALUES ('Inventory.Manufacturer.Create'), ('Inventory.Manufacturer.Edit'), ('Inventory.Manufacturer.Archive')) AS p (code)
WHERE r.code IN ('ADMINISTRATOR', 'INVENTORY_PLANNER', 'SUPERADMINISTRATOR');
