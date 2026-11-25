# SmartSpare — Data Dictionary

_Generated from the PostgreSQL catalogue by `tools/generate_db_docs.py` on 2026-11-24. Do not edit by hand — change the EF Core migrations (or their SQL parts in `db/sql/`) and regenerate._

The database is built by the EF Core migrations of each module (ADR 0004, ADR 0011); references between modules are plain ids, not foreign keys. Row counts are those of the synthetic demo dataset (`db/testdata/T001`, 2,000 SKUs).

**Legend:** PK = primary key, FK = foreign key (→ referenced table), UQ = unique, NN = NOT NULL, GEN = generated column, ID = identity.

## 1. Overview

| Schema | Layer / module | Tables | Views | Mat. views |
|---|---|---:|---:|---:|
| `ref` | Platform — reference data | 9 | 0 | 0 |
| `iam` | Identity | 8 | 1 | 0 |
| `catalog` | Inventory — catalogue | 2 | 0 | 0 |
| `inventory` | Inventory — stock & transfers | 12 | 2 | 0 |
| `fleet` | Fleet | 11 | 2 | 0 |
| `procurement` | Procurement — suppliers & purchase orders | 8 | 1 | 0 |
| `forecast` | Forecasting (ML) | 8 | 1 | 0 |
| `audit` | Platform — audit | 1 | 1 | 0 |
| `raw` | Inventory — import layer 1 (raw) | 3 | 0 | 0 |
| `staging` | Inventory — import layer 2 (staging) | 3 | 0 | 0 |
| `mart` | Reporting — data mart | 2 | 6 | 2 |
| `util` | Platform — technical utilities | 2 | 1 | 0 |

## 2. Schema `ref` — Platform — reference data

### `ref.agro_calendar_week` (table)

ISO week -> season phase mapping for Lithuania; ML feature (EP03, finding F8).

Rows (demo): **53**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `iso_week` | `smallint` | PK, NN |  | Surrogate primary key. |
| `season_phase_code` | `character varying(20)` | FK → `ref.season_phase`, NN |  | Reference to ref.season_phase. |
| `seasonal_weight` | `numeric(4,2)` | NN |  | Expert seasonal intensity (1.00 = average week); peaks Feb-Mar and Jul-Sep. |

**Data quality expectations / integrity rules**

- Foreign key `fk_agro_calendar_week_phase`: `FOREIGN KEY (season_phase_code) REFERENCES ref.season_phase(code)`
- Primary key `pk_agro_calendar_week`: `PRIMARY KEY (iso_week)`
- Check `ck_agro_calendar_week_range`: `CHECK (((iso_week >= 1) AND (iso_week <= 53)))`
- Check `ck_agro_calendar_week_weight`: `CHECK (((seasonal_weight > (0)::numeric) AND (seasonal_weight <= (5)::numeric)))`

**Indexes**

- `ix_agro_calendar_week_phase` — `btree (season_phase_code)`
- `pk_agro_calendar_week` — `btree (iso_week)`

### `ref.brand` (table)

Machinery / OEM brands (Claas, John Deere, Fendt, Massey Ferguson, Lemken, Väderstad ...).

Rows (demo): **8**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `id` | `smallint` | PK, NN, ID | `identity` | Surrogate primary key. |
| `code` | `character varying(10)` | UQ, NN |  | Business code (natural key). |
| `name` | `character varying(80)` | UQ, NN |  | Display name. |
| `country_code` | `character(2)` | NN |  | ISO 3166-1 alpha-2 country of the manufacturer. |

**Data quality expectations / integrity rules**

- Primary key `pk_brand`: `PRIMARY KEY (id)`
- Unique `uq_brand_code`: `UNIQUE (code)`
- Unique `uq_brand_name`: `UNIQUE (name)`
- Check `ck_brand_country`: `CHECK ((country_code ~ '^[A-Z]{2}$'::text))`

**Indexes**

- `pk_brand` — `btree (id)`
- `uq_brand_code` — `btree (code)`
- `uq_brand_name` — `btree (name)`

### `ref.demand_class` (table)

Syntetos-Boylan demand classification by ADI (cut-off 1.32) and CV² (cut-off 0.49); ranges are [lower, upper).

Rows (demo): **4**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `code` | `character varying(15)` | PK, NN |  | Surrogate primary key. |
| `name` | `character varying(40)` | NN |  | Display name. |
| `adi_lower` | `numeric(6,3)` | NN |  |  |
| `adi_upper` | `numeric(6,3)` | NN |  |  |
| `cv2_lower` | `numeric(6,3)` | NN |  |  |
| `cv2_upper` | `numeric(6,3)` | NN |  |  |
| `description` | `text` |  |  | Description. |

**Data quality expectations / integrity rules**

- Primary key `pk_demand_class`: `PRIMARY KEY (code)`
- Check `ck_demand_class_adi`: `CHECK (((adi_lower >= (0)::numeric) AND (adi_upper > adi_lower)))`
- Check `ck_demand_class_cv2`: `CHECK (((cv2_lower >= (0)::numeric) AND (cv2_upper > cv2_lower)))`

**Indexes**

- `pk_demand_class` — `btree (code)`

### `ref.machine_type` (table)

Machine types: tractor, combine harvester, seeder, sprayer, forage harvester ...

Rows (demo): **6**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `id` | `smallint` | PK, NN, ID | `identity` | Surrogate primary key. |
| `code` | `character varying(20)` | UQ, NN |  | Business code (natural key). |
| `name` | `character varying(80)` | NN |  | Display name. |

**Data quality expectations / integrity rules**

- Primary key `pk_machine_type`: `PRIMARY KEY (id)`
- Unique `uq_machine_type_code`: `UNIQUE (code)`

**Indexes**

- `pk_machine_type` — `btree (id)`
- `uq_machine_type_code` — `btree (code)`

### `ref.movement_type` (table)

Types of stock ledger movements and their effect on the on-hand balance.

Rows (demo): **10**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `code` | `character varying(20)` | PK, NN |  | Surrogate primary key. |
| `name` | `character varying(80)` | NN |  | Display name. |
| `balance_sign` | `smallint` | NN |  | +1 increases on-hand stock, -1 decreases it, 0 = no physical effect (imported historical demand). |
| `is_demand` | `boolean` | NN |  | TRUE if the movement represents customer/service demand used for forecasting. |
| `description` | `text` |  |  | Description. |

**Data quality expectations / integrity rules**

- Primary key `pk_movement_type`: `PRIMARY KEY (code)`
- Check `ck_movement_type_code`: `CHECK (((code)::text ~ '^[A-Z_]{3,20}$'::text))`
- Check `ck_movement_type_demand_is_issue`: `CHECK (((NOT is_demand) OR (balance_sign = ANY (ARRAY['-1'::integer, 0]))))`
- Check `ck_movement_type_sign`: `CHECK ((balance_sign = ANY (ARRAY['-1'::integer, 0, 1])))`

**Indexes**

- `pk_movement_type` — `btree (code)`

### `ref.operating_condition` (table)

Operating conditions that shorten OEM service intervals (finding F5: e.g. non-prescribed oil halves the interval).

Rows (demo): **4**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `id` | `smallint` | PK, NN, ID | `identity` | Surrogate primary key. |
| `code` | `character varying(20)` | UQ, NN |  | Business code (natural key). |
| `name` | `character varying(120)` | NN |  | Display name. |
| `interval_factor` | `numeric(4,2)` | NN |  | Multiplier applied to the OEM interval: 1.00 = normal, 0.50 = halved. |

**Data quality expectations / integrity rules**

- Primary key `pk_operating_condition`: `PRIMARY KEY (id)`
- Unique `uq_operating_condition_code`: `UNIQUE (code)`
- Check `ck_operating_condition_factor`: `CHECK (((interval_factor > (0)::numeric) AND (interval_factor <= (1)::numeric)))`

**Indexes**

- `pk_operating_condition` — `btree (id)`
- `uq_operating_condition_code` — `btree (code)`

### `ref.part_category` (table)

Hierarchical (self-referencing) spare part category tree, e.g. FILTERS > OIL_FILTERS.

Rows (demo): **31**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `id` | `integer` | PK, NN, ID | `identity` | Surrogate primary key. |
| `parent_id` | `integer` | FK → `ref.part_category` |  | Parent category; NULL for a root category (recursive 1:N relationship). |
| `code` | `character varying(30)` | UQ, NN |  | Business code (natural key). |
| `name` | `character varying(120)` | NN |  | Display name. |

**Data quality expectations / integrity rules**

- Foreign key `fk_part_category_parent`: `FOREIGN KEY (parent_id) REFERENCES ref.part_category(id) ON DELETE RESTRICT`
- Primary key `pk_part_category`: `PRIMARY KEY (id)`
- Unique `uq_part_category_code`: `UNIQUE (code)`
- Check `ck_part_category_not_self`: `CHECK ((parent_id IS DISTINCT FROM id))`

**Indexes**

- `ix_part_category_parent` — `btree (parent_id)`
- `pk_part_category` — `btree (id)`
- `uq_part_category_code` — `btree (code)`

### `ref.season_phase` (table)

Agrotechnical season phases (winter repair, sowing, crop care, harvest, autumn tillage).

Rows (demo): **6**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `code` | `character varying(20)` | PK, NN |  | Surrogate primary key. |
| `name` | `character varying(80)` | NN |  | Display name. |

**Data quality expectations / integrity rules**

- Primary key `pk_season_phase`: `PRIMARY KEY (code)`

**Indexes**

- `pk_season_phase` — `btree (code)`

### `ref.unit_of_measure` (table)

Units of measure of spare parts (PCS, L, KG, M, SET).

Rows (demo): **5**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `id` | `smallint` | PK, NN, ID | `identity` | Surrogate primary key. |
| `code` | `character varying(10)` | UQ, NN |  | Business code (natural key). |
| `name` | `character varying(50)` | NN |  | Display name. |
| `is_fractional` | `boolean` | NN | `false` | TRUE if quantities may be fractional (litres of oil); FALSE for pieces. |

**Data quality expectations / integrity rules**

- Primary key `pk_unit_of_measure`: `PRIMARY KEY (id)`
- Unique `uq_unit_of_measure_code`: `UNIQUE (code)`
- Check `ck_unit_of_measure_code`: `CHECK (((code)::text ~ '^[A-Z0-9]{1,10}$'::text))`

**Indexes**

- `pk_unit_of_measure` — `btree (id)`
- `uq_unit_of_measure_code` — `btree (code)`

## 3. Schema `iam` — Identity

### `iam.__ef_migrations_history` (table)

Rows (demo): **7**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `migration_id` | `character varying(150)` | PK, NN |  |  |
| `product_version` | `character varying(32)` | NN |  |  |

**Data quality expectations / integrity rules**

- Primary key `pk___ef_migrations_history`: `PRIMARY KEY (migration_id)`

**Indexes**

- `pk___ef_migrations_history` — `btree (migration_id)`

### `iam.app_user` (table)

Dealership employees using SmartSpare. Contains personal data (full_name, email, phone): masked for app_read / app_audit.

Rows (demo): **12**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `id` | `uuid` | PK, NN | `gen_random_uuid()` | Surrogate primary key. |
| `user_name` | `citext` | UQ, NN |  |  |
| `email` | `citext` | UQ, NN |  | PERSONAL DATA (GDPR). Corporate e-mail; masked by util.mask_email for read-only roles. |
| `full_name` | `character varying(150)` | NN |  | PERSONAL DATA (GDPR). Masked by util.mask_name for read-only roles. |
| `phone` | `character varying(30)` |  |  | PERSONAL DATA (GDPR). Masked by util.mask_phone for read-only roles. |
| `password_hash` | `text` |  |  | ASP.NET Core Identity V3 hash (PBKDF2-HMAC-SHA512, 220,000 iterations; older hashes are rehashed at sign-in). Plain text is impossible by CHECK. |
| `security_stamp` | `uuid` | NN | `gen_random_uuid()` | Changes on password/role change; invalidates issued sign-in cookies (security stamp validation). |
| `is_active` | `boolean` | NN | `true` | FALSE = deactivated, kept for history. |
| `failed_login_count` | `smallint` | NN | `0` |  |
| `lockout_end` | `timestamp with time zone` |  |  |  |
| `home_warehouse_id` | `smallint` |  |  | Reference to inventory.warehouse. |
| `last_login_at` | `timestamp with time zone` |  |  | Time of the last login event. |
| `anonymised_at` | `timestamp with time zone` |  |  | Set by iam.anonymise_user() (GDPR Art. 17); the row is kept so audit references stay valid. |
| `created_at` | `timestamp with time zone` | NN | `now()` | Time the record was created. |
| `updated_at` | `timestamp with time zone` | NN | `now()` | Time of the last change (maintained by trigger). |

**Data quality expectations / integrity rules**

- Primary key `pk_app_user`: `PRIMARY KEY (id)`
- Unique `uq_app_user_email`: `UNIQUE (email)`
- Unique `uq_app_user_user_name`: `UNIQUE (user_name)`
- Check `ck_app_user_active_has_password`: `CHECK (((password_hash IS NOT NULL) OR (NOT is_active)))`
- Check `ck_app_user_anonymised_inactive`: `CHECK (((anonymised_at IS NULL) OR (NOT is_active)))`
- Check `ck_app_user_email`: `CHECK ((email ~ '^[^@\s]+@[^@\s]+\.[^@\s]+$'::citext))`
- Check `ck_app_user_failed_logins`: `CHECK (((failed_login_count >= 0) AND (failed_login_count <= 100)))`
- Check `ck_app_user_password_hash_format`: `CHECK (((password_hash IS NULL) OR (password_hash ~ '^AQAAAA[A-Za-z0-9+/]{76,}={0,2}$'::text)))`
- Check `ck_app_user_phone`: `CHECK (((phone IS NULL) OR ((phone)::text ~ '^\+?[0-9 ()-]{7,20}$'::text)))`
- Check `ck_app_user_user_name`: `CHECK (((user_name ~ '^[a-z0-9._-]{3,50}$'::citext) AND ((user_name)::text = lower((user_name)::text))))`

**Indexes**

- `ix_app_user_home_warehouse` — `btree (home_warehouse_id)`
- `pk_app_user` — `btree (id)`
- `uq_app_user_email` — `btree (email)`
- `uq_app_user_user_name` — `btree (user_name)`

**Triggers:** `trg_app_user_updated_at: util.trg_set_updated_at`, `trg_audit: audit.trg_row_audit`

### `iam.data_protection_keys` (table)

ASP.NET Core Data Protection key ring of the single host (cookies, antiforgery tokens). Secret material: no app_read grant, no audit.

Rows (demo): **0**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `id` | `integer` | PK, NN, ID | `identity` |  |
| `friendly_name` | `text` |  |  | Key id as written by the key manager (key-&lt;guid&gt;). |
| `xml` | `text` |  |  | Key descriptor XML; encrypted at rest when the host configures key protection. |

**Data quality expectations / integrity rules**

- Primary key `pk_data_protection_keys`: `PRIMARY KEY (id)`

**Indexes**

- `pk_data_protection_keys` — `btree (id)`

### `iam.inbox_message` (table)

Integration events already handled by a consumer of this module (idempotent receiver, ADR 0003).

Rows (demo): **0**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `message_id` | `uuid` | PK, NN |  | Id of the handled event. |
| `consumer` | `character varying(300)` | PK, NN |  | Handler that consumed the event. |
| `processed_at` | `timestamp with time zone` | NN | `now()` | Time the event was handled. |

**Data quality expectations / integrity rules**

- Primary key `pk_inbox_message`: `PRIMARY KEY (message_id, consumer)`

**Indexes**

- `pk_inbox_message` — `btree (message_id, consumer)`

### `iam.outbox_message` (table)

Integration events of this module waiting for delivery (transactional outbox, ADR 0003).

Rows (demo): **0**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `id` | `uuid` | PK, NN |  | Event id; receivers deduplicate by it. |
| `occurred_at` | `timestamp with time zone` | NN |  | Time the event occurred. |
| `type` | `character varying(300)` | NN |  | CLR type of the event (Namespace.Type, Assembly). |
| `payload` | `jsonb` | NN |  | Event serialized as a JSON object. |
| `processed_at` | `timestamp with time zone` |  |  | Time the event was delivered to every consumer; NULL = pending. |
| `attempts` | `integer` | NN | `0` | Failed delivery attempts so far. |
| `error` | `character varying(2000)` |  |  | Last delivery error, if any. |

**Data quality expectations / integrity rules**

- Primary key `pk_outbox_message`: `PRIMARY KEY (id)`
- Check `ck_outbox_message_attempts`: `CHECK ((attempts >= 0))`
- Check `ck_outbox_message_payload`: `CHECK ((jsonb_typeof(payload) = 'object'::text))`

**Indexes**

- `ix_outbox_message_pending` — `btree (occurred_at) WHERE (processed_at IS NULL)`
- `pk_outbox_message` — `btree (id)`

### `iam.role` (table)

Application (RBAC) roles from the BA: BRAND_MANAGER, SERVICE_ENGINEER, WAREHOUSE_KEEPER, INVENTORY_PLANNER, SPONSOR, ADMINISTRATOR.

Rows (demo): **6**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `id` | `smallint` | PK, NN, ID | `identity` | Surrogate primary key. |
| `code` | `character varying(30)` | UQ, NN |  | Business code (natural key). |
| `name` | `character varying(80)` | NN |  | Display name. |
| `description` | `text` |  |  | Description. |

**Data quality expectations / integrity rules**

- Primary key `pk_role`: `PRIMARY KEY (id)`
- Unique `uq_role_code`: `UNIQUE (code)`
- Check `ck_role_code`: `CHECK (((code)::text ~ '^[A-Z_]{3,30}$'::text))`

**Indexes**

- `pk_role` — `btree (id)`
- `uq_role_code` — `btree (code)`

### `iam.user_brand` (table)

M:N brand manager <-> brands they are responsible for (e.g. persona Tomas: Claas and Lemken).

