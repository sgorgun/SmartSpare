-- =============================================================================
-- Reporting 0002 — masked audit trail for the sponsor and auditors.
-- Copied verbatim from V014 (view) and V015 (grants). Owned by Reporting (MODULE_MAPPING 1.7): it reads
-- iam.app_user. It stays in the audit schema until stage 9 moves it to mart (#15).
-- =============================================================================

CREATE VIEW audit.v_audit_log_masked AS
SELECT a.id, a.occurred_at, a.action, a.table_name, a.record_id, a.changed_columns,
       a.old_values, a.new_values, a.reason,
       u.user_name, util.mask_name(u.full_name) AS actor_masked, a.db_role
FROM audit.audit_log a
LEFT JOIN iam.app_user u ON u.id = a.user_id;
COMMENT ON VIEW audit.v_audit_log_masked IS 'Audit trail for the sponsor / auditors with the actor''s name masked.';

GRANT SELECT ON audit.v_audit_log_masked TO app_audit;
GRANT SELECT ON audit.v_audit_log_masked TO app_admin;
