-- =============================================================================
-- SmartSpare — cluster-level bootstrap (run ONCE per server, by the server admin)
-- =============================================================================
-- Roles are cluster-wide objects in PostgreSQL, so they are created here and not
-- in the versioned database migrations. Everything inside the database (schemas,
-- tables, functions, grants) is created by the EF Core migrations of each module
-- and their SQL parts in db/sql/ (ADR 0004, ADR 0011).
--
-- Azure Database for PostgreSQL Flexible Server: run as the server admin
-- (member of azure_pg_admin, has CREATEROLE). No superuser is required.
--
-- Usage (passwords are NEVER committed to Git — pass them as psql variables):
--   psql "host=... dbname=postgres user=<admin>" \
--        -v migrator_pwd="$MIGRATOR_PWD" -v api_pwd="$API_PWD" \
--        -v jobs_pwd="$JOBS_PWD" -v report_pwd="$REPORT_PWD" \
--        -v identity_pwd="$IDENTITY_PWD" -v inventory_pwd="$INVENTORY_PWD" \
--        -v fleet_pwd="$FLEET_PWD" -v procurement_pwd="$PROCUREMENT_PWD" \
--        -v forecasting_pwd="$FORECASTING_PWD" \
--        -f db/bootstrap/000_bootstrap_cluster.sql
--
-- Every statement is idempotent, so the script can run again to add login roles
-- introduced later; existing roles keep their passwords.
-- =============================================================================
\set ON_ERROR_STOP on

-- Passwords are stored by PostgreSQL as SCRAM-SHA-256 verifiers (never MD5).
SET password_encryption = 'scram-sha-256';

-- ---------------------------------------------------------------------------
-- 1. Owner role (NOLOGIN). Owns every database object; nobody logs in as it.
-- ---------------------------------------------------------------------------
SELECT 'CREATE ROLE smartspare_owner NOLOGIN'
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'smartspare_owner') \gexec

-- ---------------------------------------------------------------------------
-- 2. Group (privilege) roles — NOLOGIN, privileges are granted in V013.
--    app_read   read-only access, personal data only in masked form
--    app_write  application read/write (inherits app_read)
--    app_ml     ML worker: reads data marts, writes forecasts / model versions
--    app_audit  auditor / project sponsor: reads masked audit trail
--    app_admin  application administrator (user management, reference data);
--               NOT a superuser and NOT the owner of the objects
-- ---------------------------------------------------------------------------
SELECT format('CREATE ROLE %I NOLOGIN', r)
FROM unnest(ARRAY['app_read','app_write','app_ml','app_audit','app_admin']) AS r
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = r) \gexec

GRANT app_read TO app_write;
GRANT app_read TO app_ml;
GRANT app_write TO app_admin;

-- ---------------------------------------------------------------------------
-- 3. Login roles (one per workload, least privilege, none is superuser)
-- ---------------------------------------------------------------------------
SELECT format('CREATE ROLE smartspare_migrator LOGIN PASSWORD %L', :'migrator_pwd')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'smartspare_migrator') \gexec
SELECT format('CREATE ROLE smartspare_api LOGIN PASSWORD %L CONNECTION LIMIT 60', :'api_pwd')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'smartspare_api') \gexec
SELECT format('CREATE ROLE smartspare_jobs LOGIN PASSWORD %L CONNECTION LIMIT 10', :'jobs_pwd')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'smartspare_jobs') \gexec
SELECT format('CREATE ROLE smartspare_reporting LOGIN PASSWORD %L CONNECTION LIMIT 5', :'report_pwd')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'smartspare_reporting') \gexec

-- One login per service of the services topology (ADR 0015 §5, STAGE_4_PLAN section 3).
-- The Reporting service uses smartspare_reporting. Each service pools at most 4
-- connections per replica; the limit leaves room for a second replica and a job.
-- Per-module roles replace the capability roles in stage 10 (#16).
SELECT format('CREATE ROLE %I LOGIN PASSWORD %L CONNECTION LIMIT 10', l.rolname, l.pwd)
FROM (VALUES ('smartspare_identity',    :'identity_pwd'),
             ('smartspare_inventory',   :'inventory_pwd'),
             ('smartspare_fleet',       :'fleet_pwd'),
             ('smartspare_procurement', :'procurement_pwd'),
             ('smartspare_forecasting', :'forecasting_pwd')) AS l(rolname, pwd)
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = l.rolname) \gexec

-- The migrator acts as the owner (SET ROLE) when it runs migrations.
GRANT smartspare_owner TO smartspare_migrator;
GRANT app_write  TO smartspare_api;          -- ASP.NET Core API (EF Core / Npgsql)
GRANT app_write, app_ml TO smartspare_jobs;  -- Quartz.NET jobs (retraining, expiry, marts)
GRANT app_read   TO smartspare_reporting;    -- read-only reporting / BI; the Reporting service
GRANT app_audit  TO smartspare_reporting;    -- the Reporting service also reads the masked audit trail
GRANT app_write  TO smartspare_identity, smartspare_inventory, smartspare_fleet,
                    smartspare_procurement, smartspare_forecasting;
GRANT app_ml     TO smartspare_forecasting;  -- forecasts, model versions and the training job

-- Safety net: workload roles can never be superuser / create roles / DBs.
ALTER ROLE smartspare_api       NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
ALTER ROLE smartspare_jobs      NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
ALTER ROLE smartspare_reporting NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
ALTER ROLE smartspare_migrator  NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
ALTER ROLE smartspare_identity    NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
ALTER ROLE smartspare_inventory   NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
ALTER ROLE smartspare_fleet       NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
ALTER ROLE smartspare_procurement NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
ALTER ROLE smartspare_forecasting NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;

-- Statement timeouts protect the OLTP workload from runaway queries.
ALTER ROLE smartspare_api       SET statement_timeout = '15s';
ALTER ROLE smartspare_reporting SET statement_timeout = '60s';
ALTER ROLE smartspare_jobs      SET statement_timeout = '30min';
ALTER ROLE smartspare_identity    SET statement_timeout = '15s';
ALTER ROLE smartspare_inventory   SET statement_timeout = '15s';
ALTER ROLE smartspare_fleet       SET statement_timeout = '15s';
ALTER ROLE smartspare_procurement SET statement_timeout = '15s';
ALTER ROLE smartspare_forecasting SET statement_timeout = '30min'; -- its image also runs ML training

-- ---------------------------------------------------------------------------
-- 4. Database
-- ---------------------------------------------------------------------------
SELECT 'CREATE DATABASE smartspare OWNER smartspare_owner ENCODING ''UTF8'' TEMPLATE template0'
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = 'smartspare') \gexec

REVOKE ALL ON DATABASE smartspare FROM PUBLIC;
GRANT CONNECT, TEMPORARY ON DATABASE smartspare
      TO smartspare_migrator, smartspare_api, smartspare_jobs, smartspare_reporting,
         smartspare_identity, smartspare_inventory, smartspare_fleet,
         smartspare_procurement, smartspare_forecasting;
GRANT CREATE ON DATABASE smartspare TO smartspare_owner;
