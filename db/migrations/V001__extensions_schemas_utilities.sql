-- =============================================================================
-- V001 — Extensions, schemas (one per module / data layer) and shared utilities
-- =============================================================================

-- Trusted extensions (PostgreSQL 13+): creatable by the database owner.
-- On Azure Flexible Server they must be allow-listed in `azure.extensions`.
CREATE EXTENSION IF NOT EXISTS citext;      -- case-insensitive e-mail / codes
CREATE EXTENSION IF NOT EXISTS pg_trgm;     -- trigram GIN index for part search
CREATE EXTENSION IF NOT EXISTS btree_gist;  -- EXCLUDE constraints on date ranges
CREATE EXTENSION IF NOT EXISTS pgcrypto;    -- HMAC pseudonymisation, digests

-- Nobody may create objects in "public" (PostgreSQL 15+ default, made explicit).
REVOKE CREATE ON SCHEMA public FROM PUBLIC;

-- ---------------------------------------------------------------------------
-- Schemas = modules of the modular monolith + data layers
-- ---------------------------------------------------------------------------
-- Data layer "raw"     : CSV rows exactly as uploaded (all text, lineage)
CREATE SCHEMA raw;
-- Data layer "staging" : typed + validated rows, validation errors
CREATE SCHEMA staging;
-- Data layer "core"    : normalised (3NF) OLTP model, split by module:
CREATE SCHEMA ref;          -- fixed reference data (lookups)
CREATE SCHEMA iam;          -- users, roles (Identity & Access module)
CREATE SCHEMA catalog;      -- spare parts, suppliers, prices
CREATE SCHEMA inventory;    -- warehouses, stock ledger, reservations, transfers
CREATE SCHEMA fleet;        -- machines, engine hours, maintenance plans, service orders
CREATE SCHEMA procurement;  -- purchase orders
CREATE SCHEMA forecast;     -- ML models, forecasts, recommendations
CREATE SCHEMA audit;        -- immutable audit trail
-- Data layer "mart"    : denormalised read models for ML training and KPIs
CREATE SCHEMA mart;
-- Shared technical helpers
CREATE SCHEMA util;

COMMENT ON SCHEMA raw         IS 'Layer 1 (raw): uploaded CSV rows stored as text, 1:1 with the source file, for lineage and re-processing.';
COMMENT ON SCHEMA staging     IS 'Layer 2 (staging): typed and validated rows produced from raw; invalid rows are kept with error messages.';
COMMENT ON SCHEMA ref         IS 'Core layer: fixed reference (lookup) data loaded by db/seed scripts.';
COMMENT ON SCHEMA iam         IS 'Core layer / IAM module: application users, roles, brand assignments.';
COMMENT ON SCHEMA catalog     IS 'Core layer / Inventory module: spare-parts catalogue, suppliers, supplier conditions and prices.';
COMMENT ON SCHEMA inventory   IS 'Core layer / Inventory module: warehouses, stock ledger, balances, reservations, transfers.';
COMMENT ON SCHEMA fleet       IS 'Core layer / Fleet & Maintenance module: machines, engine hours, maintenance plans, service orders.';
COMMENT ON SCHEMA procurement IS 'Core layer / Procurement module: purchase orders and their lines.';
COMMENT ON SCHEMA forecast    IS 'Core layer / Forecasting module: ML model versions, forecast runs, benchmarks, recommendations.';
COMMENT ON SCHEMA audit       IS 'Core layer: append-only audit trail (US18), 180-day retention.';
COMMENT ON SCHEMA mart        IS 'Layer 3 (data mart): materialised read models for ML training, KPIs and reports.';
COMMENT ON SCHEMA util        IS 'Shared technical helpers: partitions, document numbers, masking, triggers.';

-- ---------------------------------------------------------------------------
-- Generic trigger: maintain updated_at
-- ---------------------------------------------------------------------------
CREATE FUNCTION util.trg_set_updated_at() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    NEW.updated_at := now();
    RETURN NEW;
END $$;
COMMENT ON FUNCTION util.trg_set_updated_at() IS 'BEFORE UPDATE trigger: sets updated_at = now().';

-- ---------------------------------------------------------------------------
-- Generic trigger: forbid UPDATE/DELETE on append-only tables (ledger, audit)
-- ---------------------------------------------------------------------------
CREATE FUNCTION util.trg_forbid_modification() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'Table %.% is append-only: % is not allowed', TG_TABLE_SCHEMA, TG_TABLE_NAME, TG_OP
        USING ERRCODE = 'insufficient_privilege',
              HINT = 'Post a compensating record instead of changing history.';
END $$;
COMMENT ON FUNCTION util.trg_forbid_modification() IS 'BEFORE UPDATE/DELETE trigger for append-only tables (stock ledger, audit log).';

-- ---------------------------------------------------------------------------
-- Session context: the API sets app.user_id per transaction
-- (SELECT set_config('app.user_id', '<uuid>', true)) — used by audit triggers.
-- ---------------------------------------------------------------------------
CREATE FUNCTION util.current_app_user() RETURNS uuid
LANGUAGE sql STABLE AS $$
    SELECT nullif(current_setting('app.user_id', true), '')::uuid
$$;
COMMENT ON FUNCTION util.current_app_user() IS 'Application user of the current transaction (GUC app.user_id set by the EF Core interceptor); NULL for system jobs.';

