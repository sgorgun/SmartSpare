-- =============================================================================
-- Reporting 0003 — privileges on mart objects. V015 statements restricted to mart.
-- =============================================================================

REVOKE EXECUTE ON ALL FUNCTIONS IN SCHEMA mart FROM PUBLIC;
REVOKE EXECUTE ON ALL PROCEDURES IN SCHEMA mart FROM PUBLIC;

-- ---- app_read: read the data marts -------------------------------------------
GRANT USAGE ON SCHEMA mart TO app_read;
GRANT SELECT ON ALL TABLES IN SCHEMA mart TO app_read;
-- New tables in this schema become readable automatically.
ALTER DEFAULT PRIVILEGES FOR ROLE smartspare_owner IN SCHEMA mart
    GRANT SELECT ON TABLES TO app_read;

-- ---- app_ml: Quartz.NET jobs (training reads the marts and refreshes them) ----
GRANT USAGE ON SCHEMA mart TO app_ml;
GRANT EXECUTE ON PROCEDURE mart.refresh_all() TO app_ml;

-- ---- app_admin ---------------------------------------------------------------------
GRANT EXECUTE ON PROCEDURE mart.refresh_all() TO app_admin;
