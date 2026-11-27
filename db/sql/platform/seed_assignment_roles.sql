-- =============================================================================
-- Platform seed — assignment roles of responsible employees (ADR 0026, STAGE_7_PLAN 1.1). The roles that grant
-- scope give rights: BRAND_MANAGER of a brand (approval scope), KEEPER and SERVICE_BASE of a warehouse (hub access).
-- =============================================================================
INSERT INTO ref.assignment_role (record_kind, code, name, grants_scope) VALUES
    ('brand',        'BRAND_MANAGER',      'Brand manager',      true),
    ('brand',        'PRODUCT_SPECIALIST', 'Product specialist', false),
    ('warehouse',    'KEEPER',             'Keeper',             true),
    ('warehouse',    'SERVICE_BASE',       'Service base',       true),
    ('warehouse',    'HUB_MANAGER',        'Hub manager',        false),
    ('manufacturer', 'ACCOUNT_MANAGER',    'Account manager',    false),
    ('part',         'PRODUCT_SPECIALIST', 'Product specialist', false),
    ('part',         'BUYER',              'Buyer',              false),
    ('supplier',     'BUYER',              'Buyer',              false),
    ('supplier',     'ACCOUNT_MANAGER',    'Account manager',    false)
ON CONFLICT (record_kind, code) DO NOTHING;
GRANT SELECT ON ref.assignment_role TO app_read;
