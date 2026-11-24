-- =============================================================================
-- Identity 0005 — the masked user directory becomes module-local (#11, MODULE_MAPPING X18).
-- Until PR 3c the view read inventory.warehouse for the home warehouse code and was therefore created by
-- the transitional Reporting script (reporting/9000_transitional_cross_module.sql, MODULE_MAPPING 3.2). It
-- now returns home_warehouse_id, reads iam only, and belongs to Identity; 9000 no longer creates it.
-- DROP VIEW IF EXISTS removes the transitional copy in a database built before PR 3c. No object depends on
-- the view (checked in pg_depend: only its own row type), so no CASCADE and nothing to recreate.
-- =============================================================================

DROP VIEW IF EXISTS iam.v_app_user_masked;

CREATE VIEW iam.v_app_user_masked AS
SELECT u.id, u.user_name,
       util.mask_name(u.full_name) AS full_name_masked,
       util.mask_email(u.email)    AS email_masked,
       util.mask_phone(u.phone)    AS phone_masked,
       u.is_active, u.home_warehouse_id,
       (SELECT array_agg(r.code ORDER BY r.code) FROM iam.user_role ur JOIN iam.role r ON r.id = ur.role_id
        WHERE ur.user_id = u.id) AS roles,
       u.last_login_at, u.created_at, u.anonymised_at IS NOT NULL AS is_anonymised
FROM iam.app_user u;
COMMENT ON VIEW iam.v_app_user_masked IS 'User directory with masked personal data (name -> initials, e-mail and phone partially hidden).';
GRANT SELECT ON iam.v_app_user_masked TO app_read;
