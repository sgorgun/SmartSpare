-- =============================================================================
-- V015 — Security: audit triggers, least-privilege grants, column-level
--        privileges for personal data, masking, GDPR anonymisation,
--        default privileges and an automated privilege self-check.
-- Roles (cluster level) are created by db/bootstrap/000_bootstrap_cluster.sql.
-- =============================================================================

-- ===========================================================================
-- 1. Audit triggers (US18). Arguments = columns never copied to the audit log.
-- ===========================================================================
CREATE TRIGGER trg_audit AFTER INSERT OR UPDATE OR DELETE ON iam.app_user
    FOR EACH ROW EXECUTE FUNCTION audit.trg_row_audit('full_name', 'email', 'phone', 'password_hash', 'security_stamp', 'last_login_at', 'failed_login_count');
CREATE TRIGGER trg_audit AFTER INSERT OR UPDATE OR DELETE ON iam.user_role
    FOR EACH ROW EXECUTE FUNCTION audit.trg_row_audit();
CREATE TRIGGER trg_audit AFTER INSERT OR UPDATE OR DELETE ON iam.user_brand
    FOR EACH ROW EXECUTE FUNCTION audit.trg_row_audit();
CREATE TRIGGER trg_audit AFTER INSERT OR UPDATE OR DELETE ON catalog.part
    FOR EACH ROW EXECUTE FUNCTION audit.trg_row_audit();
CREATE TRIGGER trg_audit AFTER INSERT OR UPDATE OR DELETE ON catalog.supplier_part
    FOR EACH ROW EXECUTE FUNCTION audit.trg_row_audit();
CREATE TRIGGER trg_audit AFTER INSERT OR UPDATE OR DELETE ON catalog.supplier_price
    FOR EACH ROW EXECUTE FUNCTION audit.trg_row_audit();
CREATE TRIGGER trg_audit AFTER INSERT OR UPDATE OR DELETE ON inventory.warehouse_part
    FOR EACH ROW EXECUTE FUNCTION audit.trg_row_audit();
CREATE TRIGGER trg_audit AFTER INSERT OR UPDATE OR DELETE ON inventory.stock_reservation
    FOR EACH ROW EXECUTE FUNCTION audit.trg_row_audit();
CREATE TRIGGER trg_audit AFTER INSERT OR UPDATE OR DELETE ON inventory.demand_exception
    FOR EACH ROW EXECUTE FUNCTION audit.trg_row_audit();
CREATE TRIGGER trg_audit AFTER INSERT OR UPDATE OR DELETE ON inventory.transfer_order
    FOR EACH ROW EXECUTE FUNCTION audit.trg_row_audit();
CREATE TRIGGER trg_audit AFTER INSERT OR UPDATE OR DELETE ON inventory.transfer_order_line
    FOR EACH ROW EXECUTE FUNCTION audit.trg_row_audit();
CREATE TRIGGER trg_audit AFTER INSERT OR UPDATE OR DELETE ON fleet.machine
    FOR EACH ROW EXECUTE FUNCTION audit.trg_row_audit('current_engine_hours', 'engine_hours_read_at');
CREATE TRIGGER trg_audit AFTER INSERT OR UPDATE OR DELETE ON fleet.maintenance_plan
    FOR EACH ROW EXECUTE FUNCTION audit.trg_row_audit();
CREATE TRIGGER trg_audit AFTER INSERT OR UPDATE OR DELETE ON fleet.maintenance_plan_part
    FOR EACH ROW EXECUTE FUNCTION audit.trg_row_audit();
CREATE TRIGGER trg_audit AFTER INSERT OR UPDATE OR DELETE ON fleet.service_order
    FOR EACH ROW EXECUTE FUNCTION audit.trg_row_audit();
CREATE TRIGGER trg_audit AFTER INSERT OR UPDATE OR DELETE ON procurement.purchase_order
    FOR EACH ROW EXECUTE FUNCTION audit.trg_row_audit();
