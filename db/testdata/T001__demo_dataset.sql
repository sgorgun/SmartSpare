-- =============================================================================
-- T001 — Synthetic demo / test dataset (deterministic: setseed)
-- -----------------------------------------------------------------------------
-- NOT a migration: loaded only into dev / test / demo databases:
--   psql ... -v parts=2000 -f db/testdata/T001__demo_dataset.sql
--
-- All data are synthetic (BA 2.8: no data of real companies or persons).
-- Distributional assumptions (documented for the diploma, see docs/database):
--   * 7 hubs, :parts SKUs (default 2,000; 20,000 for load tests), 8 brands
--   * each SKU stocked at Vilnius + every other hub with p = 0.45
--   * weekly demand occurrence p = 1 - exp(-lambda * seasonal_weight),
--     lambda = 0.01 + 1.2 * U^3  -> about 75 % zero weeks (finding F1: 74.8 %)
--   * demand size geometric; oils in 20-litre packs
--   * seasonal weights from ref.agro_calendar_week (peaks Feb-Mar, Jul-Sep)
--   * 104 weeks of ERP history are imported through raw -> staging -> core
--   * go-live = Monday two weeks before today; live operations after go-live
--     run through the same SQL functions the API uses.
-- Demo users log in with the demo password documented in .env.example
-- (SMARTSPARE_DEMO_USER_PASSWORD); only its real ASP.NET Core Identity V3
-- hashes are stored here. Demo environments only.
-- =============================================================================
\set ON_ERROR_STOP on
\if :{?parts}
\else
    \set parts 2000
\endif

SET ROLE smartspare_owner;
SET search_path = public;
SET client_min_messages = warning;

BEGIN;
SELECT setseed(0.2026);
SELECT set_config('app.pseudonym_key', 'demo-only-pseudonym-key-0123456789', false);

-- Dates relative to today, so the demo always looks "current".
SELECT (date_trunc('week', current_date) - interval '2 weeks')::date AS go_live \gset
SELECT (:'go_live'::date - 7 * 104)                                   AS hist_start \gset

-- ---------------------------------------------------------------------------
-- 1. Employees, positions and user accounts (personas from BA 2.2.1: Tomas, Mantas)
-- ---------------------------------------------------------------------------
-- Employees are the single source of who is who (ADR 0026): every account belongs to an employee, and its
-- roles come from the employee's position (the standard positions are seeded by the Identity migration
-- Organisation). Not every employee has an account. The demo users' employees keep the users' ids.
INSERT INTO iam.position (code, name, description) VALUES
    ('ACCOUNTANT',       'Accountant',       'Books supplier invoices; no SmartSpare roles.'),
    ('WORKSHOP_FOREMAN', 'Workshop foreman', 'Leads the workshop at a hub and services machines.');
INSERT INTO iam.position_role (position_id, role_id)
SELECT p.id, r.id FROM iam.position p, iam.role r WHERE p.code = 'WORKSHOP_FOREMAN' AND r.code = 'SERVICE_ENGINEER';

INSERT INTO iam.employee (id, code, full_name, email, phone, position_id, hired_on)
SELECT e.id::uuid, e.code, e.fn, e.email, e.ph, p.id, e.hired::date
FROM (VALUES
 ('00000000-0000-4000-8000-000000000001','E0001','Tomas Radzevičius','tomas.radzevicius@dealer.example','+370 612 10001','BRAND_MANAGER','2019-04-01'),
 ('00000000-0000-4000-8000-000000000002','E0002','Rasa Kazlauskienė','rasa.kazlauskiene@dealer.example','+370 612 10002','BRAND_MANAGER','2020-02-17'),
 ('00000000-0000-4000-8000-000000000003','E0003','Darius Petraitis','darius.petraitis@dealer.example','+370 612 10003','COMMERCIAL_DIRECTOR','2016-09-01'),
 ('00000000-0000-4000-8000-000000000004','E0004','Mantas Petrauskas','mantas.petrauskas@dealer.example','+370 612 10004','SERVICE_ENGINEER','2021-03-15'),
 ('00000000-0000-4000-8000-000000000005','E0005','Jonas Balčiūnas','jonas.balciunas@dealer.example','+370 612 10005','SERVICE_ENGINEER','2018-06-04'),
 ('00000000-0000-4000-8000-000000000006','E0006','Eglė Stankevičiūtė','egle.stankeviciute@dealer.example','+370 612 10006','WAREHOUSE_KEEPER','2022-01-10'),
 ('00000000-0000-4000-8000-000000000007','E0007','Lukas Vaitkus','lukas.vaitkus@dealer.example','+370 612 10007','WAREHOUSE_KEEPER','2020-11-02'),
 ('00000000-0000-4000-8000-000000000008','E0008','Rūta Jankauskaitė','ruta.jankauskaite@dealer.example','+370 612 10008','WAREHOUSE_KEEPER','2023-05-22'),
 ('00000000-0000-4000-8000-000000000009','E0009','Ignas Žukauskas','ignas.zukauskas@dealer.example','+370 612 10009','INVENTORY_PLANNER','2019-08-19'),
 ('00000000-0000-4000-8000-000000000010','E0010','Vida Mockutė','vida.mockute@dealer.example','+370 612 10010','MANAGING_DIRECTOR','2012-01-02'),
 ('00000000-0000-4000-8000-000000000011','E0011','SmartSpare Administrator','admin.smartspare@dealer.example',NULL,'SYSTEM_ADMINISTRATOR','2024-01-08'),
 ('00000000-0000-4000-8000-000000000012','E0012','Paulius Urbonas','paulius.urbonas@dealer.example','+370 612 10012','SERVICE_ENGINEER','2017-03-01'),
 -- Emergency access (ADR 0028): the locked Superadministrator position; password from the superadmin-password parameter.
 ('00000000-0000-4000-8000-000000000013','E0013','SmartSpare Superadministrator','superadmin.smartspare@dealer.example',NULL,'SUPERADMINISTRATOR','2024-01-08'),
 -- Employees without an account: not everyone works in SmartSpare.
 ('00000000-0000-4000-8000-000000000101','E0101','Ona Žilinskienė','ona.zilinskiene@dealer.example',NULL,'ACCOUNTANT','2015-02-02'),
 ('00000000-0000-4000-8000-000000000102','E0102','Marius Kairys',NULL,'+370 612 10102','WORKSHOP_FOREMAN','2014-07-14'),
 ('00000000-0000-4000-8000-000000000103','E0103','Giedrius Navickas',NULL,'+370 612 10103','SERVICE_ENGINEER','2024-09-02'),
 ('00000000-0000-4000-8000-000000000104','E0104','Laura Sabaliauskienė','laura.sabaliauskiene@dealer.example',NULL,'WAREHOUSE_KEEPER','2025-04-07'),
 ('00000000-0000-4000-8000-000000000105','E0105','Tadas Urbonavičius',NULL,NULL,'WAREHOUSE_KEEPER','2025-10-01'),
 ('00000000-0000-4000-8000-000000000106','E0106','Simona Jurgaitytė','simona.jurgaityte@dealer.example',NULL,'INVENTORY_PLANNER','2026-06-01'),
 ('00000000-0000-4000-8000-000000000107','E0107','Arūnas Butkus',NULL,'+370 612 10107','WORKSHOP_FOREMAN','2011-03-21'),
 ('00000000-0000-4000-8000-000000000108','E0108','Rimantė Kavaliauskaitė','rimante.kavaliauskaite@dealer.example',NULL,'ACCOUNTANT','2021-10-11')
) AS e (id, code, fn, email, ph, position, hired)
JOIN iam.position p ON p.code = e.position;

