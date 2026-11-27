-- =============================================================================
-- Platform 0009 — permission checks in SQL (ADR 0028, PR 7x2). The EF Core interceptor passes the permissions of the
-- request (added server-side from the user's roles) as app.user_permissions, next to app.user_id, app.user_roles and
-- app.warehouse_ids.
--   util.current_app_permissions()  the permission names of the current transaction;
--   util.require_permission(name)   the session user if it holds the permission, otherwise SS301 (the replacement of
--                                   util.require_role in every business function: defence in depth behind the API);
--   util.require_hub(warehouse)     now unrestricted for Organisation.Scope.AllHubs instead of the ADMINISTRATOR role.
-- Background jobs and consumers have no permissions: they act as a system user or for attribution only (ADR 0024).
-- =============================================================================

CREATE FUNCTION util.current_app_permissions() RETURNS text[]
LANGUAGE sql STABLE AS $$
    SELECT coalesce(string_to_array(nullif(current_setting('app.user_permissions', true), ''), ','), '{}')
$$;
COMMENT ON FUNCTION util.current_app_permissions() IS
    'Permissions of the application user of the current transaction (GUC app.user_permissions set by the EF Core interceptor, ADR 0028); empty for system jobs.';

CREATE FUNCTION util.require_permission(p_permission text) RETURNS uuid
LANGUAGE plpgsql STABLE AS $$
DECLARE
    v_user uuid := util.require_app_user();
BEGIN
    IF NOT (p_permission = ANY (util.current_app_permissions())) THEN
        RAISE EXCEPTION 'User % lacks the permission %', v_user, p_permission USING ERRCODE = 'SS301';
    END IF;
    RETURN v_user;
END $$;
COMMENT ON FUNCTION util.require_permission(text) IS
    'Returns the session user if it holds the permission (app.user_permissions), otherwise raises SS301 (ADR 0028).';

CREATE OR REPLACE FUNCTION util.require_hub(p_warehouse_id smallint) RETURNS uuid
LANGUAGE plpgsql STABLE AS $$
DECLARE
    v_user uuid := util.require_app_user();
BEGIN
    IF NOT ('Organisation.Scope.AllHubs' = ANY (util.current_app_permissions()))
       AND NOT (p_warehouse_id = ANY (util.current_warehouse_ids())) THEN
        RAISE EXCEPTION 'User % has no access to hub % for this change', v_user, p_warehouse_id USING ERRCODE = 'SS302';
    END IF;
    RETURN v_user;
END $$;
COMMENT ON FUNCTION util.require_hub(smallint) IS
    'Returns the session user if the user may write at the hub (Organisation.Scope.AllHubs, or the hub in app.warehouse_ids), otherwise raises SS302. Writes only (ADR 0026, ADR 0028).';

REVOKE EXECUTE ON FUNCTION util.current_app_permissions(), util.require_permission(text) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION util.current_app_permissions(), util.require_permission(text) TO app_write;
