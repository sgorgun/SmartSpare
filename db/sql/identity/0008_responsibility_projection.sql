-- =============================================================================
-- Identity 0008 — projection of the rights-bearing responsible employees (ADR 0026 decision 3, STAGE_7_PLAN 1.3).
-- Tables created by EF (migration ResponsibilityProjection). Only the ResponsibilitiesChanged consumer writes them,
-- as app_write (architecture test); everyone may read them.
-- =============================================================================

GRANT SELECT ON iam.employee_responsibility, iam.responsibility_source TO app_read;
GRANT INSERT, DELETE ON iam.employee_responsibility TO app_write;
GRANT INSERT, UPDATE ON iam.responsibility_source TO app_write;
