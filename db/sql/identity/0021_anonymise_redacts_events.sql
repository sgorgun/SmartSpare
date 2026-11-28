-- =============================================================================
-- Identity 0021 — anonymisation also redacts the employee in Identity's event payloads (ADR 0029 amendment 2, PR 7j1c;
-- product owner, 28 November 2026). EmployeeDirectoryChanged carries an employee's display name, and delivered outbox
-- rows are kept 7 days for tracing. iam.anonymise_employee now replaces the name in every such row of iam.outbox_message,
-- delivered or pending, with the anonymised name, in the same transaction. A pending row is then delivered redacted,
-- which is harmless: the anonymisation's own event is newer and wins in the copies. Consumers' inbox rows hold no payload.
-- Otherwise unchanged from identity/0012_require_permission.sql.
-- =============================================================================

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

    -- The employee's name in earlier EmployeeDirectoryChanged payloads (ADR 0029 amendment 2).
    UPDATE iam.outbox_message o
       SET payload = jsonb_set(o.payload, '{employees}',
               (SELECT jsonb_agg(CASE WHEN e->>'employeeId' = p_employee_id::text
                                      THEN e || jsonb_build_object('displayName', 'Anonymised Employee')
                                      ELSE e END ORDER BY ord)
                FROM jsonb_array_elements(o.payload->'employees') WITH ORDINALITY AS x (e, ord)))
     WHERE o.type LIKE 'SmartSpare.Modules.Identity.Contracts.EmployeeDirectoryChanged,%'
       AND o.payload->'employees' @> jsonb_build_array(jsonb_build_object('employeeId', p_employee_id::text));
END $$;
COMMENT ON FUNCTION iam.anonymise_employee(uuid) IS
    'GDPR Art. 17: erases the employee''s and the account''s personal data and redacts the name in Identity''s EmployeeDirectoryChanged payloads (ADR 0029).';