INSERT INTO iam.app_user (id, employee_id, user_name, password_hash, is_active)
SELECT u.id::uuid, u.id::uuid, u.un,
       -- No password hashes in Git (#7): a random placeholder with a valid ASP.NET Core Identity V3 header
       -- (marker 0x01, PRF HMAC-SHA512, 220,000 iterations, salt length 16) and random salt + subkey, so the
       -- format CHECK and the header test pass but no password verifies. MigrationRunner set-demo-passwords
       -- then stores real hashes of SMARTSPARE_DEMO_USER_PASSWORD (db/scripts/load_testdata.sh runs both).
       translate(encode('\x010000000200035b6000000010'::bytea || gen_random_bytes(48), 'base64'), E'\n', ''),
       u.act
FROM (VALUES
 ('00000000-0000-4000-8000-000000000001','tomas.radzevicius','Tomas Radzevičius','+370 612 10001',NULL,true),
 ('00000000-0000-4000-8000-000000000002','rasa.kazlauskiene','Rasa Kazlauskienė','+370 612 10002',NULL,true),
 ('00000000-0000-4000-8000-000000000003','darius.petraitis','Darius Petraitis','+370 612 10003',NULL,true),
 ('00000000-0000-4000-8000-000000000004','mantas.petrauskas','Mantas Petrauskas','+370 612 10004','PNV',true),
 ('00000000-0000-4000-8000-000000000005','jonas.balciunas','Jonas Balčiūnas','+370 612 10005','KNS',true),
 ('00000000-0000-4000-8000-000000000006','egle.stankeviciute','Eglė Stankevičiūtė','+370 612 10006','KLP',true),
 ('00000000-0000-4000-8000-000000000007','lukas.vaitkus','Lukas Vaitkus','+370 612 10007','PNV',true),
 ('00000000-0000-4000-8000-000000000008','ruta.jankauskaite','Rūta Jankauskaitė','+370 612 10008','VLN',true),
 ('00000000-0000-4000-8000-000000000009','ignas.zukauskas','Ignas Žukauskas','+370 612 10009',NULL,true),
 ('00000000-0000-4000-8000-000000000010','vida.mockute','Vida Mockutė','+370 612 10010',NULL,true),
 ('00000000-0000-4000-8000-000000000011','admin.smartspare','SmartSpare Administrator',NULL,NULL,true),
 ('00000000-0000-4000-8000-000000000012','paulius.urbonas','Paulius Urbonas','+370 612 10012','KNS',true),
 ('00000000-0000-4000-8000-000000000013','superadmin.smartspare','SmartSpare Superadministrator',NULL,NULL,true)
) AS u (id, un, fn, ph, wh, act);

-- Responsible employees (ADR 0026): who manages which brand and who keeps or is based at which hub. They give the
-- demo users their brand scope and hubs; the rest are assignments without rights. Identity's
-- projection (fed by ResponsibilitiesChanged) is filled as if the events had been delivered.
INSERT INTO catalog.brand_responsible (brand_id, employee_id, record_kind, assignment_role)
SELECT b.id, x.emp::uuid, 'brand', x.role
FROM (VALUES ('00000000-0000-4000-8000-000000000001','CLAAS','BRAND_MANAGER'), ('00000000-0000-4000-8000-000000000001','LEMKEN','BRAND_MANAGER'),
             ('00000000-0000-4000-8000-000000000002','JD','BRAND_MANAGER'), ('00000000-0000-4000-8000-000000000002','VAD','BRAND_MANAGER'),
             ('00000000-0000-4000-8000-000000000003','FENDT','BRAND_MANAGER'), ('00000000-0000-4000-8000-000000000003','MF','BRAND_MANAGER'),
             ('00000000-0000-4000-8000-000000000003','CASE','BRAND_MANAGER'), ('00000000-0000-4000-8000-000000000003','NH','BRAND_MANAGER'),
             ('00000000-0000-4000-8000-000000000106','JD','PRODUCT_SPECIALIST')) AS x (emp, brand, role)
JOIN catalog.brand b ON b.code = x.brand;

INSERT INTO inventory.warehouse_responsible (warehouse_id, employee_id, record_kind, assignment_role)
SELECT w.id, x.emp::uuid, 'warehouse', x.role
FROM (VALUES ('00000000-0000-4000-8000-000000000004','PNV','SERVICE_BASE'), ('00000000-0000-4000-8000-000000000005','KNS','SERVICE_BASE'),
             ('00000000-0000-4000-8000-000000000006','KLP','KEEPER'), ('00000000-0000-4000-8000-000000000007','PNV','KEEPER'),
             ('00000000-0000-4000-8000-000000000008','VLN','KEEPER'), ('00000000-0000-4000-8000-000000000104','UTN','KEEPER'),
             ('00000000-0000-4000-8000-000000000105','MRJ','KEEPER'), ('00000000-0000-4000-8000-000000000102','KNS','HUB_MANAGER'),
             ('00000000-0000-4000-8000-000000000107','SIA','SERVICE_BASE')) AS x (emp, hub, role)
JOIN inventory.warehouse w ON w.code = x.hub;

INSERT INTO iam.employee_responsibility (record_kind, record_id, employee_id, assignment_role)
SELECT 'brand', r.brand_id, r.employee_id, r.assignment_role
FROM catalog.brand_responsible r JOIN ref.assignment_role ar ON ar.record_kind = 'brand' AND ar.code = r.assignment_role AND ar.grants_scope
UNION ALL
SELECT 'warehouse', r.warehouse_id, r.employee_id, r.assignment_role
FROM inventory.warehouse_responsible r JOIN ref.assignment_role ar ON ar.record_kind = 'warehouse' AND ar.code = r.assignment_role AND ar.grants_scope;
INSERT INTO iam.responsibility_source (record_kind, record_id, source_occurred_at)
SELECT DISTINCT record_kind, record_id, now() FROM iam.employee_responsibility;

-- ---------------------------------------------------------------------------
-- 2. Suppliers
-- ---------------------------------------------------------------------------
INSERT INTO procurement.supplier (code, name, country_code, order_email, default_lead_time_days) VALUES
    ('CLAAS-PL',  'CLAAS Parts Logistics (demo)',      'DE', 'orders@claas-parts.example',    28),
    ('JD-PD',     'John Deere Parts Distribution (demo)','DE','orders@jd-parts.example',       35),
    ('AGCO-PD',   'AGCO Parts Division (demo)',        'DE', 'orders@agco-parts.example',     30),
    ('LEMKEN-SP', 'Lemken Service Parts (demo)',       'DE', 'orders@lemken-parts.example',   21),
    ('VAD-SP',    'Väderstad Spare Parts (demo)',      'SE', 'orders@vaderstad-parts.example',21),
    ('CNH-PD',    'CNH Industrial Parts (demo)',       'IT', 'orders@cnh-parts.example',      42),
    ('BALTIC-AP', 'Baltic Agro Parts UAB (demo)',      'LT', 'orders@baltic-agro.example',     7);

CREATE TEMP TABLE tmp_brand_supplier AS
SELECT b.id AS brand_id, s.id AS supplier_id, s.default_lead_time_days AS lead
FROM (VALUES ('CLAAS','CLAAS-PL'), ('JD','JD-PD'), ('FENDT','AGCO-PD'), ('MF','AGCO-PD'),
             ('LEMKEN','LEMKEN-SP'), ('VAD','VAD-SP'), ('CASE','CNH-PD'), ('NH','CNH-PD')) AS m (brand, supplier)
JOIN catalog.brand b ON b.code = m.brand
JOIN procurement.supplier s ON s.code = m.supplier;

-- ---------------------------------------------------------------------------
-- 3. Parts catalogue — loaded through the CSV pipeline (raw -> staging -> core)
-- ---------------------------------------------------------------------------
CREATE TEMP TABLE tmp_leaf AS
SELECT id, code, name, row_number() OVER (ORDER BY id) AS rn
FROM ref.part_category WHERE parent_id IS NOT NULL;

INSERT INTO raw.import_batch (source, file_name, file_sha256, uploaded_by)
VALUES ('part_catalog', 'erp_parts_catalogue_export.csv', encode(sha256('demo-catalogue-v1'::bytea), 'hex'),
        '00000000-0000-4000-8000-000000000009') RETURNING id AS cat_batch \gset

INSERT INTO raw.part_catalog_row (import_batch_id, line_no, brand_code, oem_part_number, description,
                                  category_code, uom_code, weight_kg, unit_cost, condition_based)
SELECT :cat_batch, row_number() OVER (ORDER BY x.g),
       x.brand_code,
       CASE x.brand_code
            WHEN 'CLAAS'  THEN lpad((7000000 + x.g)::text, 10, '0')
            WHEN 'JD'     THEN 'RE' || (500000 + x.g)
            WHEN 'FENDT'  THEN 'F' || lpad(x.g::text, 6, '0') || '.010'
            WHEN 'MF'     THEN 'V' || (836000000 + x.g)
            WHEN 'LEMKEN' THEN lpad((3370000 + x.g)::text, 7, '0')
            WHEN 'VAD'    THEN (400000 + x.g)::text
            WHEN 'CASE'   THEN (84000000 + x.g)::text
            ELSE               (87300000 + x.g)::text END,
       c.name || ' - ' || x.variant,
       c.code,
       CASE WHEN c.code IN ('ENGINE_OIL', 'TRANS_OIL') THEN 'L' WHEN c.code = 'GREASE' THEN 'KG' ELSE 'PCS' END,
       to_char(round((0.1 + x.r_weight * 12)::numeric, 3), 'FM9990.000'),
       to_char(CASE WHEN c.code IN ('ENGINE_OIL', 'TRANS_OIL') THEN round((3.5 + x.r_cost * 5)::numeric, 2)
                    ELSE round(exp(ln(8) + x.r_cost * ln(80))::numeric, 2) END, 'FM9999990.00'),
       CASE WHEN c.code IN ('AIR_FILTER', 'CAB_FILTER') THEN 'yes' ELSE 'no' END
FROM (SELECT g,
             (ARRAY['CLAAS','CLAAS','JD','JD','JD','FENDT','MF','MF','LEMKEN','VAD','CASE','NH'])[1 + floor(random() * 12)::int] AS brand_code,
             1 + floor(random() * 23)::int AS leaf_rn,
             (ARRAY['standard','heavy duty','series A','series B','premium','kit'])[1 + floor(random() * 6)::int] AS variant,
             random() AS r_weight, random() AS r_cost
      FROM generate_series(1, :parts) AS g ORDER BY g) x
JOIN tmp_leaf c ON c.rn = x.leaf_rn;

-- Named parts for the BA scenarios (UC01: straw chopper blade CP-4821; UC02: Lexion 500 h kit)
-- and standard service-kit parts for every tractor/combine brand.
INSERT INTO raw.part_catalog_row (import_batch_id, line_no, brand_code, oem_part_number, description, category_code, uom_code, weight_kg, unit_cost, condition_based)
SELECT :cat_batch, :parts + row_number() OVER (), b, oem, d, c, u, w, cost, 'no'
FROM (VALUES
  ('CLAAS','CP-4821',   'Heavy-duty straw chopper blade',       'CHOPPER_BLADES','PCS','0.850','38.50'),
  ('CLAAS','0011534571','Engine oil filter LEXION 8000/7000',   'OIL_FILTER','PCS','0.900','42.10'),
  ('CLAAS','0011534580','Primary fuel filter LEXION',           'FUEL_FILTER','PCS','0.600','36.80'),
  ('CLAAS','0011534592','Hydraulic return filter LEXION',       'HYD_FILTER','PCS','1.100','58.40'),
  ('CLAAS','0006673220','Drive belt threshing drum LEXION',     'BELTS','PCS','4.200','186.00'),
  ('CLAAS','0001970050','Engine oil 15W-40 CLAAS AGRIMOT',      'ENGINE_OIL','L','0.900','6.20'),
  ('CLAAS','0001970120','Transmission oil CLAAS AGRIGEAR',      'TRANS_OIL','L','0.900','7.40'),
  ('JD','RE900001','Engine oil filter 6R/8R',                   'OIL_FILTER','PCS','0.800','39.90'),
  ('JD','RE900002','Fuel filter 6R/8R',                         'FUEL_FILTER','PCS','0.500','44.20'),
  ('JD','RE900003','Hydraulic/transmission filter 6R/8R',       'HYD_FILTER','PCS','1.300','71.30'),
  ('JD','RE900004','Engine oil Plus-50 II 15W-40',              'ENGINE_OIL','L','0.900','7.10'),
  ('JD','RE900005','Transmission oil Hy-Gard',                  'TRANS_OIL','L','0.900','6.80'),
  ('FENDT','F900001.010','Engine oil filter Vario',             'OIL_FILTER','PCS','0.800','45.60'),
  ('FENDT','F900002.010','Fuel filter Vario',                   'FUEL_FILTER','PCS','0.500','49.90'),
  ('FENDT','F900003.010','Hydraulic filter Vario',              'HYD_FILTER','PCS','1.200','83.00'),
  ('FENDT','F900004.010','Engine oil 10W-40 Vario',             'ENGINE_OIL','L','0.900','7.60'),
  ('FENDT','F900005.010','Transmission oil Vario ATF',          'TRANS_OIL','L','0.900','8.10'),
  ('MF','V900000001','Engine oil filter 7S/8S',                 'OIL_FILTER','PCS','0.800','34.40'),
  ('MF','V900000002','Fuel filter 7S/8S',                       'FUEL_FILTER','PCS','0.500','37.20'),
  ('MF','V900000003','Hydraulic filter 7S/8S',                  'HYD_FILTER','PCS','1.200','66.10'),
  ('MF','V900000004','Engine oil 15W-40 AGCO',                  'ENGINE_OIL','L','0.900','6.40'),
  ('MF','V900000005','Transmission oil AGCO Powerfluid',        'TRANS_OIL','L','0.900','6.90'),
  ('CASE','90000001','Engine oil filter Magnum',                'OIL_FILTER','PCS','0.800','41.00'),
  ('CASE','90000002','Fuel filter Magnum',                      'FUEL_FILTER','PCS','0.500','46.50'),
  ('CASE','90000003','Hydraulic filter Magnum',                 'HYD_FILTER','PCS','1.300','79.80'),
  ('CASE','90000004','Engine oil 15W-40 CNH',                   'ENGINE_OIL','L','0.900','6.70'),
  ('CASE','90000005','Transmission oil CNH Hydraulic',          'TRANS_OIL','L','0.900','7.00'),
  ('NH','91000001','Engine oil filter CR/T7',                   'OIL_FILTER','PCS','0.800','40.20'),
  ('NH','91000002','Fuel filter CR/T7',                         'FUEL_FILTER','PCS','0.500','45.10'),
  ('NH','91000003','Hydraulic filter CR/T7',                    'HYD_FILTER','PCS','1.300','77.40'),
  ('NH','91000004','Engine oil 15W-40 CNH',                     'ENGINE_OIL','L','0.900','6.70'),
  ('NH','91000005','Transmission oil CNH',                      'TRANS_OIL','L','0.900','7.00')
) AS t (b, oem, d, c, u, w, cost);

UPDATE raw.import_batch SET total_rows = (SELECT count(*) FROM raw.part_catalog_row WHERE import_batch_id = :cat_batch) WHERE id = :cat_batch;
SELECT status, message FROM staging.process_batch(:cat_batch);

-- Supplier conditions: OEM supplier (preferred) for every part, aftermarket for ~30 %.
INSERT INTO procurement.supplier_part (supplier_id, part_id, supplier_part_number, moq, pack_size, lead_time_days, is_preferred)
SELECT bs.supplier_id, p.id, p.oem_part_number,
       CASE WHEN u.code = 'L' THEN 20 WHEN p.unit_cost < 20 THEN 10 WHEN p.unit_cost < 60 THEN 2 ELSE 1 END,
       CASE WHEN u.code = 'L' THEN 20 WHEN p.unit_cost < 20 THEN 5  WHEN p.unit_cost < 60 THEN 2 ELSE 1 END,
       bs.lead + (p.id % 15)::int, true
FROM catalog.part p
JOIN ref.unit_of_measure u ON u.id = p.uom_id
JOIN tmp_brand_supplier bs ON bs.brand_id = p.brand_id;

INSERT INTO procurement.supplier_part (supplier_id, part_id, supplier_part_number, moq, pack_size, lead_time_days, is_preferred)
SELECT s.id, p.id, 'BAP-' || p.id, CASE WHEN u.code = 'L' THEN 200 ELSE 5 END, CASE WHEN u.code = 'L' THEN 20 ELSE 5 END, 7 + (p.id % 7)::int, false
FROM catalog.part p
JOIN ref.unit_of_measure u ON u.id = p.uom_id
CROSS JOIN procurement.supplier s
WHERE s.code = 'BALTIC-AP' AND p.id % 10 < 3;

-- Price history (SCD type 2 style validity ranges): +5 % from 1 January.
INSERT INTO procurement.supplier_price (supplier_part_id, valid_during, unit_price)
SELECT sp.id, daterange(:'hist_start'::date, date_trunc('year', current_date)::date),
       round(p.unit_cost * CASE WHEN sp.is_preferred THEN 0.95 ELSE 0.85 END, 2)
FROM procurement.supplier_part sp JOIN catalog.part p ON p.id = sp.part_id;
INSERT INTO procurement.supplier_price (supplier_part_id, valid_during, unit_price)
SELECT sp.id, daterange(date_trunc('year', current_date)::date, NULL),
       round(p.unit_cost * CASE WHEN sp.is_preferred THEN 1.00 ELSE 0.90 END, 2)
FROM procurement.supplier_part sp JOIN catalog.part p ON p.id = sp.part_id;

-- Analogue link for a "new" SKU without history (US15).
INSERT INTO catalog.part_analogue (part_id, analogue_part_id, scaling_factor)
SELECT (SELECT id FROM catalog.part WHERE oem_part_number = 'RE900001'),
       (SELECT min(p.id) FROM catalog.part p JOIN ref.part_category c ON c.id = p.category_id
        WHERE c.code = 'OIL_FILTER' AND p.brand_id = (SELECT id FROM catalog.brand WHERE code = 'JD') AND p.oem_part_number <> 'RE900001'),
       1.200;

-- ---------------------------------------------------------------------------
-- 4. Stocking policy per hub (warehouse_part) and demand model per series
-- ---------------------------------------------------------------------------
INSERT INTO inventory.warehouse_part (warehouse_id, part_id, bin_location)
SELECT w.id, p.id, format('%s-%s-%s', w.code, chr(65 + (p.id % 20)::int), lpad((p.id % 99 + 1)::text, 2, '0'))
FROM catalog.part p CROSS JOIN inventory.warehouse w
WHERE w.code = 'VLN' OR random() < 0.45
   OR p.oem_part_number IN ('CP-4821','0011534571','0011534580','0011534592','0006673220','0001970050','0001970120')
   OR p.oem_part_number ~ '^(RE9000|F9000|V9000000|9000000|9100000)'
ORDER BY p.id, w.id;

CREATE TEMP TABLE tmp_series AS
SELECT wp.warehouse_id, wp.part_id, u.code AS uom,
       0.01 + 1.2 * power(random(), 3)                          AS lambda,
       0.3 + 2.5 * random()                                     AS size_scale,
       random() < 0.10                                          AS dead
FROM inventory.warehouse_part wp
JOIN catalog.part p ON p.id = wp.part_id
JOIN ref.unit_of_measure u ON u.id = p.uom_id
ORDER BY wp.warehouse_id, wp.part_id;
-- ~10 % of series stop selling a year before go-live -> realistic dead stock (BA: 14-17 %).

-- ---------------------------------------------------------------------------
-- 5. Two years of ERP demand history through the import pipeline
-- ---------------------------------------------------------------------------
INSERT INTO raw.import_batch (source, file_name, file_sha256, uploaded_by)
VALUES ('demand_history', 'erp_demand_history_2y.csv', encode(sha256('demo-history-v1'::bytea), 'hex'),
        '00000000-0000-4000-8000-000000000009') RETURNING id AS hist_batch \gset

INSERT INTO raw.demand_history_row (import_batch_id, line_no, doc_date, warehouse_code, brand_code, oem_part_number, quantity, doc_ref)
SELECT :hist_batch, row_number() OVER (ORDER BY d.doc_date, d.warehouse_id, d.part_id),
       to_char(d.doc_date, 'YYYY-MM-DD'), w.code, b.code, p.oem_part_number,
       d.qty::text, 'ERP-S' || lpad((row_number() OVER (ORDER BY d.doc_date, d.warehouse_id, d.part_id))::text, 8, '0')
FROM (
    SELECT s.warehouse_id, s.part_id,
           wk + (floor(random() * 6))::int AS doc_date,
           CASE WHEN s.uom = 'L' THEN 20 * (1 + floor(-ln(1 - random()) * 0.8))
                ELSE 1 + floor(-ln(1 - random()) * s.size_scale) END
           * CASE WHEN random() < 0.0004 THEN 25 ELSE 1 END                  -- rare one-off bulk orders (US08)
           AS qty,
           random() AS occ, s.lambda, ac.seasonal_weight, s.dead, wk
    FROM tmp_series s
    CROSS JOIN generate_series(:'hist_start'::date, :'go_live'::date - 7, interval '1 week') AS g(wk_ts)
    CROSS JOIN LATERAL (SELECT g.wk_ts::date AS wk) k
    JOIN ref.agro_calendar_week ac ON ac.iso_week = extract(week FROM k.wk)
    ORDER BY s.warehouse_id, s.part_id, k.wk
) d
JOIN inventory.warehouse w ON w.id = d.warehouse_id
JOIN catalog.part p ON p.id = d.part_id
JOIN catalog.brand b ON b.id = p.brand_id
WHERE d.occ < 1 - exp(-d.lambda * d.seasonal_weight)
  AND NOT (d.dead AND d.wk >= :'go_live'::date - 365);

UPDATE raw.import_batch SET total_rows = (SELECT count(*) FROM raw.demand_history_row WHERE import_batch_id = :hist_batch) WHERE id = :hist_batch;
SELECT status, message FROM staging.process_batch(:hist_batch);

-- A correction file: 150 rows already imported + 40 new + 10 invalid (5 %: passes the gate)
INSERT INTO raw.import_batch (source, file_name, file_sha256, uploaded_by)
VALUES ('demand_history', 'erp_demand_corrections.csv', encode(sha256('demo-history-corrections'::bytea), 'hex'),
        '00000000-0000-4000-8000-000000000009') RETURNING id AS corr_batch \gset
INSERT INTO raw.demand_history_row (import_batch_id, line_no, doc_date, warehouse_code, brand_code, oem_part_number, quantity, doc_ref)
SELECT :corr_batch, row_number() OVER (ORDER BY src, n), doc_date, warehouse_code, brand_code, oem_part_number, quantity, doc_ref
FROM (
    SELECT 1 AS src, line_no AS n, doc_date, warehouse_code, brand_code, oem_part_number, quantity, doc_ref
    FROM raw.demand_history_row WHERE import_batch_id = :hist_batch AND line_no % 997 = 0 AND line_no <= 997 * 150
    UNION ALL
    SELECT 2, line_no, doc_date, warehouse_code, brand_code, oem_part_number, quantity, 'ERP-C' || line_no
    FROM raw.demand_history_row WHERE import_batch_id = :hist_batch AND line_no % 1999 = 1 AND line_no <= 1999 * 40
    UNION ALL
    SELECT 3, n, d, wc, bc, oem, q, 'ERP-BAD' || n FROM (VALUES
        (1, '2025-13-01', 'VLN', 'JD', 'RE900001', '2'),   (2, '01.02.2025', 'VLN', 'JD', 'RE900001', '1'),
        (3, '2025-03-03', 'XXX', 'JD', 'RE900001', '1'),   (4, '2025-03-03', 'KNS', 'JD', 'NOPE-1', '1'),
        (5, '2025-03-03', 'KNS', 'JD', 'RE900002', '-4'),  (6, '2025-03-03', 'KNS', 'JD', 'RE900002', 'two'),
        (7, '2099-01-01', 'KNS', 'JD', 'RE900002', '1'),   (8, '2025-03-04', 'MRJ', 'ZZ', 'RE900002', '1'),
        (9, '',           'KNS', 'JD', 'RE900002', '1'),   (10,'2025-03-05', 'KNS', 'JD', 'RE900003', '1e9')
    ) AS bad (n, d, wc, bc, oem, q)
) x;
UPDATE raw.import_batch SET total_rows = (SELECT count(*) FROM raw.demand_history_row WHERE import_batch_id = :corr_batch) WHERE id = :corr_batch;
SELECT status, message FROM staging.process_batch(:corr_batch);

-- A bad catalogue update (3 of 38 rows invalid = 7.9 % > 5 %): rejected as a whole.
INSERT INTO raw.import_batch (source, file_name, file_sha256, uploaded_by)
VALUES ('part_catalog', 'catalogue_price_update_bad.csv', encode(sha256('demo-catalogue-bad'::bytea), 'hex'),
        '00000000-0000-4000-8000-000000000001') RETURNING id AS bad_batch \gset
INSERT INTO raw.part_catalog_row (import_batch_id, line_no, brand_code, oem_part_number, description, category_code, uom_code, weight_kg, unit_cost, condition_based)
SELECT :bad_batch, row_number() OVER (ORDER BY p.id), b.code, p.oem_part_number, p.description, c.code, u.code,
       p.weight_kg::text, round(p.unit_cost * 1.03, 2)::text, 'no'
FROM catalog.part p JOIN catalog.brand b ON b.id = p.brand_id JOIN ref.part_category c ON c.id = p.category_id
JOIN ref.unit_of_measure u ON u.id = p.uom_id
WHERE b.code = 'CLAAS' AND p.id % 5 = 0 AND p.oem_part_number <> 'CP-4821'
ORDER BY p.id LIMIT 35;
INSERT INTO raw.part_catalog_row (import_batch_id, line_no, brand_code, oem_part_number, description, category_code, uom_code, weight_kg, unit_cost, condition_based) VALUES
    (:bad_batch, 36, 'KUBOTA', 'K1234-5678', 'Unknown brand oil filter', 'OIL_FILTER', 'PCS', '0.5', '22.00', 'no'),
    (:bad_batch, 37, 'CLAAS',  '0099999999', 'Price not a number',       'OIL_FILTER', 'PCS', '0.5', 'abc',   'no'),
    (:bad_batch, 38, 'CLAAS',  '0099999999', 'Duplicate line',           'OIL_FILTER', 'PCS', '0.5', '22.00', 'no');
UPDATE raw.import_batch SET total_rows = 38 WHERE id = :bad_batch;
SELECT status, message FROM staging.process_batch(:bad_batch);

-- ---------------------------------------------------------------------------
-- 6. Go-live: opening balances and safety stock from the demand model
-- ---------------------------------------------------------------------------
UPDATE inventory.warehouse_part wp
   SET safety_stock_qty = CASE WHEN s.uom = 'L' THEN 20 * ceil(s.lambda * 1.5) ELSE ceil(s.lambda * s.size_scale * 1.5) END
  FROM tmp_series s
 WHERE s.warehouse_id = wp.warehouse_id AND s.part_id = wp.part_id;

INSERT INTO inventory.stock_movement (occurred_at, warehouse_id, part_id, movement_type_code, quantity, unit_cost, note)
SELECT (:'go_live'::date + time '06:00') AT TIME ZONE 'Europe/Vilnius', s.warehouse_id, s.part_id, 'OPENING_BALANCE',
       CASE WHEN s.uom = 'L' THEN 20 * (2 + floor(random() * 12))
            WHEN s.dead THEN 2 + floor(random() * 10)
            ELSE greatest(1, ceil(s.lambda * (1 + s.size_scale) * (3 + random() * 14))) END,
       p.unit_cost, 'Go-live stock count'
FROM tmp_series s JOIN catalog.part p ON p.id = s.part_id
WHERE random() < 0.93                                     -- a few series start with zero stock
ORDER BY s.warehouse_id, s.part_id;

-- UC01 scenario: CP-4821 surplus in Klaipėda (38, safety 8), shortage in Panevėžys.
UPDATE inventory.warehouse_part wp SET safety_stock_qty = CASE w.code WHEN 'KLP' THEN 8 WHEN 'PNV' THEN 4 ELSE 2 END
FROM inventory.warehouse w, catalog.part p
WHERE w.id = wp.warehouse_id AND p.id = wp.part_id AND p.oem_part_number = 'CP-4821';
INSERT INTO inventory.stock_movement (occurred_at, warehouse_id, part_id, movement_type_code, quantity, unit_cost, note, adjustment_reason_id)
SELECT (:'go_live'::date + time '07:00') AT TIME ZONE 'Europe/Vilnius', w.id, p.id, 'ADJUSTMENT_IN',
       CASE w.code WHEN 'KLP' THEN 38 WHEN 'PNV' THEN 6 ELSE 3 END - coalesce(b.on_hand_qty, 0), p.unit_cost, 'UC01 scenario stock count',
       (SELECT id FROM inventory.adjustment_reason WHERE code = 'COUNT_CORRECTION')
FROM inventory.warehouse w CROSS JOIN catalog.part p
LEFT JOIN inventory.stock_balance b ON b.warehouse_id = w.id AND b.part_id = p.id
WHERE p.oem_part_number = 'CP-4821'
  AND CASE w.code WHEN 'KLP' THEN 38 WHEN 'PNV' THEN 6 ELSE 3 END > coalesce(b.on_hand_qty, 0);
INSERT INTO inventory.stock_movement (occurred_at, warehouse_id, part_id, movement_type_code, quantity, unit_cost, note, adjustment_reason_id)
SELECT (:'go_live'::date + time '07:00') AT TIME ZONE 'Europe/Vilnius', w.id, p.id, 'ADJUSTMENT_OUT',
       b.on_hand_qty - CASE w.code WHEN 'KLP' THEN 38 WHEN 'PNV' THEN 6 ELSE 3 END, p.unit_cost, 'UC01 scenario stock count',
       (SELECT id FROM inventory.adjustment_reason WHERE code = 'COUNT_CORRECTION')
FROM inventory.warehouse w CROSS JOIN catalog.part p
JOIN inventory.stock_balance b ON b.warehouse_id = w.id AND b.part_id = p.id
WHERE p.oem_part_number = 'CP-4821'
  AND CASE w.code WHEN 'KLP' THEN 38 WHEN 'PNV' THEN 6 ELSE 3 END < b.on_hand_qty;

-- Service-kit parts are well stocked at every hub.
INSERT INTO inventory.stock_movement (occurred_at, warehouse_id, part_id, movement_type_code, quantity, unit_cost, note, adjustment_reason_id)
SELECT (:'go_live'::date + time '07:30') AT TIME ZONE 'Europe/Vilnius', wp.warehouse_id, p.id, 'ADJUSTMENT_IN',
       CASE WHEN u.code = 'L' THEN 400 ELSE 12 END, p.unit_cost, 'Service kit stock build-up', (SELECT id FROM inventory.adjustment_reason WHERE code = 'COUNT_CORRECTION')
FROM catalog.part p JOIN ref.unit_of_measure u ON u.id = p.uom_id
JOIN inventory.warehouse_part wp ON wp.part_id = p.id
WHERE p.oem_part_number ~ '^(00115345|0006673220|000197|RE9000|F9000|V9000000|9000000|9100000)';

-- Live counter sales after go-live (never more than the opening stock of a series).
INSERT INTO inventory.stock_movement (occurred_at, warehouse_id, part_id, movement_type_code, quantity, unit_cost, external_doc_ref, created_by)
SELECT x.occurred_at, x.warehouse_id, x.part_id, 'ISSUE_SALE', x.qty, x.unit_cost, x.doc, '00000000-0000-4000-8000-000000000008'
FROM (
    SELECT c.*, sum(c.qty) OVER (PARTITION BY c.warehouse_id, c.part_id ORDER BY c.occurred_at
                                 ROWS UNBOUNDED PRECEDING) AS cum, b.available_qty
    FROM (
        SELECT s.warehouse_id, s.part_id, p.unit_cost,
               ((:'go_live'::date + d) + time '10:00' + (random() * interval '7 hours')) AT TIME ZONE 'Europe/Vilnius' AS occurred_at,
               CASE WHEN s.uom = 'L' THEN 20 ELSE 1 + floor(-ln(1 - random()) * s.size_scale) END AS qty,
               'POS-' || lpad((row_number() OVER (ORDER BY s.warehouse_id, s.part_id, d))::text, 7, '0') AS doc,
               random() AS occ, s.lambda, s.dead
        FROM tmp_series s
        JOIN catalog.part p ON p.id = s.part_id
        CROSS JOIN generate_series(0, (current_date - :'go_live'::date) - 1) AS d
        ORDER BY s.warehouse_id, s.part_id, d
    ) c
    JOIN inventory.stock_balance b ON b.warehouse_id = c.warehouse_id AND b.part_id = c.part_id
    WHERE c.occ < 1 - exp(-c.lambda / 7.0) AND NOT c.dead
      AND c.part_id <> (SELECT id FROM catalog.part WHERE oem_part_number = 'CP-4821')
) x
WHERE x.cum <= x.available_qty - 0
ORDER BY x.occurred_at;

-- ---------------------------------------------------------------------------
-- 7. Fleet: customers (pseudonymised), models, OEM plans + kits, machines, readings
-- ---------------------------------------------------------------------------
INSERT INTO fleet.customer (customer_code, erp_ref_hash, region_warehouse_id, farm_size_ha)
SELECT 'CUST-' || lpad(g::text, 6, '0'), util.pseudonymise('ERP-CUST-' || g),
       w.ids[1 + floor(random() * array_length(w.ids, 1))::int],
       round((50 + random() * 3000)::numeric, 1)
FROM generate_series(1, 250) g
CROSS JOIN (SELECT array_agg(id ORDER BY id) AS ids FROM inventory.warehouse) w
ORDER BY g;

INSERT INTO fleet.machine_model (brand_id, machine_type_id, model_name)
SELECT b.id, t.id, m.model
FROM (VALUES ('CLAAS','COMBINE','Lexion 8700'), ('CLAAS','COMBINE','Lexion 7600'), ('CLAAS','TRACTOR','Axion 870'),
             ('JD','TRACTOR','6R 150'), ('JD','TRACTOR','8R 410'), ('JD','COMBINE','S780'),
             ('FENDT','TRACTOR','724 Vario'), ('FENDT','TRACTOR','942 Vario'),
             ('MF','TRACTOR','8S.265'), ('MF','TRACTOR','7S.180'),
             ('CASE','TRACTOR','Magnum 340'), ('NH','COMBINE','CR8.90'), ('NH','TRACTOR','T7.270'),
             ('LEMKEN','PLOUGH','Juwel 8'), ('VAD','SEEDER','Rapid A 600J')) AS m (brand, mtype, model)
JOIN catalog.brand b ON b.code = m.brand JOIN ref.machine_type t ON t.code = m.mtype;

-- OEM intervals differ by brand (finding F5, Appendix C.9)
-- Calendar intervals (STAGE_6_PLAN F3): engine service yearly, transmission service every two years, whichever comes first.
INSERT INTO fleet.maintenance_plan (model_id, interval_hours, name, interval_months)
SELECT mm.id, iv.h, iv.label, CASE WHEN iv.label = 'Engine service' THEN 12 ELSE 24 END
FROM fleet.machine_model mm
JOIN catalog.brand b ON b.id = mm.brand_id
JOIN ref.machine_type t ON t.id = mm.machine_type_id AND t.code IN ('TRACTOR', 'COMBINE')
CROSS JOIN LATERAL (VALUES
    (CASE b.code WHEN 'FENDT' THEN 750 WHEN 'CASE' THEN 600 WHEN 'NH' THEN 600 ELSE 500 END, 'Engine service'),
    (CASE b.code WHEN 'FENDT' THEN 1800 WHEN 'CASE' THEN 1200 WHEN 'NH' THEN 1200 ELSE 1500 END, 'Transmission & hydraulic service')
) AS iv (h, label);

-- Kits: engine service = oil filter + fuel filter + engine oil (+ UC02 parts for Lexion);
--       transmission service = hydraulic filter x2 + transmission oil.
INSERT INTO fleet.maintenance_plan_part (maintenance_plan_id, part_id, quantity)
SELECT mp.id, p.id,
       CASE c.code WHEN 'ENGINE_OIL' THEN CASE WHEN t.code = 'COMBINE' THEN 30 ELSE 18 END
                   WHEN 'TRANS_OIL'  THEN CASE WHEN t.code = 'COMBINE' THEN 40 ELSE 60 END
                   WHEN 'HYD_FILTER' THEN CASE WHEN mp.name LIKE 'Transmission%' THEN 2 ELSE 1 END
                   ELSE 1 END
FROM fleet.maintenance_plan mp
JOIN fleet.machine_model mm ON mm.id = mp.model_id
JOIN ref.machine_type t ON t.id = mm.machine_type_id
JOIN catalog.part p ON p.brand_id = mm.brand_id
JOIN ref.part_category c ON c.id = p.category_id
WHERE p.oem_part_number IN ('0011534571','0011534580','0011534592','0006673220','0001970050','0001970120',
                            'RE900001','RE900002','RE900003','RE900004','RE900005',
                            'F900001.010','F900002.010','F900003.010','F900004.010','F900005.010',
                            'V900000001','V900000002','V900000003','V900000004','V900000005',
                            '90000001','90000002','90000003','90000004','90000005',
                            '91000001','91000002','91000003','91000004','91000005')
  AND ((mp.name = 'Engine service' AND (c.code IN ('OIL_FILTER', 'FUEL_FILTER', 'ENGINE_OIL')
                                         OR (mm.model_name LIKE 'Lexion%' AND c.code IN ('HYD_FILTER', 'BELTS'))))
    OR (mp.name LIKE 'Transmission%' AND c.code IN ('HYD_FILTER', 'TRANS_OIL')));

INSERT INTO fleet.machine (vin, model_id, customer_id, operating_condition_id, home_warehouse_id, manufacture_year, commissioned_on)
SELECT upper(substr(md5('vin-' || g), 1, 17)), m.id, c.id, oc.id, c.region_warehouse_id, y.yr, make_date(y.yr, 4, 1)
FROM (SELECT g,
             1 + floor(random() * 15)::int AS model_rn,
             1 + floor(random() * 250)::int AS cust_rn,
             CASE WHEN random() < 0.70 THEN 'NORMAL' WHEN random() < 0.5 THEN 'SEVERE_DUST'
                  WHEN random() < 0.6 THEN 'HIGH_LOAD' ELSE 'NON_OEM' END AS cond,
             2008 + floor(random() * 18)::int AS yr
      FROM generate_series(1, 500) g ORDER BY g) x
CROSS JOIN LATERAL (SELECT x.yr) y (yr)
JOIN (SELECT id, row_number() OVER (ORDER BY id) rn FROM fleet.machine_model) m ON m.rn = x.model_rn
JOIN (SELECT id, region_warehouse_id, row_number() OVER (ORDER BY id) rn FROM fleet.customer) c ON c.rn = x.cust_rn
JOIN ref.operating_condition oc ON oc.code = x.cond;

-- Four readings per machine in the year before go-live (plausible increments).
INSERT INTO fleet.engine_hours_reading (machine_id, reading_hours, read_at, source)
SELECT r.machine_id,
       round(r.base + sum(r.inc) OVER (PARTITION BY r.machine_id ORDER BY r.k), 1),
       r.read_at, 'import'
FROM (
    SELECT m.id AS machine_id, k,
           (2026 - m.manufacture_year) * (250 + (m.id % 7) * 60) AS base,
           CASE WHEN k = 1 THEN 0 ELSE (40 + random() * 260)::numeric END AS inc,
           ((:'go_live'::date - 330 + (k - 1) * 100) + time '09:00') AT TIME ZONE 'Europe/Vilnius' AS read_at
    FROM fleet.machine m CROSS JOIN generate_series(1, 4) k
    ORDER BY m.id, k
) r
ORDER BY r.machine_id, r.read_at;

-- UC02 machine: Claas Lexion 8700 of a Panevėžys customer, last reading 440 h.
INSERT INTO fleet.machine (vin, model_id, customer_id, operating_condition_id, home_warehouse_id, manufacture_year, commissioned_on)
SELECT 'LX901PNV2026', mm.id, c.id, oc.id, w.id, 2025, DATE '2025-06-15'
FROM fleet.machine_model mm, inventory.warehouse w, ref.operating_condition oc,
     LATERAL (SELECT id FROM fleet.customer WHERE region_warehouse_id = w.id ORDER BY id LIMIT 1) c
WHERE mm.model_name = 'Lexion 8700' AND w.code = 'PNV' AND oc.code = 'NORMAL';
INSERT INTO fleet.engine_hours_reading (machine_id, reading_hours, read_at, source)
SELECT id, 440, ((:'go_live'::date - 20) + time '08:00') AT TIME ZONE 'Europe/Vilnius', 'service_visit'
FROM fleet.machine WHERE vin = 'LX901PNV2026';

-- ---------------------------------------------------------------------------
-- 8. Live operations after go-live, executed through the business functions
-- ---------------------------------------------------------------------------
-- Acts as a signed-in user the way SessionContextInterceptor does (MODULE_MAPPING 3.6): app.user_id,
-- app.user_roles, app.user_permissions (ADR 0028) and app.warehouse_ids (ADR 0026). The application takes them from the
-- user's claims; this script reads them from iam: roles and their permissions from the employee's position. NULL clears
-- the session user.
CREATE FUNCTION pg_temp.t001_act_as(p_user uuid) RETURNS void LANGUAGE sql AS $$
    SELECT set_config('app.user_id', coalesce(p_user::text, ''), false),
           set_config('app.user_roles', coalesce((SELECT string_agg(r.code, ',' ORDER BY r.code)
                                                   FROM iam.app_user u JOIN iam.employee e ON e.id = u.employee_id
                                                   JOIN iam.position_role pr ON pr.position_id = e.position_id
                                                   JOIN iam.role r ON r.id = pr.role_id
                                                   WHERE u.id = p_user AND u.is_active AND NOT e.is_archived), ''), false),
           set_config('app.user_permissions', coalesce((SELECT string_agg(DISTINCT rp.permission, ',' ORDER BY rp.permission)
                                                         FROM iam.app_user u JOIN iam.employee e ON e.id = u.employee_id
                                                         JOIN iam.position_role pr ON pr.position_id = e.position_id
                                                         JOIN iam.role_permission rp ON rp.role_id = pr.role_id
                                                         WHERE u.id = p_user AND u.is_active AND NOT e.is_archived), ''), false),
           set_config('app.warehouse_ids', coalesce((SELECT string_agg(DISTINCT r.record_id::text, ',')
                                                     FROM iam.app_user u
                                                     JOIN iam.employee_responsibility r ON r.employee_id = u.employee_id AND r.record_kind = 'warehouse'
                                                     JOIN ref.assignment_role ar ON ar.record_kind = r.record_kind AND ar.code = r.assignment_role AND ar.grants_scope
                                                     WHERE u.id = p_user), ''), false);
$$;

-- UC02: Mantas logs 512 h on the Lexion -> 500 h service drafted (its kit is soft-reserved at Panevėžys below)
SELECT pg_temp.t001_act_as('00000000-0000-4000-8000-000000000004');
SELECT * FROM fleet.log_engine_hours((SELECT id FROM fleet.machine WHERE vin = 'LX901PNV2026'), 512, now() - interval '3 hours');

-- Jonas logs new readings for 60 machines (some cross a service threshold)
SELECT pg_temp.t001_act_as('00000000-0000-4000-8000-000000000005');
SELECT count(*) AS drafted_service_orders
FROM (SELECT m.id, m.current_engine_hours FROM fleet.machine m
      JOIN fleet.machine_model mm ON mm.id = m.model_id
      JOIN ref.machine_type t ON t.id = mm.machine_type_id AND t.code IN ('TRACTOR', 'COMBINE')
      WHERE m.vin <> 'LX901PNV2026' ORDER BY m.id LIMIT 60) x
CROSS JOIN LATERAL fleet.log_engine_hours(x.id, x.current_engine_hours + 30 + (x.id % 5) * 45, now() - interval '1 day') so;

-- Inventory reserves the drafted kits as its consumer of ServiceOrderDrafted does (STAGE_6_PLAN 5.3, PR 6g2): soft holds
-- for 7 days via inventory.reserve_service_kit, then Fleet stores the held quantity and moves full kits to parts_reserved,
-- as its consumer of ServiceKitReserved does. The events themselves are not replayed by this SQL script.
DO $$
DECLARE
    v_so record;
BEGIN
    FOR v_so IN SELECT so.id, so.warehouse_id FROM fleet.service_order so WHERE so.status = 'draft' ORDER BY so.id LOOP
        UPDATE fleet.service_order_line l SET qty_reserved = k.reserved_qty
        FROM inventory.reserve_service_kit(
                 v_so.warehouse_id,
                 (SELECT array_agg(id ORDER BY part_id) FROM fleet.service_order_line WHERE service_order_id = v_so.id),
                 (SELECT array_agg(part_id ORDER BY part_id) FROM fleet.service_order_line WHERE service_order_id = v_so.id),
                 (SELECT array_agg(qty_required ORDER BY part_id) FROM fleet.service_order_line WHERE service_order_id = v_so.id),
                 now() + interval '7 days') k
        WHERE l.id = k.service_order_line_id;
        UPDATE fleet.service_order SET kit_reserved_at = now() WHERE id = v_so.id;
        UPDATE fleet.service_order SET status = 'parts_reserved'
        WHERE id = v_so.id
          AND EXISTS (SELECT 1 FROM fleet.service_order_line WHERE service_order_id = v_so.id)
          AND NOT EXISTS (SELECT 1 FROM fleet.service_order_line WHERE service_order_id = v_so.id AND qty_reserved < qty_required);
    END LOOP;
END $$;

-- Complete 10 service orders (parts issued -> ISSUE_SERVICE demand)
SELECT fleet.complete_service_order(so.id, m.current_engine_hours)
FROM fleet.service_order so JOIN fleet.machine m ON m.id = so.machine_id
WHERE so.status = 'parts_reserved' AND m.vin <> 'LX901PNV2026'
ORDER BY so.id LIMIT 10;

-- Inventory issues the completed kits as its consumer of ServiceOrderCompleted does (STAGE_6_PLAN 5.4, PR 6h2), and Fleet
-- stores the issued quantities as its consumer of ServiceKitIssued does.
DO $$
DECLARE
    v_so record;
BEGIN
    FOR v_so IN SELECT so.id, so.warehouse_id FROM fleet.service_order so
                WHERE so.status = 'completed' AND EXISTS (SELECT 1 FROM fleet.service_order_line l WHERE l.service_order_id = so.id AND l.qty_issued < l.qty_required)
                ORDER BY so.id LOOP
        UPDATE fleet.service_order_line l SET qty_issued = k.issued_qty, qty_reserved = 0
        FROM inventory.issue_service_kit(
                 v_so.warehouse_id,
                 (SELECT array_agg(id ORDER BY part_id) FROM fleet.service_order_line WHERE service_order_id = v_so.id),
                 (SELECT array_agg(part_id ORDER BY part_id) FROM fleet.service_order_line WHERE service_order_id = v_so.id),
                 (SELECT array_agg(qty_required - qty_issued ORDER BY part_id) FROM fleet.service_order_line WHERE service_order_id = v_so.id)) k
        WHERE l.id = k.service_order_line_id;
    END LOOP;
END $$;

-- An unplanned chopper rebuild at Panevėžys needs 8 x CP-4821 (no stock reserved yet) — UC01 maintenance demand
SELECT pg_temp.t001_act_as('00000000-0000-4000-8000-000000000004');
WITH so AS (
    INSERT INTO fleet.service_order (machine_id, warehouse_id, created_by)
    SELECT m.id, w.id, '00000000-0000-4000-8000-000000000004'
    FROM fleet.machine m, inventory.warehouse w WHERE m.vin = 'LX901PNV2026' AND w.code = 'PNV'
    RETURNING id)
INSERT INTO fleet.service_order_line (service_order_id, part_id, qty_required)
SELECT so.id, p.id, 8 FROM so, catalog.part p WHERE p.oem_part_number = 'CP-4821';

-- Fleet screens (STAGE_6_PLAN 6j/6k): every service-order state and both due edge cases exist after "Load demo data".
-- The chopper rebuild above stays "reserving": its ServiceOrderDrafted was never sent, so Inventory never answered.
-- Overdue: a John Deere 6R 150 in Vilnius, 80 h past its 2,500 h engine service, with no open order.
INSERT INTO fleet.machine (vin, model_id, customer_id, operating_condition_id, home_warehouse_id, manufacture_year, commissioned_on)
SELECT 'JD6R150VLN2019OVD', mm.id, c.id, oc.id, w.id, 2019, DATE '2019-04-01'
FROM fleet.machine_model mm, inventory.warehouse w, ref.operating_condition oc,
     LATERAL (SELECT id FROM fleet.customer WHERE region_warehouse_id = w.id ORDER BY id LIMIT 1) c
WHERE mm.model_name = '6R 150' AND w.code = 'VLN' AND oc.code = 'NORMAL';
-- Due in a few hours: three Fendt 724 Vario of one Klaipėda farm, commissioned ten months ago and 8 h before their first
-- 750 h engine service (due by hours: the yearly calendar interval ends about six weeks later). FT724KLP2024DEMO is the
-- one of the demo walkthrough; the end-to-end scenario (6l) uses one per viewport, as each run services its machine.
INSERT INTO fleet.machine (vin, model_id, customer_id, operating_condition_id, home_warehouse_id, manufacture_year, commissioned_on)
SELECT v.vin, mm.id, c.id, oc.id, w.id, 2025, current_date - 310
FROM (VALUES ('FT724KLP2024DEMO'), ('FT724KLP2024DEMO2'), ('FT724KLP2024DEMO3')) AS v (vin),
     fleet.machine_model mm, inventory.warehouse w, ref.operating_condition oc,
     LATERAL (SELECT id FROM fleet.customer WHERE region_warehouse_id = w.id ORDER BY id LIMIT 1) c
WHERE mm.model_name = '724 Vario' AND w.code = 'KLP' AND oc.code = 'NORMAL'
ORDER BY v.vin;
INSERT INTO fleet.engine_hours_reading (machine_id, reading_hours, read_at, source)
SELECT m.id, r.hours, ((current_date - r.days_ago) + time '09:00') AT TIME ZONE 'Europe/Vilnius', r.source::fleet.reading_source
FROM fleet.machine m
JOIN (VALUES ('JD6R150VLN2019OVD', 300, 2300.0, 'import'), ('JD6R150VLN2019OVD', 200, 2420.0, 'import'),
             ('JD6R150VLN2019OVD', 100, 2560.0, 'import'), ('JD6R150VLN2019OVD', 20, 2580.0, 'import'),
             ('FT724KLP2024DEMO', 300, 60.0, 'import'), ('FT724KLP2024DEMO', 200, 300.0, 'import'),
             ('FT724KLP2024DEMO', 100, 520.0, 'import'), ('FT724KLP2024DEMO', 1, 742.0, 'telematics')) AS r (vin, days_ago, hours, source)
  ON m.vin = r.vin OR (r.vin = 'FT724KLP2024DEMO' AND m.vin IN ('FT724KLP2024DEMO2', 'FT724KLP2024DEMO3'))
ORDER BY m.id, r.days_ago DESC;

-- Reserves an order's kit and stores the answer, as the consumers of ServiceOrderDrafted and ServiceKitReserved do.
CREATE FUNCTION pg_temp.t001_reserve_kit(p_order bigint) RETURNS void LANGUAGE sql AS $$
    UPDATE fleet.service_order_line l SET qty_reserved = k.reserved_qty
    FROM inventory.reserve_service_kit(
             (SELECT warehouse_id FROM fleet.service_order WHERE id = p_order),
             (SELECT array_agg(id ORDER BY part_id) FROM fleet.service_order_line WHERE service_order_id = p_order),
             (SELECT array_agg(part_id ORDER BY part_id) FROM fleet.service_order_line WHERE service_order_id = p_order),
             (SELECT array_agg(qty_required ORDER BY part_id) FROM fleet.service_order_line WHERE service_order_id = p_order),
             now() + interval '7 days') k
    WHERE l.id = k.service_order_line_id;
    UPDATE fleet.service_order SET kit_reserved_at = now() WHERE id = p_order;
$$;

-- An unplanned repair needing more than the hub holds: `p_extra` units above what is available of a part that is in no kit.
CREATE FUNCTION pg_temp.t001_short_repair(p_offset int, p_extra numeric) RETURNS bigint LANGUAGE sql AS $$
    WITH pick AS (
        SELECT b.warehouse_id, b.part_id, b.on_hand_qty - b.reserved_qty AS available
        FROM inventory.stock_balance b
        WHERE b.on_hand_qty - b.reserved_qty BETWEEN 1 AND 4
          AND NOT EXISTS (SELECT 1 FROM fleet.maintenance_plan_part k WHERE k.part_id = b.part_id)
          AND b.part_id <> (SELECT id FROM catalog.part WHERE oem_part_number = 'CP-4821')   -- the UC01 scenario stays as it is
          AND EXISTS (SELECT 1 FROM fleet.machine m WHERE m.home_warehouse_id = b.warehouse_id)
        ORDER BY b.part_id, b.warehouse_id OFFSET p_offset LIMIT 1
    ), so AS (
        INSERT INTO fleet.service_order (machine_id, warehouse_id, created_by)
        SELECT (SELECT id FROM fleet.machine WHERE home_warehouse_id = pick.warehouse_id ORDER BY id LIMIT 1),
               pick.warehouse_id, '00000000-0000-4000-8000-000000000004'
        FROM pick
        RETURNING id
    ), line AS (
        INSERT INTO fleet.service_order_line (service_order_id, part_id, qty_required)
        SELECT so.id, pick.part_id, pick.available + p_extra FROM so, pick
        RETURNING service_order_id
    )
    SELECT service_order_id FROM line;
$$;

SELECT pg_temp.t001_act_as('00000000-0000-4000-8000-000000000004');   -- Mantas, service engineer
-- In progress: the first full kit of the other machines is started.
UPDATE fleet.service_order SET status = 'in_progress'
WHERE id = (SELECT so.id FROM fleet.service_order so JOIN fleet.machine m ON m.id = so.machine_id
            WHERE so.status = 'parts_reserved' AND m.vin <> 'LX901PNV2026' ORDER BY so.id LIMIT 1);
-- Cancelled: the next one, and Inventory releases its holds (ServiceOrderCancelled, PR 6h3).
SELECT fleet.cancel_service_order(x.id, 'Customer postponed the service to after the harvest'),
       inventory.release_service_kit(ARRAY(SELECT id FROM fleet.service_order_line WHERE service_order_id = x.id))
FROM (SELECT so.id FROM fleet.service_order so JOIN fleet.machine m ON m.id = so.machine_id
      WHERE so.status = 'parts_reserved' AND m.vin <> 'LX901PNV2026' ORDER BY so.id LIMIT 1) x;
-- Partial kit: an open repair of which the hub holds all it has, 3 units short.
SELECT pg_temp.t001_reserve_kit(pg_temp.t001_short_repair(0, 3));
-- Completed with a shortfall (F7): a repair 2 units short is completed; Inventory issues what it has.
SELECT pg_temp.t001_short_repair(1, 2) AS short_order \gset
SELECT pg_temp.t001_reserve_kit(:short_order);
SELECT fleet.complete_service_order(:short_order, m.current_engine_hours)
FROM fleet.service_order so JOIN fleet.machine m ON m.id = so.machine_id WHERE so.id = :short_order;
UPDATE fleet.service_order_line l SET qty_issued = k.issued_qty, qty_reserved = 0
FROM inventory.issue_service_kit(
         (SELECT warehouse_id FROM fleet.service_order WHERE id = :short_order),
         (SELECT array_agg(id ORDER BY part_id) FROM fleet.service_order_line WHERE service_order_id = :short_order),
         (SELECT array_agg(part_id ORDER BY part_id) FROM fleet.service_order_line WHERE service_order_id = :short_order),
         (SELECT array_agg(qty_required ORDER BY part_id) FROM fleet.service_order_line WHERE service_order_id = :short_order)) k
WHERE l.id = k.service_order_line_id;

-- Service history imported from the ERP: each plan of a generated machine that has no service order for it gets one completed
-- service at the last threshold it passed, dated across 107 % of the plan's calendar interval (spread by a hash of VIN
-- and plan, so the random sequence of this script stays as it was): a few percent of the fleet is calendar-overdue,
-- as at a real dealer, instead of almost all of it with the commissioning date as anchor. Machines that have not
-- reached their first threshold keep the commissioning date. The named machines above are left exactly as they are.
INSERT INTO fleet.service_order (machine_id, maintenance_plan_id, warehouse_id, status, due_engine_hours, trigger_engine_hours,
                                 completed_engine_hours, completed_at, created_at)