CREATE TRIGGER trg_audit AFTER INSERT OR UPDATE OR DELETE ON procurement.purchase_order_line
    FOR EACH ROW EXECUTE FUNCTION audit.trg_row_audit();
CREATE TRIGGER trg_audit AFTER INSERT OR UPDATE OR DELETE ON forecast.ml_model_version
    FOR EACH ROW EXECUTE FUNCTION audit.trg_row_audit();
CREATE TRIGGER trg_audit AFTER INSERT OR UPDATE OR DELETE ON forecast.recommendation
    FOR EACH ROW EXECUTE FUNCTION audit.trg_row_audit();

-- ===========================================================================
-- 2. Model status guard: 'active'/'retired' only via forecast.promote_model()
--    (which runs as the schema owner — SECURITY DEFINER).
-- ===========================================================================
CREATE FUNCTION forecast.trg_model_status_guard() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.status IS DISTINCT FROM OLD.status THEN
        IF NOT ((OLD.status = 'training'  AND NEW.status IN ('candidate', 'rejected'))
             OR (OLD.status = 'candidate' AND NEW.status IN ('active', 'rejected'))
             OR (OLD.status = 'active'    AND NEW.status = 'retired')) THEN
            RAISE EXCEPTION 'Invalid model transition % -> %', OLD.status, NEW.status USING ERRCODE = 'SS300';
        END IF;
        IF NEW.status IN ('active', 'retired')
           AND current_user <> (SELECT nspowner::regrole::name FROM pg_namespace WHERE nspname = 'forecast') THEN
            RAISE EXCEPTION 'Model activation is only possible through forecast.promote_model()' USING ERRCODE = 'insufficient_privilege';
        END IF;
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER trg_model_status_guard BEFORE UPDATE ON forecast.ml_model_version
    FOR EACH ROW EXECUTE FUNCTION forecast.trg_model_status_guard();

-- ===========================================================================
-- 3. GDPR: right to erasure for employees (Art. 17) — anonymise, keep FKs
-- ===========================================================================
CREATE FUNCTION iam.anonymise_user(p_user_id uuid) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_admin uuid := util.require_role('ADMINISTRATOR');
    v_tag   text := left(md5(p_user_id::text), 12);
BEGIN
    IF p_user_id = v_admin THEN
        RAISE EXCEPTION 'Administrators cannot anonymise themselves' USING ERRCODE = 'SS301';
    END IF;
    PERFORM set_config('app.reason', 'GDPR Art. 17 erasure request', true);
    DELETE FROM iam.user_role  WHERE user_id = p_user_id;
    DELETE FROM iam.user_brand WHERE user_id = p_user_id;
    UPDATE iam.app_user
       SET user_name = 'anon_' || v_tag, email = 'anon-' || v_tag || '@anonymised.invalid',
           full_name = 'Anonymised User', phone = NULL, password_hash = NULL,
           is_active = false, lockout_end = NULL, last_login_at = NULL,
           security_stamp = gen_random_uuid(), anonymised_at = now()
     WHERE id = p_user_id AND anonymised_at IS NULL;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'User % not found or already anonymised', p_user_id USING ERRCODE = 'no_data_found';
    END IF;
END $$;
COMMENT ON FUNCTION iam.anonymise_user(uuid) IS 'Irreversibly replaces personal data of a user; the row (and audit references) remain.';

-- ===========================================================================
-- 4. Privileges
-- ===========================================================================
-- Functions are not executable by PUBLIC (PostgreSQL default is EXECUTE TO PUBLIC).
REVOKE EXECUTE ON ALL FUNCTIONS IN SCHEMA util, iam, catalog, inventory, fleet, procurement, forecast, audit, raw, staging, mart, ref FROM PUBLIC;
REVOKE EXECUTE ON ALL PROCEDURES IN SCHEMA inventory, forecast, audit, mart FROM PUBLIC;
ALTER DEFAULT PRIVILEGES FOR ROLE smartspare_owner REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;

