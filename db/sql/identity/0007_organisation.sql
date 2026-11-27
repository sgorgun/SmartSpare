-- =============================================================================
-- Identity 0007 — organisation: positions with role templates and employees (ADR 0026,
-- STAGE_7_PLAN sections 1.1 and 1.6, migration step 1). The tables are created by EF (migration
-- Organisation); this script seeds the standard positions, turns every existing user into an
-- employee with a position whose template equals the user's roles, and adds triggers, grants, the
-- masked views and iam.anonymise_employee. From here on an account's roles come from its
-- employee's position; iam.user_role stays as a read-only shadow until PR 7e drops it.
-- =============================================================================

-- ---- 1. Standard positions (initial master data; editable, archived instead of deleted) ---------
INSERT INTO iam.position (code, name, description) VALUES
    ('BRAND_MANAGER',        'Brand manager',        'Responsible for the parts business of one or more brands.'),
    ('COMMERCIAL_DIRECTOR',  'Commercial director',  'Brand manager who also sponsors purchasing decisions.'),
    ('SERVICE_ENGINEER',     'Service engineer',     'Services customer machines in the field and in the workshop.'),
    ('WAREHOUSE_KEEPER',     'Warehouse keeper',     'Receives, stores and issues parts at a hub.'),
    ('INVENTORY_PLANNER',    'Inventory planner',    'Plans stock levels, transfers and purchases across the hubs.'),
    ('MANAGING_DIRECTOR',    'Managing director',    'Sponsor: approves large purchases and follows the KPIs.'),
    ('SYSTEM_ADMINISTRATOR', 'System administrator', 'Administers SmartSpare: people, master data and access.')
ON CONFLICT (code) DO NOTHING;

INSERT INTO iam.position_role (position_id, role_id)
SELECT p.id, r.id
FROM (VALUES ('BRAND_MANAGER', 'BRAND_MANAGER'), ('COMMERCIAL_DIRECTOR', 'BRAND_MANAGER'), ('COMMERCIAL_DIRECTOR', 'SPONSOR'),
             ('SERVICE_ENGINEER', 'SERVICE_ENGINEER'), ('WAREHOUSE_KEEPER', 'WAREHOUSE_KEEPER'),
             ('INVENTORY_PLANNER', 'INVENTORY_PLANNER'), ('MANAGING_DIRECTOR', 'SPONSOR'),
             ('SYSTEM_ADMINISTRATOR', 'ADMINISTRATOR')) AS t (position_code, role_code)
JOIN iam.position p ON p.code = t.position_code
JOIN iam.role r     ON r.code = t.role_code
ON CONFLICT DO NOTHING;

-- ---- 2. Existing users become employees (idempotent: users with an employee are skipped) --------
DO $$
DECLARE
    v_user     record;
    v_position smallint;
    v_number   integer := coalesce((SELECT max(substr(code, 2)::integer) FROM iam.employee), 0);
BEGIN
    FOR v_user IN
        SELECT u.id, u.full_name, u.email, u.phone, u.created_at, u.anonymised_at,
               coalesce(array_agg(r.code::text ORDER BY r.code) FILTER (WHERE r.code IS NOT NULL), '{}') AS roles
        FROM iam.app_user u
        LEFT JOIN iam.user_role ur ON ur.user_id = u.id
        LEFT JOIN iam.role r       ON r.id = ur.role_id
        WHERE u.employee_id IS NULL
        GROUP BY u.id
        ORDER BY u.created_at, u.id
    LOOP
        -- The position whose template is exactly the user's role set; a new one if none exists.
        SELECT p.id INTO v_position
        FROM iam.position p
        WHERE NOT p.is_archived
          AND coalesce((SELECT array_agg(r.code::text ORDER BY r.code) FROM iam.position_role pr JOIN iam.role r ON r.id = pr.role_id
                        WHERE pr.position_id = p.id), '{}') = v_user.roles
        ORDER BY p.id
        LIMIT 1;

        IF v_position IS NULL THEN
            INSERT INTO iam.position (code, name, description)
            VALUES ('POS_' || upper(left(md5(array_to_string(v_user.roles, ',')), 8)),
                    CASE WHEN cardinality(v_user.roles) = 0 THEN 'Staff without system roles'
                         ELSE initcap(replace(lower(array_to_string(v_user.roles, ' and ')), '_', ' ')) END,
                    'Created by the stage 7 migration from the roles of existing users.')
            RETURNING id INTO v_position;
            INSERT INTO iam.position_role (position_id, role_id)
            SELECT v_position, r.id FROM iam.role r WHERE r.code = ANY (v_user.roles);
        END IF;

        v_number := v_number + 1;
        INSERT INTO iam.employee (id, code, full_name, email, phone, position_id, hired_on,
                                  is_archived, archived_at, left_on, anonymised_at)
        VALUES (v_user.id, 'E' || lpad(v_number::text, 4, '0'),
                CASE WHEN v_user.anonymised_at IS NULL THEN v_user.full_name ELSE 'Anonymised Employee' END,
                CASE WHEN v_user.anonymised_at IS NULL THEN v_user.email END,
                CASE WHEN v_user.anonymised_at IS NULL THEN v_user.phone END,
                v_position, v_user.created_at::date,
                v_user.anonymised_at IS NOT NULL, v_user.anonymised_at, v_user.anonymised_at::date, v_user.anonymised_at);

        UPDATE iam.app_user SET employee_id = v_user.id WHERE id = v_user.id;
    END LOOP;