SELECT m.id, mp.id, m.home_warehouse_id, 'completed', h.threshold, h.threshold, h.threshold, h.done_at, h.done_at - interval '2 days'
FROM fleet.machine m
JOIN fleet.maintenance_plan mp ON mp.model_id = m.model_id AND mp.interval_months IS NOT NULL
JOIN ref.operating_condition oc ON oc.id = m.operating_condition_id
CROSS JOIN LATERAL (SELECT round(mp.interval_hours * oc.interval_factor, 1) AS e,
                           (hashtextextended(m.vin || ':' || mp.id, 0) & 1023)::numeric / 1024 AS u) x
CROSS JOIN LATERAL (SELECT floor(m.current_engine_hours / x.e) * x.e AS threshold,
                           now() - make_interval(days => floor(x.u * 1.07 * mp.interval_months * 30.4)::int) AS done_at) h
WHERE m.vin NOT IN ('LX901PNV2026', 'JD6R150VLN2019OVD', 'FT724KLP2024DEMO', 'FT724KLP2024DEMO2', 'FT724KLP2024DEMO3')
  AND NOT EXISTS (SELECT 1 FROM fleet.service_order so WHERE so.machine_id = m.id AND so.maintenance_plan_id = mp.id)
  AND h.threshold > 0
ORDER BY m.id, mp.id;