-- ---- app_read: read business data; personal data only masked ---------------
GRANT USAGE ON SCHEMA ref, iam, catalog, inventory, fleet, procurement, forecast, mart, util TO app_read;
GRANT SELECT ON ALL TABLES IN SCHEMA ref, catalog, inventory, fleet, procurement, forecast, mart TO app_read;
GRANT SELECT ON iam.role, iam.user_role, iam.user_brand, iam.v_app_user_masked TO app_read;
-- column-level: no full_name / email / phone / password_hash / security_stamp
GRANT SELECT (id, user_name, is_active, home_warehouse_id, last_login_at, anonymised_at, created_at, updated_at)
    ON iam.app_user TO app_read;
GRANT EXECUTE ON FUNCTION util.mask_email(text), util.mask_name(text), util.mask_phone(text),
                          util.current_app_user(),
                          fleet.fn_maintenance_due(bigint, numeric),
                          inventory.fn_in_transit_qty(smallint, bigint),
                          forecast.fn_stock_position(smallint, bigint, bigint, numeric),
                          forecast.fn_classify_demand(numeric, numeric)
    TO app_read;
-- New tables in these schemas become readable automatically.
ALTER DEFAULT PRIVILEGES FOR ROLE smartspare_owner IN SCHEMA ref, catalog, inventory, fleet, procurement, forecast, mart
    GRANT SELECT ON TABLES TO app_read;

-- ---- app_write: the ASP.NET Core API -----------------------------------------
GRANT USAGE ON SCHEMA raw, staging TO app_write;
GRANT SELECT ON ALL TABLES IN SCHEMA raw, staging TO app_write;
GRANT SELECT ON iam.app_user TO app_write;                              -- authentication
GRANT UPDATE (password_hash, security_stamp, last_login_at, failed_login_count, lockout_end, full_name, phone)
    ON iam.app_user TO app_write;                                         -- login + self-service profile

GRANT INSERT, UPDATE ON catalog.supplier, catalog.part, catalog.supplier_part TO app_write;
GRANT INSERT, UPDATE, DELETE ON catalog.part_analogue, catalog.supplier_price TO app_write;

GRANT INSERT ON inventory.warehouse_part TO app_write;
GRANT UPDATE (bin_location, safety_stock_qty, max_stock_qty, is_stocked) ON inventory.warehouse_part TO app_write;
GRANT INSERT ON inventory.stock_movement TO app_write;                   -- sales, adjustments; balance via trigger
-- NO write privilege on inventory.stock_balance / stock_reservation / demand_exception:
-- they change only through triggers and SECURITY DEFINER functions.
GRANT INSERT, DELETE ON inventory.transfer_order TO app_write;
GRANT UPDATE (is_urgent, from_warehouse_id, to_warehouse_id) ON inventory.transfer_order TO app_write;
GRANT INSERT, DELETE ON inventory.transfer_order_line TO app_write;
GRANT UPDATE (quantity, override_reason) ON inventory.transfer_order_line TO app_write;

GRANT INSERT ON fleet.customer, fleet.machine_model, fleet.machine, fleet.engine_hours_reading TO app_write;
GRANT UPDATE (region_warehouse_id, farm_size_ha, is_active) ON fleet.customer TO app_write;
GRANT UPDATE (model_id, customer_id, operating_condition_id, home_warehouse_id, status, commissioned_on) ON fleet.machine TO app_write;
GRANT INSERT, UPDATE, DELETE ON fleet.maintenance_plan, fleet.maintenance_plan_part TO app_write;
GRANT INSERT ON fleet.service_order TO app_write;                        -- unplanned repairs
GRANT UPDATE (warehouse_id) ON fleet.service_order TO app_write;
GRANT INSERT, DELETE ON fleet.service_order_line TO app_write;
GRANT UPDATE (qty_required) ON fleet.service_order_line TO app_write;

