-- =============================================================================
-- Identity 0001 — iam: functions, triggers, audit triggers.
-- Statements copied verbatim from V001 (schema comment), V004 and V015. Tables, keys,
-- CHECKs, indexes and comments are created by IdentityDbContext (ADR 0011, MODULE_MAPPING 1.2).
-- Not here: iam.v_app_user_masked reads inventory.warehouse and is created by the last
-- Reporting migration in PR 2d (MODULE_MAPPING 3.2).
-- =============================================================================

COMMENT ON SCHEMA iam         IS 'Core layer / IAM module: application users, roles, brand assignments.';

CREATE TRIGGER trg_app_user_updated_at BEFORE UPDATE ON iam.app_user
    FOR EACH ROW EXECUTE FUNCTION util.trg_set_updated_at();

-- Helper used by business functions for authorisation checks inside the DB.
CREATE FUNCTION iam.user_has_role(p_user_id uuid, VARIADIC p_roles text[]) RETURNS boolean
LANGUAGE sql STABLE AS $$
    SELECT EXISTS (
        SELECT 1
        FROM iam.user_role ur
        JOIN iam.role r     ON r.id = ur.role_id
        JOIN iam.app_user u ON u.id = ur.user_id
        WHERE ur.user_id = p_user_id AND u.is_active AND r.code = ANY (p_roles))
$$;
COMMENT ON FUNCTION iam.user_has_role(uuid, text[]) IS 'TRUE if the active user has at least one of the given role codes.';

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

-- Audit triggers (US18). Arguments = columns never copied to the audit log.
CREATE TRIGGER trg_audit AFTER INSERT OR UPDATE OR DELETE ON iam.app_user
    FOR EACH ROW EXECUTE FUNCTION audit.trg_row_audit('full_name', 'email', 'phone', 'password_hash', 'security_stamp', 'last_login_at', 'failed_login_count');
CREATE TRIGGER trg_audit AFTER INSERT OR UPDATE OR DELETE ON iam.user_role
    FOR EACH ROW EXECUTE FUNCTION audit.trg_row_audit();
CREATE TRIGGER trg_audit AFTER INSERT OR UPDATE OR DELETE ON iam.user_brand
    FOR EACH ROW EXECUTE FUNCTION audit.trg_row_audit();