-- The Fleet screens need every state: fail the load rather than demo an empty tab.
DO $$
DECLARE
    v_overdue_pct numeric;
BEGIN
    IF (SELECT count(DISTINCT status) FROM fleet.service_order) < 5
       OR NOT EXISTS (SELECT 1 FROM fleet.service_order WHERE status = 'draft' AND kit_reserved_at IS NULL)
       OR NOT EXISTS (SELECT 1 FROM fleet.service_order so JOIN fleet.service_order_line l ON l.service_order_id = so.id
                      WHERE so.status = 'draft' AND so.kit_reserved_at IS NOT NULL AND l.qty_reserved BETWEEN 0.001 AND l.qty_required - 0.001)
       OR NOT EXISTS (SELECT 1 FROM fleet.service_order so JOIN fleet.service_order_line l ON l.service_order_id = so.id
                      WHERE so.status = 'completed' AND l.qty_issued < l.qty_required)
       OR NOT EXISTS (SELECT 1 FROM fleet.machine WHERE vin IN ('JD6R150VLN2019OVD', 'FT724KLP2024DEMO', 'FT724KLP2024DEMO2', 'FT724KLP2024DEMO3')
                      HAVING count(*) = 4) THEN
        RAISE EXCEPTION 'T001: a Fleet demo state is missing (statuses, reserving, partial kit, shortfall or the named machines)';
    END IF;
    -- A believable due list: of the generated machines' plans without an open order, 5–10 % are calendar-overdue.
    SELECT round(100.0 * count(*) FILTER (WHERE coalesce(l.last_done, m.commissioned_on) + make_interval(months => mp.interval_months) < current_date)
                 / count(*), 1)
      INTO v_overdue_pct
    FROM fleet.machine m
    JOIN fleet.maintenance_plan mp ON mp.model_id = m.model_id AND mp.interval_months IS NOT NULL
    LEFT JOIN LATERAL (SELECT max(so.completed_at)::date AS last_done FROM fleet.service_order so
                       WHERE so.machine_id = m.id AND so.maintenance_plan_id = mp.id AND so.status = 'completed') l ON true
    WHERE m.vin NOT IN ('LX901PNV2026', 'JD6R150VLN2019OVD', 'FT724KLP2024DEMO', 'FT724KLP2024DEMO2', 'FT724KLP2024DEMO3')
      AND NOT EXISTS (SELECT 1 FROM fleet.service_order so WHERE so.machine_id = m.id AND so.maintenance_plan_id = mp.id
                      AND so.status IN ('draft', 'parts_reserved', 'in_progress'));
    IF v_overdue_pct NOT BETWEEN 5 AND 10 THEN
        RAISE EXCEPTION 'T001: % %% of the generated fleet is calendar-overdue, expected 5 to 10 %%', v_overdue_pct;
    END IF;
    RAISE NOTICE 'T001: % %% of the generated fleet is calendar-overdue', v_overdue_pct;
