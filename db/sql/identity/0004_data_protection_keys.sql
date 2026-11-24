-- =============================================================================
-- Identity 0004 — grants of the Data Protection key ring (#22, STAGE_3_PLAN section 5).
-- The table is created by the EF migration AddDataProtectionKeys. The key XML is secret material: the API login
-- (app_write) reads and adds keys, app_read gets nothing (no masked view either), and the audit trigger is
-- deliberately not attached, so key material never reaches audit.audit_log.
-- =============================================================================

GRANT SELECT, INSERT ON iam.data_protection_keys TO app_write;

-- The identity column's sequence (INSERT through the default does not need it, explicit nextval would).
DO $$
BEGIN
    EXECUTE format('GRANT USAGE ON SEQUENCE %s TO app_write', pg_get_serial_sequence('iam.data_protection_keys', 'id'));
END $$;
