-- =============================================================================
-- R001 — Reference (fixed) data. Repeatable: re-applied automatically by the
-- migrator whenever this file changes. Idempotent (upsert by natural key).
-- Sources: BA document (7 hubs, roles, demand classes, season peaks F8,
-- condition factors F5), OEM documentation summary (Appendix C.9).
-- =============================================================================

INSERT INTO ref.unit_of_measure (code, name, is_fractional) VALUES
    ('PCS', 'Piece',     false),
    ('SET', 'Set / kit', false),
    ('L',   'Litre',     true),
    ('KG',  'Kilogram',  true),
    ('M',   'Metre',     true)
ON CONFLICT (code) DO UPDATE SET name = EXCLUDED.name, is_fractional = EXCLUDED.is_fractional;

INSERT INTO ref.brand (code, name, country_code) VALUES
    ('CLAAS',  'Claas',           'DE'),
    ('JD',     'John Deere',      'US'),
    ('FENDT',  'Fendt',           'DE'),
    ('MF',     'Massey Ferguson', 'US'),
    ('LEMKEN', 'Lemken',          'DE'),
    ('VAD',    'Väderstad',       'SE'),
    ('CASE',   'Case IH',         'US'),
    ('NH',     'New Holland',     'IT')
ON CONFLICT (code) DO UPDATE SET name = EXCLUDED.name, country_code = EXCLUDED.country_code;

INSERT INTO ref.machine_type (code, name) VALUES
    ('TRACTOR', 'Tractor'),
    ('COMBINE', 'Combine harvester'),
    ('SEEDER',  'Seed drill / planter'),
    ('SPRAYER', 'Crop sprayer'),
    ('PLOUGH',  'Plough / soil tillage'),
    ('FORAGE',  'Forage harvester')
ON CONFLICT (code) DO UPDATE SET name = EXCLUDED.name;

-- Category tree: roots first, then children (parent resolved by code).
INSERT INTO ref.part_category (code, name, parent_id) VALUES
    ('FILTERS',    'Filters',                 NULL),
    ('LUBRICANTS', 'Oils and lubricants',     NULL),
    ('DRIVE',      'Drive train',             NULL),
    ('CUTTING',    'Cutting and threshing',   NULL),
    ('SOIL',       'Soil engaging parts',     NULL),
    ('SEEDING',    'Seeding parts',           NULL),
    ('HYDRAULICS', 'Hydraulics',              NULL),
    ('ELECTRICAL', 'Electrical',              NULL)
ON CONFLICT (code) DO UPDATE SET name = EXCLUDED.name;

INSERT INTO ref.part_category (code, name, parent_id)
SELECT c.code, c.name, p.id
FROM (VALUES
    ('OIL_FILTER',     'Engine oil filters',          'FILTERS'),
    ('FUEL_FILTER',    'Fuel filters',                'FILTERS'),
    ('HYD_FILTER',     'Hydraulic / transmission filters', 'FILTERS'),
    ('AIR_FILTER',     'Air filters',                 'FILTERS'),
    ('CAB_FILTER',     'Cab filters',                 'FILTERS'),
    ('ENGINE_OIL',     'Engine oil',                  'LUBRICANTS'),
    ('TRANS_OIL',      'Transmission / hydraulic oil','LUBRICANTS'),
    ('GREASE',         'Grease',                      'LUBRICANTS'),
    ('BELTS',          'Belts',                       'DRIVE'),
    ('CHAINS',         'Chains and sprockets',        'DRIVE'),
    ('BEARINGS',       'Bearings',                    'DRIVE'),
    ('KNIVES',         'Knives and sections',         'CUTTING'),
    ('CHOPPER_BLADES', 'Straw chopper blades',        'CUTTING'),
    ('RASP_BARS',      'Rasp bars and concaves',      'CUTTING'),
    ('SHARES',         'Plough shares and points',    'SOIL'),
    ('DISCS',          'Discs',                       'SOIL'),
    ('TINES',          'Tines',                       'SOIL'),
    ('COULTERS',       'Coulters',                    'SEEDING'),
    ('METERING',       'Metering units',              'SEEDING'),
    ('HOSES',          'Hydraulic hoses',             'HYDRAULICS'),
    ('SEALS',          'Seals and O-rings',           'HYDRAULICS'),
    ('LIGHTS',         'Lights',                      'ELECTRICAL'),
    ('SENSORS',        'Sensors',                     'ELECTRICAL')
) AS c (code, name, parent_code)
JOIN ref.part_category p ON p.code = c.parent_code
ON CONFLICT (code) DO UPDATE SET name = EXCLUDED.name, parent_id = EXCLUDED.parent_id;