END $$;

-- Exceptional demand: planner flags the strongest 3-sigma outliers (US08)
SELECT pg_temp.t001_act_as('00000000-0000-4000-8000-000000000009');
SELECT inventory.flag_exceptional_demand(c.movement_id, c.occurred_at,
       format('One-off bulk order (z = %s), fleet overhaul of a farming group', c.z_score), 'auto_3sigma')
FROM inventory.find_exceptional_demand_candidates(3, interval '24 months', 8) c
ORDER BY c.z_score DESC LIMIT 12;

-- ---------------------------------------------------------------------------
-- 9. Purchase orders (US13, US14): four-eyes approval, export, partial receipt
-- ---------------------------------------------------------------------------
SELECT pg_temp.t001_act_as('00000000-0000-4000-8000-000000000002');   -- Rasa
INSERT INTO procurement.purchase_order (supplier_id, warehouse_id, created_by)
SELECT s.id, w.id, '00000000-0000-4000-8000-000000000002' FROM procurement.supplier s, inventory.warehouse w
WHERE s.code = 'JD-PD' AND w.code = 'VLN' RETURNING id AS po_big \gset
INSERT INTO procurement.purchase_order_line (purchase_order_id, part_id, quantity)
SELECT :po_big, p.id, sp.moq * CASE WHEN u.code = 'L' THEN 10 ELSE 30 END
FROM catalog.part p JOIN procurement.supplier_part sp ON sp.part_id = p.id AND sp.is_preferred
JOIN ref.unit_of_measure u ON u.id = p.uom_id
WHERE p.oem_part_number IN ('RE900001', 'RE900002', 'RE900003', 'RE900004');
SELECT procurement.submit_purchase_order(:po_big);
SELECT pg_temp.t001_act_as('00000000-0000-4000-8000-000000000010');   -- Vida (sponsor) approves > EUR 5,000
SELECT procurement.approve_purchase_order(:po_big);
SELECT pg_temp.t001_act_as('00000000-0000-4000-8000-000000000002');
SELECT procurement.export_purchase_order(:po_big);
SELECT pg_temp.t001_act_as('00000000-0000-4000-8000-000000000008');   -- Rūta receives part of it in Vilnius
SELECT procurement.receive_purchase_order_line(l.id, l.quantity / 2)
FROM procurement.purchase_order_line l WHERE l.purchase_order_id = :po_big ORDER BY l.id LIMIT 2;

