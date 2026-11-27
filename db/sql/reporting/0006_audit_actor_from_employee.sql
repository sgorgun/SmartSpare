-- =============================================================================
-- Reporting 0006 — the masked audit trail names the actor from the employee (ADR 0026): the account no longer holds
-- the person's name. Same columns as before, so callers are unchanged.
-- =============================================================================

CREATE OR REPLACE VIEW audit.v_audit_log_masked AS
SELECT a.id, a.occurred_at, a.action, a.table_name, a.record_id, a.changed_columns,
       a.old_values, a.new_values, a.reason,
       u.user_name, util.mask_name(e.full_name) AS actor_masked, a.db_role
FROM audit.audit_log a
LEFT JOIN iam.app_user u ON u.id = a.user_id
LEFT JOIN iam.employee e ON e.id = u.employee_id;
COMMENT ON VIEW audit.v_audit_log_masked IS 'Audit trail for the sponsor / auditors with the actor''s name (from the employee) masked.';