-- ---------------------------------------------------------------------------
-- Gap-less, per-year document numbers: SO-2026-000412, TR-2026-000842 ...
-- A row lock on the counter serialises concurrent requests for one prefix/year.
-- ---------------------------------------------------------------------------
CREATE TABLE util.document_counter (
    prefix      varchar(4)  NOT NULL CHECK (prefix ~ '^[A-Z]{2,4}$'),
    year        smallint    NOT NULL CHECK (year BETWEEN 2000 AND 2100),
    last_value  integer     NOT NULL DEFAULT 0 CHECK (last_value >= 0),
    CONSTRAINT pk_document_counter PRIMARY KEY (prefix, year)
);
COMMENT ON TABLE util.document_counter IS 'Last issued business document number per prefix and year (gap-less numbering).';

CREATE FUNCTION util.next_document_number(p_prefix text, p_at timestamptz DEFAULT now())
RETURNS text LANGUAGE plpgsql AS $$
DECLARE
    v_year  smallint := extract(year FROM p_at AT TIME ZONE 'Europe/Vilnius');
    v_value integer;
BEGIN
    INSERT INTO util.document_counter AS c (prefix, year, last_value)
    VALUES (p_prefix, v_year, 1)
    ON CONFLICT (prefix, year) DO UPDATE SET last_value = c.last_value + 1
    RETURNING c.last_value INTO v_value;
    RETURN format('%s-%s-%s', p_prefix, v_year, lpad(v_value::text, 6, '0'));
END $$;
COMMENT ON FUNCTION util.next_document_number(text, timestamptz) IS 'Returns the next gap-less document number, e.g. SO-2026-000412. Runs inside the caller''s transaction, so a rollback releases nothing and no gap appears.';

-- ---------------------------------------------------------------------------
-- Monthly range partitions: creates missing partitions [from, from+months)
-- Called by the migrations and monthly by a Quartz.NET maintenance job.
-- ---------------------------------------------------------------------------
CREATE FUNCTION util.ensure_monthly_partitions(p_parent regclass, p_from date, p_months integer)
RETURNS integer LANGUAGE plpgsql AS $$
DECLARE
    v_schema  text;
    v_table   text;
    v_start   date := date_trunc('month', p_from)::date;
    v_created integer := 0;
    v_name    text;
BEGIN
    SELECT n.nspname, c.relname INTO v_schema, v_table
    FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE c.oid = p_parent;

    FOR i IN 0 .. p_months - 1 LOOP
        v_name := format('%s_%s', v_table, to_char(v_start + make_interval(months => i), 'YYYYMM'));
        IF to_regclass(format('%I.%I', v_schema, v_name)) IS NULL THEN
            EXECUTE format('CREATE TABLE %I.%I PARTITION OF %s FOR VALUES FROM (%L) TO (%L)',
                           v_schema, v_name, p_parent,
                           v_start + make_interval(months => i),
                           v_start + make_interval(months => i + 1));
            v_created := v_created + 1;
        END IF;
    END LOOP;
    RETURN v_created;
END $$;
COMMENT ON FUNCTION util.ensure_monthly_partitions(regclass, date, integer) IS 'Creates missing monthly RANGE partitions of a partitioned table; returns the number created.';

-- ---------------------------------------------------------------------------
-- Personal-data masking (used by masked views for app_read / app_audit)
-- ---------------------------------------------------------------------------
CREATE FUNCTION util.mask_email(p_email text) RETURNS text
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    -- tomas.radzevicius@dealer.lt -> t***@d***.lt
    SELECT CASE
        WHEN p_email IS NULL THEN NULL
        WHEN position('@' IN p_email) = 0 THEN '***'
        ELSE left(split_part(p_email, '@', 1), 1) || '***@'
             || left(split_part(p_email, '@', 2), 1) || '***'
             || coalesce(substring(split_part(p_email, '@', 2) FROM '\.[^.]+$'), '')
    END
$$;

CREATE FUNCTION util.mask_name(p_full_name text) RETURNS text
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    -- Tomas Radzevičius -> T. R***
    SELECT CASE WHEN p_full_name IS NULL THEN NULL ELSE
        (SELECT string_agg(CASE WHEN ord = 1 THEN left(w, 1) || '.' ELSE left(w, 1) || '***' END, ' ' ORDER BY ord)
         FROM regexp_split_to_table(btrim(p_full_name), '\s+') WITH ORDINALITY AS t(w, ord))
    END
$$;

CREATE FUNCTION util.mask_phone(p_phone text) RETURNS text
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    -- +370 612 34567 -> +370 *** **567  (keeps country code and last 3 digits)
    SELECT CASE WHEN p_phone IS NULL THEN NULL ELSE
        coalesce(substring(p_phone FROM '^\+\d{3}'), '') || ' *** **' || right(regexp_replace(p_phone, '\D', '', 'g'), 3)
    END
$$;
COMMENT ON FUNCTION util.mask_email(text) IS 'Masks an e-mail address for display to roles without access to personal data.';
COMMENT ON FUNCTION util.mask_name(text)  IS 'Masks a full name to initials.';
COMMENT ON FUNCTION util.mask_phone(text) IS 'Masks a phone number, keeping the country code and last 3 digits.';

-- Pseudonymisation of external identifiers (e.g. ERP customer IDs):
-- keyed HMAC-SHA256; the key comes from the session (Azure Key Vault -> app).
CREATE FUNCTION util.pseudonymise(p_value text) RETURNS bytea
LANGUAGE plpgsql STABLE AS $$
DECLARE
    v_key text := current_setting('app.pseudonym_key', true);
BEGIN
    IF v_key IS NULL OR length(v_key) < 16 THEN
        RAISE EXCEPTION 'app.pseudonym_key is not set (min. 16 characters)' USING ERRCODE = 'invalid_parameter_value';
    END IF;
    RETURN public.hmac(p_value, v_key, 'sha256');
END $$;
COMMENT ON FUNCTION util.pseudonymise(text) IS 'Keyed HMAC-SHA256 pseudonym of an external identifier (GDPR Art. 4(5)). Key supplied per session via app.pseudonym_key.';
