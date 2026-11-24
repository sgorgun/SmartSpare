-- =============================================================================
-- T03 — Roles and privileges (least privilege, masking, no superuser).
-- =============================================================================
\ir _helpers.sql
BEGIN;

SELECT pg_temp.ok((SELECT count(*) = 0 FROM util.v_privilege_findings), 'Privilege self-check returns no findings');
SELECT pg_temp.ok((SELECT NOT bool_or(rolsuper OR rolcreaterole OR rolcreatedb OR rolbypassrls) FROM pg_roles
                   WHERE rolname IN ('smartspare_api', 'smartspare_jobs', 'smartspare_reporting', 'smartspare_migrator')),
                  'No application login is superuser / CREATEROLE / CREATEDB / BYPASSRLS');
SELECT pg_temp.ok((SELECT count(*) = 0 FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
                   WHERE p.prosecdef AND n.nspname NOT IN ('pg_catalog', 'information_schema')
                     AND NOT EXISTS (SELECT 1 FROM unnest(p.proconfig) c WHERE c LIKE 'search_path=%')),
                  'Every SECURITY DEFINER function pins search_path');
SELECT pg_temp.ok((SELECT rolpassword IS NULL OR rolpassword LIKE 'SCRAM-SHA-256$%' FROM pg_authid WHERE rolname = 'smartspare_api'),
                  'DB login passwords stored as SCRAM-SHA-256 (not MD5)');

-- app_read (reporting): business data yes, personal data only masked ----------------
SET LOCAL ROLE smartspare_reporting;
SELECT pg_temp.ok((SELECT count(*) > 0 FROM inventory.v_stock_overview), 'app_read: reads stock overview');
SELECT pg_temp.expect_error($$SELECT email FROM iam.app_user LIMIT 1$$, '42501', 'app_read: cannot read e-mail column');
SELECT pg_temp.expect_error($$SELECT password_hash FROM iam.app_user LIMIT 1$$, '42501', 'app_read: cannot read password hashes');
SELECT pg_temp.ok((SELECT email_masked = 't***@d***.example' AND full_name_masked = 'T. R***' AND phone_masked = '+370 *** **001'
                   FROM iam.v_app_user_masked WHERE user_name = 'tomas.radzevicius'), 'app_read: masked name / e-mail / phone');
SELECT pg_temp.expect_error($$UPDATE catalog.part SET unit_cost = 0 WHERE false$$, '42501', 'app_read: no write access');
SELECT pg_temp.expect_error($$SELECT * FROM audit.audit_log LIMIT 1$$, '42501', 'app_read: no access to the audit trail');
SELECT pg_temp.expect_error($$SELECT * FROM raw.import_batch LIMIT 1$$, '42501', 'app_read: no access to the raw layer');

-- app_write (API) ------------------------------------------------------------------------
SET LOCAL ROLE smartspare_api;
SELECT pg_temp.expect_error($$UPDATE inventory.stock_balance SET on_hand_qty = 999 WHERE false$$, '42501',
                            'app_write: balances cannot be changed directly (only via ledger triggers)');
SELECT pg_temp.expect_error($$INSERT INTO inventory.stock_reservation (warehouse_id, part_id, quantity, status, expires_at, transfer_order_line_id) VALUES (1, 1, 1, 'soft', now(), 1)$$,
                            '42501', 'app_write: reservations only through business functions');
SELECT pg_temp.expect_error($$UPDATE inventory.transfer_order SET status = 'received' WHERE false$$, '42501',
                            'app_write: status column not updatable (workflow functions only)');
SELECT pg_temp.expect_error($$DELETE FROM audit.audit_log WHERE false$$, '42501', 'app_write: cannot delete audit records');
SELECT pg_temp.expect_error($$UPDATE inventory.stock_movement SET quantity = 1 WHERE false$$, '42501', 'app_write: ledger not updatable');
SELECT pg_temp.expect_error($$CREATE TABLE public.hack (id int)$$, '42501', 'app_write: cannot create objects (no DDL)');
SELECT pg_temp.expect_error($$SELECT iam.anonymise_user(gen_random_uuid())$$, '42501', 'app_write: cannot run admin-only functions');
SELECT pg_temp.ok((SELECT count(*) = 11 AND bool_and(coalesce(pg_temp.identity_v3_iterations(password_hash), 0) >= 220000)
                   FROM iam.app_user WHERE id::text LIKE '00000000-0000-4000-8000-0000000000__' AND anonymised_at IS NULL),
                  'app_write: reads the Identity V3 hashes of the demo users for login (PBKDF2-HMAC-SHA512, >= 220,000 iterations)');
SELECT pg_temp.act_as('jonas.balciunas');
SELECT pg_temp.expect_error($$SELECT procurement.approve_purchase_order(1)$$, 'SS301', 'DB-level RBAC: a service engineer cannot approve POs');
-- X1 (#11): util.require_role trusts the session roles set by SessionContextInterceptor, not iam (MODULE_MAPPING 3.6).
SELECT pg_temp.act_as('vida.mockute');                    -- SPONSOR in iam
SELECT set_config('app.user_roles', '', false);
SELECT pg_temp.expect_error($$SELECT util.require_role('SPONSOR')$$, 'SS301',
                            'DB-level RBAC reads the session roles: no role in app.user_roles, no access (#11)');
SELECT set_config('app.user_roles', 'SPONSOR', false);
SELECT pg_temp.ok(util.require_role('SPONSOR') = util.current_app_user(),
                  'DB-level RBAC: a role in app.user_roles passes util.require_role (#11)');
-- Only Identity and Reporting SQL reads iam (MODULE_MAPPING 3.6, STAGE_3_PLAN exit criterion 4): no function outside
-- iam names it (the suite's own pg_temp helpers aside), and the only views on it are Reporting's masked audit view
-- and two TRANSITIONAL views (#13, #14).
SELECT pg_temp.ok((SELECT array_agg(obj ORDER BY obj) FROM (
                     SELECT n.nspname || '.' || p.proname
                       FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
                      WHERE n.nspname <> 'iam' AND n.nspname !~ '^(pg_|information_schema$)' AND p.prosrc ~ 'iam[.]'
                     UNION
                     SELECT vn.nspname || '.' || v.relname
                       FROM pg_depend d JOIN pg_rewrite r ON r.oid = d.objid
                       JOIN pg_class v ON v.oid = r.ev_class JOIN pg_namespace vn ON vn.oid = v.relnamespace
                       JOIN pg_class t ON t.oid = d.refobjid JOIN pg_namespace tn ON tn.oid = t.relnamespace
                      WHERE tn.nspname = 'iam' AND vn.nspname <> 'iam') s(obj))
                  = ARRAY['audit.v_audit_log_masked', 'forecast.v_recommendation_explained', 'procurement.v_purchase_order_overview'],
                  'Only Identity and Reporting SQL reads iam: no other function, Reporting''s masked audit view and two TRANSITIONAL views');

-- app_ml (jobs) --------------------------------------------------------------------------
SET LOCAL ROLE smartspare_jobs;
CALL mart.refresh_all();
SELECT pg_temp.ok((SELECT status = 'ok' FROM mart.refresh_log ORDER BY id DESC LIMIT 1), 'app_ml: refreshes data marts (concurrently) and logs the load');
SELECT pg_temp.expect_error($$UPDATE iam.app_user SET is_active = false WHERE false$$, '42501', 'app_ml: no access to user administration');

ROLLBACK;
