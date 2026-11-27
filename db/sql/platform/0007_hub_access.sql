-- =============================================================================
-- Platform 0007 — hub access is a list and guards writes only (ADR 0026 decision 5, STAGE_7_PLAN 1.2).
-- SessionContextInterceptor sets app.warehouse_ids (sorted, comma-separated hub ids from the ss:warehouse claims)
-- transaction-locally. util.require_hub(warehouse) is the one write check: ADMINISTRATOR is unrestricted, any
-- other user needs the hub in the list, so a keeper without a hub assignment has no hub access (least privilege;
-- before, a user without a home hub was unrestricted). Reads are never scoped by hub: no read view or query calls
-- util.require_hub (architecture test). util.current_home_warehouse_id() is no longer called and goes in PR 7e.
-- =============================================================================

CREATE OR REPLACE FUNCTION util.current_warehouse_ids() RETURNS smallint[]
LANGUAGE sql STABLE AS $$
    SELECT coalesce(string_to_array(nullif(current_setting('app.warehouse_ids', true), ''), ',')::smallint[], '{}')
$$;
COMMENT ON FUNCTION util.current_warehouse_ids() IS
    'Hubs the application user of the current transaction may write at (GUC app.warehouse_ids set by the EF Core interceptor, ADR 0026); empty if none.';

CREATE OR REPLACE FUNCTION util.require_hub(p_warehouse_id smallint) RETURNS uuid
LANGUAGE plpgsql STABLE AS $$
DECLARE
    v_user uuid := util.require_app_user();
BEGIN
    IF NOT ('ADMINISTRATOR' = ANY (util.current_app_roles()))
       AND NOT (p_warehouse_id = ANY (util.current_warehouse_ids())) THEN
        RAISE EXCEPTION 'User % has no access to hub % for this change', v_user, p_warehouse_id USING ERRCODE = 'SS302';
    END IF;
    RETURN v_user;
END $$;
COMMENT ON FUNCTION util.require_hub(smallint) IS
    'Returns the session user if the user may write at the hub (ADMINISTRATOR, or the hub in app.warehouse_ids), otherwise raises SS302. Writes only (ADR 0026).';

REVOKE EXECUTE ON FUNCTION util.current_warehouse_ids(), util.require_hub(smallint) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION util.current_warehouse_ids(), util.require_hub(smallint) TO app_write;
