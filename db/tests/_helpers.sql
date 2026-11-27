-- Test helpers (session-local, pg_temp). Included by every test file.
-- Every assertion prints "PASS ..." or aborts the test file with "FAIL ...".
\set ON_ERROR_STOP on
SET client_min_messages = notice;

CREATE OR REPLACE FUNCTION pg_temp.ok(p_condition boolean, p_label text) RETURNS void
LANGUAGE plpgsql AS $$
BEGIN
    IF p_condition IS TRUE THEN
        RAISE NOTICE 'PASS  %', p_label;
    ELSE
        RAISE EXCEPTION 'FAIL  % (condition is %)', p_label, coalesce(p_condition::text, 'NULL');
    END IF;
END $$;

-- Executes p_sql and asserts that it fails with SQLSTATE p_state.
CREATE OR REPLACE FUNCTION pg_temp.expect_error(p_sql text, p_state text, p_label text) RETURNS void
LANGUAGE plpgsql AS $$
BEGIN
    BEGIN
        EXECUTE p_sql;
    EXCEPTION WHEN others THEN
        IF SQLSTATE = p_state THEN
            RAISE NOTICE 'PASS  %  [% %]', p_label, SQLSTATE, left(SQLERRM, 110);
            RETURN;
        END IF;
        RAISE EXCEPTION 'FAIL  %: expected SQLSTATE %, got % (%)', p_label, p_state, SQLSTATE, SQLERRM;
    END;
    RAISE EXCEPTION 'FAIL  %: expected SQLSTATE %, but the statement succeeded', p_label, p_state;
END $$;

-- Asserts that the plan of p_sql uses the index p_index (EXPLAIN, no execution).
CREATE OR REPLACE FUNCTION pg_temp.uses_index(p_sql text, p_index text, p_label text) RETURNS void
LANGUAGE plpgsql AS $$
DECLARE
    v_plan text;
BEGIN
    EXECUTE 'EXPLAIN (FORMAT JSON) ' || p_sql INTO v_plan;
    IF position(p_index IN v_plan) > 0 THEN
        RAISE NOTICE 'PASS  % (uses %)', p_label, p_index;
    ELSE
        RAISE EXCEPTION 'FAIL  % — plan does not use %: %', p_label, p_index, v_plan;
    END IF;
END $$;

-- Iteration count of an ASP.NET Core Identity V3 hash with PRF HMAC-SHA512 and a 16-byte salt; NULL for anything
-- else. The V3 header is: marker 0x01, then PRF, iteration count and salt length as big-endian int32.
-- Tests check the decoded header instead of a literal prefix, which changes with the iteration count.
CREATE OR REPLACE FUNCTION pg_temp.identity_v3_iterations(p_hash text) RETURNS bigint
LANGUAGE sql AS $$
    -- << and | have the same precedence in PostgreSQL, hence the parentheses around every shift.
    SELECT CASE
             WHEN length(b) >= 61 AND get_byte(b, 0) = 1
              AND ((get_byte(b, 1)::bigint << 24) | (get_byte(b, 2) << 16) | (get_byte(b, 3) << 8) | get_byte(b, 4)) = 2
              AND ((get_byte(b, 9)::bigint << 24) | (get_byte(b, 10) << 16) | (get_byte(b, 11) << 8) | get_byte(b, 12)) = 16
             THEN (get_byte(b, 5)::bigint << 24) | (get_byte(b, 6) << 16) | (get_byte(b, 7) << 8) | get_byte(b, 8)
           END
    FROM (SELECT decode(p_hash, 'base64') AS b) AS h
$$;

-- Acts as a signed-in user the way SessionContextInterceptor does (MODULE_MAPPING 3.6): app.user_id,
-- app.user_roles (the role template of the employee's position, ADR 0026) and app.home_warehouse_id. The
-- application takes roles and warehouse from the claims; the tests read them from iam.
CREATE OR REPLACE FUNCTION pg_temp.act_as(p_user_name text) RETURNS uuid
LANGUAGE sql AS $$
    SELECT set_config('app.user_roles', coalesce((SELECT string_agg(r.code, ',' ORDER BY r.code)
                                                  FROM iam.app_user u
                                                  JOIN iam.employee e ON e.id = u.employee_id
                                                  JOIN iam.position_role pr ON pr.position_id = e.position_id
                                                  JOIN iam.role r ON r.id = pr.role_id
                                                  WHERE u.user_name = p_user_name AND u.is_active AND NOT e.is_archived), ''), false),
           set_config('app.home_warehouse_id', coalesce((SELECT home_warehouse_id::text FROM iam.app_user
                                                         WHERE user_name = p_user_name), ''), false);
    SELECT set_config('app.user_id', (SELECT id::text FROM iam.app_user WHERE user_name = p_user_name), false)::uuid
$$;
