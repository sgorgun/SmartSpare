-- =============================================================================
-- Identity 0019 — the parts catalogue's permissions (STAGE_7_PLAN 3.1, PR 7i1, ADR 0028). The roles that maintain
-- catalogues (Administrator, Inventory planner) and the Superadministrator create, change and archive parts. The first
-- permission migration through iam.grant_default_permission (#211): a seeded role an administrator changed gets the
-- permission too, audited as the system user and marked "Changed by upgrade v0.7.0" until an administrator confirms it.
-- =============================================================================

INSERT INTO iam.permission (code, module, description) VALUES
    ('Inventory.Part.Create', 'Inventory', 'Add parts to the catalogue.'),
    ('Inventory.Part.Edit', 'Inventory', 'Change parts of the catalogue.'),
    ('Inventory.Part.Archive', 'Inventory', 'Archive or restore parts.');

SELECT iam.grant_default_permission(r.code, p.code, 'v0.7.0')
FROM (VALUES ('ADMINISTRATOR'), ('INVENTORY_PLANNER'), ('SUPERADMINISTRATOR')) AS r (code)
CROSS JOIN (VALUES ('Inventory.Part.Create'), ('Inventory.Part.Edit'), ('Inventory.Part.Archive')) AS p (code);