END $$;

-- ---- 3. Triggers ------------------------------------------------------------------------------------
CREATE TRIGGER trg_position_updated_at BEFORE UPDATE ON iam.position
    FOR EACH ROW EXECUTE FUNCTION util.trg_set_updated_at();
CREATE TRIGGER trg_employee_updated_at BEFORE UPDATE ON iam.employee
    FOR EACH ROW EXECUTE FUNCTION util.trg_set_updated_at();

-- Audit (US18): who changed a position, its template or an employee. Personal data is never copied.
CREATE TRIGGER trg_audit AFTER INSERT OR UPDATE OR DELETE ON iam.position
    FOR EACH ROW EXECUTE FUNCTION audit.trg_row_audit();
CREATE TRIGGER trg_audit AFTER INSERT OR UPDATE OR DELETE ON iam.position_role
    FOR EACH ROW EXECUTE FUNCTION audit.trg_row_audit();
CREATE TRIGGER trg_audit AFTER INSERT OR UPDATE OR DELETE ON iam.employee
    FOR EACH ROW EXECUTE FUNCTION audit.trg_row_audit('full_name', 'email', 'phone');

-- ---- 4. Roles now come from the position --------------------------------------------------------------
CREATE OR REPLACE FUNCTION iam.user_has_role(p_user_id uuid, VARIADIC p_roles text[]) RETURNS boolean
LANGUAGE sql STABLE AS $$
    SELECT EXISTS (
        SELECT 1
        FROM iam.app_user u
        JOIN iam.employee e       ON e.id = u.employee_id
        JOIN iam.position_role pr ON pr.position_id = e.position_id
        JOIN iam.role r           ON r.id = pr.role_id
        WHERE u.id = p_user_id AND u.is_active AND NOT e.is_archived AND r.code = ANY (p_roles))
$$;
COMMENT ON FUNCTION iam.user_has_role(uuid, text[]) IS
    'TRUE if the active user''s employee holds a position whose role template has at least one of the given roles (ADR 0026).';

-- The masked directory keeps its columns; names and roles come from the employee and the position.
CREATE OR REPLACE VIEW iam.v_app_user_masked AS
SELECT u.id, u.user_name,
       util.mask_name(e.full_name) AS full_name_masked,
       util.mask_email(e.email)    AS email_masked,
       util.mask_phone(e.phone)    AS phone_masked,
       u.is_active, u.home_warehouse_id,
       (SELECT array_agg(r.code ORDER BY r.code) FROM iam.position_role pr JOIN iam.role r ON r.id = pr.role_id
        WHERE pr.position_id = e.position_id) AS roles,
       u.last_login_at, u.created_at, u.anonymised_at IS NOT NULL AS is_anonymised
FROM iam.app_user u
JOIN iam.employee e ON e.id = u.employee_id;
COMMENT ON VIEW iam.v_app_user_masked IS
    'User directory with masked personal data from the employee (name -> initials, e-mail and phone partially hidden); roles from the position.';

CREATE VIEW iam.v_employee_masked AS
SELECT e.id, e.code, util.mask_name(e.full_name) AS full_name_masked, util.mask_email(e.email) AS email_masked,
       e.position_id, e.hired_on, e.left_on, e.is_archived, e.anonymised_at IS NOT NULL AS is_anonymised
FROM iam.employee e;
COMMENT ON VIEW iam.v_employee_masked IS 'Employees with masked personal data, for read-only roles.';

-- ---- 5. GDPR: erasure of an employee (Art. 17) ----------------------------------------------------------
CREATE FUNCTION iam.anonymise_employee(p_employee_id uuid) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_admin   uuid := util.require_role('ADMINISTRATOR');
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
COMMENT ON FUNCTION iam.anonymise_employee(uuid) IS
    'Irreversibly replaces the personal data of an employee and of the account; the rows (and audit references) remain.';

-- ---- 6. Privileges ------------------------------------------------------------------------------------------
REVOKE EXECUTE ON FUNCTION iam.anonymise_employee(uuid) FROM PUBLIC;
GRANT SELECT ON iam.position, iam.position_role TO app_read;
GRANT SELECT (id, code, position_id, hired_on, left_on, is_archived, archived_at, archived_by, anonymised_at, created_at, updated_at)
    ON iam.employee TO app_read;
GRANT SELECT ON iam.v_employee_masked TO app_read;
GRANT SELECT (employee_id) ON iam.app_user TO app_read;
GRANT SELECT ON iam.position, iam.position_role, iam.employee TO app_write;   -- sign-in and claims
GRANT INSERT, UPDATE ON iam.position, iam.employee TO app_admin;
GRANT INSERT, DELETE ON iam.position_role TO app_admin;
GRANT EXECUTE ON FUNCTION iam.anonymise_employee(uuid) TO app_admin;
