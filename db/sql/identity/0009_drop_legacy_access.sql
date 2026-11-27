-- =============================================================================
-- Identity 0009 — migration step 3 of STAGE_7_PLAN 1.6 (ADR 0026): the legacy access data goes. Rights come only from
-- positions and the projection of responsibility assignments, so iam.user_role, iam.user_brand and
-- iam.app_user.home_warehouse_id are dropped, and the account's copy of the employee's personal data (full_name,
-- email, phone) is emptied. Those three columns stay as always-empty columns only because the applied script of
-- InitialReporting creates audit.v_audit_log_masked over app_user.full_name and an applied script is never edited
-- (#152); Reporting's migration AuditActorFromEmployee reads the employee instead. Nothing writes them any more.
--
-- Drift guard: the migration refuses to run if any account's legacy roles differ from its position's template, or a
-- legacy brand or home hub has no projected assignment. That happens only on a database that skipped migration steps
-- 1 or 2 (PRs 7c2, 7d2a); such a local database is reset (README).
-- =============================================================================

DO $$
DECLARE
    v_count integer;
BEGIN
    SELECT count(*) INTO v_count
    FROM iam.app_user u
    WHERE u.anonymised_at IS NULL
      AND EXISTS (SELECT 1 FROM iam.user_role ur WHERE ur.user_id = u.id)
      AND (SELECT array_agg(r.code::text ORDER BY r.code) FROM iam.user_role ur JOIN iam.role r ON r.id = ur.role_id WHERE ur.user_id = u.id)
          IS DISTINCT FROM
          (SELECT array_agg(r.code::text ORDER BY r.code) FROM iam.employee e JOIN iam.position_role pr ON pr.position_id = e.position_id
           JOIN iam.role r ON r.id = pr.role_id WHERE e.id = u.employee_id);
    IF v_count > 0 THEN
        RAISE EXCEPTION 'Access drift: % account(s) have legacy roles that differ from their position''s template', v_count
            USING HINT = 'The database skipped migration step 1 (Organisation); reset the local database (README).';
    END IF;

    SELECT count(*) INTO v_count
    FROM iam.user_brand ub JOIN iam.app_user u ON u.id = ub.user_id
    WHERE u.anonymised_at IS NULL
      AND NOT EXISTS (SELECT 1 FROM iam.employee_responsibility r
                      WHERE r.record_kind = 'brand' AND r.record_id = ub.brand_id AND r.employee_id = u.employee_id AND r.assignment_role = 'BRAND_MANAGER');
    IF v_count > 0 THEN
        RAISE EXCEPTION 'Access drift: % legacy brand(s) were never copied to brand-manager assignments', v_count
            USING HINT = 'The database skipped migration step 2 (Reporting LegacyAccessToAssignments); reset the local database (README).';
    END IF;

    SELECT count(*) INTO v_count
    FROM iam.app_user u
    WHERE u.home_warehouse_id IS NOT NULL AND u.anonymised_at IS NULL
      AND NOT EXISTS (SELECT 1 FROM iam.employee_responsibility r
                      WHERE r.record_kind = 'warehouse' AND r.record_id = u.home_warehouse_id AND r.employee_id = u.employee_id);
    IF v_count > 0 THEN
        RAISE EXCEPTION 'Access drift: % legacy home hub(s) were never copied to warehouse assignments', v_count
            USING HINT = 'The database skipped migration step 2 (Reporting LegacyAccessToAssignments); reset the local database (README).';
    END IF;
END $$;

-- The masked directory no longer has a home hub (hubs come from assignments; the API shows them as rights).
DROP VIEW iam.v_app_user_masked;
CREATE VIEW iam.v_app_user_masked AS
SELECT u.id, u.user_name,
       util.mask_name(e.full_name) AS full_name_masked,
       util.mask_email(e.email)    AS email_masked,
       util.mask_phone(e.phone)    AS phone_masked,
       u.is_active,
       (SELECT array_agg(r.code ORDER BY r.code) FROM iam.position_role pr JOIN iam.role r ON r.id = pr.role_id
        WHERE pr.position_id = e.position_id) AS roles,
       u.last_login_at, u.created_at, u.anonymised_at IS NOT NULL AS is_anonymised
FROM iam.app_user u
JOIN iam.employee e ON e.id = u.employee_id;
COMMENT ON VIEW iam.v_app_user_masked IS
    'User directory with masked personal data from the employee (name -> initials, e-mail and phone partially hidden); roles from the position.';
GRANT SELECT ON iam.v_app_user_masked TO app_read;

DROP TABLE iam.user_brand;
DROP TABLE iam.user_role;
ALTER TABLE iam.app_user DROP COLUMN home_warehouse_id;

ALTER TABLE iam.app_user DROP CONSTRAINT uq_app_user_email;
ALTER TABLE iam.app_user ALTER COLUMN email DROP NOT NULL, ALTER COLUMN full_name DROP NOT NULL;
UPDATE iam.app_user SET email = NULL, full_name = NULL, phone = NULL
 WHERE email IS NOT NULL OR full_name IS NOT NULL OR phone IS NOT NULL;
COMMENT ON COLUMN iam.app_user.full_name IS 'Always empty since stage 7 (ADR 0026): the name is the employee''s; kept for an applied view script (#152).';
COMMENT ON COLUMN iam.app_user.email IS 'Always empty since stage 7 (ADR 0026): the e-mail is the employee''s; kept for an applied view script (#152).';
COMMENT ON COLUMN iam.app_user.phone IS 'Always empty since stage 7 (ADR 0026): the phone is the employee''s; kept for an applied view script (#152).';

-- GDPR erasure of an account without the legacy objects; the employee's data is erased by iam.anonymise_employee.
CREATE OR REPLACE FUNCTION iam.anonymise_user(p_user_id uuid) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_admin uuid := util.require_role('ADMINISTRATOR');
    v_tag   text := left(md5(p_user_id::text), 12);
BEGIN
    IF p_user_id = v_admin THEN
        RAISE EXCEPTION 'Administrators cannot anonymise themselves' USING ERRCODE = 'SS301';
    END IF;
    PERFORM set_config('app.reason', 'GDPR Art. 17 erasure request', true);
    UPDATE iam.app_user
       SET user_name = 'anon_' || v_tag, password_hash = NULL, is_active = false, lockout_end = NULL, last_login_at = NULL,
           security_stamp = gen_random_uuid(), anonymised_at = now()
     WHERE id = p_user_id AND anonymised_at IS NULL;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'User % not found or already anonymised', p_user_id USING ERRCODE = 'no_data_found';
    END IF;
END $$;
