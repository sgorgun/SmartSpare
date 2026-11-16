-- =============================================================================
-- T04 — Index usage of the critical queries (plans checked with EXPLAIN).
-- Run on the demo dataset (parts >= 2,000). See docs/database for timings.
-- =============================================================================
\ir _helpers.sql
BEGIN;
SET LOCAL ROLE smartspare_owner;

SELECT pg_temp.uses_index(
    $$SELECT * FROM inventory.stock_movement WHERE part_id = 1200 AND warehouse_id = 1 AND occurred_at >= now() - interval '12 months'$$,
    'part_id_warehouse_id_occurred_at_idx', 'Demand history of one SKU at one hub (composite index on every partition)');
SELECT pg_temp.uses_index(
    $$SELECT * FROM inventory.stock_movement WHERE part_id = 1200 AND occurred_at >= now() - interval '3 months'$$,
    'Subplans Removed', 'Time-range query: partition pruning skips old partitions');
SELECT pg_temp.uses_index(
    $$SELECT * FROM forecast.demand_forecast WHERE part_id = 1200 AND warehouse_id = 2$$,
    'ix_demand_forecast_part_wh_week', 'Forecast history of a SKU across runs');
SELECT pg_temp.uses_index(
    $$SELECT * FROM audit.audit_log WHERE table_name = 'catalog.part' AND record_id = '1200' ORDER BY occurred_at DESC$$,
    'table_name_record_id_occurred_at_idx', 'Audit history of one record');
SELECT pg_temp.uses_index(
    $$SELECT * FROM mart.mv_weekly_demand WHERE warehouse_id = 2 AND part_id = 1200$$,
    'uq_mv_weekly_demand', 'Mart look-up per series');
-- Below ~10k SKUs a sequential scan of the catalogue is cheaper and the planner rightly
-- prefers it; at the BA scale (20,000 SKUs) the trigram index wins (docs/database, §7).
SELECT set_config('enable_seqscan', (count(*) >= 10000)::text, true) FROM catalog.part;
SELECT pg_temp.uses_index(
    $$SELECT * FROM catalog.part WHERE description ILIKE '%threshing drum%'$$,
    'ix_part_description_trgm', 'Catalogue search ILIKE %...% (trigram GIN)');
SELECT pg_temp.uses_index(
    $$SELECT * FROM catalog.part WHERE oem_part_number ILIKE '%4821%'$$,
    'ix_part_oem_number_trgm', 'Search by fragment of the OEM number (trigram GIN)');
RESET enable_seqscan;
SELECT pg_temp.uses_index(
    $$SELECT * FROM staging.demand_history WHERE row_hash = '\x00' AND is_valid AND NOT is_duplicate$$,
    'ix_staging_demand_history_hash', 'Import deduplication look-up (partial index)');

ROLLBACK;
