-- =============================================================================
-- Fleet 0009 — retention of delivered integration events (migration MessageRetention, #30, STAGE_6_PLAN 8).
-- =============================================================================

-- Deletes delivered outbox rows (processed_at before p_outbox_before) and handled inbox rows (processed_at before
-- p_inbox_before) of this module, at most p_batch of each per call, so transactions and locks stay short; the caller
-- repeats while a full batch was deleted. Undelivered outbox rows, including the parked ones (exhausted_at), are never
-- touched. SECURITY DEFINER: the application roles keep no DELETE on the tables (functions are the write API,
-- DATABASE_DESIGN 8.2); the job outbox-retention calls it as the named system user (ADR 0024).
CREATE FUNCTION fleet.purge_processed_messages(p_outbox_before timestamptz, p_inbox_before timestamptz, p_batch integer)
RETURNS TABLE (outbox_deleted integer, inbox_deleted integer)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
BEGIN
    IF p_batch IS NULL OR p_batch < 1 THEN
        RAISE EXCEPTION 'p_batch must be positive' USING ERRCODE = 'invalid_parameter_value';
    END IF;

    DELETE FROM fleet.outbox_message
     WHERE ctid IN (SELECT ctid FROM fleet.outbox_message
                    WHERE processed_at IS NOT NULL AND processed_at < p_outbox_before
                    LIMIT p_batch);
    GET DIAGNOSTICS outbox_deleted = ROW_COUNT;

    DELETE FROM fleet.inbox_message
     WHERE ctid IN (SELECT ctid FROM fleet.inbox_message
                    WHERE processed_at < p_inbox_before
                    LIMIT p_batch);
    GET DIAGNOSTICS inbox_deleted = ROW_COUNT;

    RETURN NEXT;
END $$;
COMMENT ON FUNCTION fleet.purge_processed_messages(timestamptz, timestamptz, integer) IS
    'Deletes delivered outbox and handled inbox rows older than the cut-offs, one batch of each; never undelivered rows (#30).';

REVOKE EXECUTE ON FUNCTION fleet.purge_processed_messages(timestamptz, timestamptz, integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION fleet.purge_processed_messages(timestamptz, timestamptz, integer) TO app_write;
