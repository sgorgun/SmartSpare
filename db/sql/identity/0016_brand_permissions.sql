-- =============================================================================
-- Identity 0016 — the brand catalogue's permissions (STAGE_7_PLAN 3.1, PR 7h2, ADR 0028). Brands are Inventory's master
-- data since PR 7h1; creating, changing and archiving them are new permissions, held by default by the roles that
-- maintain catalogues (Administrator, Inventory planner) and, like every permission, by the Superadministrator.
-- =============================================================================

INSERT INTO iam.permission (code, module, description) VALUES
    ('Inventory.Brand.Create', 'Inventory', 'Add brands.'),
    ('Inventory.Brand.Edit', 'Inventory', 'Change brands.'),
    ('Inventory.Brand.Archive', 'Inventory', 'Archive or restore brands.');

INSERT INTO iam.role_permission (role_id, permission)
SELECT r.id, p.code
FROM iam.role r
CROSS JOIN (VALUES ('Inventory.Brand.Create'), ('Inventory.Brand.Edit'), ('Inventory.Brand.Archive')) AS p (code)
WHERE r.code IN ('ADMINISTRATOR', 'INVENTORY_PLANNER', 'SUPERADMINISTRATOR');