Rows (demo): **8**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `user_id` | `uuid` | PK, FK → `iam.app_user`, NN |  | Reference to iam.app_user. |
| `brand_id` | `smallint` | PK, FK → `ref.brand`, NN |  | Reference to ref.brand. |

**Data quality expectations / integrity rules**

- Foreign key `fk_user_brand_brand`: `FOREIGN KEY (brand_id) REFERENCES ref.brand(id) ON DELETE CASCADE`
- Foreign key `fk_user_brand_user`: `FOREIGN KEY (user_id) REFERENCES iam.app_user(id) ON DELETE CASCADE`
- Primary key `pk_user_brand`: `PRIMARY KEY (user_id, brand_id)`

**Indexes**

- `ix_user_brand_brand` — `btree (brand_id)`
- `pk_user_brand` — `btree (user_id, brand_id)`

**Triggers:** `trg_audit: audit.trg_row_audit`

### `iam.user_role` (table)

M:N user <-> role assignment.

Rows (demo): **12**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `user_id` | `uuid` | PK, FK → `iam.app_user`, NN |  | Reference to iam.app_user. |
| `role_id` | `smallint` | PK, FK → `iam.role`, NN |  | Reference to iam.role. |
| `granted_at` | `timestamp with time zone` | NN | `now()` | Time of the granted event. |
| `granted_by` | `uuid` | FK → `iam.app_user` |  | User who performed the step (iam.app_user). |

**Data quality expectations / integrity rules**

- Foreign key `fk_user_role_granted_by`: `FOREIGN KEY (granted_by) REFERENCES iam.app_user(id) ON DELETE SET NULL`
- Foreign key `fk_user_role_role`: `FOREIGN KEY (role_id) REFERENCES iam.role(id) ON DELETE RESTRICT`
- Foreign key `fk_user_role_user`: `FOREIGN KEY (user_id) REFERENCES iam.app_user(id) ON DELETE CASCADE`
- Primary key `pk_user_role`: `PRIMARY KEY (user_id, role_id)`

**Indexes**

- `ix_user_role_granted_by` — `btree (granted_by)`
- `ix_user_role_role` — `btree (role_id)`
- `pk_user_role` — `btree (user_id, role_id)`

**Triggers:** `trg_audit: audit.trg_row_audit`

### `iam.v_app_user_masked` (view)

User directory with masked personal data (name -> initials, e-mail and phone partially hidden).

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `id` | `uuid` |  |  |  |
| `user_name` | `citext` |  |  |  |
| `full_name_masked` | `text` |  |  |  |
| `email_masked` | `text` |  |  |  |
| `phone_masked` | `text` |  |  |  |
| `is_active` | `boolean` |  |  |  |
| `home_warehouse_id` | `smallint` |  |  |  |
| `roles` | `character varying[]` |  |  |  |
| `last_login_at` | `timestamp with time zone` |  |  |  |
| `created_at` | `timestamp with time zone` |  |  |  |
| `is_anonymised` | `boolean` |  |  |  |

## 4. Schema `catalog` — Inventory — catalogue

### `catalog.part` (table)

Spare part (SKU) catalogue; natural key = brand + OEM part number.

Rows (demo): **2,032**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `id` | `bigint` | PK, NN, ID | `identity` | Surrogate primary key. |
| `brand_id` | `smallint` | FK → `ref.brand`, NN |  | Reference to ref.brand. |
| `oem_part_number` | `character varying(40)` | NN |  | Manufacturer part number, upper-case, normalised during import. |
| `description` | `character varying(300)` | NN |  | Description. |
| `category_id` | `integer` | FK → `ref.part_category`, NN |  | Reference to ref.part_category. |
| `uom_id` | `smallint` | FK → `ref.unit_of_measure`, NN |  | Reference to ref.unit_of_measure. |
| `weight_kg` | `numeric(9,3)` |  |  |  |
| `unit_cost` | `numeric(12,2)` | NN |  | Standard (valuation) cost per unit, EUR. Money is numeric, never float. |
| `is_condition_based` | `boolean` | NN | `false` | TRUE for parts replaced "as required" (air/cab filters, finding F6): demand only from the forecast stream. |
| `is_active` | `boolean` | NN | `true` | FALSE = deactivated, kept for history. |
| `created_at` | `timestamp with time zone` | NN | `now()` | Time the record was created. |
| `updated_at` | `timestamp with time zone` | NN | `now()` | Time of the last change (maintained by trigger). |

**Data quality expectations / integrity rules**

- Foreign key `fk_part_brand`: `FOREIGN KEY (brand_id) REFERENCES ref.brand(id)`
- Foreign key `fk_part_category`: `FOREIGN KEY (category_id) REFERENCES ref.part_category(id)`
- Foreign key `fk_part_uom`: `FOREIGN KEY (uom_id) REFERENCES ref.unit_of_measure(id)`
- Primary key `pk_part`: `PRIMARY KEY (id)`
- Unique `uq_part_brand_oem`: `UNIQUE (brand_id, oem_part_number)`
- Check `ck_part_description`: `CHECK ((length(btrim((description)::text)) >= 3))`
- Check `ck_part_oem_number`: `CHECK (((oem_part_number)::text ~ '^[A-Z0-9][A-Z0-9.\-/]{2,39}$'::text))`
- Check `ck_part_unit_cost`: `CHECK ((unit_cost >= (0)::numeric))`
- Check `ck_part_weight`: `CHECK (((weight_kg IS NULL) OR (weight_kg >= (0)::numeric)))`

**Indexes**

- `ix_part_category` — `btree (category_id)`
- `ix_part_description_trgm` — `gin (description gin_trgm_ops)`
- `ix_part_oem_number_trgm` — `gin (oem_part_number gin_trgm_ops)`
- `ix_part_uom` — `btree (uom_id)`
- `pk_part` — `btree (id)`
- `uq_part_brand_oem` — `btree (brand_id, oem_part_number)`

**Triggers:** `trg_audit: audit.trg_row_audit`, `trg_part_updated_at: util.trg_set_updated_at`

### `catalog.part_analogue` (table)

New SKU -> historical analogue/predecessor SKU with a demand scaling factor (US15).

Rows (demo): **1**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `part_id` | `bigint` | PK, FK → `catalog.part`, NN |  | Reference to catalog.part. |
| `analogue_part_id` | `bigint` | PK, FK → `catalog.part`, NN |  | Reference to catalog.part. |
| `scaling_factor` | `numeric(5,3)` | NN | `1.000` | Multiplier applied to the analogue's demand history (US15). |
| `valid_from` | `date` | NN | `CURRENT_DATE` |  |
| `created_at` | `timestamp with time zone` | NN | `now()` | Time the record was created. |

**Data quality expectations / integrity rules**

- Foreign key `fk_part_analogue_analogue`: `FOREIGN KEY (analogue_part_id) REFERENCES catalog.part(id) ON DELETE RESTRICT`
- Foreign key `fk_part_analogue_part`: `FOREIGN KEY (part_id) REFERENCES catalog.part(id) ON DELETE CASCADE`
- Primary key `pk_part_analogue`: `PRIMARY KEY (part_id, analogue_part_id)`
- Check `ck_part_analogue_distinct`: `CHECK ((part_id <> analogue_part_id))`
- Check `ck_part_analogue_factor`: `CHECK (((scaling_factor > (0)::numeric) AND (scaling_factor <= (10)::numeric)))`

**Indexes**

- `ix_part_analogue_analogue` — `btree (analogue_part_id)`
- `pk_part_analogue` — `btree (part_id, analogue_part_id)`

## 5. Schema `inventory` — Inventory — stock & transfers

### `inventory.__ef_migrations_history` (table)

Rows (demo): **3**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `migration_id` | `character varying(150)` | PK, NN |  |  |
| `product_version` | `character varying(32)` | NN |  |  |

**Data quality expectations / integrity rules**

- Primary key `pk___ef_migrations_history`: `PRIMARY KEY (migration_id)`

**Indexes**

- `pk___ef_migrations_history` — `btree (migration_id)`

### `inventory.demand_exception` (table)

1:1 annotation of a demand movement as exceptional (US08). Kept separate because the ledger is immutable; excluded from ML training.

Rows (demo): **12**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `movement_id` | `bigint` | PK, FK → `inventory.stock_movement`, NN |  | Reference to inventory.stock_movement. |
| `movement_occurred_at` | `timestamp with time zone` | PK, FK → `inventory.stock_movement`, NN |  | Reference to inventory.stock_movement. |
| `reason` | `character varying(300)` | NN |  |  |
| `detection_method` | `character varying(20)` | NN | `'manual'::character varying` |  |
| `flagged_by` | `uuid` |  |  | User who performed the step (iam.app_user). |
| `flagged_at` | `timestamp with time zone` | NN | `now()` | Time of the flagged event. |

**Data quality expectations / integrity rules**

- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202401(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey1`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202402(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey10`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202411(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey11`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202412(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey12`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202501(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey13`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202502(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey14`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202503(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey15`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202504(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey16`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202505(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey17`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202506(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey18`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202507(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey19`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202508(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey2`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202403(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey20`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202509(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey21`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202510(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey22`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202511(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey23`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202512(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey24`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202601(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey25`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202602(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey26`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202603(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey27`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202604(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey28`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202605(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey29`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202606(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey3`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202404(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey30`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202607(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey31`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202608(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey32`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202609(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey33`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202610(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey34`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202611(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey35`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202612(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey36`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202701(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey37`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202702(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey38`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202703(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey39`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202704(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey4`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202405(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey40`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202705(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey41`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202706(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey42`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202707(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey43`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202708(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey44`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202709(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey45`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202710(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey46`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202711(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey47`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202712(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey48`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202801(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey49`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202802(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey5`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202406(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey50`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202803(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey51`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202804(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey52`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202805(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey53`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202806(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey54`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202807(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey55`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202808(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey56`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202809(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey57`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202810(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey58`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202811(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey59`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202812(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey6`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202407(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey60`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_default(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey7`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202408(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey8`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202409(id, occurred_at) ON DELETE CASCADE`
- Foreign key `demand_exception_movement_id_movement_occurred_at_fkey9`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement_202410(id, occurred_at) ON DELETE CASCADE`
- Foreign key `fk_demand_exception_movement`: `FOREIGN KEY (movement_id, movement_occurred_at) REFERENCES inventory.stock_movement(id, occurred_at) ON DELETE CASCADE`
- Primary key `pk_demand_exception`: `PRIMARY KEY (movement_id, movement_occurred_at)`
- Check `ck_demand_exception_method`: `CHECK (((detection_method)::text = ANY ((ARRAY['manual'::character varying, 'auto_3sigma'::character varying])::text[])))`
- Check `ck_demand_exception_reason`: `CHECK ((length(btrim((reason)::text)) >= 5))`

**Indexes**

- `ix_demand_exception_flagged_by` — `btree (flagged_by)`
- `pk_demand_exception` — `btree (movement_id, movement_occurred_at)`

**Triggers:** `trg_audit: audit.trg_row_audit`, `trg_demand_exception_validate: inventory.trg_demand_exception_validate`

### `inventory.inbox_message` (table)

Integration events already handled by a consumer of this module (idempotent receiver, ADR 0003).

Rows (demo): **0**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `message_id` | `uuid` | PK, NN |  | Id of the handled event. |
| `consumer` | `character varying(300)` | PK, NN |  | Handler that consumed the event. |
| `processed_at` | `timestamp with time zone` | NN | `now()` | Time the event was handled. |

**Data quality expectations / integrity rules**

- Primary key `pk_inbox_message`: `PRIMARY KEY (message_id, consumer)`

**Indexes**

- `pk_inbox_message` — `btree (message_id, consumer)`

### `inventory.outbox_message` (table)

Integration events of this module waiting for delivery (transactional outbox, ADR 0003).

Rows (demo): **0**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `id` | `uuid` | PK, NN |  | Event id; receivers deduplicate by it. |
| `occurred_at` | `timestamp with time zone` | NN |  | Time the event occurred. |
| `type` | `character varying(300)` | NN |  | CLR type of the event (Namespace.Type, Assembly). |
| `payload` | `jsonb` | NN |  | Event serialized as a JSON object. |
| `processed_at` | `timestamp with time zone` |  |  | Time the event was delivered to every consumer; NULL = pending. |
| `attempts` | `integer` | NN | `0` | Failed delivery attempts so far. |
| `error` | `character varying(2000)` |  |  | Last delivery error, if any. |

**Data quality expectations / integrity rules**

- Primary key `pk_outbox_message`: `PRIMARY KEY (id)`
- Check `ck_outbox_message_attempts`: `CHECK ((attempts >= 0))`
- Check `ck_outbox_message_payload`: `CHECK ((jsonb_typeof(payload) = 'object'::text))`

**Indexes**

- `ix_outbox_message_pending` — `btree (occurred_at) WHERE (processed_at IS NULL)`
- `pk_outbox_message` — `btree (id)`

### `inventory.stock_balance` (table)

Current stock per warehouse and part. Controlled redundancy: = SUM of the ledger, kept in sync by triggers for O(1) reads (NFR01).

Rows (demo): **7,111**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `warehouse_id` | `smallint` | PK, FK → `inventory.warehouse_part`, NN |  | Reference to inventory.warehouse_part. |
| `part_id` | `bigint` | PK, FK → `inventory.warehouse_part`, NN |  | Reference to inventory.warehouse_part. |
| `on_hand_qty` | `numeric(12,3)` | NN | `0` | Physical quantity in the warehouse (SUM of ledger movements); never negative. |
| `reserved_qty` | `numeric(12,3)` | NN | `0` | Quantity held by active (soft/firm) reservations; never above on_hand_qty. |
| `available_qty` | `numeric(12,3)` | GEN | `(on_hand_qty - reserved_qty)` | Generated column: on_hand - reserved (never stored inconsistently). |
| `last_movement_at` | `timestamp with time zone` |  |  | Time of the latest physical movement (US04 "last updated"). |
| `updated_at` | `timestamp with time zone` | NN | `now()` | Time of the last change (maintained by trigger). |

**Data quality expectations / integrity rules**

- Foreign key `fk_stock_balance_warehouse_part`: `FOREIGN KEY (warehouse_id, part_id) REFERENCES inventory.warehouse_part(warehouse_id, part_id) ON DELETE RESTRICT`
- Primary key `pk_stock_balance`: `PRIMARY KEY (warehouse_id, part_id)`
- Check `ck_stock_balance_on_hand_non_negative`: `CHECK ((on_hand_qty >= (0)::numeric))`
- Check `ck_stock_balance_reserved_le_on_hand`: `CHECK ((reserved_qty <= on_hand_qty))`
- Check `ck_stock_balance_reserved_non_negative`: `CHECK ((reserved_qty >= (0)::numeric))`

**Indexes**

- `ix_stock_balance_part` — `btree (part_id)`
- `pk_stock_balance` — `btree (warehouse_id, part_id)`

### `inventory.stock_movement` (partitioned table)

Append-only stock ledger (single source of truth for stock and demand history). Partitioned by month; corrections only via compensating movements.

Rows (demo): **197,902** · Partitioning: `RANGE (occurred_at)`, 61 partitions

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `id` | `bigint` | PK, NN, ID | `identity` |  |
| `occurred_at` | `timestamp with time zone` | PK, NN |  | Business time of the movement; partition key (monthly ranges). |
| `warehouse_id` | `smallint` | FK → `inventory.warehouse`, NN |  | Reference to inventory.warehouse. |
| `part_id` | `bigint` | FK → `catalog.part`, NN |  | Reference to catalog.part. |
| `movement_type_code` | `character varying(20)` | FK → `ref.movement_type`, NN |  | Type of movement; defines the sign (+/-/0) and whether it is demand. |
| `quantity` | `numeric(12,3)` | NN |  | Always positive; direction comes from ref.movement_type.balance_sign. |
| `unit_cost` | `numeric(12,2)` |  |  | Valuation cost per unit at the time of the movement (EUR). |
| `service_order_line_id` | `bigint` |  |  | Reference to fleet.service_order_line. |
| `transfer_order_line_id` | `bigint` | FK → `inventory.transfer_order_line` |  | Reference to inventory.transfer_order_line. |
| `purchase_order_line_id` | `bigint` |  |  | Reference to procurement.purchase_order_line. |
| `import_batch_id` | `bigint` | FK → `raw.import_batch` |  | Reference to raw.import_batch. |
| `external_doc_ref` | `character varying(40)` |  |  | Document number in an external system (POS receipt, ERP document). |
| `note` | `character varying(300)` |  |  |  |
| `created_by` | `uuid` |  |  | User who created the record (iam.app_user). |
| `created_at` | `timestamp with time zone` | NN | `now()` | Time the record was created. |

**Data quality expectations / integrity rules**

- Foreign key `fk_stock_movement_import_batch`: `FOREIGN KEY (import_batch_id) REFERENCES raw.import_batch(id)`
- Foreign key `fk_stock_movement_part`: `FOREIGN KEY (part_id) REFERENCES catalog.part(id)`
- Foreign key `fk_stock_movement_transfer_line`: `FOREIGN KEY (transfer_order_line_id) REFERENCES inventory.transfer_order_line(id)`
- Foreign key `fk_stock_movement_type`: `FOREIGN KEY (movement_type_code) REFERENCES ref.movement_type(code)`
- Foreign key `fk_stock_movement_warehouse`: `FOREIGN KEY (warehouse_id) REFERENCES inventory.warehouse(id)`
- Primary key `pk_stock_movement`: `PRIMARY KEY (id, occurred_at)`
- Check `ck_stock_movement_quantity`: `CHECK ((quantity > (0)::numeric))`
- Check `ck_stock_movement_single_document`: `CHECK ((num_nonnulls(service_order_line_id, transfer_order_line_id, purchase_order_line_id, import_batch_id) <= 1))`
- Check `ck_stock_movement_source_document`: `CHECK ( CASE movement_type_code     WHEN 'RECEIPT_PO'::text THEN (purchase_order_line_id IS NOT NULL)     WHEN 'TRANSFER_OUT'::text THEN (transfer_order_line_id IS NOT NULL)     WHEN 'TRANSFER_IN'::text THEN (transfer_order_line_id IS NOT NULL)     WHEN 'ISSUE_SERVICE'::text THEN (service_order_line_id IS NOT NULL)     WHEN 'ISSUE_SALE'::text THEN (external_doc_ref IS NOT NULL)     WHEN 'HISTORICAL_DEMAND'::text THEN (import_batch_id IS NOT NULL)     WHEN 'ADJUSTMENT_IN'::text THEN (note IS NOT NULL)     WHEN 'ADJUSTMENT_OUT'::text THEN (note IS NOT NULL)     ELSE true END)`
- Check `ck_stock_movement_unit_cost`: `CHECK (((unit_cost IS NULL) OR (unit_cost >= (0)::numeric)))`

**Indexes**

- `ix_stock_movement_import_batch` — `btree (import_batch_id) WHERE (import_batch_id IS NOT NULL)`
- `ix_stock_movement_occurred_brin` — `brin (occurred_at)`
- `ix_stock_movement_part_wh_time` — `btree (part_id, warehouse_id, occurred_at)`
- `ix_stock_movement_po_line` — `btree (purchase_order_line_id) WHERE (purchase_order_line_id IS NOT NULL)`
- `ix_stock_movement_service_line` — `btree (service_order_line_id) WHERE (service_order_line_id IS NOT NULL)`
- `ix_stock_movement_transfer_line` — `btree (transfer_order_line_id) WHERE (transfer_order_line_id IS NOT NULL)`
- `pk_stock_movement` — `btree (id, occurred_at)`

**Triggers:** `trg_stock_movement_apply: inventory.trg_stock_movement_apply`, `trg_stock_movement_immutable: util.trg_forbid_modification`

### `inventory.stock_reservation` (table)

Stock holds. Active (soft/firm) quantities are summed into stock_balance.reserved_qty by trigger.

Rows (demo): **307**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `id` | `bigint` | PK, NN, ID | `identity` | Surrogate primary key. |
| `warehouse_id` | `smallint` | FK → `inventory.stock_balance`, NN |  | Reference to inventory.stock_balance. |
| `part_id` | `bigint` | FK → `inventory.stock_balance`, NN |  | Reference to inventory.stock_balance. |
| `quantity` | `numeric(12,3)` | NN |  | Quantity in the unit of measure of the part. |
| `status` | `inventory.reservation_status` | NN | `'soft'::inventory.reservation_status` | Workflow status (state machine guarded by trigger). |
| `expires_at` | `timestamp with time zone` |  |  | End of a soft hold (now + 7 days for service kits, UC02). |
| `service_order_line_id` | `bigint` |  |  | Reference to fleet.service_order_line. |
| `transfer_order_line_id` | `bigint` | FK → `inventory.transfer_order_line` |  | Reference to inventory.transfer_order_line. |
| `created_by` | `uuid` |  |  | User who created the record (iam.app_user). |
| `created_at` | `timestamp with time zone` | NN | `now()` | Time the record was created. |
| `closed_at` | `timestamp with time zone` |  |  | Time of the closed event. |

**Data quality expectations / integrity rules**

- Foreign key `fk_stock_reservation_balance`: `FOREIGN KEY (warehouse_id, part_id) REFERENCES inventory.stock_balance(warehouse_id, part_id) ON DELETE RESTRICT`
- Foreign key `fk_stock_reservation_transfer_line`: `FOREIGN KEY (transfer_order_line_id) REFERENCES inventory.transfer_order_line(id) ON DELETE CASCADE`
- Primary key `pk_stock_reservation`: `PRIMARY KEY (id)`
- Check `ck_stock_reservation_closed`: `CHECK (((status = ANY (ARRAY['soft'::inventory.reservation_status, 'firm'::inventory.reservation_status])) = (closed_at IS NULL)))`
- Check `ck_stock_reservation_one_source`: `CHECK ((num_nonnulls(service_order_line_id, transfer_order_line_id) = 1))`
- Check `ck_stock_reservation_quantity`: `CHECK ((quantity > (0)::numeric))`
- Check `ck_stock_reservation_soft_expires`: `CHECK (((status <> 'soft'::inventory.reservation_status) OR (expires_at IS NOT NULL)))`

**Indexes**

- `ix_stock_reservation_active` — `btree (warehouse_id, part_id) WHERE (status = ANY (ARRAY['soft'::inventory.reservation_status, 'firm'::inventory.reservation_status]))`
- `ix_stock_reservation_created_by` — `btree (created_by)`
- `ix_stock_reservation_service_line` — `btree (service_order_line_id) WHERE (service_order_line_id IS NOT NULL)`
- `ix_stock_reservation_soft_expiry` — `btree (expires_at) WHERE (status = 'soft'::inventory.reservation_status)`
- `ix_stock_reservation_transfer_line` — `btree (transfer_order_line_id) WHERE (transfer_order_line_id IS NOT NULL)`
- `pk_stock_reservation` — `btree (id)`

**Triggers:** `trg_audit: audit.trg_row_audit`, `trg_stock_reservation_guard: inventory.trg_stock_reservation_guard`, `trg_stock_reservation_sync: inventory.trg_stock_reservation_sync`

### `inventory.transfer_order` (table)

Inter-warehouse transfer order (TR-YYYY-NNNNNN). Status changes only through inventory.approve/dispatch/receive/cancel_transfer().

Rows (demo): **52**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `id` | `bigint` | PK, NN, ID | `identity` | Surrogate primary key. |
| `number` | `character varying(20)` | UQ, NN |  | Business document number (assigned by trigger). |
| `from_warehouse_id` | `smallint` | FK → `inventory.warehouse_route`, NN |  | Reference to inventory.warehouse_route. |
| `to_warehouse_id` | `smallint` | FK → `inventory.warehouse_route`, NN |  | Reference to inventory.warehouse_route. |
| `status` | `inventory.transfer_status` | NN | `'proposed'::inventory.transfer_status` | Workflow status (state machine guarded by trigger). |
| `is_urgent` | `boolean` | NN | `false` | Flag: urgent. |
| `requested_by` | `uuid` |  |  | User who created the record (iam.app_user). |
| `approved_by` | `uuid` |  |  | User who performed the step (iam.app_user). |
| `approved_at` | `timestamp with time zone` |  |  | Time of the approved event. |
| `dispatched_by` | `uuid` |  |  | User who performed the step (iam.app_user). |
| `dispatched_at` | `timestamp with time zone` |  |  | Time of the dispatched event. |
| `received_by` | `uuid` |  |  | User who performed the step (iam.app_user). |
| `received_at` | `timestamp with time zone` |  |  | Time of the received event. |
| `cancel_reason` | `character varying(300)` |  |  |  |
| `created_at` | `timestamp with time zone` | NN | `now()` | Time the record was created. |
| `updated_at` | `timestamp with time zone` | NN | `now()` | Time of the last change (maintained by trigger). |

**Data quality expectations / integrity rules**

- Foreign key `fk_transfer_order_route`: `FOREIGN KEY (from_warehouse_id, to_warehouse_id) REFERENCES inventory.warehouse_route(from_warehouse_id, to_warehouse_id)`
- Primary key `pk_transfer_order`: `PRIMARY KEY (id)`
- Unique `uq_transfer_order_number`: `UNIQUE (number)`
- Check `ck_transfer_order_approved`: `CHECK (((status = ANY (ARRAY['proposed'::inventory.transfer_status, 'cancelled'::inventory.transfer_status])) OR (approved_at IS NOT NULL)))`
- Check `ck_transfer_order_cancel`: `CHECK (((status = 'cancelled'::inventory.transfer_status) = (cancel_reason IS NOT NULL)))`
- Check `ck_transfer_order_dispatched`: `CHECK (((status <> ALL (ARRAY['dispatched'::inventory.transfer_status, 'received'::inventory.transfer_status])) OR (dispatched_at IS NOT NULL)))`
- Check `ck_transfer_order_distinct`: `CHECK ((from_warehouse_id <> to_warehouse_id))`
- Check `ck_transfer_order_number`: `CHECK (((number)::text ~ '^TR-\d{4}-\d{6}$'::text))`
- Check `ck_transfer_order_received`: `CHECK (((status = 'received'::inventory.transfer_status) = (received_at IS NOT NULL)))`
- Check `ck_transfer_order_timeline`: `CHECK (((dispatched_at IS NULL) OR (dispatched_at >= approved_at)))`
- Check `ck_transfer_order_timeline2`: `CHECK (((received_at IS NULL) OR (received_at >= dispatched_at)))`

**Indexes**

- `ix_transfer_order_approved_by` — `btree (approved_by)`
- `ix_transfer_order_dispatched_by` — `btree (dispatched_by)`
- `ix_transfer_order_open` — `btree (status, created_at) WHERE (status = ANY (ARRAY['proposed'::inventory.transfer_status, 'approved'::inventory.transfer_status, 'dispatched'::inventory.transfer_status]))`
- `ix_transfer_order_received_by` — `btree (received_by)`
- `ix_transfer_order_requested_by` — `btree (requested_by)`
- `ix_transfer_order_to_wh` — `btree (to_warehouse_id)`
- `pk_transfer_order` — `btree (id)`
- `uq_transfer_order_number` — `btree (number)`

**Triggers:** `trg_audit: audit.trg_row_audit`, `trg_transfer_order_number: util.trg_assign_document_number`, `trg_transfer_order_status_guard: inventory.trg_transfer_order_status_guard`, `trg_transfer_order_updated_at: util.trg_set_updated_at`

### `inventory.transfer_order_line` (table)

Parts and quantities of a transfer; proposed_qty keeps the system proposal for KPI-04 (acceptance rate).

Rows (demo): **52**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `id` | `bigint` | PK, NN, ID | `identity` | Surrogate primary key. |
| `transfer_order_id` | `bigint` | FK → `inventory.transfer_order`, NN |  | Reference to inventory.transfer_order. |
| `part_id` | `bigint` | FK → `catalog.part`, NN |  | Reference to catalog.part. |
| `quantity` | `numeric(12,3)` | NN |  | Quantity in the unit of measure of the part. |
| `proposed_qty` | `numeric(12,3)` |  |  | Quantity proposed by the system; differs from quantity only with override_reason (KPI-04). |
| `override_reason` | `character varying(300)` |  |  | Mandatory justification of a manual change (UC01 A2). |

**Data quality expectations / integrity rules**

- Foreign key `fk_transfer_order_line_order`: `FOREIGN KEY (transfer_order_id) REFERENCES inventory.transfer_order(id) ON DELETE CASCADE`
- Foreign key `fk_transfer_order_line_part`: `FOREIGN KEY (part_id) REFERENCES catalog.part(id)`
- Primary key `pk_transfer_order_line`: `PRIMARY KEY (id)`
- Unique `uq_transfer_order_line_part`: `UNIQUE (transfer_order_id, part_id)`
- Check `ck_transfer_order_line_override`: `CHECK (((proposed_qty IS NULL) OR (quantity = proposed_qty) OR (override_reason IS NOT NULL)))`
- Check `ck_transfer_order_line_proposed`: `CHECK (((proposed_qty IS NULL) OR (proposed_qty > (0)::numeric)))`
- Check `ck_transfer_order_line_qty`: `CHECK ((quantity > (0)::numeric))`

**Indexes**

- `ix_transfer_order_line_part` — `btree (part_id)`
- `pk_transfer_order_line` — `btree (id)`
- `uq_transfer_order_line_part` — `btree (transfer_order_id, part_id)`

**Triggers:** `trg_audit: audit.trg_row_audit`, `trg_transfer_line_editable: inventory.trg_transfer_line_editable`

### `inventory.v_in_transit` (view)

Inbound quantity per hub and part: dispatched transfers + open approved purchase orders.

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `warehouse_id` | `smallint` |  |  |  |
| `part_id` | `bigint` |  |  |  |
| `in_transit_qty` | `numeric` |  |  |  |
| `transfer_qty` | `numeric` |  |  |  |
| `purchase_qty` | `numeric` |  |  |  |

### `inventory.v_stock_overview` (view)

US04: on-hand, reserved, available and in-transit stock per part and warehouse with last-updated timestamp.

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `part_id` | `bigint` |  |  |  |
| `brand_code` | `character varying(10)` |  |  |  |
| `oem_part_number` | `character varying(40)` |  |  |  |
| `description` | `character varying(300)` |  |  |  |
| `category_code` | `character varying(30)` |  |  |  |
| `warehouse_id` | `smallint` |  |  |  |
| `warehouse_code` | `character varying(10)` |  |  |  |
| `warehouse_name` | `character varying(120)` |  |  |  |
| `bin_location` | `character varying(20)` |  |  |  |
| `on_hand_qty` | `numeric` |  |  |  |
| `reserved_qty` | `numeric` |  |  |  |
| `available_qty` | `numeric` |  |  |  |
| `in_transit_qty` | `numeric` |  |  |  |
| `safety_stock_qty` | `numeric(12,3)` |  |  |  |
| `below_safety_stock` | `boolean` |  |  |  |
| `stock_value_eur` | `numeric` |  |  |  |
| `last_movement_at` | `timestamp with time zone` |  |  |  |
| `balance_updated_at` | `timestamp with time zone` |  |  |  |

### `inventory.warehouse` (table)

Regional branch warehouses (Vilnius central hub, Kaunas, Klaipėda, Šiauliai, Panevėžys, Marijampolė, Utena).

Rows (demo): **7**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `id` | `smallint` | PK, NN, ID | `identity` | Surrogate primary key. |
| `code` | `character varying(10)` | UQ, NN |  | Business code (natural key). |
| `name` | `character varying(120)` | NN |  | Display name. |
| `city` | `character varying(80)` | NN |  |  |
| `address` | `character varying(200)` |  |  |  |
| `is_central_hub` | `boolean` | NN | `false` | Flag: central hub. |
| `latitude` | `numeric(8,5)` |  |  |  |
| `longitude` | `numeric(8,5)` |  |  |  |
| `is_active` | `boolean` | NN | `true` | FALSE = deactivated, kept for history. |
| `created_at` | `timestamp with time zone` | NN | `now()` | Time the record was created. |

**Data quality expectations / integrity rules**

- Primary key `pk_warehouse`: `PRIMARY KEY (id)`
- Unique `uq_warehouse_code`: `UNIQUE (code)`
- Check `ck_warehouse_code`: `CHECK (((code)::text ~ '^[A-Z]{3,10}$'::text))`
- Check `ck_warehouse_lat`: `CHECK (((latitude >= ('-90'::integer)::numeric) AND (latitude <= (90)::numeric)))`
- Check `ck_warehouse_lon`: `CHECK (((longitude >= ('-180'::integer)::numeric) AND (longitude <= (180)::numeric)))`

**Indexes**

- `pk_warehouse` — `btree (id)`
- `uq_warehouse_code` — `btree (code)`
- `uq_warehouse_single_central_hub` — `btree (is_central_hub) WHERE is_central_hub`

### `inventory.warehouse_part` (table)

Stocking policy of a part in a warehouse: shelf location, safety stock, maximum.

Rows (demo): **7,594**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `warehouse_id` | `smallint` | PK, FK → `inventory.warehouse`, NN |  | Reference to inventory.warehouse. |
| `part_id` | `bigint` | PK, FK → `catalog.part`, NN |  | Reference to catalog.part. |
| `bin_location` | `character varying(20)` |  |  |  |
| `safety_stock_qty` | `numeric(12,3)` | NN | `0` | Local safety buffer; a donor hub may only give away stock above it (US10). |
| `max_stock_qty` | `numeric(12,3)` |  |  |  |
| `is_stocked` | `boolean` | NN | `true` | Flag: stocked. |
| `updated_at` | `timestamp with time zone` | NN | `now()` | Time of the last change (maintained by trigger). |

**Data quality expectations / integrity rules**

- Foreign key `fk_warehouse_part_part`: `FOREIGN KEY (part_id) REFERENCES catalog.part(id) ON DELETE RESTRICT`
- Foreign key `fk_warehouse_part_warehouse`: `FOREIGN KEY (warehouse_id) REFERENCES inventory.warehouse(id) ON DELETE RESTRICT`
- Primary key `pk_warehouse_part`: `PRIMARY KEY (warehouse_id, part_id)`
- Check `ck_warehouse_part_bin`: `CHECK (((bin_location IS NULL) OR ((bin_location)::text ~ '^[A-Z0-9-]{2,20}$'::text)))`
- Check `ck_warehouse_part_max`: `CHECK (((max_stock_qty IS NULL) OR (max_stock_qty >= safety_stock_qty)))`
- Check `ck_warehouse_part_safety`: `CHECK ((safety_stock_qty >= (0)::numeric))`

**Indexes**

- `ix_warehouse_part_part` — `btree (part_id)`
- `pk_warehouse_part` — `btree (warehouse_id, part_id)`

**Triggers:** `trg_audit: audit.trg_row_audit`, `trg_warehouse_part_updated_at: util.trg_set_updated_at`

### `inventory.warehouse_route` (table)

Fixed inter-depot transport matrix (M:N warehouse-warehouse): distance, transit time, cost. Used by the rebalancing engine (US10).

Rows (demo): **42**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `from_warehouse_id` | `smallint` | PK, FK → `inventory.warehouse`, NN |  | Reference to inventory.warehouse. |
| `to_warehouse_id` | `smallint` | PK, FK → `inventory.warehouse`, NN |  | Reference to inventory.warehouse. |
| `distance_km` | `numeric(6,1)` | NN |  |  |
| `transit_hours` | `numeric(5,1)` | NN |  | Planned inter-depot transport time incl. consolidation. |
| `transport_cost_eur` | `numeric(10,2)` | NN |  | Cost of one inter-depot shipment (EUR). |

**Data quality expectations / integrity rules**

- Foreign key `fk_warehouse_route_from`: `FOREIGN KEY (from_warehouse_id) REFERENCES inventory.warehouse(id) ON DELETE CASCADE`
- Foreign key `fk_warehouse_route_to`: `FOREIGN KEY (to_warehouse_id) REFERENCES inventory.warehouse(id) ON DELETE CASCADE`
- Primary key `pk_warehouse_route`: `PRIMARY KEY (from_warehouse_id, to_warehouse_id)`
- Check `ck_warehouse_route_cost`: `CHECK ((transport_cost_eur >= (0)::numeric))`
- Check `ck_warehouse_route_distance`: `CHECK ((distance_km > (0)::numeric))`
- Check `ck_warehouse_route_distinct`: `CHECK ((from_warehouse_id <> to_warehouse_id))`
- Check `ck_warehouse_route_transit`: `CHECK (((transit_hours > (0)::numeric) AND (transit_hours <= (240)::numeric)))`

**Indexes**

- `ix_warehouse_route_to` — `btree (to_warehouse_id)`
- `pk_warehouse_route` — `btree (from_warehouse_id, to_warehouse_id)`

## 6. Schema `fleet` — Fleet

### `fleet.__ef_migrations_history` (table)

Rows (demo): **2**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `migration_id` | `character varying(150)` | PK, NN |  |  |
| `product_version` | `character varying(32)` | NN |  |  |

**Data quality expectations / integrity rules**

- Primary key `pk___ef_migrations_history`: `PRIMARY KEY (migration_id)`

**Indexes**

- `pk___ef_migrations_history` — `btree (migration_id)`

### `fleet.customer` (table)

Machine owner (farm). Pseudonymised: only a code and an HMAC of the ERP id are stored, no names or contacts.

Rows (demo): **250**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `id` | `bigint` | PK, NN, ID | `identity` | Surrogate primary key. |
| `customer_code` | `character varying(12)` | UQ, NN |  |  |
| `erp_ref_hash` | `bytea` | UQ |  | HMAC-SHA256 of the ERP customer id (util.pseudonymise); allows matching re-imports without storing the id. |
| `region_warehouse_id` | `smallint` | NN |  | Reference to inventory.warehouse. |
| `farm_size_ha` | `numeric(8,1)` |  |  |  |
| `is_active` | `boolean` | NN | `true` | FALSE = deactivated, kept for history. |
| `created_at` | `timestamp with time zone` | NN | `now()` | Time the record was created. |

**Data quality expectations / integrity rules**

- Primary key `pk_customer`: `PRIMARY KEY (id)`
- Unique `uq_customer_code`: `UNIQUE (customer_code)`
- Unique `uq_customer_erp_ref_hash`: `UNIQUE (erp_ref_hash)`
- Check `ck_customer_code`: `CHECK (((customer_code)::text ~ '^CUST-\d{6}$'::text))`
- Check `ck_customer_erp_ref_hash`: `CHECK (((erp_ref_hash IS NULL) OR (length(erp_ref_hash) = 32)))`
- Check `ck_customer_farm_size`: `CHECK (((farm_size_ha IS NULL) OR (farm_size_ha > (0)::numeric)))`

**Indexes**

- `ix_customer_region_warehouse` — `btree (region_warehouse_id)`
- `pk_customer` — `btree (id)`
- `uq_customer_code` — `btree (customer_code)`
- `uq_customer_erp_ref_hash` — `btree (erp_ref_hash)`

### `fleet.engine_hours_reading` (table)

Odometer history (US05). Monotonic and physically plausible readings are enforced by trigger.

Rows (demo): **2,062**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `id` | `bigint` | PK, NN, ID | `identity` | Surrogate primary key. |
| `machine_id` | `bigint` | FK → `fleet.machine`, NN |  | Reference to fleet.machine. |
| `reading_hours` | `numeric(8,1)` | NN |  | Odometer value; must not decrease and must be physically plausible. |
| `read_at` | `timestamp with time zone` | NN |  | Time the reading was taken in the field (may be earlier than created_at for offline entry). |
| `source` | `fleet.reading_source` | NN | `'manual'::fleet.reading_source` |  |
| `recorded_by` | `uuid` |  |  | User who performed the step (iam.app_user). |
| `created_at` | `timestamp with time zone` | NN | `now()` | Time the record was created. |

**Data quality expectations / integrity rules**

- Foreign key `fk_engine_hours_reading_machine`: `FOREIGN KEY (machine_id) REFERENCES fleet.machine(id) ON DELETE CASCADE`
- Primary key `pk_engine_hours_reading`: `PRIMARY KEY (id)`
- Unique `uq_engine_hours_reading_machine_time`: `UNIQUE (machine_id, read_at)`
- Check `ck_engine_hours_reading_hours`: `CHECK (((reading_hours >= (0)::numeric) AND (reading_hours <= (100000)::numeric)))`
- Check `ck_engine_hours_reading_not_future`: `CHECK ((read_at <= (created_at + '00:05:00'::interval)))`

**Indexes**

- `ix_engine_hours_reading_user` — `btree (recorded_by)`
- `pk_engine_hours_reading` — `btree (id)`
- `uq_engine_hours_reading_machine_time` — `btree (machine_id, read_at)`

**Triggers:** `trg_engine_hours_apply: fleet.trg_engine_hours_apply`, `trg_engine_hours_immutable: util.trg_forbid_modification`, `trg_engine_hours_validate: fleet.trg_engine_hours_validate`

### `fleet.inbox_message` (table)

Integration events already handled by a consumer of this module (idempotent receiver, ADR 0003).

Rows (demo): **0**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `message_id` | `uuid` | PK, NN |  | Id of the handled event. |
| `consumer` | `character varying(300)` | PK, NN |  | Handler that consumed the event. |
| `processed_at` | `timestamp with time zone` | NN | `now()` | Time the event was handled. |

**Data quality expectations / integrity rules**

- Primary key `pk_inbox_message`: `PRIMARY KEY (message_id, consumer)`

**Indexes**

- `pk_inbox_message` — `btree (message_id, consumer)`

### `fleet.machine` (table)

Registered machine (installed base). current_engine_hours is maintained by the engine-hour reading trigger.

Rows (demo): **501**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `id` | `bigint` | PK, NN, ID | `identity` | Surrogate primary key. |
| `vin` | `character varying(20)` | UQ, NN |  | Vehicle / product identification number (unique). |
| `model_id` | `integer` | FK → `fleet.machine_model`, NN |  | Reference to fleet.machine_model. |
| `customer_id` | `bigint` | FK → `fleet.customer`, NN |  | Reference to fleet.customer. |
| `operating_condition_id` | `smallint` | FK → `ref.operating_condition`, NN |  | Operating condition that shortens OEM intervals (F5). |
| `home_warehouse_id` | `smallint` | NN |  | Nearest depot where service kits are reserved. |
| `manufacture_year` | `smallint` | NN |  |  |
| `current_engine_hours` | `numeric(8,1)` | NN | `0` | Latest odometer value; maintained by the reading trigger. |
| `engine_hours_read_at` | `timestamp with time zone` |  |  | Time of the engine hours read event. |
| `status` | `fleet.machine_status` | NN | `'active'::fleet.machine_status` | Workflow status (state machine guarded by trigger). |
| `commissioned_on` | `date` |  |  |  |
| `created_at` | `timestamp with time zone` | NN | `now()` | Time the record was created. |
| `updated_at` | `timestamp with time zone` | NN | `now()` | Time of the last change (maintained by trigger). |

**Data quality expectations / integrity rules**

- Foreign key `fk_machine_condition`: `FOREIGN KEY (operating_condition_id) REFERENCES ref.operating_condition(id)`
- Foreign key `fk_machine_customer`: `FOREIGN KEY (customer_id) REFERENCES fleet.customer(id)`
- Foreign key `fk_machine_model`: `FOREIGN KEY (model_id) REFERENCES fleet.machine_model(id)`
- Primary key `pk_machine`: `PRIMARY KEY (id)`
- Unique `uq_machine_vin`: `UNIQUE (vin)`
- Check `ck_machine_commissioned`: `CHECK (((commissioned_on IS NULL) OR (EXTRACT(year FROM commissioned_on) >= (manufacture_year)::numeric)))`
- Check `ck_machine_engine_hours`: `CHECK (((current_engine_hours >= (0)::numeric) AND (current_engine_hours <= (100000)::numeric)))`
- Check `ck_machine_vin`: `CHECK (((vin)::text ~ '^[A-Z0-9]{8,20}$'::text))`
- Check `ck_machine_year`: `CHECK (((manufacture_year >= 1950) AND (manufacture_year <= 2100)))`

**Indexes**

- `ix_machine_condition` — `btree (operating_condition_id)`
- `ix_machine_customer` — `btree (customer_id)`
- `ix_machine_home_wh_active` — `btree (home_warehouse_id) WHERE (status = 'active'::fleet.machine_status)`
- `ix_machine_model` — `btree (model_id)`
- `ix_machine_vin_trgm` — `gin (vin gin_trgm_ops)`
- `pk_machine` — `btree (id)`
- `uq_machine_vin` — `btree (vin)`

**Triggers:** `trg_audit: audit.trg_row_audit`, `trg_machine_updated_at: util.trg_set_updated_at`

### `fleet.machine_model` (table)

Machine model, e.g. Claas Lexion 8700, John Deere 6R 150.

Rows (demo): **15**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `id` | `integer` | PK, NN, ID | `identity` | Surrogate primary key. |
| `brand_id` | `smallint` | FK → `ref.brand`, NN |  | Reference to ref.brand. |
| `machine_type_id` | `smallint` | FK → `ref.machine_type`, NN |  | Reference to ref.machine_type. |
| `model_name` | `character varying(80)` | NN |  |  |

**Data quality expectations / integrity rules**

- Foreign key `fk_machine_model_brand`: `FOREIGN KEY (brand_id) REFERENCES ref.brand(id)`
- Foreign key `fk_machine_model_type`: `FOREIGN KEY (machine_type_id) REFERENCES ref.machine_type(id)`
- Primary key `pk_machine_model`: `PRIMARY KEY (id)`
- Unique `uq_machine_model_brand_name`: `UNIQUE (brand_id, model_name)`

**Indexes**

- `ix_machine_model_type` — `btree (machine_type_id)`
- `pk_machine_model` — `btree (id)`
- `uq_machine_model_brand_name` — `btree (brand_id, model_name)`

### `fleet.maintenance_plan` (table)

OEM service interval of a model (e.g. 500 h engine oil service). The effective interval = interval_hours x operating_condition.interval_factor.

Rows (demo): **26**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `id` | `integer` | PK, NN, ID | `identity` | Surrogate primary key. |
| `model_id` | `integer` | FK → `fleet.machine_model`, NN |  | Reference to fleet.machine_model. |
| `interval_hours` | `integer` | NN |  | OEM interval in engine hours (e.g. 500). |
| `name` | `character varying(120)` | NN |  | Display name. |
| `is_active` | `boolean` | NN | `true` | FALSE = deactivated, kept for history. |

**Data quality expectations / integrity rules**

- Foreign key `fk_maintenance_plan_model`: `FOREIGN KEY (model_id) REFERENCES fleet.machine_model(id) ON DELETE CASCADE`
- Primary key `pk_maintenance_plan`: `PRIMARY KEY (id)`
- Unique `uq_maintenance_plan_model_interval`: `UNIQUE (model_id, interval_hours)`
- Check `ck_maintenance_plan_interval`: `CHECK (((interval_hours >= 10) AND (interval_hours <= 20000)))`

**Indexes**

- `pk_maintenance_plan` — `btree (id)`
- `uq_maintenance_plan_model_interval` — `btree (model_id, interval_hours)`

**Triggers:** `trg_audit: audit.trg_row_audit`

### `fleet.maintenance_plan_part` (table)

Service kit / bill of materials of a maintenance plan (M:N plan <-> part with quantity).

Rows (demo): **69**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `maintenance_plan_id` | `integer` | PK, FK → `fleet.maintenance_plan`, NN |  | Reference to fleet.maintenance_plan. |
| `part_id` | `bigint` | PK, NN |  | Reference to catalog.part. |
| `quantity` | `numeric(12,3)` | NN |  | Quantity of the part in the service kit. |

**Data quality expectations / integrity rules**

- Foreign key `fk_maintenance_plan_part_plan`: `FOREIGN KEY (maintenance_plan_id) REFERENCES fleet.maintenance_plan(id) ON DELETE CASCADE`
- Primary key `pk_maintenance_plan_part`: `PRIMARY KEY (maintenance_plan_id, part_id)`
- Check `ck_maintenance_plan_part_qty`: `CHECK ((quantity > (0)::numeric))`

**Indexes**

- `ix_maintenance_plan_part_part` — `btree (part_id)`
- `pk_maintenance_plan_part` — `btree (maintenance_plan_id, part_id)`

**Triggers:** `trg_audit: audit.trg_row_audit`

### `fleet.outbox_message` (table)

Integration events of this module waiting for delivery (transactional outbox, ADR 0003).

Rows (demo): **0**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `id` | `uuid` | PK, NN |  | Event id; receivers deduplicate by it. |
| `occurred_at` | `timestamp with time zone` | NN |  | Time the event occurred. |
| `type` | `character varying(300)` | NN |  | CLR type of the event (Namespace.Type, Assembly). |
| `payload` | `jsonb` | NN |  | Event serialized as a JSON object. |
| `processed_at` | `timestamp with time zone` |  |  | Time the event was delivered to every consumer; NULL = pending. |
| `attempts` | `integer` | NN | `0` | Failed delivery attempts so far. |
| `error` | `character varying(2000)` |  |  | Last delivery error, if any. |

**Data quality expectations / integrity rules**

- Primary key `pk_outbox_message`: `PRIMARY KEY (id)`
- Check `ck_outbox_message_attempts`: `CHECK ((attempts >= 0))`
- Check `ck_outbox_message_payload`: `CHECK ((jsonb_typeof(payload) = 'object'::text))`

**Indexes**

- `ix_outbox_message_pending` — `btree (occurred_at) WHERE (processed_at IS NULL)`
- `pk_outbox_message` — `btree (id)`

### `fleet.service_order` (table)

Service order (SO-YYYY-NNNNNN). Planned (plan + due hours) or unplanned repair (plan NULL). Completed orders form the maintenance history.

Rows (demo): **90**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `id` | `bigint` | PK, NN, ID | `identity` | Surrogate primary key. |
| `number` | `character varying(20)` | UQ, NN |  | Business document number (assigned by trigger). |
| `machine_id` | `bigint` | FK → `fleet.machine`, NN |  | Reference to fleet.machine. |
| `maintenance_plan_id` | `integer` | FK → `fleet.maintenance_plan` |  | Reference to fleet.maintenance_plan. |
| `warehouse_id` | `smallint` | NN |  | Reference to inventory.warehouse. |
| `status` | `fleet.service_order_status` | NN | `'draft'::fleet.service_order_status` | Workflow status (state machine guarded by trigger). |
| `due_engine_hours` | `numeric(8,1)` |  |  | Engine-hour threshold this planned service covers (e.g. 500, 1000, 1500 ...). |
| `trigger_engine_hours` | `numeric(8,1)` |  |  | Engine hours of the reading that triggered the draft. |
| `completed_engine_hours` | `numeric(8,1)` |  |  |  |
| `created_by` | `uuid` |  |  | User who created the record (iam.app_user). |
| `created_at` | `timestamp with time zone` | NN | `now()` | Time the record was created. |
| `completed_at` | `timestamp with time zone` |  |  | Time of the completed event. |
| `cancel_reason` | `character varying(300)` |  |  |  |
| `updated_at` | `timestamp with time zone` | NN | `now()` | Time of the last change (maintained by trigger). |

**Data quality expectations / integrity rules**

- Foreign key `fk_service_order_machine`: `FOREIGN KEY (machine_id) REFERENCES fleet.machine(id)`
- Foreign key `fk_service_order_plan`: `FOREIGN KEY (maintenance_plan_id) REFERENCES fleet.maintenance_plan(id)`
- Primary key `pk_service_order`: `PRIMARY KEY (id)`
- Unique `uq_service_order_number`: `UNIQUE (number)`
- Check `ck_service_order_cancel`: `CHECK (((status = 'cancelled'::fleet.service_order_status) = (cancel_reason IS NOT NULL)))`
- Check `ck_service_order_completed`: `CHECK (((status = 'completed'::fleet.service_order_status) = ((completed_at IS NOT NULL) AND (completed_engine_hours IS NOT NULL))))`
- Check `ck_service_order_number`: `CHECK (((number)::text ~ '^SO-\d{4}-\d{6}$'::text))`
- Check `ck_service_order_planned_due`: `CHECK (((maintenance_plan_id IS NULL) OR (due_engine_hours IS NOT NULL)))`

**Indexes**

- `ix_service_order_created_by` — `btree (created_by)`
- `ix_service_order_open` — `btree (warehouse_id, status) WHERE (status = ANY (ARRAY['draft'::fleet.service_order_status, 'parts_reserved'::fleet.service_order_status, 'in_progress'::fleet.service_order_status]))`
- `ix_service_order_plan` — `btree (maintenance_plan_id)`
- `pk_service_order` — `btree (id)`
- `uq_service_order_machine_plan_due` — `btree (machine_id, maintenance_plan_id, due_engine_hours) WHERE ((status <> 'cancelled'::fleet.service_order_status) AND (maintenance_plan_id IS NOT NULL))`
- `uq_service_order_number` — `btree (number)`

**Triggers:** `trg_audit: audit.trg_row_audit`, `trg_service_order_number: util.trg_assign_document_number`, `trg_service_order_status_guard: fleet.trg_service_order_status_guard`, `trg_service_order_updated_at: util.trg_set_updated_at`

### `fleet.service_order_line` (table)

Parts required by a service order (copied from the kit, editable for repairs).

Rows (demo): **256**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `id` | `bigint` | PK, NN, ID | `identity` | Surrogate primary key. |
| `service_order_id` | `bigint` | FK → `fleet.service_order`, NN |  | Reference to fleet.service_order. |
| `part_id` | `bigint` | NN |  | Reference to catalog.part. |
| `qty_required` | `numeric(12,3)` | NN |  | Quantity in the unit of measure of the part. |
| `qty_issued` | `numeric(12,3)` | NN | `0` | Quantity already issued from stock (ISSUE_SERVICE). |

**Data quality expectations / integrity rules**

- Foreign key `fk_service_order_line_order`: `FOREIGN KEY (service_order_id) REFERENCES fleet.service_order(id) ON DELETE CASCADE`
- Primary key `pk_service_order_line`: `PRIMARY KEY (id)`
- Unique `uq_service_order_line_part`: `UNIQUE (service_order_id, part_id)`
- Check `ck_service_order_line_issued`: `CHECK (((qty_issued >= (0)::numeric) AND (qty_issued <= qty_required)))`
- Check `ck_service_order_line_required`: `CHECK ((qty_required > (0)::numeric))`

**Indexes**

- `ix_service_order_line_part` — `btree (part_id)`
- `pk_service_order_line` — `btree (id)`
- `uq_service_order_line_part` — `btree (service_order_id, part_id)`

### `fleet.v_machine_maintenance_status` (view)

Machine card (WF03): current engine hours and the next not yet planned OEM service.

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `machine_id` | `bigint` |  |  |  |
| `vin` | `character varying(20)` |  |  |  |
| `brand_name` | `character varying(80)` |  |  |  |
| `model_name` | `character varying(80)` |  |  |  |
| `machine_type_code` | `character varying(20)` |  |  |  |
| `customer_code` | `character varying(12)` |  |  |  |
| `home_warehouse_code` | `character varying(10)` |  |  |  |
| `operating_condition_code` | `character varying(20)` |  |  |  |
| `current_engine_hours` | `numeric(8,1)` |  |  |  |
| `engine_hours_read_at` | `timestamp with time zone` |  |  |  |
| `next_service` | `character varying` |  |  |  |
| `next_service_due_hours` | `numeric` |  |  |  |
| `next_service_remaining_hours` | `numeric` |  |  |  |
| `is_overdue` | `boolean` |  |  |  |
| `open_service_orders` | `bigint` |  |  |  |

### `fleet.v_service_kit_shortage` (view)

Open service orders whose kit could not be fully reserved (input for transfers / purchasing).

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `service_order_id` | `bigint` |  |  |  |
| `number` | `character varying(20)` |  |  |  |
| `status` | `fleet.service_order_status` |  |  |  |
| `warehouse_code` | `character varying(10)` |  |  |  |
| `part_id` | `bigint` |  |  |  |
| `oem_part_number` | `character varying(40)` |  |  |  |
| `description` | `character varying(300)` |  |  |  |
| `qty_required` | `numeric(12,3)` |  |  |  |
| `qty_issued` | `numeric(12,3)` |  |  |  |
| `qty_reserved` | `numeric` |  |  |  |
| `qty_missing` | `numeric` |  |  |  |

## 7. Schema `procurement` — Procurement — suppliers & purchase orders

### `procurement.__ef_migrations_history` (table)

Rows (demo): **1**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `migration_id` | `character varying(150)` | PK, NN |  |  |
| `product_version` | `character varying(32)` | NN |  |  |

**Data quality expectations / integrity rules**

- Primary key `pk___ef_migrations_history`: `PRIMARY KEY (migration_id)`

**Indexes**

- `pk___ef_migrations_history` — `btree (migration_id)`

### `procurement.inbox_message` (table)

Integration events already handled by a consumer of this module (idempotent receiver, ADR 0003).

Rows (demo): **0**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `message_id` | `uuid` | PK, NN |  | Id of the handled event. |
| `consumer` | `character varying(300)` | PK, NN |  | Handler that consumed the event. |
| `processed_at` | `timestamp with time zone` | NN | `now()` | Time the event was handled. |

**Data quality expectations / integrity rules**

- Primary key `pk_inbox_message`: `PRIMARY KEY (message_id, consumer)`

**Indexes**

- `pk_inbox_message` — `btree (message_id, consumer)`

### `procurement.outbox_message` (table)

Integration events of this module waiting for delivery (transactional outbox, ADR 0003).

Rows (demo): **0**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `id` | `uuid` | PK, NN |  | Event id; receivers deduplicate by it. |
| `occurred_at` | `timestamp with time zone` | NN |  | Time the event occurred. |
| `type` | `character varying(300)` | NN |  | CLR type of the event (Namespace.Type, Assembly). |
| `payload` | `jsonb` | NN |  | Event serialized as a JSON object. |
| `processed_at` | `timestamp with time zone` |  |  | Time the event was delivered to every consumer; NULL = pending. |
| `attempts` | `integer` | NN | `0` | Failed delivery attempts so far. |
| `error` | `character varying(2000)` |  |  | Last delivery error, if any. |

**Data quality expectations / integrity rules**

- Primary key `pk_outbox_message`: `PRIMARY KEY (id)`
- Check `ck_outbox_message_attempts`: `CHECK ((attempts >= 0))`
- Check `ck_outbox_message_payload`: `CHECK ((jsonb_typeof(payload) = 'object'::text))`

**Indexes**

- `ix_outbox_message_pending` — `btree (occurred_at) WHERE (processed_at IS NULL)`
- `pk_outbox_message` — `btree (id)`

### `procurement.purchase_order` (table)

Supplier purchase order (PO-YYYY-NNNNNN). Drafts are generated from recommendations and approved by management (US14).

Rows (demo): **33**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `id` | `bigint` | PK, NN, ID | `identity` | Surrogate primary key. |
| `number` | `character varying(20)` | UQ, NN |  | Business document number (assigned by trigger). |
| `supplier_id` | `integer` | FK → `procurement.supplier`, NN |  | Reference to procurement.supplier. |
| `warehouse_id` | `smallint` | NN |  | Reference to inventory.warehouse. |
| `status` | `procurement.po_status` | NN | `'draft'::procurement.po_status` | Workflow status (state machine guarded by trigger). |
| `is_urgent` | `boolean` | NN | `false` | US16 emergency direct purchase ("Urgent Harvest Dispatch"), bypasses weekly batching. |
| `urgent_reason` | `character varying(300)` |  |  |  |
| `currency_code` | `character(3)` | NN | `'EUR'::bpchar` |  |
| `total_amount` | `numeric(14,2)` | NN | `0` | Controlled redundancy: SUM(line_amount), maintained by trigger; needed by the four-eyes CHECK. |
| `expected_arrival_date` | `date` |  |  | Approval date + longest supplier lead time of the lines. |
| `created_by` | `uuid` | NN |  | User who created the record (iam.app_user). |
| `created_at` | `timestamp with time zone` | NN | `now()` | Time the record was created. |
| `submitted_at` | `timestamp with time zone` |  |  | Time of the submitted event. |
| `approved_by` | `uuid` |  |  | User who performed the step (iam.app_user). |
| `approved_at` | `timestamp with time zone` |  |  | Time of the approved event. |
| `exported_at` | `timestamp with time zone` |  |  | Time of the exported event. |
| `cancel_reason` | `character varying(300)` |  |  |  |
| `updated_at` | `timestamp with time zone` | NN | `now()` | Time of the last change (maintained by trigger). |

**Data quality expectations / integrity rules**

- Foreign key `fk_purchase_order_supplier`: `FOREIGN KEY (supplier_id) REFERENCES procurement.supplier(id)`
- Primary key `pk_purchase_order`: `PRIMARY KEY (id)`
- Unique `uq_purchase_order_number`: `UNIQUE (number)`
- Check `ck_purchase_order_approved`: `CHECK (((status = ANY (ARRAY['draft'::procurement.po_status, 'pending_approval'::procurement.po_status, 'cancelled'::procurement.po_status])) OR ((approved_by IS NOT NULL) AND (approved_at IS NOT NULL))))`
- Check `ck_purchase_order_cancel`: `CHECK (((status = 'cancelled'::procurement.po_status) = (cancel_reason IS NOT NULL)))`
- Check `ck_purchase_order_currency`: `CHECK ((currency_code ~ '^[A-Z]{3}$'::text))`
- Check `ck_purchase_order_four_eyes`: `CHECK (((approved_by IS NULL) OR (total_amount <= (5000)::numeric) OR (approved_by <> created_by)))`
- Check `ck_purchase_order_number`: `CHECK (((number)::text ~ '^PO-\d{4}-\d{6}$'::text))`
- Check `ck_purchase_order_timeline`: `CHECK (((approved_at IS NULL) OR (approved_at >= created_at)))`
- Check `ck_purchase_order_total`: `CHECK ((total_amount >= (0)::numeric))`
- Check `ck_purchase_order_urgent`: `CHECK (((NOT is_urgent) OR (urgent_reason IS NOT NULL)))`

**Indexes**

- `ix_purchase_order_approved_by` — `btree (approved_by)`
- `ix_purchase_order_created_by` — `btree (created_by)`
- `ix_purchase_order_open` — `btree (status, expected_arrival_date) WHERE (status = ANY (ARRAY['draft'::procurement.po_status, 'pending_approval'::procurement.po_status, 'approved'::procurement.po_status, 'exported'::procurement.po_status, 'partially_received'::procurement.po_status]))`
- `ix_purchase_order_supplier_created` — `btree (supplier_id, created_at DESC)`
- `ix_purchase_order_warehouse` — `btree (warehouse_id)`
- `pk_purchase_order` — `btree (id)`
- `uq_purchase_order_number` — `btree (number)`

**Triggers:** `trg_audit: audit.trg_row_audit`, `trg_po_status_guard: procurement.trg_po_status_guard`, `trg_purchase_order_number: util.trg_assign_document_number`, `trg_purchase_order_updated_at: util.trg_set_updated_at`

### `procurement.purchase_order_line` (table)

PO line. MOQ / pack-size rules of procurement.supplier_part are enforced by trigger.

Rows (demo): **58**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `id` | `bigint` | PK, NN, ID | `identity` | Surrogate primary key. |
| `purchase_order_id` | `bigint` | FK → `procurement.purchase_order`, NN |  | Reference to procurement.purchase_order. |
| `part_id` | `bigint` | NN |  | Reference to catalog.part. |
| `quantity` | `numeric(12,3)` | NN |  | Quantity in the unit of measure of the part. |
| `unit_price` | `numeric(12,2)` | NN |  |  |
| `line_amount` | `numeric(14,2)` | GEN | `round((quantity * unit_price), 2)` | Generated column: round(quantity x unit_price, 2). |
| `received_qty` | `numeric(12,3)` | NN | `0` | Quantity received so far (partial receipts). |
| `proposed_qty` | `numeric(12,3)` |  |  |  |
| `override_reason` | `character varying(300)` |  |  |  |

**Data quality expectations / integrity rules**

- Foreign key `fk_purchase_order_line_order`: `FOREIGN KEY (purchase_order_id) REFERENCES procurement.purchase_order(id) ON DELETE CASCADE`
- Primary key `pk_purchase_order_line`: `PRIMARY KEY (id)`
- Unique `uq_purchase_order_line_part`: `UNIQUE (purchase_order_id, part_id)`
- Check `ck_purchase_order_line_override`: `CHECK (((proposed_qty IS NULL) OR (quantity = proposed_qty) OR (override_reason IS NOT NULL)))`
- Check `ck_purchase_order_line_price`: `CHECK ((unit_price > (0)::numeric))`
- Check `ck_purchase_order_line_qty`: `CHECK ((quantity > (0)::numeric))`
- Check `ck_purchase_order_line_received`: `CHECK (((received_qty >= (0)::numeric) AND (received_qty <= quantity)))`

**Indexes**

- `ix_purchase_order_line_part` — `btree (part_id)`
- `pk_purchase_order_line` — `btree (id)`
- `uq_purchase_order_line_part` — `btree (purchase_order_id, part_id)`

**Triggers:** `trg_audit: audit.trg_row_audit`, `trg_po_line_total: procurement.trg_po_line_total`, `trg_po_line_validate: procurement.trg_po_line_validate`

### `procurement.supplier` (table)

OEM suppliers / distributors. Business contact data only (no personal data).

Rows (demo): **7**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `id` | `integer` | PK, NN, ID | `identity` | Surrogate primary key. |
| `code` | `character varying(20)` | UQ, NN |  | Business code (natural key). |
| `name` | `character varying(200)` | UQ, NN |  | Display name. |
| `country_code` | `character(2)` | NN |  |  |
| `order_email` | `citext` |  |  | Shared order mailbox of the company (e.g. orders@...), not a personal address. |
| `default_lead_time_days` | `smallint` | NN |  |  |
| `currency_code` | `character(3)` | NN | `'EUR'::bpchar` |  |
| `is_active` | `boolean` | NN | `true` | FALSE = deactivated, kept for history. |
| `created_at` | `timestamp with time zone` | NN | `now()` | Time the record was created. |
| `updated_at` | `timestamp with time zone` | NN | `now()` | Time of the last change (maintained by trigger). |

**Data quality expectations / integrity rules**

- Primary key `pk_supplier`: `PRIMARY KEY (id)`
- Unique `uq_supplier_code`: `UNIQUE (code)`
- Unique `uq_supplier_name`: `UNIQUE (name)`
- Check `ck_supplier_country`: `CHECK ((country_code ~ '^[A-Z]{2}$'::text))`
- Check `ck_supplier_currency`: `CHECK ((currency_code ~ '^[A-Z]{3}$'::text))`
- Check `ck_supplier_email`: `CHECK (((order_email IS NULL) OR (order_email ~ '^[^@\s]+@[^@\s]+\.[^@\s]+$'::citext)))`
- Check `ck_supplier_lead_time`: `CHECK (((default_lead_time_days >= 1) AND (default_lead_time_days <= 365)))`

**Indexes**

- `pk_supplier` — `btree (id)`
- `uq_supplier_code` — `btree (code)`
- `uq_supplier_name` — `btree (name)`

**Triggers:** `trg_supplier_updated_at: util.trg_set_updated_at`

### `procurement.supplier_part` (table)

Commercial conditions of a supplier for a part: MOQ, pack size, lead time (US12).

Rows (demo): **2,643**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `id` | `bigint` | PK, NN, ID | `identity` | Surrogate primary key. |
| `supplier_id` | `integer` | FK → `procurement.supplier`, NN |  | Reference to procurement.supplier. |
| `part_id` | `bigint` | NN |  | Reference to catalog.part. |
| `supplier_part_number` | `character varying(40)` |  |  |  |
| `moq` | `numeric(12,3)` | NN | `1` | Minimum order quantity; must be a multiple of pack_size. |
| `pack_size` | `numeric(12,3)` | NN | `1` | Packaging increment; ordered quantities must be multiples of it. |
| `lead_time_days` | `smallint` | NN |  | Supplier lead time for this part (calendar days). |
| `is_preferred` | `boolean` | NN | `false` | Preferred supplier used for automatic PO drafts (max. one per part). |
| `created_at` | `timestamp with time zone` | NN | `now()` | Time the record was created. |
| `updated_at` | `timestamp with time zone` | NN | `now()` | Time of the last change (maintained by trigger). |

**Data quality expectations / integrity rules**

- Foreign key `fk_supplier_part_supplier`: `FOREIGN KEY (supplier_id) REFERENCES procurement.supplier(id) ON DELETE RESTRICT`
- Primary key `pk_supplier_part`: `PRIMARY KEY (id)`
- Unique `uq_supplier_part`: `UNIQUE (supplier_id, part_id)`
- Check `ck_supplier_part_lead_time`: `CHECK (((lead_time_days >= 1) AND (lead_time_days <= 365)))`
- Check `ck_supplier_part_moq`: `CHECK ((moq > (0)::numeric))`
- Check `ck_supplier_part_moq_pack`: `CHECK ((mod(moq, pack_size) = (0)::numeric))`
- Check `ck_supplier_part_pack`: `CHECK ((pack_size > (0)::numeric))`

**Indexes**

- `ix_supplier_part_part` — `btree (part_id)`
- `pk_supplier_part` — `btree (id)`
- `uq_supplier_part` — `btree (supplier_id, part_id)`
- `uq_supplier_part_preferred` — `btree (part_id) WHERE is_preferred`

**Triggers:** `trg_audit: audit.trg_row_audit`, `trg_supplier_part_updated_at: util.trg_set_updated_at`

### `procurement.supplier_price` (table)

Supplier purchase price history; non-overlapping validity periods per supplier-part (EXCLUDE USING gist).

Rows (demo): **5,286**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `id` | `bigint` | PK, NN, ID | `identity` | Surrogate primary key. |
| `supplier_part_id` | `bigint` | FK → `procurement.supplier_part`, NN |  | Reference to procurement.supplier_part. |
| `valid_during` | `daterange` | NN |  | Validity period [from, to); open upper bound = valid until further notice. |
| `unit_price` | `numeric(12,2)` | NN |  |  |

**Data quality expectations / integrity rules**

- Foreign key `fk_supplier_price_supplier_part`: `FOREIGN KEY (supplier_part_id) REFERENCES procurement.supplier_part(id) ON DELETE CASCADE`
- Primary key `pk_supplier_price`: `PRIMARY KEY (id)`
- Check `ck_supplier_price_amount`: `CHECK ((unit_price > (0)::numeric))`
- Check `ck_supplier_price_range`: `CHECK (((NOT isempty(valid_during)) AND (lower(valid_during) IS NOT NULL)))`
- Exclusion `ex_supplier_price_no_overlap`: `EXCLUDE USING gist (supplier_part_id WITH =, valid_during WITH &&)`

**Indexes**

- `ex_supplier_price_no_overlap` — `gist (supplier_part_id, valid_during)`
- `pk_supplier_price` — `btree (id)`

**Triggers:** `trg_audit: audit.trg_row_audit`

### `procurement.v_purchase_order_overview` (view)

WF04: purchase order list with totals and approval state.

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `id` | `bigint` |  |  |  |
| `number` | `character varying(20)` |  |  |  |
| `supplier_code` | `character varying(20)` |  |  |  |
| `supplier_name` | `character varying(200)` |  |  |  |
| `warehouse_code` | `character varying(10)` |  |  |  |
| `status` | `procurement.po_status` |  |  |  |
| `is_urgent` | `boolean` |  |  |  |
| `total_amount` | `numeric(14,2)` |  |  |  |
| `currency_code` | `character(3)` |  |  |  |
| `line_count` | `bigint` |  |  |  |
| `received_qty` | `numeric` |  |  |  |
| `ordered_qty` | `numeric` |  |  |  |
| `created_by_user` | `citext` |  |  |  |
| `approved_by_user` | `citext` |  |  |  |
| `requires_four_eyes` | `boolean` |  |  |  |
| `created_at` | `timestamp with time zone` |  |  |  |
| `approved_at` | `timestamp with time zone` |  |  |  |
| `expected_arrival_date` | `date` |  |  |  |

## 8. Schema `forecast` — Forecasting (ML)

### `forecast.__ef_migrations_history` (table)

Rows (demo): **1**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `migration_id` | `character varying(150)` | PK, NN |  |  |
| `product_version` | `character varying(32)` | NN |  |  |

**Data quality expectations / integrity rules**

- Primary key `pk___ef_migrations_history`: `PRIMARY KEY (migration_id)`

**Indexes**

- `pk___ef_migrations_history` — `btree (migration_id)`

### `forecast.benchmark_result` (table)

Holdout accuracy of a candidate model and of the 5 benchmarks per demand class (KPI-02: ML must have >= 10% lower RMSSE).

Rows (demo): **48**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `model_version_id` | `integer` | PK, FK → `forecast.ml_model_version`, NN |  | Reference to forecast.ml_model_version. |
| `method` | `forecast.forecast_method` | PK, NN |  |  |
| `demand_class_code` | `character varying(15)` | PK, FK → `ref.demand_class`, NN |  | Reference to ref.demand_class. |
| `series_count` | `integer` | NN |  |  |
| `rmsse` | `numeric(8,4)` | NN |  |  |
| `mase` | `numeric(8,4)` | NN |  |  |

**Data quality expectations / integrity rules**

- Foreign key `fk_benchmark_result_class`: `FOREIGN KEY (demand_class_code) REFERENCES ref.demand_class(code)`
- Foreign key `fk_benchmark_result_model`: `FOREIGN KEY (model_version_id) REFERENCES forecast.ml_model_version(id) ON DELETE CASCADE`
- Primary key `pk_benchmark_result`: `PRIMARY KEY (model_version_id, method, demand_class_code)`
- Check `ck_benchmark_result_metrics`: `CHECK (((rmsse >= (0)::numeric) AND (mase >= (0)::numeric)))`
- Check `ck_benchmark_result_series`: `CHECK ((series_count > 0))`

**Indexes**

- `ix_benchmark_result_class` — `btree (demand_class_code)`
- `pk_benchmark_result` — `btree (model_version_id, method, demand_class_code)`

### `forecast.demand_forecast` (table)

Weekly forecast quantity per run, warehouse and part (ternary relationship run x warehouse x part, plus week).

Rows (demo): **115,152**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `forecast_run_id` | `bigint` | PK, FK → `forecast.forecast_run`, NN |  | Reference to forecast.forecast_run. |
| `warehouse_id` | `smallint` | PK, NN |  | Reference to inventory.warehouse_part. |
| `part_id` | `bigint` | PK, NN |  | Reference to inventory.warehouse_part. |
| `week_start` | `date` | PK, NN |  | Monday of the forecast ISO week. |
| `forecast_qty` | `numeric(12,3)` | NN |  |  |

**Data quality expectations / integrity rules**

- Foreign key `fk_demand_forecast_run`: `FOREIGN KEY (forecast_run_id) REFERENCES forecast.forecast_run(id) ON DELETE CASCADE`
- Primary key `pk_demand_forecast`: `PRIMARY KEY (forecast_run_id, warehouse_id, part_id, week_start)`
- Check `ck_demand_forecast_monday`: `CHECK ((EXTRACT(isodow FROM week_start) = (1)::numeric))`
- Check `ck_demand_forecast_qty`: `CHECK ((forecast_qty >= (0)::numeric))`

**Indexes**

- `ix_demand_forecast_part_wh_week` — `btree (part_id, warehouse_id, week_start)`
- `pk_demand_forecast` — `btree (forecast_run_id, warehouse_id, part_id, week_start)`

### `forecast.forecast_run` (table)

One execution of the forecasting pipeline (Quartz.NET weekly or user-triggered, horizon 4-12 weeks).

Rows (demo): **2**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `id` | `bigint` | PK, NN, ID | `identity` | Surrogate primary key. |
| `model_version_id` | `integer` | FK → `forecast.ml_model_version`, NN |  | Reference to forecast.ml_model_version. |
| `status` | `forecast.run_status` | NN | `'running'::forecast.run_status` | Workflow status (state machine guarded by trigger). |
| `horizon_weeks` | `smallint` | NN |  |  |
| `first_week` | `date` | NN |  |  |
| `triggered_by` | `character varying(10)` | NN |  |  |
| `requested_by` | `uuid` |  |  | User who created the record (iam.app_user). |
| `started_at` | `timestamp with time zone` | NN | `now()` | Time of the started event. |
| `finished_at` | `timestamp with time zone` |  |  | Time of the finished event. |
| `error_message` | `text` |  |  |  |

**Data quality expectations / integrity rules**

- Foreign key `fk_forecast_run_model`: `FOREIGN KEY (model_version_id) REFERENCES forecast.ml_model_version(id)`
- Primary key `pk_forecast_run`: `PRIMARY KEY (id)`
- Check `ck_forecast_run_error`: `CHECK (((status = 'failed'::forecast.run_status) = (error_message IS NOT NULL)))`
- Check `ck_forecast_run_finished`: `CHECK (((status = 'running'::forecast.run_status) = (finished_at IS NULL)))`
- Check `ck_forecast_run_first_week_monday`: `CHECK ((EXTRACT(isodow FROM first_week) = (1)::numeric))`
- Check `ck_forecast_run_horizon`: `CHECK (((horizon_weeks >= 4) AND (horizon_weeks <= 12)))`
- Check `ck_forecast_run_timeline`: `CHECK (((finished_at IS NULL) OR (finished_at >= started_at)))`
- Check `ck_forecast_run_trigger`: `CHECK (((triggered_by)::text = ANY ((ARRAY['scheduler'::character varying, 'user'::character varying])::text[])))`
- Check `ck_forecast_run_user`: `CHECK ((((triggered_by)::text = 'scheduler'::text) OR (requested_by IS NOT NULL)))`

**Indexes**

- `ix_forecast_run_latest` — `btree (started_at DESC) WHERE (status = 'succeeded'::forecast.run_status)`
- `ix_forecast_run_model` — `btree (model_version_id)`
- `ix_forecast_run_user` — `btree (requested_by)`
- `pk_forecast_run` — `btree (id)`

### `forecast.inbox_message` (table)

Integration events already handled by a consumer of this module (idempotent receiver, ADR 0003).

Rows (demo): **0**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `message_id` | `uuid` | PK, NN |  | Id of the handled event. |
| `consumer` | `character varying(300)` | PK, NN |  | Handler that consumed the event. |
| `processed_at` | `timestamp with time zone` | NN | `now()` | Time the event was handled. |

**Data quality expectations / integrity rules**

- Primary key `pk_inbox_message`: `PRIMARY KEY (message_id, consumer)`

**Indexes**

- `pk_inbox_message` — `btree (message_id, consumer)`

### `forecast.ml_model_version` (table)

Versioned ML / benchmark models. A candidate is promoted to active only if it beats the best benchmark (forecast.promote_model).

Rows (demo): **2**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `id` | `integer` | PK, NN, ID | `identity` | Surrogate primary key. |
| `algorithm` | `forecast.forecast_method` | NN |  |  |
| `version_label` | `character varying(30)` | NN |  |  |
| `demand_class_code` | `character varying(15)` | FK → `ref.demand_class` |  | Reference to ref.demand_class. |
| `status` | `forecast.model_status` | NN | `'training'::forecast.model_status` | Workflow status (state machine guarded by trigger). |
| `training_from` | `date` | NN |  |  |
| `training_to` | `date` | NN |  |  |
| `holdout_from` | `date` | NN |  |  |
| `holdout_to` | `date` | NN |  |  |
| `rmsse` | `numeric(8,4)` |  |  | Root mean squared scaled error on the holdout period (KPI-02 metric). |
| `mase` | `numeric(8,4)` |  |  | Mean absolute scaled error on the holdout period. |
| `mae` | `numeric(12,4)` |  |  |  |
| `rmse` | `numeric(12,4)` |  |  |  |
| `hyperparameters` | `jsonb` | NN | `'{}'::jsonb` | Algorithm-specific settings as a JSON object, e.g. {"numberOfTrees":200,"numberOfLeaves":31,"learningRate":0.1}. |
| `artifact_uri` | `character varying(500)` |  |  | Location of the serialised ML.NET model (.zip) in Azure Blob Storage. |
| `artifact_sha256` | `character(64)` |  |  |  |
| `trained_at` | `timestamp with time zone` |  |  | Time of the trained event. |
| `promoted_at` | `timestamp with time zone` |  |  | Time of the promoted event. |
| `retired_at` | `timestamp with time zone` |  |  | Time of the retired event. |
| `created_at` | `timestamp with time zone` | NN | `now()` | Time the record was created. |

**Data quality expectations / integrity rules**

- Foreign key `fk_ml_model_version_class`: `FOREIGN KEY (demand_class_code) REFERENCES ref.demand_class(code)`
- Primary key `pk_ml_model_version`: `PRIMARY KEY (id)`
- Unique `uq_ml_model_version_label`: `UNIQUE (algorithm, version_label)`
- Check `ck_ml_model_version_active`: `CHECK (((status <> 'active'::forecast.model_status) OR (promoted_at IS NOT NULL)))`
- Check `ck_ml_model_version_artifact`: `CHECK (((algorithm <> 'fasttree'::forecast.forecast_method) OR (status = 'training'::forecast.model_status) OR (artifact_uri IS NOT NULL)))`
- Check `ck_ml_model_version_chronological_holdout`: `CHECK (((holdout_from > training_to) AND (holdout_to >= holdout_from)))`
- Check `ck_ml_model_version_evaluated`: `CHECK (((status = ANY (ARRAY['training'::forecast.model_status, 'rejected'::forecast.model_status])) OR ((rmsse IS NOT NULL) AND (trained_at IS NOT NULL))))`
- Check `ck_ml_model_version_hyperparameters`: `CHECK ((jsonb_typeof(hyperparameters) = 'object'::text))`
- Check `ck_ml_model_version_label`: `CHECK (((version_label)::text ~ '^v\d+\.\d+(\.\d+)?$'::text))`
- Check `ck_ml_model_version_metrics`: `CHECK ((((rmsse IS NULL) OR (rmsse >= (0)::numeric)) AND ((mase IS NULL) OR (mase >= (0)::numeric)) AND ((mae IS NULL) OR (mae >= (0)::numeric)) AND ((rmse IS NULL) OR (rmse >= (0)::numeric))))`
- Check `ck_ml_model_version_sha`: `CHECK (((artifact_sha256 IS NULL) OR (artifact_sha256 ~ '^[0-9a-f]{64}$'::text)))`
- Check `ck_ml_model_version_training`: `CHECK ((training_to > training_from))`

**Indexes**

- `ix_ml_model_version_class` — `btree (demand_class_code)`
- `pk_ml_model_version` — `btree (id)`
- `uq_ml_model_version_label` — `btree (algorithm, version_label)`
- `uq_ml_model_version_one_active` — `btree (COALESCE(demand_class_code, '*'::character varying)) WHERE (status = 'active'::forecast.model_status)`

**Triggers:** `trg_audit: audit.trg_row_audit`, `trg_model_status_guard: forecast.trg_model_status_guard`

### `forecast.outbox_message` (table)

Integration events of this module waiting for delivery (transactional outbox, ADR 0003).

Rows (demo): **0**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `id` | `uuid` | PK, NN |  | Event id; receivers deduplicate by it. |
| `occurred_at` | `timestamp with time zone` | NN |  | Time the event occurred. |
| `type` | `character varying(300)` | NN |  | CLR type of the event (Namespace.Type, Assembly). |
| `payload` | `jsonb` | NN |  | Event serialized as a JSON object. |
| `processed_at` | `timestamp with time zone` |  |  | Time the event was delivered to every consumer; NULL = pending. |
| `attempts` | `integer` | NN | `0` | Failed delivery attempts so far. |
| `error` | `character varying(2000)` |  |  | Last delivery error, if any. |

**Data quality expectations / integrity rules**

- Primary key `pk_outbox_message`: `PRIMARY KEY (id)`
- Check `ck_outbox_message_attempts`: `CHECK ((attempts >= 0))`
- Check `ck_outbox_message_payload`: `CHECK ((jsonb_typeof(payload) = 'object'::text))`

**Indexes**

- `ix_outbox_message_pending` — `btree (occurred_at) WHERE (processed_at IS NULL)`
- `pk_outbox_message` — `btree (id)`

### `forecast.recommendation` (table)

Explainable transfer / purchase proposal. Stores every driver of the calculation (NFR08) and the human decision (KPI-04).

Rows (demo): **120**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `id` | `bigint` | PK, NN, ID | `identity` | Surrogate primary key. |
| `forecast_run_id` | `bigint` | FK → `forecast.forecast_run`, NN |  | Reference to forecast.forecast_run. |
| `kind` | `forecast.recommendation_kind` | NN |  |  |
| `warehouse_id` | `smallint` | NN |  | Reference to inventory.warehouse_part. |
| `part_id` | `bigint` | NN |  | Reference to inventory.warehouse_part. |
| `donor_warehouse_id` | `smallint` |  |  | Reference to inventory.warehouse. |
| `supplier_id` | `integer` |  |  | Reference to procurement.supplier. |
| `base_demand_qty` | `numeric(12,3)` | NN |  | NFR08 (a): ML forecast over the horizon. |
| `maintenance_demand_qty` | `numeric(12,3)` | NN |  | NFR08 (b): planned maintenance demand not yet reserved. |
| `lead_time_buffer_qty` | `numeric(12,3)` | NN |  | NFR08 (c): demand expected during the replenishment lead time. |
| `safety_stock_qty` | `numeric(12,3)` | NN |  | NFR08 (d): safety stock of the hub. |
| `available_qty` | `numeric(12,3)` | NN |  |  |
| `in_transit_qty` | `numeric(12,3)` | NN |  |  |
| `net_position_qty` | `numeric(12,3)` | NN |  | Available + in transit - (a + b + c + d); negative = shortage. |
| `recommended_qty` | `numeric(12,3)` | NN |  |  |
| `estimated_cost_eur` | `numeric(12,2)` | NN |  |  |
| `alternative_cost_eur` | `numeric(12,2)` |  |  | Cost of the alternative (e.g. emergency supplier order EUR 280 vs. transfer EUR 45, UC01 step 9). |
| `status` | `forecast.recommendation_status` | NN | `'open'::forecast.recommendation_status` | Workflow status (state machine guarded by trigger). |
| `final_qty` | `numeric(12,3)` |  |  | Quantity decided by the user (KPI-04: accepted when equal to recommended_qty). |
| `decided_by` | `uuid` |  |  | User who performed the step (iam.app_user). |
| `decided_at` | `timestamp with time zone` |  |  | Time of the decided event. |
| `decision_reason` | `character varying(300)` |  |  |  |
| `transfer_order_id` | `bigint` |  |  | Reference to inventory.transfer_order. |
| `purchase_order_id` | `bigint` |  |  | Reference to procurement.purchase_order. |
| `created_at` | `timestamp with time zone` | NN | `now()` | Time the record was created. |

**Data quality expectations / integrity rules**

- Foreign key `fk_recommendation_run`: `FOREIGN KEY (forecast_run_id) REFERENCES forecast.forecast_run(id) ON DELETE CASCADE`
- Primary key `pk_recommendation`: `PRIMARY KEY (id)`
- Check `ck_recommendation_accepted`: `CHECK (((status <> 'accepted'::forecast.recommendation_status) OR (final_qty = recommended_qty)))`
- Check `ck_recommendation_cost`: `CHECK (((estimated_cost_eur >= (0)::numeric) AND ((alternative_cost_eur IS NULL) OR (alternative_cost_eur >= (0)::numeric))))`
- Check `ck_recommendation_decided`: `CHECK (((status = ANY (ARRAY['open'::forecast.recommendation_status, 'expired'::forecast.recommendation_status])) = (decided_at IS NULL)))`
- Check `ck_recommendation_drivers`: `CHECK (((base_demand_qty >= (0)::numeric) AND (maintenance_demand_qty >= (0)::numeric) AND (lead_time_buffer_qty >= (0)::numeric) AND (safety_stock_qty >= (0)::numeric) AND (available_qty >= (0)::numeric) AND (in_transit_qty >= (0)::numeric)))`
- Check `ck_recommendation_final_qty`: `CHECK (((status <> ALL (ARRAY['accepted'::forecast.recommendation_status, 'overridden'::forecast.recommendation_status])) OR ((final_qty > (0)::numeric) AND (decided_by IS NOT NULL))))`
- Check `ck_recommendation_kind_purchase`: `CHECK (((kind <> 'purchase'::forecast.recommendation_kind) OR ((supplier_id IS NOT NULL) AND (donor_warehouse_id IS NULL))))`
- Check `ck_recommendation_kind_transfer`: `CHECK (((kind <> 'transfer'::forecast.recommendation_kind) OR ((donor_warehouse_id IS NOT NULL) AND (supplier_id IS NULL) AND (donor_warehouse_id <> warehouse_id))))`
- Check `ck_recommendation_net_position`: `CHECK ((net_position_qty = ((available_qty + in_transit_qty) - (((base_demand_qty + maintenance_demand_qty) + lead_time_buffer_qty) + safety_stock_qty))))`
- Check `ck_recommendation_override`: `CHECK (((status <> ALL (ARRAY['overridden'::forecast.recommendation_status, 'rejected'::forecast.recommendation_status])) OR (decision_reason IS NOT NULL)))`
- Check `ck_recommendation_override_qty`: `CHECK (((status <> 'overridden'::forecast.recommendation_status) OR (final_qty <> recommended_qty)))`
- Check `ck_recommendation_qty`: `CHECK ((recommended_qty > (0)::numeric))`

**Indexes**

- `ix_recommendation_decided_by` — `btree (decided_by)`
- `ix_recommendation_donor` — `btree (donor_warehouse_id) WHERE (donor_warehouse_id IS NOT NULL)`
- `ix_recommendation_open` — `btree (kind, created_at) WHERE (status = 'open'::forecast.recommendation_status)`
- `ix_recommendation_po` — `btree (purchase_order_id) WHERE (purchase_order_id IS NOT NULL)`
- `ix_recommendation_run` — `btree (forecast_run_id)`
- `ix_recommendation_supplier` — `btree (supplier_id) WHERE (supplier_id IS NOT NULL)`
- `ix_recommendation_transfer` — `btree (transfer_order_id) WHERE (transfer_order_id IS NOT NULL)`
- `ix_recommendation_wh_part` — `btree (warehouse_id, part_id)`
- `pk_recommendation` — `btree (id)`

**Triggers:** `trg_audit: audit.trg_row_audit`

### `forecast.v_recommendation_explained` (view)

NFR08: decomposition (a) base demand, (b) maintenance, (c) lead-time buffer, (d) safety stock, stock position and economics of each proposal.

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `id` | `bigint` |  |  |  |
| `kind` | `forecast.recommendation_kind` |  |  |  |
| `status` | `forecast.recommendation_status` |  |  |  |
| `created_at` | `timestamp with time zone` |  |  |  |
| `oem_part_number` | `character varying(40)` |  |  |  |
| `description` | `character varying(300)` |  |  |  |
| `brand_code` | `character varying(10)` |  |  |  |
| `warehouse_code` | `character varying(10)` |  |  |  |
| `donor_warehouse_code` | `character varying(10)` |  |  |  |
| `supplier_name` | `character varying(200)` |  |  |  |
| `a_base_demand` | `numeric(12,3)` |  |  |  |
| `b_maintenance_demand` | `numeric(12,3)` |  |  |  |
| `c_lead_time_buffer` | `numeric(12,3)` |  |  |  |
| `d_safety_stock` | `numeric(12,3)` |  |  |  |
| `total_requirement` | `numeric` |  |  |  |
| `available_qty` | `numeric(12,3)` |  |  |  |
| `in_transit_qty` | `numeric(12,3)` |  |  |  |
| `net_position_qty` | `numeric(12,3)` |  |  |  |
| `recommended_qty` | `numeric(12,3)` |  |  |  |
| `a_share_pct` | `numeric` |  |  |  |
| `b_share_pct` | `numeric` |  |  |  |
| `estimated_cost_eur` | `numeric(12,2)` |  |  |  |
| `alternative_cost_eur` | `numeric(12,2)` |  |  |  |
| `saving_vs_alternative_eur` | `numeric` |  |  |  |
| `final_qty` | `numeric(12,3)` |  |  |  |
| `decision_reason` | `character varying(300)` |  |  |  |
| `decided_by_user` | `citext` |  |  |  |
| `decided_at` | `timestamp with time zone` |  |  |  |
| `algorithm` | `forecast.forecast_method` |  |  |  |
| `model_version` | `character varying(30)` |  |  |  |

## 9. Schema `audit` — Platform — audit

### `audit.audit_log` (partitioned table)

Immutable audit trail of state-changing operations: timestamp, user, action, previous and new values, reason (US18).

Rows (demo): **26,955** · Partitioning: `RANGE (occurred_at)`, 49 partitions

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `id` | `bigint` | PK, NN, ID | `identity` |  |
| `occurred_at` | `timestamp with time zone` | PK, NN | `clock_timestamp()` | Time of the occurred event. |
| `user_id` | `uuid` |  |  | Application user (GUC app.user_id); NULL for background jobs. |
| `db_role` | `name` | NN | `SESSION_USER` |  |
| `action` | `character varying(10)` | NN |  |  |
| `table_name` | `character varying(80)` | NN |  |  |
| `record_id` | `character varying(80)` | NN |  |  |
| `old_values` | `jsonb` |  |  | Previous values of the changed columns only (personal data columns are excluded). |
| `new_values` | `jsonb` |  |  |  |
| `changed_columns` | `text[]` |  |  |  |
| `reason` | `character varying(300)` |  |  | Business justification (GUC app.reason), e.g. reason for a manual override. |
| `transaction_id` | `bigint` | NN | `txid_current()` |  |

**Data quality expectations / integrity rules**

- Primary key `pk_audit_log`: `PRIMARY KEY (id, occurred_at)`
- Check `ck_audit_log_action`: `CHECK (((action)::text = ANY ((ARRAY['INSERT'::character varying, 'UPDATE'::character varying, 'DELETE'::character varying])::text[])))`
- Check `ck_audit_log_new`: `CHECK (((new_values IS NULL) OR (jsonb_typeof(new_values) = 'object'::text)))`
- Check `ck_audit_log_old`: `CHECK (((old_values IS NULL) OR (jsonb_typeof(old_values) = 'object'::text)))`
- Check `ck_audit_log_shape`: `CHECK (((((action)::text = 'INSERT'::text) AND (old_values IS NULL) AND (new_values IS NOT NULL)) OR (((action)::text = 'UPDATE'::text) AND (old_values IS NOT NULL) AND (new_values IS NOT NULL)) OR (((action)::text = 'DELETE'::text) AND (old_values IS NOT NULL) AND (new_values IS NULL))))`

**Indexes**

- `ix_audit_log_occurred_brin` — `brin (occurred_at)`
- `ix_audit_log_record` — `btree (table_name, record_id, occurred_at DESC)`
- `ix_audit_log_user` — `btree (user_id, occurred_at DESC) WHERE (user_id IS NOT NULL)`
- `pk_audit_log` — `btree (id, occurred_at)`

**Triggers:** `trg_audit_log_immutable: util.trg_forbid_modification`

### `audit.v_audit_log_masked` (view)

Audit trail for the sponsor / auditors with the actor's name masked.

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `id` | `bigint` |  |  |  |
| `occurred_at` | `timestamp with time zone` |  |  |  |
| `action` | `character varying(10)` |  |  |  |
| `table_name` | `character varying(80)` |  |  |  |
| `record_id` | `character varying(80)` |  |  |  |
| `changed_columns` | `text[]` |  |  |  |
| `old_values` | `jsonb` |  |  |  |
| `new_values` | `jsonb` |  |  |  |
| `reason` | `character varying(300)` |  |  |  |
| `user_name` | `citext` |  |  |  |
| `actor_masked` | `text` |  |  |  |
| `db_role` | `name` |  |  |  |

## 10. Schema `raw` — Inventory — import layer 1 (raw)

### `raw.demand_history_row` (table)

Raw layer: historical demand (ERP sales/service issues) CSV rows as text.

Rows (demo): **186,816**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `import_batch_id` | `bigint` | PK, FK → `raw.import_batch`, NN |  | Reference to raw.import_batch. |
| `line_no` | `integer` | PK, NN |  | Line number in the uploaded file. |
| `doc_date` | `text` |  |  |  |
| `warehouse_code` | `text` |  |  |  |
| `brand_code` | `text` |  |  |  |
| `oem_part_number` | `text` |  |  |  |
| `quantity` | `text` |  |  | Quantity in the unit of measure of the part. |
| `doc_ref` | `text` |  |  |  |

**Data quality expectations / integrity rules**

- Foreign key `fk_demand_history_row_batch`: `FOREIGN KEY (import_batch_id) REFERENCES raw.import_batch(id) ON DELETE CASCADE`
- Primary key `pk_demand_history_row`: `PRIMARY KEY (import_batch_id, line_no)`
- Check `ck_demand_history_row_line`: `CHECK ((line_no > 0))`

**Indexes**

- `pk_demand_history_row` — `btree (import_batch_id, line_no)`

### `raw.import_batch` (table)

One uploaded CSV file: source, SHA-256 (duplicate-file protection), processing status and row counts (load monitoring).

Rows (demo): **4**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `id` | `bigint` | PK, NN, ID | `identity` | Surrogate primary key. |
| `source` | `raw.import_source` | NN |  |  |
| `file_name` | `character varying(255)` | NN |  |  |
| `file_sha256` | `character(64)` | NN |  | SHA-256 of the file content; the same file cannot be imported twice. |
| `status` | `raw.import_status` | NN | `'uploaded'::raw.import_status` | Workflow status (state machine guarded by trigger). |
| `total_rows` | `integer` | NN | `0` |  |
| `valid_rows` | `integer` |  |  |  |
| `error_rows` | `integer` |  |  |  |
| `loaded_rows` | `integer` |  |  |  |
| `uploaded_by` | `uuid` |  |  | User who created the record (iam.app_user). |
| `uploaded_at` | `timestamp with time zone` | NN | `now()` | Time of the uploaded event. |
| `processed_at` | `timestamp with time zone` |  |  | Time of the processed event. |
| `message` | `text` |  |  |  |

**Data quality expectations / integrity rules**

- Primary key `pk_import_batch`: `PRIMARY KEY (id)`
- Unique `uq_import_batch_file`: `UNIQUE (source, file_sha256)`
- Check `ck_import_batch_counts`: `CHECK (((total_rows >= 0) AND ((COALESCE(valid_rows, 0) + COALESCE(error_rows, 0)) <= total_rows) AND (COALESCE(loaded_rows, 0) <= COALESCE(valid_rows, total_rows))))`
- Check `ck_import_batch_processed`: `CHECK (((status = 'uploaded'::raw.import_status) OR (processed_at IS NOT NULL)))`
- Check `ck_import_batch_sha`: `CHECK ((file_sha256 ~ '^[0-9a-f]{64}$'::text))`

**Indexes**

- `ix_import_batch_status` — `btree (status, uploaded_at DESC)`
- `ix_import_batch_user` — `btree (uploaded_by)`
- `pk_import_batch` — `btree (id)`
- `uq_import_batch_file` — `btree (source, file_sha256)`

### `raw.part_catalog_row` (table)

Raw layer: part catalogue CSV rows as text (columns: brand, oem_part_number, description, category, uom, weight_kg, unit_cost, condition_based).

Rows (demo): **2,070**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `import_batch_id` | `bigint` | PK, FK → `raw.import_batch`, NN |  | Reference to raw.import_batch. |
| `line_no` | `integer` | PK, NN |  | Line number in the uploaded file. |
| `brand_code` | `text` |  |  |  |
| `oem_part_number` | `text` |  |  |  |
| `description` | `text` |  |  | Description. |
| `category_code` | `text` |  |  |  |
| `uom_code` | `text` |  |  |  |
| `weight_kg` | `text` |  |  |  |
| `unit_cost` | `text` |  |  |  |
| `condition_based` | `text` |  |  |  |

**Data quality expectations / integrity rules**

- Foreign key `fk_part_catalog_row_batch`: `FOREIGN KEY (import_batch_id) REFERENCES raw.import_batch(id) ON DELETE CASCADE`
- Primary key `pk_part_catalog_row`: `PRIMARY KEY (import_batch_id, line_no)`
- Check `ck_part_catalog_row_line`: `CHECK ((line_no > 0))`

**Indexes**

- `pk_part_catalog_row` — `btree (import_batch_id, line_no)`

## 11. Schema `staging` — Inventory — import layer 2 (staging)

### `staging.demand_history` (table)

Staging layer: typed historical demand rows; duplicates (same row_hash as an earlier loaded row) are flagged, not loaded.

Rows (demo): **186,816**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `import_batch_id` | `bigint` | PK, FK → `raw.demand_history_row`, NN |  | Reference to raw.demand_history_row. |
| `line_no` | `integer` | PK, FK → `raw.demand_history_row`, NN |  | Reference to raw.demand_history_row. |
| `occurred_at` | `timestamp with time zone` |  |  | Time of the occurred event. |
| `warehouse_id` | `smallint` |  |  |  |
| `part_id` | `bigint` |  |  |  |
| `quantity` | `numeric(12,3)` |  |  | Quantity in the unit of measure of the part. |
| `doc_ref` | `character varying(40)` |  |  |  |
| `row_hash` | `bytea` |  |  | SHA-256 of the normalised business key (date\|hub\|part\|doc_ref\|qty) for deduplication. |
| `is_valid` | `boolean` | NN |  | Flag: valid. |
| `is_duplicate` | `boolean` | NN | `false` | TRUE if the row was already loaded by this or an earlier batch. |

**Data quality expectations / integrity rules**

- Foreign key `fk_staging_demand_history_raw`: `FOREIGN KEY (import_batch_id, line_no) REFERENCES raw.demand_history_row(import_batch_id, line_no) ON DELETE CASCADE`
- Primary key `pk_staging_demand_history`: `PRIMARY KEY (import_batch_id, line_no)`
- Check `ck_staging_demand_history_valid`: `CHECK (((NOT is_valid) OR ((occurred_at IS NOT NULL) AND (warehouse_id IS NOT NULL) AND (part_id IS NOT NULL) AND (quantity > (0)::numeric) AND (row_hash IS NOT NULL))))`

**Indexes**

- `ix_staging_demand_history_hash` — `btree (row_hash) WHERE (is_valid AND (NOT is_duplicate))`
- `pk_staging_demand_history` — `btree (import_batch_id, line_no)`

### `staging.import_error` (table)

Data-quality errors found while validating raw rows (shown to the user after an upload).

Rows (demo): **163**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `id` | `bigint` | PK, NN, ID | `identity` | Surrogate primary key. |
| `import_batch_id` | `bigint` | FK → `raw.import_batch`, NN |  | Reference to raw.import_batch. |
| `line_no` | `integer` | NN |  | Line number in the uploaded file. |
| `column_name` | `character varying(40)` | NN |  |  |
| `error_code` | `character varying(30)` | NN |  |  |
| `message` | `character varying(300)` | NN |  |  |

**Data quality expectations / integrity rules**

- Foreign key `fk_import_error_batch`: `FOREIGN KEY (import_batch_id) REFERENCES raw.import_batch(id) ON DELETE CASCADE`
- Primary key `pk_import_error`: `PRIMARY KEY (id)`
- Check `ck_import_error_code`: `CHECK (((error_code)::text = ANY ((ARRAY['REQUIRED'::character varying, 'TYPE'::character varying, 'RANGE'::character varying, 'FORMAT'::character varying, 'UNKNOWN_REF'::character varying, 'DUPLICATE'::character varying])::text[])))`

**Indexes**

- `ix_import_error_batch` — `btree (import_batch_id, line_no)`
- `pk_import_error` — `btree (id)`

### `staging.part_catalog` (table)

Staging layer: typed and validated part catalogue rows (valid rows are upserted into catalog.part).

Rows (demo): **2,070**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `import_batch_id` | `bigint` | PK, FK → `raw.part_catalog_row`, NN |  | Reference to raw.part_catalog_row. |
| `line_no` | `integer` | PK, FK → `raw.part_catalog_row`, NN |  | Reference to raw.part_catalog_row. |
| `brand_id` | `smallint` |  |  |  |
| `oem_part_number` | `character varying(40)` |  |  |  |
| `description` | `character varying(300)` |  |  | Description. |
| `category_id` | `integer` |  |  |  |
| `uom_id` | `smallint` |  |  |  |
| `weight_kg` | `numeric(9,3)` |  |  |  |
| `unit_cost` | `numeric(12,2)` |  |  |  |
| `is_condition_based` | `boolean` |  |  | Flag: condition based. |
| `is_valid` | `boolean` | NN |  | Flag: valid. |

**Data quality expectations / integrity rules**

- Foreign key `fk_staging_part_catalog_raw`: `FOREIGN KEY (import_batch_id, line_no) REFERENCES raw.part_catalog_row(import_batch_id, line_no) ON DELETE CASCADE`
- Primary key `pk_staging_part_catalog`: `PRIMARY KEY (import_batch_id, line_no)`
- Check `ck_staging_part_catalog_valid`: `CHECK (((NOT is_valid) OR ((brand_id IS NOT NULL) AND (oem_part_number IS NOT NULL) AND (description IS NOT NULL) AND (category_id IS NOT NULL) AND (uom_id IS NOT NULL) AND (unit_cost IS NOT NULL))))`

**Indexes**

- `pk_staging_part_catalog` — `btree (import_batch_id, line_no)`

## 12. Schema `mart` — Reporting — data mart

### `mart.__ef_migrations_history` (table)

Rows (demo): **2**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `migration_id` | `character varying(150)` | PK, NN |  |  |
| `product_version` | `character varying(32)` | NN |  |  |

**Data quality expectations / integrity rules**

- Primary key `pk___ef_migrations_history`: `PRIMARY KEY (migration_id)`

**Indexes**

- `pk___ef_migrations_history` — `btree (migration_id)`

### `mart.mv_demand_profile` (materialized view)

Data mart: ADI / CV² demand classification per series (intermittent, lumpy, smooth, erratic).

Rows (demo): **7,197**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `warehouse_id` | `smallint` |  |  |  |
| `part_id` | `bigint` |  |  |  |
| `first_week` | `date` |  |  |  |
| `last_week` | `date` |  |  |  |
| `last_demand_week` | `date` |  |  |  |
| `periods` | `integer` |  |  |  |
| `nonzero_weeks` | `bigint` |  |  |  |
| `total_qty` | `numeric` |  |  |  |
| `adi` | `numeric` |  |  |  |
| `cv2` | `numeric` |  |  |  |
| `demand_class_code` | `character varying` |  |  |  |
| `computed_at` | `timestamp with time zone` |  |  |  |

**Indexes**

- `ix_mv_demand_profile_class` — `btree (demand_class_code)`
- `uq_mv_demand_profile` — `btree (warehouse_id, part_id)`

### `mart.mv_weekly_demand` (materialized view)

Data mart: weekly demand (only non-zero weeks) per warehouse and part; source for ML training and accuracy.

Rows (demo): **189,670**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `week_start` | `date` |  |  |  |
| `warehouse_id` | `smallint` |  |  |  |
| `part_id` | `bigint` |  |  |  |
| `demand_qty` | `numeric` |  |  |  |
| `demand_lines` | `bigint` |  |  |  |
| `service_qty` | `numeric` |  |  |  |

**Indexes**

- `ix_mv_weekly_demand_week` — `btree (week_start)`
- `uq_mv_weekly_demand` — `btree (warehouse_id, part_id, week_start)`

### `mart.refresh_log` (table)

Monitoring of data mart refreshes (duration, row count, failures).

Rows (demo): **4**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `id` | `bigint` | PK, NN, ID | `identity` | Surrogate primary key. |
| `view_name` | `character varying(80)` | NN |  |  |
| `started_at` | `timestamp with time zone` | NN |  | Time of the started event. |
| `finished_at` | `timestamp with time zone` | NN |  | Time of the finished event. |
| `row_count` | `bigint` |  |  |  |
| `status` | `character varying(10)` | NN |  | Workflow status (state machine guarded by trigger). |
| `message` | `text` |  |  |  |

**Data quality expectations / integrity rules**

- Primary key `pk_refresh_log`: `PRIMARY KEY (id)`
- Check `ck_refresh_log_status`: `CHECK (((status)::text = ANY ((ARRAY['ok'::character varying, 'failed'::character varying])::text[])))`
- Check `ck_refresh_log_time`: `CHECK ((finished_at >= started_at))`

**Indexes**

- `ix_refresh_log_view_time` — `btree (view_name, started_at DESC)`
- `pk_refresh_log` — `btree (id)`

### `mart.v_data_quality` (view)

Automated reconciliation checks (ledger vs. balance, reservations, PO totals, odometer, duplicates). Expected: all failed_rows = 0.

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `check_name` | `text` |  |  |  |
| `failed_rows` | `bigint` |  |  |  |

### `mart.v_forecast_accuracy` (view)

Forecast vs. actual per run and demand class (MAE, RMSE, bias) for completed weeks.

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `forecast_run_id` | `bigint` |  |  |  |
| `demand_class_code` | `character varying` |  |  |  |
| `points` | `bigint` |  |  |  |
| `mae` | `numeric` |  |  |  |
| `rmse` | `numeric` |  |  |  |
| `bias` | `numeric` |  |  |  |

### `mart.v_import_monitor` (view)

Load report of every CSV import (row counts, error rate, duration).

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `id` | `bigint` |  |  |  |
| `source` | `raw.import_source` |  |  |  |
| `file_name` | `character varying(255)` |  |  |  |
| `status` | `raw.import_status` |  |  |  |
| `total_rows` | `integer` |  |  |  |
| `valid_rows` | `integer` |  |  |  |
| `error_rows` | `integer` |  |  |  |
| `loaded_rows` | `integer` |  |  |  |
| `error_rate_pct` | `numeric` |  |  |  |
| `uploaded_at` | `timestamp with time zone` |  |  |  |
| `processed_at` | `timestamp with time zone` |  |  |  |
| `processing_time` | `interval` |  |  |  |
| `message` | `text` |  |  |  |

### `mart.v_kpi_dead_stock` (view)

KPI-05: value share of stock without any demand in 12 months, per hub and for the network (target <= 10 %).

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `warehouse_code` | `character varying` |  |  |  |
| `stock_value_eur` | `numeric` |  |  |  |
| `dead_stock_value_eur` | `numeric` |  |  |  |
| `dead_stock_ratio_pct` | `numeric` |  |  |  |
| `target_pct` | `numeric` |  |  |  |

### `mart.v_kpi_recommendation_acceptance` (view)

KPI-04: share of recommendations accepted without change (target >= 80 %); ROLLUP gives subtotals.

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `month` | `date` |  |  |  |
| `kind` | `forecast.recommendation_kind` |  |  |  |
| `decided` | `bigint` |  |  |  |
| `accepted` | `bigint` |  |  |  |
| `overridden` | `bigint` |  |  |  |
| `rejected` | `bigint` |  |  |  |
| `acceptance_rate_pct` | `numeric` |  |  |  |
| `target_pct` | `numeric` |  |  |  |

### `mart.v_ml_training_set` (view)

ML.NET training/inference features: dense weekly series with season, lags (1,2,4,52) and rolling means (window functions).

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `warehouse_id` | `smallint` |  |  |  |
| `part_id` | `bigint` |  |  |  |
| `week_start` | `date` |  |  |  |
| `iso_week` | `smallint` |  |  |  |
| `season_phase_code` | `character varying(20)` |  |  |  |
| `seasonal_weight` | `numeric(4,2)` |  |  |  |
| `brand_id` | `smallint` |  |  |  |
| `category_id` | `integer` |  |  |  |
| `demand_class_code` | `character varying` |  |  |  |
| `demand_qty` | `numeric` |  |  |  |
| `lag_1` | `numeric` |  |  |  |
| `lag_2` | `numeric` |  |  |  |
| `lag_4` | `numeric` |  |  |  |
| `lag_52` | `numeric` |  |  |  |
| `rolling_mean_4` | `numeric` |  |  |  |
| `rolling_mean_13` | `numeric` |  |  |  |
| `nonzero_weeks_13` | `bigint` |  |  |  |

## 13. Schema `util` — Platform — technical utilities

### `util.__ef_migrations_history` (table)

Rows (demo): **4**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `migration_id` | `character varying(150)` | PK, NN |  |  |
| `product_version` | `character varying(32)` | NN |  |  |

**Data quality expectations / integrity rules**

- Primary key `pk___ef_migrations_history`: `PRIMARY KEY (migration_id)`

**Indexes**

- `pk___ef_migrations_history` — `btree (migration_id)`

### `util.document_counter` (table)

Last issued business document number per prefix and year (gap-less numbering).

Rows (demo): **3**

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `prefix` | `character varying(4)` | PK, NN |  |  |
| `year` | `smallint` | PK, NN |  |  |
| `last_value` | `integer` | NN | `0` |  |

**Data quality expectations / integrity rules**

- Primary key `pk_document_counter`: `PRIMARY KEY (prefix, year)`
- Check `document_counter_last_value_check`: `CHECK ((last_value >= 0))`
- Check `document_counter_prefix_check`: `CHECK (((prefix)::text ~ '^[A-Z]{2,4}$'::text))`
- Check `document_counter_year_check`: `CHECK (((year >= 2000) AND (year <= 2100)))`

**Indexes**

- `pk_document_counter` — `btree (prefix, year)`

### `util.v_privilege_findings` (view)

Security self-check: must return zero rows. Executed by the test suite after every migration.

| Column | Type | Keys / flags | Default | Description |
|---|---|---|---|---|
| `finding` | `text` |  |  |  |
| `object_name` | `text` |  |  |  |

## 14. Functions, procedures and trigger functions

| Routine | Kind | Security | Returns | Description |
|---|---|---|---|---|
| `iam.anonymise_user(uuid)` | function (plpgsql) | DEFINER | `void` | Irreversibly replaces personal data of a user; the row (and audit references) remain. |
| `iam.user_has_role(uuid,text[])` | function (sql) | invoker | `boolean` | TRUE if the active user has at least one of the given role codes. |
| `inventory.approve_transfer(bigint)` | function (plpgsql) | DEFINER | `void` | UC01 step 10-11: approves a proposed transfer and places firm reservations at the donor (atomic, all lines or none). |
| `inventory.cancel_transfer(bigint,text)` | function (plpgsql) | DEFINER | `void` |  |
| `inventory.dispatch_transfer(bigint)` | function (plpgsql) | DEFINER | `void` | US11: consumes the firm reservations, posts TRANSFER_OUT movements, status -> dispatched (stock is now in transit). |
| `inventory.expire_soft_reservations(integer)` | procedure (plpgsql) | invoker | `procedure` | Expires overdue soft reservations and returns affected service orders to draft. |
| `inventory.find_exceptional_demand_candidates(numeric,interval,integer)` | function (sql) | invoker | `TABLE(movement_id bigint, occurred_at timestamp with time zone, warehouse_id smallint, part_id bigint, quantity numeric, mean_qty numeric, stddev_qty numeric, z_score numeric)` | US08: demand lines above mean + 3 sigma of the SKU (window functions), not yet flagged. |
| `inventory.flag_exceptional_demand(bigint,timestamp with time zone,text,text)` | function (plpgsql) | DEFINER | `void` |  |
| `inventory.fn_in_transit_qty(smallint,bigint)` | function (sql) | invoker | `numeric` | Inbound quantity: dispatched transfers + not yet received approved purchase orders. |
| `inventory.receive_transfer(bigint)` | function (plpgsql) | DEFINER | `void` | US11: posts TRANSFER_IN movements at the recipient hub, status -> received. |
| `inventory.trg_demand_exception_validate()` | trigger function (plpgsql) | invoker | `trigger` |  |
| `inventory.trg_stock_movement_apply()` | trigger function (plpgsql) | DEFINER | `trigger` | AFTER INSERT on the ledger: applies +/- quantity to stock_balance with a row lock; rejects issues exceeding available stock (SS200). |
| `inventory.trg_stock_reservation_guard()` | trigger function (plpgsql) | invoker | `trigger` |  |
| `inventory.trg_stock_reservation_sync()` | trigger function (plpgsql) | DEFINER | `trigger` | Keeps stock_balance.reserved_qty equal to the sum of active (soft/firm) reservations. |
| `inventory.trg_transfer_line_editable()` | trigger function (plpgsql) | invoker | `trigger` |  |
| `inventory.trg_transfer_order_status_guard()` | trigger function (plpgsql) | invoker | `trigger` |  |
| `fleet.cancel_service_order(bigint,text)` | function (plpgsql) | DEFINER | `void` |  |
| `fleet.complete_service_order(bigint,numeric)` | function (plpgsql) | DEFINER | `void` | Consumes the kit reservations, issues parts (ISSUE_SERVICE = demand) and closes the order. |
| `fleet.create_service_order_draft(bigint,integer,numeric)` | function (plpgsql) | DEFINER | `bigint` |  |
| `fleet.fn_maintenance_due(bigint,numeric)` | function (sql) | invoker | `TABLE(maintenance_plan_id integer, plan_name character varying, interval_hours integer, effective_interval numeric, due_engine_hours numeric, current_engine_hours numeric, remaining_hours numeric, is_overdue boolean)` | Planned services of a machine due within the horizon (effective interval = OEM interval x condition factor). |
| `fleet.log_engine_hours(bigint,numeric,timestamp with time zone,fleet.reading_source)` | function (plpgsql) | DEFINER | `TABLE(service_order_id bigint, service_order_number character varying, due_engine_hours numeric, fully_reserved boolean)` | UC02: validates and stores a reading, then drafts service orders with soft-reserved kits for every plan due within 50 h. Atomic. |
| `fleet.trg_engine_hours_apply()` | trigger function (plpgsql) | DEFINER | `trigger` |  |
| `fleet.trg_engine_hours_validate()` | trigger function (plpgsql) | invoker | `trigger` |  |
| `fleet.trg_service_order_status_guard()` | trigger function (plpgsql) | invoker | `trigger` |  |
| `procurement.approve_purchase_order(bigint)` | function (plpgsql) | DEFINER | `void` | US14: approves a submitted PO; > EUR 5,000 requires an approver other than the author (also a CHECK constraint). |
| `procurement.cancel_purchase_order(bigint,text)` | function (plpgsql) | DEFINER | `void` |  |
| `procurement.export_purchase_order(bigint)` | function (plpgsql) | DEFINER | `void` |  |
| `procurement.receive_purchase_order_line(bigint,numeric)` | function (plpgsql) | DEFINER | `void` |  |
| `procurement.submit_purchase_order(bigint)` | function (plpgsql) | DEFINER | `void` |  |
| `procurement.trg_po_line_total()` | trigger function (plpgsql) | DEFINER | `trigger` |  |
| `procurement.trg_po_line_validate()` | trigger function (plpgsql) | invoker | `trigger` |  |
| `procurement.trg_po_status_guard()` | trigger function (plpgsql) | invoker | `trigger` |  |
| `forecast.decide_recommendation(bigint,text,numeric,text)` | function (plpgsql) | DEFINER | `bigint` | Accept / override (with reason) / reject a recommendation; creates and approves a transfer or adds a line to a consolidated draft PO, in one transaction. |
| `forecast.expire_open_recommendations(interval)` | procedure (sql) | invoker | `procedure` |  |
| `forecast.fn_classify_demand(numeric,numeric)` | function (sql) | invoker | `character varying` | Syntetos-Boylan class from ADI and CV² using the ranges in ref.demand_class. |
| `forecast.fn_stock_position(smallint,bigint,bigint,numeric)` | function (sql) | invoker | `TABLE(available_qty numeric, in_transit_qty numeric, base_demand_qty numeric, maintenance_demand_qty numeric, lead_time_buffer_qty numeric, safety_stock_qty numeric, net_position_qty numeric)` | Net = Available + InTransit - (ML forecast + unreserved maintenance demand + lead-time buffer + safety stock). Used by the recommendation engine. |
| `forecast.promote_model(integer,numeric)` | function (plpgsql) | DEFINER | `boolean` | Promotes a candidate only if its weighted RMSSE is >= 10 % lower than the best of the 5 benchmarks; retires the previous active model atomically. |
| `forecast.trg_model_status_guard()` | trigger function (plpgsql) | invoker | `trigger` |  |
| `audit.purge_expired(interval)` | procedure (plpgsql) | invoker | `procedure` | Drops audit partitions older than the retention period (default 180 days). Run monthly by Quartz.NET. |
| `audit.trg_row_audit()` | trigger function (plpgsql) | DEFINER | `trigger` | AFTER ROW trigger writing changed columns into audit.audit_log; record_id = id column or the *_id columns of a composite key; arguments = excluded columns (PII, secrets). |
| `staging.load_demand_history(bigint)` | function (plpgsql) | invoker | `integer` |  |
| `staging.load_part_catalog(bigint)` | function (plpgsql) | invoker | `integer` |  |
| `staging.process_batch(bigint,numeric)` | function (plpgsql) | DEFINER | `raw.import_batch` | Validates a raw batch into staging and loads core tables if the share of invalid rows <= threshold (default 5 %). Returns the load report. |
| `staging.validate_demand_history(bigint)` | function (plpgsql) | invoker | `integer` |  |
| `staging.validate_part_catalog(bigint)` | function (plpgsql) | invoker | `integer` |  |
| `mart.refresh_all()` | procedure (plpgsql) | DEFINER | `procedure` | Refreshes the data marts (concurrently once populated) and logs duration/rows/failures. Nightly Quartz.NET job and after imports. |
| `util.current_app_roles()` | function (sql) | invoker | `text[]` | Role codes of the application user of the current transaction (GUC app.user_roles set by the EF Core interceptor); empty for system jobs. |
| `util.current_app_user()` | function (sql) | invoker | `uuid` | Application user of the current transaction (GUC app.user_id set by the EF Core interceptor); NULL for system jobs. |
| `util.current_home_warehouse_id()` | function (sql) | invoker | `smallint` | Home warehouse of the application user of the current transaction (GUC app.home_warehouse_id set by the EF Core interceptor); NULL if the user has none. |
| `util.ensure_monthly_partitions(regclass,date,integer)` | function (plpgsql) | invoker | `integer` | Creates missing monthly RANGE partitions of a partitioned table; returns the number created. |
| `util.mask_email(text)` | function (sql) | invoker | `text` | Masks an e-mail address for display to roles without access to personal data. |
| `util.mask_name(text)` | function (sql) | invoker | `text` | Masks a full name to initials. |
| `util.mask_phone(text)` | function (sql) | invoker | `text` | Masks a phone number, keeping the country code and last 3 digits. |
| `util.next_document_number(text,timestamp with time zone)` | function (plpgsql) | invoker | `text` | Returns the next gap-less document number, e.g. SO-2026-000412. Runs inside the caller's transaction, so a rollback releases nothing and no gap appears. |
| `util.pseudonymise(text)` | function (plpgsql) | invoker | `bytea` | Keyed HMAC-SHA256 pseudonym of an external identifier (GDPR Art. 4(5)). Key supplied per session via app.pseudonym_key. |
| `util.require_app_user()` | function (plpgsql) | invoker | `uuid` |  |
| `util.require_role(text[])` | function (plpgsql) | invoker | `uuid` | Returns the session user if the session roles (app.user_roles) include one of the roles, otherwise raises SS301 (defence in depth behind API authorisation). |
| `util.trg_assign_document_number()` | trigger function (plpgsql) | DEFINER | `trigger` |  |
| `util.trg_forbid_modification()` | trigger function (plpgsql) | invoker | `trigger` | BEFORE UPDATE/DELETE trigger for append-only tables (stock ledger, audit log). |
| `util.trg_set_updated_at()` | trigger function (plpgsql) | invoker | `trigger` | BEFORE UPDATE trigger: sets updated_at = now(). |
| `util.try_date(text)` | function (plpgsql) | invoker | `date` | Casts an ISO-8601 date (YYYY-MM-DD) to date; NULL if invalid. |
| `util.try_numeric(text)` | function (plpgsql) | invoker | `numeric` | Casts text to numeric (accepts decimal comma); NULL if not a number. |
