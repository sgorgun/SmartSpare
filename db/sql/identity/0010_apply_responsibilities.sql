-- =============================================================================
-- Identity 0010 — the projection of responsibility assignments has one write path (ADR 0026 decision 3,
-- STAGE_7_PLAN 1.7). Until now app_write could insert and delete projection rows directly, and every application
-- login holds app_write (the Inventory and Fleet service logins too). Now:
--   * the direct INSERT/UPDATE/DELETE privileges are revoked from app_write;
--   * iam.apply_responsibilities (SECURITY DEFINER) is the only way to change the projection: it applies one
--     ResponsibilitiesChanged state transfer if it is newer than the last one of that record, and rotates the stamps
--     of the employees whose rights changed;
--   * EXECUTE goes only to the logins that host the Identity module: smartspare_api (the single host) and
--     smartspare_identity (the Identity service); the Inventory, Fleet and other service logins cannot call it.
-- Per-module roles (stage 10, #16) later narrow smartspare_api too.
-- =============================================================================

REVOKE INSERT, UPDATE, DELETE ON iam.employee_responsibility FROM app_write;
REVOKE INSERT, UPDATE, DELETE ON iam.responsibility_source FROM app_write;

CREATE FUNCTION iam.apply_responsibilities(
    p_record_kind text, p_record_id smallint, p_occurred_at timestamptz, p_employee_ids uuid[], p_roles text[]) RETURNS boolean
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_previous uuid[];
BEGIN
    PERFORM util.require_app_user();      -- the consumer acts as the user who made the change (ADR 0024)
    -- One record at a time, so two deliveries of the same record cannot interleave.
    PERFORM pg_advisory_xact_lock(hashtext('iam.responsibility:' || p_record_kind || ':' || p_record_id));
    IF EXISTS (SELECT 1 FROM iam.responsibility_source
               WHERE record_kind = p_record_kind AND record_id = p_record_id AND source_occurred_at >= p_occurred_at) THEN
        RETURN false;                     -- an older or repeated state never overwrites a newer one
    END IF;

    SELECT coalesce(array_agg(employee_id), '{}') INTO v_previous
    FROM iam.employee_responsibility WHERE record_kind = p_record_kind AND record_id = p_record_id;
    DELETE FROM iam.employee_responsibility WHERE record_kind = p_record_kind AND record_id = p_record_id;
    INSERT INTO iam.employee_responsibility (record_kind, record_id, employee_id, assignment_role)
    SELECT DISTINCT p_record_kind, p_record_id, a.employee_id, a.assignment_role
    FROM unnest(p_employee_ids, p_roles) AS a (employee_id, assignment_role)
    JOIN iam.employee e ON e.id = a.employee_id;

    UPDATE iam.app_user SET security_stamp = gen_random_uuid()
     WHERE employee_id = ANY (v_previous || p_employee_ids);

    INSERT INTO iam.responsibility_source (record_kind, record_id, source_occurred_at)
    VALUES (p_record_kind, p_record_id, p_occurred_at)
    ON CONFLICT (record_kind, record_id) DO UPDATE SET source_occurred_at = EXCLUDED.source_occurred_at;
    RETURN true;
END $$;
COMMENT ON FUNCTION iam.apply_responsibilities(text, smallint, timestamptz, uuid[], text[]) IS
    'Applies one ResponsibilitiesChanged state transfer to the projection if it is newer, and rotates the affected stamps; the only write path (ADR 0026).';

REVOKE EXECUTE ON FUNCTION iam.apply_responsibilities(text, smallint, timestamptz, uuid[], text[]) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION iam.apply_responsibilities(text, smallint, timestamptz, uuid[], text[]) TO smartspare_api, smartspare_identity;
