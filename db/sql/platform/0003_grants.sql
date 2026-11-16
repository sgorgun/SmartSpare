-- =============================================================================
-- Platform 0003 — privileges on Platform objects and the privilege self-check.
-- Platform part of V015: statements are V015's, restricted to the schemas
-- ref, util and audit; grants on module objects move to the module scripts.
-- Roles are cluster objects created by db/bootstrap/000_bootstrap_cluster.sql.
-- =============================================================================

-- Functions are not executable by PUBLIC (PostgreSQL default is EXECUTE TO PUBLIC).
REVOKE EXECUTE ON ALL FUNCTIONS IN SCHEMA util, audit, ref FROM PUBLIC;
REVOKE EXECUTE ON ALL PROCEDURES IN SCHEMA audit FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE smartspare_owner REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;

-- ---- app_read ------------------------------------------------------------------
GRANT USAGE ON SCHEMA ref, util TO app_read;
GRANT SELECT ON ALL TABLES IN SCHEMA ref TO app_read;
GRANT EXECUTE ON FUNCTION util.mask_email(text), util.mask_name(text), util.mask_phone(text),
                          util.current_app_user()
    TO app_read;
-- New tables in this schema become readable automatically.
ALTER DEFAULT PRIVILEGES FOR ROLE smartspare_owner IN SCHEMA ref
    GRANT SELECT ON TABLES TO app_read;

-- ---- app_write -------------------------------------------------------------------
GRANT EXECUTE ON FUNCTION util.require_app_user(), util.require_role(text[]) TO app_write;

-- ---- app_audit -------------------------------------------------------------------
GRANT USAGE ON SCHEMA audit TO app_audit;
GRANT EXECUTE ON FUNCTION util.mask_name(text) TO app_audit;

-- ---- app_admin -------------------------------------------------------------------
GRANT USAGE ON SCHEMA audit TO app_admin;
GRANT SELECT ON audit.audit_log TO app_admin;
GRANT INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA ref TO app_admin;
GRANT EXECUTE ON PROCEDURE audit.purge_expired(interval) TO app_admin;

-- ===========================================================================
-- 5. Privilege self-check — run after every migration (db/tests/T03).
--    Every returned row is a finding that must be fixed.
-- ===========================================================================
CREATE VIEW util.v_privilege_findings AS
-- (a) tables / views in business schemas that app_read cannot read
SELECT 'app_read cannot SELECT' AS finding, n.nspname || '.' || c.relname AS object_name
FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE n.nspname IN ('ref', 'catalog', 'inventory', 'fleet', 'procurement', 'forecast', 'mart')
  AND c.relkind IN ('r', 'p', 'v', 'm') AND NOT c.relispartition
  AND NOT has_table_privilege('app_read', c.oid, 'SELECT')
UNION ALL
-- (b) personal-data columns readable by app_read
SELECT 'app_read can read personal data column', 'iam.app_user.' || col
FROM unnest(ARRAY['full_name', 'email', 'phone', 'password_hash']) AS col
WHERE has_column_privilege('app_read', 'iam.app_user', col, 'SELECT')
UNION ALL
-- (c) functions executable by PUBLIC
SELECT 'function executable by PUBLIC', p.oid::regprocedure::text
FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
WHERE n.nspname IN ('util', 'iam', 'catalog', 'inventory', 'fleet', 'procurement', 'forecast', 'audit', 'raw', 'staging', 'mart', 'ref')
  AND has_function_privilege('public', p.oid, 'EXECUTE')
UNION ALL
-- (d) SECURITY DEFINER without a pinned search_path (search_path hijacking)
SELECT 'SECURITY DEFINER without search_path', p.oid::regprocedure::text
FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
WHERE p.prosecdef AND n.nspname NOT IN ('pg_catalog', 'information_schema')
  AND NOT EXISTS (SELECT 1 FROM unnest(p.proconfig) cfg WHERE cfg LIKE 'search_path=%')
UNION ALL
-- (e) application login roles that are superuser or can bypass RLS / create roles
SELECT 'privileged application login', r.rolname::text
FROM pg_roles r
WHERE r.rolname IN ('smartspare_api', 'smartspare_jobs', 'smartspare_reporting', 'smartspare_migrator')
  AND (r.rolsuper OR r.rolbypassrls OR r.rolcreaterole OR r.rolcreatedb)
UNION ALL
-- (f) application roles able to modify append-only or trigger-maintained tables
SELECT 'write privilege on protected table', r || ' -> ' || t
FROM unnest(ARRAY['app_write', 'app_ml', 'app_read']) AS r,
     unnest(ARRAY['inventory.stock_balance', 'audit.audit_log', 'inventory.stock_reservation']) AS t
WHERE has_table_privilege(r, t, 'UPDATE') OR has_table_privilege(r, t, 'DELETE')
UNION ALL
SELECT 'UPDATE/DELETE privilege on ledger', r
FROM unnest(ARRAY['app_write', 'app_ml', 'app_read', 'app_admin']) AS r
WHERE has_table_privilege(r, 'inventory.stock_movement', 'UPDATE') OR has_table_privilege(r, 'inventory.stock_movement', 'DELETE');
COMMENT ON VIEW util.v_privilege_findings IS 'Security self-check: must return zero rows. Executed by the test suite after every migration.';
