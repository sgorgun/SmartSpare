-- =============================================================================
-- Identity 0012 — the business functions check permissions, not role codes (ADR 0028, PR 7x2). Each function is redefined
-- as it was last defined, with util.require_role(...) replaced by util.require_permission(...) with the permission its
-- endpoint requires (PolicySqlPermissionParityTests). The signatures and grants are unchanged.
-- Generated from the latest definitions; applied scripts stay untouched (#152).
-- =============================================================================

-- iam.anonymise_employee, as last defined in identity/0007_organisation.sql, with the permission check.
CREATE OR REPLACE FUNCTION iam.anonymise_employee(p_employee_id uuid) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_admin   uuid := util.require_permission('Organisation.Employee.Anonymise');
    v_account uuid;
BEGIN
    SELECT id INTO v_account FROM iam.app_user WHERE employee_id = p_employee_id;
    IF v_account = v_admin THEN
        RAISE EXCEPTION 'Administrators cannot anonymise themselves' USING ERRCODE = 'SS301';
    END IF;
    PERFORM set_config('app.reason', 'GDPR Art. 17 erasure request', true);
    UPDATE iam.employee
       SET full_name = 'Anonymised Employee', email = NULL, phone = NULL,
           is_archived = true, archived_at = coalesce(archived_at, now()), archived_by = coalesce(archived_by, v_admin),
           left_on = coalesce(left_on, current_date), anonymised_at = now()
     WHERE id = p_employee_id AND anonymised_at IS NULL;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Employee % not found or already anonymised', p_employee_id USING ERRCODE = 'no_data_found';
    END IF;
    IF v_account IS NOT NULL AND EXISTS (SELECT 1 FROM iam.app_user WHERE id = v_account AND anonymised_at IS NULL) THEN
        PERFORM iam.anonymise_user(v_account);
    END IF;
END $$;

-- iam.anonymise_user, as last defined in identity/0009_drop_legacy_access.sql, with the permission check.
CREATE OR REPLACE FUNCTION iam.anonymise_user(p_user_id uuid) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_admin uuid := util.require_permission('Organisation.Employee.Anonymise');
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