-- F5: John Deere halves intervals when prescribed oil/filters/fuel are not used.
INSERT INTO ref.operating_condition (code, name, interval_factor) VALUES
    ('NORMAL',      'Normal operation with OEM fluids',               1.00),
    ('HIGH_LOAD',   'Heavy draft / continuous high load',             0.80),
    ('SEVERE_DUST', 'Severe dust (harvest, dry tillage)',             0.75),
    ('NON_OEM',     'Non-prescribed oil, filters or fuel quality',    0.50)
ON CONFLICT (code) DO UPDATE SET name = EXCLUDED.name, interval_factor = EXCLUDED.interval_factor;

INSERT INTO ref.movement_type (code, name, balance_sign, is_demand, description) VALUES
    ('OPENING_BALANCE',      'Opening balance',             1, false, 'Initial stock at go-live (data migration).'),
    ('RECEIPT_PO',           'Receipt from supplier',       1, false, 'Goods receipt of a purchase order line.'),
    ('RETURN_FROM_CUSTOMER', 'Customer return',             1, false, 'Unused part returned by a customer.'),
    ('ADJUSTMENT_IN',        'Positive inventory adjustment', 1, false, 'Stock count surplus (note required).'),
    ('TRANSFER_IN',          'Transfer receipt',            1, false, 'Receipt of an inter-warehouse transfer.'),
    ('TRANSFER_OUT',         'Transfer dispatch',          -1, false, 'Dispatch of an inter-warehouse transfer.'),
    ('ISSUE_SERVICE',        'Issue to service order',     -1, true,  'Parts consumed by maintenance / repair (demand).'),
    ('ISSUE_SALE',           'Counter sale',               -1, true,  'Parts sold to a customer (demand).'),
    ('ADJUSTMENT_OUT',       'Negative inventory adjustment', -1, false, 'Stock count shortage, scrap (note required).'),
    ('HISTORICAL_DEMAND',    'Imported historical demand',  0, true,  'Demand history from the legacy ERP; no stock effect.')
ON CONFLICT (code) DO UPDATE
    SET name = EXCLUDED.name, balance_sign = EXCLUDED.balance_sign,
        is_demand = EXCLUDED.is_demand, description = EXCLUDED.description;

-- Syntetos-Boylan-Croston classification, cut-offs ADI 1.32 and CV² 0.49.
INSERT INTO ref.demand_class (code, name, adi_lower, adi_upper, cv2_lower, cv2_upper, description) VALUES
    ('SMOOTH',       'Smooth',       0,    1.32, 0,    0.49, 'Regular demand, low size variability.'),
    ('ERRATIC',      'Erratic',      0,    1.32, 0.49, 999,  'Regular occurrence, highly variable size.'),
    ('INTERMITTENT', 'Intermittent', 1.32, 999,  0,    0.49, 'Many zero periods, stable size.'),
    ('LUMPY',        'Lumpy',        1.32, 999,  0.49, 999,  'Many zero periods and variable size.')
ON CONFLICT (code) DO UPDATE
    SET name = EXCLUDED.name, adi_lower = EXCLUDED.adi_lower, adi_upper = EXCLUDED.adi_upper,
        cv2_lower = EXCLUDED.cv2_lower, cv2_upper = EXCLUDED.cv2_upper, description = EXCLUDED.description;

-- F8: peaks in February-March and July-September.
INSERT INTO ref.season_phase (code, name) VALUES
    ('WINTER_REPAIR',  'Winter repair season'),
    ('PRE_SEASON',     'Pre-season preparation (Feb-Mar peak)'),
    ('SOWING',         'Spring sowing'),
    ('CROP_CARE',      'Crop care / spraying'),
    ('HARVEST',        'Harvest (Jul-Sep peak)'),
    ('AUTUMN_TILLAGE', 'Autumn tillage and winter crops')
ON CONFLICT (code) DO UPDATE SET name = EXCLUDED.name;

INSERT INTO ref.agro_calendar_week (iso_week, season_phase_code, seasonal_weight)
SELECT w,
       CASE WHEN w <= 5  THEN 'WINTER_REPAIR'
            WHEN w <= 13 THEN 'PRE_SEASON'
            WHEN w <= 19 THEN 'SOWING'
            WHEN w <= 26 THEN 'CROP_CARE'
            WHEN w <= 39 THEN 'HARVEST'
            WHEN w <= 46 THEN 'AUTUMN_TILLAGE'
            ELSE 'WINTER_REPAIR' END,
       CASE WHEN w <= 5  THEN 0.70
            WHEN w <= 13 THEN 1.60
            WHEN w <= 19 THEN 1.20
            WHEN w <= 26 THEN 0.80
            WHEN w <= 39 THEN 1.80
            WHEN w <= 46 THEN 0.90
            ELSE 0.50 END
