-- =============================================================================
-- Platform 0008 — change history of one catalogue record (ADR 0025, STAGE_7_PLAN 2.5).
--   audit.history_table      the allow-list: the migration of a module registers each catalogue table whose history its
--                            users may read (the audit trigger already leaves personal-data columns out).
--   audit.record_history     time, action, account id, changed columns, old and new values and reason of one record,
--                            newest first, one page at a time, with the total count. SECURITY DEFINER because the
--                            application roles cannot read audit.audit_log; refuses a table not on the allow-list.
-- The endpoint resolves the account ids to names through the employee directory of Identity.
-- =============================================================================

CREATE TABLE audit.history_table (
    table_name varchar(80) NOT NULL,
    CONSTRAINT pk_history_table PRIMARY KEY (table_name),
    CONSTRAINT ck_history_table_name CHECK (table_name ~ '^[a-z_]+\.[a-z_]+$')
);
COMMENT ON TABLE audit.history_table IS 'Catalogue tables whose per-record change history the application may show (audit.record_history).';
-- USAGE on the schema only lets app_read call the function; audit.audit_log itself stays closed to it (T03).
GRANT USAGE ON SCHEMA audit TO app_read;
GRANT SELECT ON audit.history_table TO app_read;

CREATE FUNCTION audit.record_history(p_table text, p_record_id text, p_page int, p_page_size int)
RETURNS TABLE (occurred_at timestamptz, action varchar, user_id uuid, changed_columns text[],
               old_values jsonb, new_values jsonb, reason varchar, total_count bigint)
LANGUAGE plpgsql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM audit.history_table h WHERE h.table_name = p_table) THEN
        RAISE EXCEPTION 'The history of % is not available', p_table USING ERRCODE = 'insufficient_privilege';
    END IF;
    IF p_page < 1 OR p_page_size NOT BETWEEN 1 AND 100 THEN
        RAISE EXCEPTION 'Page % of size % is out of range', p_page, p_page_size USING ERRCODE = 'invalid_parameter_value';
    END IF;

    RETURN QUERY
    SELECT a.occurred_at, a.action, a.user_id, a.changed_columns, a.old_values, a.new_values, a.reason, count(*) OVER ()
    FROM audit.audit_log a
    WHERE a.table_name = p_table AND a.record_id = p_record_id
    ORDER BY a.occurred_at DESC, a.id DESC
    OFFSET (p_page - 1) * p_page_size LIMIT p_page_size;
END $$;
COMMENT ON FUNCTION audit.record_history(text, text, int, int) IS
    'Change history of one record of an allow-listed catalogue table (audit.history_table), newest first (ADR 0025).';
REVOKE EXECUTE ON FUNCTION audit.record_history(text, text, int, int) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION audit.record_history(text, text, int, int) TO app_read;
