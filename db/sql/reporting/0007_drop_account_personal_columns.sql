-- =============================================================================
-- Reporting 0007 — the account's copy of the employee's personal data goes for good (ADR 0026, STAGE_7_PLAN 1.6).
-- PR 7e emptied iam.app_user.full_name, email and phone but kept them, because the applied script of InitialReporting
-- creates audit.v_audit_log_masked over app_user.full_name. This forward-only migration finishes the job without
-- editing an applied script (#152):
--   (a) the masked audit trail is (re)defined over the employee, so no object reads the account's columns any more;
--   (b) the privilege self-check checks the employee's personal columns instead of the account's, and also flags
--       any direct write privilege on the rights projection (ADR 0026);
--   (c) the three columns are dropped.
-- It runs in the Reporting context because Reporting owns the dependent view and the migrator applies Reporting
-- last: on a fresh database InitialReporting first creates the old view, AuditActorFromEmployee and (a) replace it,
-- and only then (b) can drop the columns. A drop in an Identity migration would run before InitialReporting and break
-- it. This is the second cross-module step of the stage, recorded in ADR 0026 decision 6 next to the one-time copy.
-- =============================================================================

CREATE OR REPLACE VIEW audit.v_audit_log_masked AS
SELECT a.id, a.occurred_at, a.action, a.table_name, a.record_id, a.changed_columns,
       a.old_values, a.new_values, a.reason,
       u.user_name, util.mask_name(e.full_name) AS actor_masked, a.db_role
FROM audit.audit_log a
LEFT JOIN iam.app_user u ON u.id = a.user_id
LEFT JOIN iam.employee e ON e.id = u.employee_id;

CREATE OR REPLACE VIEW util.v_privilege_findings AS
-- (a) tables / views in business schemas that app_read cannot read
SELECT 'app_read cannot SELECT' AS finding, n.nspname || '.' || c.relname AS object_name
FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE n.nspname IN ('ref', 'catalog', 'inventory', 'fleet', 'procurement', 'forecast', 'mart')
  AND c.relkind IN ('r', 'p', 'v', 'm') AND NOT c.relispartition
  AND NOT has_table_privilege('app_read', c.oid, 'SELECT')
UNION ALL
-- (b) personal-data columns readable by app_read (the person's data is the employee's since ADR 0026)
SELECT 'app_read can read personal data column', t || '.' || col
FROM (VALUES ('iam.employee', 'full_name'), ('iam.employee', 'email'), ('iam.employee', 'phone'),
             ('iam.app_user', 'password_hash')) AS pd (t, col)
WHERE has_column_privilege('app_read', t, col, 'SELECT')
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
WHERE has_table_privilege(r, 'inventory.stock_movement', 'UPDATE') OR has_table_privilege(r, 'inventory.stock_movement', 'DELETE')
UNION ALL
-- (g) direct writes on Identity's rights projection (ADR 0026: only iam.apply_responsibilities changes it)
SELECT 'direct write privilege on rights projection', r || ' -> ' || t
FROM unnest(ARRAY['app_write', 'app_ml', 'app_read', 'app_admin']) AS r,
     unnest(ARRAY['iam.employee_responsibility', 'iam.responsibility_source']) AS t
WHERE has_table_privilege(r, t, 'INSERT') OR has_table_privilege(r, t, 'UPDATE') OR has_table_privilege(r, t, 'DELETE');

ALTER TABLE iam.app_user DROP COLUMN full_name, DROP COLUMN email, DROP COLUMN phone;
