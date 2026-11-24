-- =============================================================================
-- Platform 0005 — role and warehouse checks read the session context (#11, MODULE_MAPPING X1, 3.6).
-- SessionContextInterceptor sets app.user_id, app.reason, app.user_roles (comma-separated role codes) and
-- app.home_warehouse_id transaction-locally from the authenticated principal. util.require_role no longer
-- reads iam (iam.user_has_role stays Identity-internal), so the Platform depends on no module.
-- =============================================================================

CREATE OR REPLACE FUNCTION util.current_app_roles() RETURNS text[]
LANGUAGE sql STABLE AS $$
    SELECT coalesce(string_to_array(nullif(current_setting('app.user_roles', true), ''), ','), '{}')
$$;
COMMENT ON FUNCTION util.current_app_roles() IS 'Role codes of the application user of the current transaction (GUC app.user_roles set by the EF Core interceptor); empty for system jobs.';

CREATE OR REPLACE FUNCTION util.current_home_warehouse_id() RETURNS smallint
LANGUAGE sql STABLE AS $$
    SELECT nullif(current_setting('app.home_warehouse_id', true), '')::smallint
$$;
COMMENT ON FUNCTION util.current_home_warehouse_id() IS 'Home warehouse of the application user of the current transaction (GUC app.home_warehouse_id set by the EF Core interceptor); NULL if the user has none.';

CREATE OR REPLACE FUNCTION util.require_role(VARIADIC p_roles text[]) RETURNS uuid
LANGUAGE plpgsql STABLE AS $$
DECLARE
    v_user uuid := util.require_app_user();
BEGIN
    IF NOT (util.current_app_roles() && p_roles) THEN
        RAISE EXCEPTION 'User % lacks the required role (one of %)', v_user, p_roles USING ERRCODE = 'SS301';
    END IF;
    RETURN v_user;
END $$;
COMMENT ON FUNCTION util.require_role(text[]) IS 'Returns the session user if the session roles (app.user_roles) include one of the roles, otherwise raises SS301 (defence in depth behind API authorisation).';

-- New functions get EXECUTE for PUBLIC by default; the Platform grants are explicit (0003_grants.sql).
REVOKE EXECUTE ON FUNCTION util.current_app_roles(), util.current_home_warehouse_id() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION util.current_app_roles(), util.current_home_warehouse_id() TO app_write;
