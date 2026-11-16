-- =============================================================================
-- Identity 0002 — privileges on iam objects. V015 statements restricted to iam;
-- personal-data columns (full_name, email, phone, password hash) stay hidden from app_read.
-- Roles are cluster objects created by db/bootstrap/000_bootstrap_cluster.sql.
-- iam.v_app_user_masked and its grant follow in PR 2d (Reporting, MODULE_MAPPING 3.2).
-- =============================================================================

REVOKE EXECUTE ON ALL FUNCTIONS IN SCHEMA iam FROM PUBLIC;

-- ---- app_read: read business data; personal data only masked ---------------
GRANT USAGE ON SCHEMA iam TO app_read;
GRANT SELECT ON iam.role, iam.user_role, iam.user_brand TO app_read;
-- column-level: no full_name / email / phone / password_hash / security_stamp
GRANT SELECT (id, user_name, is_active, home_warehouse_id, last_login_at, anonymised_at, created_at, updated_at)
    ON iam.app_user TO app_read;

-- ---- app_write: the ASP.NET Core API -----------------------------------------
GRANT SELECT ON iam.app_user TO app_write;                              -- authentication
GRANT UPDATE (password_hash, security_stamp, last_login_at, failed_login_count, lockout_end, full_name, phone)
    ON iam.app_user TO app_write;                                         -- login + self-service profile
GRANT EXECUTE ON FUNCTION iam.user_has_role(uuid, text[]) TO app_write;

-- ---- app_audit: sponsor / auditor --------------------------------------------
GRANT USAGE ON SCHEMA iam TO app_audit;

-- ---- app_admin: application administration (not superuser, not owner) -------
GRANT INSERT, UPDATE, DELETE ON iam.role, iam.user_role, iam.user_brand TO app_admin;
GRANT INSERT, UPDATE ON iam.app_user TO app_admin;
GRANT EXECUTE ON FUNCTION iam.anonymise_user(uuid) TO app_admin;
