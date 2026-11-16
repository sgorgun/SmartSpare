-- =============================================================================
-- Inventory seed — 7 regional hubs and the 42-route transport matrix, verbatim from R001.
-- Idempotent: upsert by natural key.
-- =============================================================================

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