FROM generate_series(1, 53) AS w
ON CONFLICT (iso_week) DO UPDATE
    SET season_phase_code = EXCLUDED.season_phase_code, seasonal_weight = EXCLUDED.seasonal_weight;

INSERT INTO iam.role (code, name, description) VALUES
    ('BRAND_MANAGER',     'Category brand manager',   'Owns parts availability of assigned brands; approves transfers and POs.'),
    ('SERVICE_ENGINEER',  'Field service engineer',   'Registers machines, logs engine hours, executes service orders.'),
    ('WAREHOUSE_KEEPER',  'Local warehouse keeper',   'Dispatches and receives transfers and supplier deliveries.'),
    ('INVENTORY_PLANNER', 'Central inventory planner','Runs forecasts, configures supplier rules, flags exceptional demand.'),
    ('SPONSOR',           'Project sponsor / manager','Approves large purchase orders, reads the audit trail.'),
    ('ADMINISTRATOR',     'System administrator',     'Manages users, roles and reference data.')
ON CONFLICT (code) DO UPDATE SET name = EXCLUDED.name, description = EXCLUDED.description;

-- Organisational reference data: the 7 regional hubs (BA 1.1).
INSERT INTO inventory.warehouse (code, name, city, is_central_hub, latitude, longitude) VALUES
    ('VLN', 'Vilnius Central Hub',    'Vilnius',     true,  54.68716, 25.27965),
    ('KNS', 'Kaunas Regional Depot',  'Kaunas',      false, 54.89852, 23.90359),
    ('KLP', 'Klaipėda Regional Depot','Klaipėda',    false, 55.70329, 21.14427),
    ('SIA', 'Šiauliai Regional Depot','Šiauliai',    false, 55.93490, 23.31370),
    ('PNV', 'Panevėžys Regional Depot','Panevėžys',  false, 55.73480, 24.35750),
    ('MRJ', 'Marijampolė Regional Depot','Marijampolė', false, 54.55990, 23.35410),
    ('UTN', 'Utena Regional Depot',   'Utena',       false, 55.49764, 25.59918)
ON CONFLICT (code) DO UPDATE
    SET name = EXCLUDED.name, city = EXCLUDED.city, is_central_hub = EXCLUDED.is_central_hub,
        latitude = EXCLUDED.latitude, longitude = EXCLUDED.longitude;

-- Transport matrix (road km). Transit = 12 h consolidation + 35 km/h average;
-- cost = EUR 15 + EUR 1 per 7 km  => Klaipėda-Panevėžys 210 km: 18 h, EUR 45 (UC01).
INSERT INTO inventory.warehouse_route (from_warehouse_id, to_warehouse_id, distance_km, transit_hours, transport_cost_eur)
SELECT f.id, t.id, d.km, round(12 + d.km / 35.0, 1), round(15 + d.km / 7.0, 2)
FROM (VALUES
    ('VLN','KNS',103), ('VLN','KLP',311), ('VLN','SIA',214), ('VLN','PNV',136), ('VLN','MRJ',140), ('VLN','UTN',96),
    ('KNS','KLP',212), ('KNS','SIA',142), ('KNS','PNV',110), ('KNS','MRJ',58),  ('KNS','UTN',135),
    ('KLP','SIA',155), ('KLP','PNV',210), ('KLP','MRJ',250), ('KLP','UTN',330),
    ('SIA','PNV',80),  ('SIA','MRJ',190), ('SIA','UTN',180),
    ('PNV','MRJ',165), ('PNV','UTN',100),
    ('MRJ','UTN',190)
) AS pair (a, b, km)
CROSS JOIN LATERAL (VALUES (pair.a, pair.b, pair.km), (pair.b, pair.a, pair.km)) AS d (src, dst, km)
JOIN inventory.warehouse f ON f.code = d.src
JOIN inventory.warehouse t ON t.code = d.dst
ON CONFLICT (from_warehouse_id, to_warehouse_id) DO UPDATE
    SET distance_km = EXCLUDED.distance_km, transit_hours = EXCLUDED.transit_hours,
        transport_cost_eur = EXCLUDED.transport_cost_eur;