-- Small PO by Darius, self-approved (allowed below EUR 5,000) and fully received in Kaunas by admin
SELECT pg_temp.t001_act_as('00000000-0000-4000-8000-000000000003');
INSERT INTO procurement.purchase_order (supplier_id, warehouse_id, created_by)
SELECT s.id, w.id, '00000000-0000-4000-8000-000000000003' FROM procurement.supplier s, inventory.warehouse w
WHERE s.code = 'AGCO-PD' AND w.code = 'KNS' RETURNING id AS po_small \gset
INSERT INTO procurement.purchase_order_line (purchase_order_id, part_id, quantity)
SELECT :po_small, p.id, sp.moq FROM catalog.part p JOIN procurement.supplier_part sp ON sp.part_id = p.id AND sp.is_preferred
WHERE p.oem_part_number IN ('V900000001', 'F900001.010');
SELECT procurement.submit_purchase_order(:po_small);
SELECT procurement.approve_purchase_order(:po_small);
SELECT pg_temp.t001_act_as('00000000-0000-4000-8000-000000000011');
SELECT procurement.receive_purchase_order_line(l.id, l.quantity) FROM procurement.purchase_order_line l WHERE l.purchase_order_id = :po_small;

-- Urgent harvest order (US16), left pending approval
SELECT pg_temp.t001_act_as('00000000-0000-4000-8000-000000000001');   -- Tomas
INSERT INTO procurement.purchase_order (supplier_id, warehouse_id, is_urgent, urgent_reason, created_by)
SELECT s.id, w.id, true, 'Urgent Harvest Dispatch: Lexion breakdown, customer CUST-000017', '00000000-0000-4000-8000-000000000001'
FROM procurement.supplier s, inventory.warehouse w WHERE s.code = 'CLAAS-PL' AND w.code = 'PNV' RETURNING id AS po_urgent \gset
INSERT INTO procurement.purchase_order_line (purchase_order_id, part_id, quantity)
SELECT :po_urgent, p.id, 2 FROM catalog.part p WHERE p.oem_part_number = '0006673220';
SELECT procurement.submit_purchase_order(:po_urgent);