GRANT INSERT ON procurement.purchase_order TO app_write;
GRANT UPDATE (warehouse_id, is_urgent, urgent_reason, supplier_id) ON procurement.purchase_order TO app_write;
GRANT INSERT, DELETE ON procurement.purchase_order_line TO app_write;
GRANT UPDATE (quantity, unit_price, override_reason) ON procurement.purchase_order_line TO app_write;

GRANT INSERT ON raw.import_batch, raw.part_catalog_row, raw.demand_history_row TO app_write;

GRANT EXECUTE ON FUNCTION
    util.require_app_user(), util.require_role(text[]), iam.user_has_role(uuid, text[]),
    inventory.approve_transfer(bigint), inventory.dispatch_transfer(bigint), inventory.receive_transfer(bigint),
    inventory.cancel_transfer(bigint, text),
    inventory.flag_exceptional_demand(bigint, timestamptz, text, text),
    inventory.find_exceptional_demand_candidates(numeric, interval, integer),
    fleet.log_engine_hours(bigint, numeric, timestamptz, fleet.reading_source),
    fleet.create_service_order_draft(bigint, integer, numeric),
    fleet.complete_service_order(bigint, numeric), fleet.cancel_service_order(bigint, text),
    procurement.submit_purchase_order(bigint), procurement.approve_purchase_order(bigint),
    procurement.export_purchase_order(bigint), procurement.receive_purchase_order_line(bigint, numeric),
    procurement.cancel_purchase_order(bigint, text),
    forecast.decide_recommendation(bigint, text, numeric, text),
    staging.process_batch(bigint, numeric)
TO app_write;

-- ---- app_ml: Quartz.NET forecasting / retraining jobs ------------------------
GRANT USAGE ON SCHEMA mart, forecast TO app_ml;
GRANT INSERT ON forecast.ml_model_version, forecast.benchmark_result, forecast.forecast_run,
                forecast.demand_forecast, forecast.recommendation TO app_ml;
GRANT UPDATE (status, rmsse, mase, mae, rmse, hyperparameters, artifact_uri, artifact_sha256, trained_at)
    ON forecast.ml_model_version TO app_ml;
GRANT UPDATE (status, finished_at, error_message) ON forecast.forecast_run TO app_ml;
GRANT DELETE ON forecast.demand_forecast TO app_ml;                      -- re-run of a failed run
GRANT EXECUTE ON FUNCTION forecast.promote_model(integer, numeric) TO app_ml;
GRANT EXECUTE ON PROCEDURE mart.refresh_all(), forecast.expire_open_recommendations(interval),
                           inventory.expire_soft_reservations(integer) TO app_ml;

-- ---- app_audit: sponsor / auditor --------------------------------------------
GRANT USAGE ON SCHEMA audit, iam TO app_audit;
GRANT SELECT ON audit.v_audit_log_masked TO app_audit;
GRANT EXECUTE ON FUNCTION util.mask_name(text) TO app_audit;

-- ---- app_admin: application administration (not superuser, not owner) -------
GRANT USAGE ON SCHEMA audit TO app_admin;
GRANT SELECT ON audit.audit_log, audit.v_audit_log_masked TO app_admin;
GRANT INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA ref TO app_admin;
GRANT INSERT, UPDATE, DELETE ON iam.role, iam.user_role, iam.user_brand TO app_admin;
GRANT INSERT, UPDATE ON iam.app_user TO app_admin;
GRANT INSERT, UPDATE ON inventory.warehouse, inventory.warehouse_route TO app_admin;
GRANT EXECUTE ON FUNCTION iam.anonymise_user(uuid) TO app_admin;
GRANT EXECUTE ON PROCEDURE audit.purge_expired(interval), mart.refresh_all() TO app_admin;

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
