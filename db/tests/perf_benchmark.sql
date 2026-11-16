-- Performance benchmark of the critical queries (run on a database loaded with -v parts=20000).
-- Each query is measured with its index and with index access disabled, median of 5 runs.
\set ON_ERROR_STOP on
SET ROLE smartspare_owner;
CREATE TEMP TABLE bench (query text, variant text, ms numeric);
CREATE FUNCTION pg_temp.measure(p_label text, p_variant text, p_sql text, p_no_index boolean DEFAULT false) RETURNS void
LANGUAGE plpgsql AS $$
DECLARE t0 timestamptz; v numeric[] := '{}'; plan json;
BEGIN
  IF p_no_index THEN PERFORM set_config('enable_indexscan','off',true), set_config('enable_bitmapscan','off',true), set_config('enable_indexonlyscan','off',true); END IF;
  FOR i IN 1..6 LOOP
    EXECUTE 'EXPLAIN (ANALYZE, FORMAT JSON) ' || p_sql INTO plan;
    IF i > 1 THEN v := v || ((plan->0->>'Execution Time')::numeric + (plan->0->>'Planning Time')::numeric); END IF;
  END LOOP;
  INSERT INTO bench SELECT p_label, p_variant, round((SELECT percentile_cont(0.5) WITHIN GROUP (ORDER BY x) FROM unnest(v) x)::numeric, 2);
  PERFORM set_config('enable_indexscan','on',true), set_config('enable_bitmapscan','on',true), set_config('enable_indexonlyscan','on',true);
END $$;
BEGIN;
SELECT pg_temp.measure('US03 catalogue search ILIKE ''%threshing drum%'' (20k SKUs)', 'trigram GIN', $q$SELECT * FROM catalog.part WHERE description ILIKE '%threshing drum%'$q$);
SELECT pg_temp.measure('US03 catalogue search ILIKE ''%threshing drum%'' (20k SKUs)', 'no index', $q$SELECT * FROM catalog.part WHERE description ILIKE '%threshing drum%'$q$, true);
SELECT pg_temp.measure('US03 search by OEM fragment ''%4821%''', 'trigram GIN', $q$SELECT * FROM catalog.part WHERE oem_part_number ILIKE '%4821%'$q$);
SELECT pg_temp.measure('US03 search by OEM fragment ''%4821%''', 'no index', $q$SELECT * FROM catalog.part WHERE oem_part_number ILIKE '%4821%'$q$, true);
SELECT pg_temp.measure('US04 7-hub stock overview of one SKU (view)', 'indexes', $q$SELECT * FROM inventory.v_stock_overview WHERE part_id = 12345$q$);
SELECT pg_temp.measure('US04 7-hub stock overview of one SKU (view)', 'no index', $q$SELECT * FROM inventory.v_stock_overview WHERE part_id = 12345$q$, true);
SELECT pg_temp.measure('Demand history of a SKU at a hub, 12 months (1.9 M ledger rows)', 'partitions + composite', $q$SELECT * FROM inventory.stock_movement WHERE part_id = 12345 AND warehouse_id = 1 AND occurred_at >= now() - interval '12 months'$q$);
SELECT pg_temp.measure('Demand history of a SKU at a hub, 12 months (1.9 M ledger rows)', 'no index', $q$SELECT * FROM inventory.stock_movement WHERE part_id = 12345 AND warehouse_id = 1 AND occurred_at >= now() - interval '12 months'$q$, true);
SELECT pg_temp.measure('Soft reservations to expire (job)', 'partial index', $q$SELECT * FROM inventory.stock_reservation WHERE status = 'soft' AND expires_at < now()$q$);
SELECT pg_temp.measure('Audit history of one record (248k audit rows)', 'partitioned index', $q$SELECT * FROM audit.audit_log WHERE table_name = 'catalog.part' AND record_id = '12345' ORDER BY occurred_at DESC$q$);
SELECT pg_temp.measure('Audit history of one record (248k audit rows)', 'no index', $q$SELECT * FROM audit.audit_log WHERE table_name = 'catalog.part' AND record_id = '12345' ORDER BY occurred_at DESC$q$, true);
SELECT pg_temp.measure('UC01 net position of one SKU at one hub (fn_stock_position)', 'indexes', $q$SELECT * FROM forecast.fn_stock_position(1::smallint, 12345, (SELECT max(id) FROM forecast.forecast_run), 1)$q$);
SELECT pg_temp.measure('Forecast of one SKU, all runs (1.1 M rows)', 'composite index', $q$SELECT * FROM forecast.demand_forecast WHERE part_id = 12345 AND warehouse_id = 1$q$);
SELECT pg_temp.measure('Forecast of one SKU, all runs (1.1 M rows)', 'no index', $q$SELECT * FROM forecast.demand_forecast WHERE part_id = 12345 AND warehouse_id = 1$q$, true);
SELECT pg_temp.measure('KPI-05 dead stock per hub + network (ROLLUP)', 'indexes', $q$SELECT * FROM mart.v_kpi_dead_stock$q$);
COMMIT;
SELECT query, max(ms) FILTER (WHERE variant NOT LIKE 'no index') AS with_index_ms,
       max(ms) FILTER (WHERE variant = 'no index') AS without_index_ms,
       max(variant) FILTER (WHERE variant <> 'no index') AS access_path
FROM bench GROUP BY query ORDER BY min(ctid);