-- ---------------------------------------------------------------------------
-- 10. ML model registry, benchmarks (KPI-02) and automatic promotion
-- ---------------------------------------------------------------------------
CALL mart.refresh_all();

INSERT INTO forecast.ml_model_version (algorithm, version_label, status, training_from, training_to, holdout_from, holdout_to,
                                       hyperparameters, artifact_uri, artifact_sha256)
VALUES ('fasttree', 'v1.0', 'training', :'hist_start'::date, :'go_live'::date - 92, :'go_live'::date - 91, :'go_live'::date - 1,
        '{"numberOfTrees": 200, "numberOfLeaves": 31, "minimumExampleCountPerLeaf": 10, "learningRate": 0.1}',
        'https://smartsparedemo.blob.core.windows.net/models/fasttree-v1.0.zip', encode(sha256('model-v1.0'::bytea), 'hex'))
RETURNING id AS m10 \gset
INSERT INTO forecast.benchmark_result (model_version_id, method, demand_class_code, series_count, rmsse, mase)
SELECT :m10, m.method::forecast.forecast_method, c.code, c.n, round(m.rmsse * c.f, 4), round(m.mase * c.f, 4)
FROM (VALUES ('zero', 0.93, 0.77), ('ma3', 0.74, 1.21), ('ses', 0.69, 1.18), ('croston', 0.78, 1.26),
             ('sba', 0.77, 1.24), ('fasttree', 0.60, 1.10)) AS m (method, rmsse, mase)
CROSS JOIN (SELECT demand_class_code AS code, count(*)::int AS n,
                   CASE demand_class_code WHEN 'LUMPY' THEN 1.15 WHEN 'SMOOTH' THEN 0.80 WHEN 'ERRATIC' THEN 1.05 ELSE 1.0 END AS f
            FROM mart.mv_demand_profile GROUP BY demand_class_code) c;
UPDATE forecast.ml_model_version SET status = 'candidate', rmsse = 0.6050, mase = 1.1100, mae = 0.4120, rmse = 0.9310, trained_at = now() - interval '6 days'
WHERE id = :m10;
SELECT forecast.promote_model(:m10) AS v10_promoted;              -- >= 10 % better than SES -> active

INSERT INTO forecast.ml_model_version (algorithm, version_label, status, training_from, training_to, holdout_from, holdout_to,
                                       hyperparameters, artifact_uri, artifact_sha256)
VALUES ('fasttree', 'v1.1', 'training', :'hist_start'::date + 7, :'go_live'::date - 85, :'go_live'::date - 84, :'go_live'::date - 1,
        '{"numberOfTrees": 400, "numberOfLeaves": 63, "minimumExampleCountPerLeaf": 5, "learningRate": 0.05}',
        'https://smartsparedemo.blob.core.windows.net/models/fasttree-v1.1.zip', encode(sha256('model-v1.1'::bytea), 'hex'))
RETURNING id AS m11 \gset
INSERT INTO forecast.benchmark_result (model_version_id, method, demand_class_code, series_count, rmsse, mase)
SELECT :m11, method, demand_class_code, series_count,
       CASE WHEN method = 'fasttree' THEN round(rmsse * 1.10, 4) ELSE rmsse END, mase
FROM forecast.benchmark_result WHERE model_version_id = :m10;
UPDATE forecast.ml_model_version SET status = 'candidate', rmsse = 0.6600, mase = 1.1500, trained_at = now() - interval '1 day' WHERE id = :m11;
SELECT forecast.promote_model(:m11) AS v11_promoted;              -- only ~4 % better -> rejected, v1.0 stays active

-- ---------------------------------------------------------------------------
-- 11. Forecast runs: a past run (for accuracy) and the current 8-week run
-- ---------------------------------------------------------------------------
INSERT INTO forecast.forecast_run (model_version_id, status, horizon_weeks, first_week, triggered_by, started_at, finished_at)
VALUES (:m10, 'succeeded', 8, :'go_live'::date - 28, 'scheduler', now() - interval '29 days', now() - interval '29 days' + interval '41 seconds'),
       (:m10, 'succeeded', 8, (date_trunc('week', current_date) + interval '1 week')::date, 'scheduler', now() - interval '2 hours', now() - interval '2 hours' + interval '38 seconds');
SELECT max(id) AS run_now, min(id) AS run_past FROM forecast.forecast_run \gset

-- Synthetic stand-in for ML.NET output: 13-week mean x seasonal index (documented as such).
INSERT INTO forecast.demand_forecast (forecast_run_id, warehouse_id, part_id, week_start, forecast_qty)
SELECT r.id, pr.warehouse_id, pr.part_id, (r.first_week + 7 * h)::date,
       round(coalesce(base.mean13, 0) * ac.seasonal_weight / 1.15 * (0.85 + 0.3 * random())::numeric, 3)
FROM forecast.forecast_run r
CROSS JOIN generate_series(0, 7) h
JOIN mart.mv_demand_profile pr ON true
JOIN ref.agro_calendar_week ac ON ac.iso_week = extract(week FROM (r.first_week + 7 * h))
CROSS JOIN LATERAL (SELECT sum(d.demand_qty) / 13.0 AS mean13 FROM mart.mv_weekly_demand d
                    WHERE d.warehouse_id = pr.warehouse_id AND d.part_id = pr.part_id
                      AND d.week_start >= r.first_week - 91 AND d.week_start < r.first_week) base
ORDER BY r.id, pr.warehouse_id, pr.part_id, h;

-- UC01: Panevėžys expects 4 x CP-4821 of stochastic demand in the next weeks.
INSERT INTO forecast.demand_forecast (forecast_run_id, warehouse_id, part_id, week_start, forecast_qty)
SELECT :run_now, w.id, p.id, (r.first_week + 7 * h)::date, 0.5
FROM forecast.forecast_run r, inventory.warehouse w, catalog.part p, generate_series(0, 7) h
WHERE r.id = :run_now AND w.code = 'PNV' AND p.oem_part_number = 'CP-4821'
ON CONFLICT (forecast_run_id, warehouse_id, part_id, week_start) DO UPDATE SET forecast_qty = EXCLUDED.forecast_qty;

