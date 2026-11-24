-- =============================================================================
-- Platform 0006 — audit rows of UPDATEs name the changed record (#71).
-- 0002_audit_log.sql reduced OLD/NEW to the changed columns before it read the record id, so every UPDATE
-- that didn't change "id" got record_id '?'. The id is now taken from the full row first: the "id" column,
-- or for composite keys the *_id columns joined with '|' in column-name order (as for INSERT and DELETE).
-- =============================================================================

CREATE OR REPLACE FUNCTION audit.trg_row_audit() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_row      jsonb := coalesce(to_jsonb(NEW), to_jsonb(OLD));
    v_old      jsonb;
    v_new      jsonb;
    v_excluded text[] := ARRAY['updated_at'] || coalesce(TG_ARGV::text[], '{}');
    v_changed  text[];
    v_id       text;
BEGIN
    -- record id from the full row, before the values are reduced to the changed columns
    v_id := v_row ->> 'id';
    IF v_id IS NULL THEN  -- composite keys: concatenate *_id columns
        SELECT string_agg(value, '|' ORDER BY key) INTO v_id
        FROM jsonb_each_text(v_row)
        WHERE key LIKE '%\_id' ESCAPE '\';
    END IF;

    IF TG_OP IN ('UPDATE', 'DELETE') THEN v_old := to_jsonb(OLD) - v_excluded; END IF;
    IF TG_OP IN ('INSERT', 'UPDATE') THEN v_new := to_jsonb(NEW) - v_excluded; END IF;

    IF TG_OP = 'UPDATE' THEN
        SELECT array_agg(n.key ORDER BY n.key) INTO v_changed
        FROM jsonb_each(v_new) n
        WHERE n.value IS DISTINCT FROM v_old -> n.key;
        IF v_changed IS NULL THEN RETURN NULL; END IF;     -- nothing relevant changed
        -- keep only the changed columns
        SELECT jsonb_object_agg(k, v_old -> k), jsonb_object_agg(k, v_new -> k)
          INTO v_old, v_new
        FROM unnest(v_changed) AS k;
    END IF;

    INSERT INTO audit.audit_log (user_id, action, table_name, record_id, old_values, new_values, changed_columns, reason)
    VALUES (nullif(current_setting('app.user_id', true), '')::uuid,
            TG_OP, TG_TABLE_SCHEMA || '.' || TG_TABLE_NAME, coalesce(v_id, '?'),
            v_old, v_new, v_changed,
            nullif(current_setting('app.reason', true), ''));
    RETURN NULL;
END $$;
COMMENT ON FUNCTION audit.trg_row_audit() IS 'AFTER ROW trigger writing changed columns into audit.audit_log; record_id = id column or the *_id columns of a composite key; arguments = excluded columns (PII, secrets).';
