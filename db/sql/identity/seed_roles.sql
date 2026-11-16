-- =============================================================================
-- Identity seed — application (RBAC) roles, copied verbatim from R001.
-- Idempotent: upsert by natural key.
-- =============================================================================

INSERT INTO iam.role (code, name, description) VALUES
    ('BRAND_MANAGER',     'Category brand manager',   'Owns parts availability of assigned brands; approves transfers and POs.'),
    ('SERVICE_ENGINEER',  'Field service engineer',   'Registers machines, logs engine hours, executes service orders.'),
    ('WAREHOUSE_KEEPER',  'Local warehouse keeper',   'Dispatches and receives transfers and supplier deliveries.'),
    ('INVENTORY_PLANNER', 'Central inventory planner','Runs forecasts, configures supplier rules, flags exceptional demand.'),
    ('SPONSOR',           'Project sponsor / manager','Approves large purchase orders, reads the audit trail.'),
    ('ADMINISTRATOR',     'System administrator',     'Manages users, roles and reference data.')
ON CONFLICT (code) DO UPDATE SET name = EXCLUDED.name, description = EXCLUDED.description;
