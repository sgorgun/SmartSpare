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

CREATE OR REPLACE FUNCTION pg_temp.act_as(p_user_name text) RETURNS uuid
LANGUAGE sql AS $$
    SELECT set_config('app.user_id', (SELECT id::text FROM iam.app_user WHERE user_name = p_user_name), false)::uuid
$$;