-- ---------------------------------------------------------------------------
-- 12. Recommendations (simplified SQL version of the C# rebalancing engine)
-- ---------------------------------------------------------------------------
CREATE TEMP TABLE tmp_pos AS
SELECT wp.warehouse_id, wp.part_id, sp.*
FROM inventory.warehouse_part wp
JOIN mart.mv_demand_profile pr ON pr.warehouse_id = wp.warehouse_id AND pr.part_id = wp.part_id
CROSS JOIN LATERAL forecast.fn_stock_position(wp.warehouse_id, wp.part_id, :run_now, 1) sp
UNION
SELECT wp.warehouse_id, wp.part_id, sp.*
FROM inventory.warehouse_part wp JOIN catalog.part p ON p.id = wp.part_id,
     LATERAL forecast.fn_stock_position(wp.warehouse_id, wp.part_id, :run_now, 1) sp
WHERE p.oem_part_number = 'CP-4821';

-- Transfers: deficit at a hub, donor with surplus above safety + own forecast, cheapest route.
INSERT INTO forecast.recommendation (forecast_run_id, kind, warehouse_id, part_id, donor_warehouse_id,
       base_demand_qty, maintenance_demand_qty, lead_time_buffer_qty, safety_stock_qty, available_qty, in_transit_qty,
       net_position_qty, recommended_qty, estimated_cost_eur, alternative_cost_eur)
SELECT :run_now, 'transfer', d.warehouse_id, d.part_id, dn.donor_id,
       d.base_demand_qty, d.maintenance_demand_qty, d.lead_time_buffer_qty, d.safety_stock_qty, d.available_qty, d.in_transit_qty,
       d.net_position_qty, ceil(-d.net_position_qty), dn.transport_cost_eur,
       round(ceil(-d.net_position_qty) * p.unit_cost * 0.20 + 230, 2)
FROM tmp_pos d
JOIN catalog.part p ON p.id = d.part_id
CROSS JOIN LATERAL (
    SELECT o.warehouse_id AS donor_id, r.transport_cost_eur
    FROM tmp_pos o
    JOIN inventory.warehouse_route r ON r.from_warehouse_id = o.warehouse_id AND r.to_warehouse_id = d.warehouse_id
    WHERE o.part_id = d.part_id AND o.warehouse_id <> d.warehouse_id
      AND o.available_qty - o.safety_stock_qty - o.base_demand_qty >= ceil(-d.net_position_qty)
    ORDER BY r.transport_cost_eur LIMIT 1) dn
WHERE d.net_position_qty < 0 AND d.available_qty + d.in_transit_qty < d.safety_stock_qty + d.base_demand_qty + d.maintenance_demand_qty
ORDER BY p.oem_part_number = 'CP-4821' DESC, (-d.net_position_qty * p.unit_cost) DESC   -- UC01 case always included
LIMIT 60;

-- Purchases: no donor available -> preferred supplier, quantity rounded to MOQ / pack size.
INSERT INTO forecast.recommendation (forecast_run_id, kind, warehouse_id, part_id, supplier_id,
       base_demand_qty, maintenance_demand_qty, lead_time_buffer_qty, safety_stock_qty, available_qty, in_transit_qty,
       net_position_qty, recommended_qty, estimated_cost_eur)
SELECT :run_now, 'purchase', x.warehouse_id, x.part_id, sp.supplier_id,
       x.base_demand_qty, x.maintenance_demand_qty, x.lead_time_buffer_qty, x.safety_stock_qty, x.available_qty, x.in_transit_qty,
       x.net_position_qty, q.qty, round(q.qty * pr.unit_price, 2)
FROM (SELECT d.warehouse_id, d.part_id, s.*
      FROM tmp_pos d
      CROSS JOIN LATERAL forecast.fn_stock_position(d.warehouse_id, d.part_id, :run_now, NULL) s
      WHERE d.net_position_qty < 0
        AND NOT EXISTS (SELECT 1 FROM forecast.recommendation r
                        WHERE r.forecast_run_id = :run_now AND r.warehouse_id = d.warehouse_id AND r.part_id = d.part_id)) x
JOIN procurement.supplier_part sp ON sp.part_id = x.part_id AND sp.is_preferred
JOIN procurement.supplier_price pr ON pr.supplier_part_id = sp.id AND pr.valid_during @> current_date
CROSS JOIN LATERAL (SELECT ceil(greatest(-x.net_position_qty, sp.moq) / sp.pack_size) * sp.pack_size AS qty) q
WHERE x.net_position_qty < 0
ORDER BY (-x.net_position_qty * pr.unit_price) DESC
LIMIT 60;

-- Decisions by brand managers / planner: ~80 % accepted, overrides with reason, rejections; some left open.
DO $$
DECLARE
    r       record;
    v_i     integer := 0;
    v_pack  numeric;
BEGIN
    FOR r IN SELECT rc.id, rc.kind, rc.recommended_qty, rc.part_id, rc.supplier_id,
                    (SELECT oem_part_number FROM catalog.part WHERE id = rc.part_id) AS oem
             FROM forecast.recommendation rc ORDER BY rc.id LOOP
        v_i := v_i + 1;
        PERFORM pg_temp.t001_act_as((CASE v_i % 3 WHEN 0 THEN '00000000-0000-4000-8000-000000000001'
                                                      WHEN 1 THEN '00000000-0000-4000-8000-000000000002'
                                                      ELSE '00000000-0000-4000-8000-000000000009' END)::uuid);
        BEGIN
            IF r.oem = 'CP-4821' THEN
                PERFORM pg_temp.t001_act_as('00000000-0000-4000-8000-000000000001');   -- Tomas, UC01 step 10
                PERFORM forecast.decide_recommendation(r.id, 'accept');
            ELSIF v_i % 10 = 7 THEN
                CONTINUE;                                                              -- stays open
            ELSIF v_i % 17 = 3 THEN
                PERFORM forecast.decide_recommendation(r.id, 'reject', NULL, 'Customer cancelled the overhaul; demand will not materialise');
            ELSIF v_i % 9 = 4 THEN
                IF r.kind = 'purchase' THEN
                    SELECT pack_size INTO v_pack FROM procurement.supplier_part WHERE supplier_id = r.supplier_id AND part_id = r.part_id;
                    PERFORM forecast.decide_recommendation(r.id, 'override', r.recommended_qty + v_pack,
                                                           'Accelerated field operations expected (rain forecast), UC01 A2');
                ELSIF r.recommended_qty > 1 THEN
                    PERFORM forecast.decide_recommendation(r.id, 'override', r.recommended_qty - 1,
                                                           'One unit will be covered by a customer return');
                ELSE
                    PERFORM forecast.decide_recommendation(r.id, 'accept');
                END IF;
            ELSE
                PERFORM forecast.decide_recommendation(r.id, 'accept');
            END IF;
        EXCEPTION WHEN sqlstate 'SS201' OR sqlstate 'SS200' THEN
            NULL;     -- donor already used by an earlier transfer of the same SKU: left open for the next run
        END;
    END LOOP;
END $$;

-- UC01 step 11 + US11: Eglė dispatches from Klaipėda, Lukas receives in Panevėžys.
SELECT pg_temp.t001_act_as('00000000-0000-4000-8000-000000000006');
SELECT inventory.dispatch_transfer(t.id) FROM inventory.transfer_order t
JOIN inventory.warehouse w ON w.id = t.from_warehouse_id WHERE w.code = 'KLP' AND t.status = 'approved';
SELECT pg_temp.t001_act_as('00000000-0000-4000-8000-000000000007');
SELECT inventory.receive_transfer(t.id) FROM inventory.transfer_order t
JOIN inventory.warehouse w ON w.id = t.to_warehouse_id WHERE w.code = 'PNV' AND t.status = 'dispatched';
-- Admin dispatches a few other approved transfers (they stay "in transit").
SELECT pg_temp.t001_act_as('00000000-0000-4000-8000-000000000011');
SELECT inventory.dispatch_transfer(t.id) FROM inventory.transfer_order t WHERE t.status = 'approved' AND t.id % 2 = 0;

-- Consolidated draft POs from purchase recommendations: submit and approve some.
SELECT pg_temp.t001_act_as('00000000-0000-4000-8000-000000000009');
SELECT procurement.submit_purchase_order(po.id) FROM procurement.purchase_order po
WHERE po.status = 'draft' AND po.id % 2 = 1 AND EXISTS (SELECT 1 FROM procurement.purchase_order_line l WHERE l.purchase_order_id = po.id);
SELECT pg_temp.t001_act_as('00000000-0000-4000-8000-000000000010');
SELECT procurement.approve_purchase_order(po.id) FROM procurement.purchase_order po
WHERE po.status = 'pending_approval' AND NOT po.is_urgent AND po.id % 3 <> 0;

-- GDPR: the former employee is anonymised by the administrator.
SELECT pg_temp.t001_act_as('00000000-0000-4000-8000-000000000011');
SELECT iam.anonymise_employee('00000000-0000-4000-8000-000000000012');

-- Fleet's copy of the catalogue (STAGE_6_PLAN 5.2), as if every PartCatalogChanged had been delivered: the parts above
-- were written by SQL, so no event exists for them.
INSERT INTO fleet.part_snapshot (part_id, brand_code, oem_part_number, description, unit_of_measure, is_active, source_occurred_at)
SELECT p.id, b.code, p.oem_part_number, p.description, u.code, p.is_active, now()
FROM catalog.part p JOIN catalog.brand b ON b.id = p.brand_id JOIN ref.unit_of_measure u ON u.id = p.uom_id;

-- Part manufacturers (STAGE_7_PLAN 3.1, PR 7h3): three makers; the oil filters of John Deere are made by MANN+HUMMEL.
INSERT INTO catalog.manufacturer (code, name, country_code, website) VALUES
    ('MANN', 'MANN+HUMMEL', 'DE', 'https://www.mann-hummel.com'),
    ('BOSCH', 'Robert Bosch', 'DE', 'https://www.bosch.com'),
    ('DONALDSON', 'Donaldson', 'US', 'https://www.donaldson.com');
UPDATE catalog.part SET manufacturer_id = (SELECT id FROM catalog.manufacturer WHERE code = 'MANN')
 WHERE brand_id = (SELECT id FROM catalog.brand WHERE code = 'JD')
   AND category_id = (SELECT id FROM ref.part_category WHERE code = 'OIL_FILTER');

-- Fleet's and Identity's copies of the brands (STAGE_7_PLAN 3.2), as if inventory.brand-catalog-publish had run; dated
-- at the epoch, so any real BrandCatalogChanged is newer.
INSERT INTO fleet.brand_snapshot (brand_id, code, name, is_archived, source_occurred_at)
SELECT id, code, name, is_archived, 'epoch' FROM catalog.brand ON CONFLICT (brand_id) DO NOTHING;
INSERT INTO iam.brand_snapshot (brand_id, code, name, is_archived, source_occurred_at)
SELECT id, code, name, is_archived, 'epoch' FROM catalog.brand ON CONFLICT (brand_id) DO NOTHING;

SELECT pg_temp.t001_act_as(NULL);
CALL mart.refresh_all();
COMMIT;

SET client_min_messages = error;
ANALYZE;
RESET client_min_messages;

-- Summary
SELECT 'parts' AS item, count(*) FROM catalog.part
UNION ALL SELECT 'warehouse_parts (series)', count(*) FROM inventory.warehouse_part
UNION ALL SELECT 'stock movements', count(*) FROM inventory.stock_movement
UNION ALL SELECT 'fleet part snapshots', count(*) FROM fleet.part_snapshot
UNION ALL SELECT 'machines', count(*) FROM fleet.machine
UNION ALL SELECT 'engine-hour readings', count(*) FROM fleet.engine_hours_reading
UNION ALL SELECT 'service orders', count(*) FROM fleet.service_order
UNION ALL SELECT 'reservations', count(*) FROM inventory.stock_reservation
UNION ALL SELECT 'transfer orders', count(*) FROM inventory.transfer_order
UNION ALL SELECT 'purchase orders', count(*) FROM procurement.purchase_order
UNION ALL SELECT 'forecast values', count(*) FROM forecast.demand_forecast
UNION ALL SELECT 'recommendations', count(*) FROM forecast.recommendation
UNION ALL SELECT 'audit records', count(*) FROM audit.audit_log
UNION ALL SELECT 'import batches', count(*) FROM raw.import_batch;
