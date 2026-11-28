-- =============================================================================
-- Identity 0018 — permissions granted by an upgrade to an edited seeded role (#211, ADR 0028 amendment). iam.role gains
-- is_customised and upgrade_pending (EF). From now on a migration that gives a new permission to a default role calls
-- iam.grant_default_permission instead of inserting into iam.role_permission itself:
--   * the grant is audited as the system user with the reason "granted by upgrade <version>";
--   * a role an administrator customised is marked upgrade_pending = <version> until an administrator confirms it on
--     the roles screen (POST /roles/{id}/confirm-upgrade).
-- Applied scripts are never edited; the grants made before this script stay as they were.
-- =============================================================================

CREATE FUNCTION iam.grant_default_permission(p_role_code text, p_permission text, p_version text) RETURNS void
LANGUAGE plpgsql AS $$
DECLARE
    v_role_id     smallint;
    v_customised  boolean;
    v_user        text := coalesce(current_setting('app.user_id', true), '');
    v_reason      text := coalesce(current_setting('app.reason', true), '');
BEGIN
    SELECT id, is_customised INTO v_role_id, v_customised FROM iam.role WHERE code = p_role_code;
    IF v_role_id IS NULL THEN
        RETURN;
    END IF;

    PERFORM set_config('app.user_id', '00000000-0000-4000-8000-00000000f001', true);
    PERFORM set_config('app.reason', 'granted by upgrade ' || p_version, true);
    INSERT INTO iam.role_permission (role_id, permission) VALUES (v_role_id, p_permission) ON CONFLICT DO NOTHING;
    IF FOUND AND v_customised THEN
        UPDATE iam.role SET upgrade_pending = p_version WHERE id = v_role_id;
    END IF;

    PERFORM set_config('app.user_id', v_user, true);
    PERFORM set_config('app.reason', v_reason, true);
END $$;
COMMENT ON FUNCTION iam.grant_default_permission(text, text, text) IS
    'For migrations: grants a permission to a seeded role, audited as the system user ("granted by upgrade <version>"); a customised role is marked upgrade_pending (#211).';
REVOKE ALL ON FUNCTION iam.grant_default_permission(text, text, text) FROM PUBLIC;
