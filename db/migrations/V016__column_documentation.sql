-- =============================================================================
-- V016 — Column documentation for the data dictionary.
-- (Added as a new migration — applied migrations are never edited.)
-- 1. Explicit comments for business columns.
-- 2. Generic comments for technical columns that are still undocumented
--    (keys, foreign keys, timestamps, actors), derived from the catalogue.
-- =============================================================================

COMMENT ON COLUMN inventory.stock_balance.on_hand_qty      IS 'Physical quantity in the warehouse (SUM of ledger movements); never negative.';
COMMENT ON COLUMN inventory.stock_balance.reserved_qty     IS 'Quantity held by active (soft/firm) reservations; never above on_hand_qty.';
COMMENT ON COLUMN inventory.stock_balance.last_movement_at IS 'Time of the latest physical movement (US04 "last updated").';
COMMENT ON COLUMN inventory.stock_movement.occurred_at     IS 'Business time of the movement; partition key (monthly ranges).';
COMMENT ON COLUMN inventory.stock_movement.movement_type_code IS 'Type of movement; defines the sign (+/-/0) and whether it is demand.';
COMMENT ON COLUMN inventory.stock_movement.external_doc_ref IS 'Document number in an external system (POS receipt, ERP document).';
COMMENT ON COLUMN inventory.stock_reservation.expires_at   IS 'End of a soft hold (now + 7 days for service kits, UC02).';
COMMENT ON COLUMN inventory.transfer_order_line.proposed_qty IS 'Quantity proposed by the system; differs from quantity only with override_reason (KPI-04).';
COMMENT ON COLUMN inventory.transfer_order_line.override_reason IS 'Mandatory justification of a manual change (UC01 A2).';
COMMENT ON COLUMN inventory.warehouse_route.transit_hours  IS 'Planned inter-depot transport time incl. consolidation.';
COMMENT ON COLUMN inventory.warehouse_route.transport_cost_eur IS 'Cost of one inter-depot shipment (EUR).';
COMMENT ON COLUMN catalog.supplier_part.pack_size          IS 'Packaging increment; ordered quantities must be multiples of it.';
COMMENT ON COLUMN catalog.supplier_part.lead_time_days     IS 'Supplier lead time for this part (calendar days).';
COMMENT ON COLUMN catalog.supplier_part.is_preferred       IS 'Preferred supplier used for automatic PO drafts (max. one per part).';
COMMENT ON COLUMN catalog.part_analogue.scaling_factor     IS 'Multiplier applied to the analogue''s demand history (US15).';
COMMENT ON COLUMN fleet.machine.current_engine_hours       IS 'Latest odometer value; maintained by the reading trigger.';
COMMENT ON COLUMN fleet.machine.operating_condition_id     IS 'Operating condition that shortens OEM intervals (F5).';
COMMENT ON COLUMN fleet.engine_hours_reading.reading_hours IS 'Odometer value; must not decrease and must be physically plausible.';
COMMENT ON COLUMN fleet.maintenance_plan.interval_hours    IS 'OEM interval in engine hours (e.g. 500).';
COMMENT ON COLUMN fleet.maintenance_plan_part.quantity     IS 'Quantity of the part in the service kit.';
COMMENT ON COLUMN fleet.service_order.trigger_engine_hours IS 'Engine hours of the reading that triggered the draft.';
COMMENT ON COLUMN fleet.service_order_line.qty_issued      IS 'Quantity already issued from stock (ISSUE_SERVICE).';
COMMENT ON COLUMN procurement.purchase_order.expected_arrival_date IS 'Approval date + longest supplier lead time of the lines.';
COMMENT ON COLUMN procurement.purchase_order_line.received_qty IS 'Quantity received so far (partial receipts).';
COMMENT ON COLUMN forecast.recommendation.base_demand_qty  IS 'NFR08 (a): ML forecast over the horizon.';
COMMENT ON COLUMN forecast.recommendation.maintenance_demand_qty IS 'NFR08 (b): planned maintenance demand not yet reserved.';
COMMENT ON COLUMN forecast.recommendation.lead_time_buffer_qty IS 'NFR08 (c): demand expected during the replenishment lead time.';
COMMENT ON COLUMN forecast.recommendation.safety_stock_qty IS 'NFR08 (d): safety stock of the hub.';
COMMENT ON COLUMN forecast.recommendation.net_position_qty IS 'Available + in transit - (a + b + c + d); negative = shortage.';
COMMENT ON COLUMN forecast.recommendation.final_qty        IS 'Quantity decided by the user (KPI-04: accepted when equal to recommended_qty).';
COMMENT ON COLUMN forecast.demand_forecast.week_start      IS 'Monday of the forecast ISO week.';
COMMENT ON COLUMN forecast.ml_model_version.rmsse          IS 'Root mean squared scaled error on the holdout period (KPI-02 metric).';
COMMENT ON COLUMN forecast.ml_model_version.mase           IS 'Mean absolute scaled error on the holdout period.';
COMMENT ON COLUMN raw.import_batch.file_sha256             IS 'SHA-256 of the file content; the same file cannot be imported twice.';
COMMENT ON COLUMN staging.demand_history.is_duplicate      IS 'TRUE if the row was already loaded by this or an earlier batch.';

-- Generic documentation of remaining technical columns.
DO $$
DECLARE
    r record;
    v_text text;
BEGIN
    FOR r IN
        SELECT n.nspname, c.relname, a.attname, a.attnum, c.oid,
               (SELECT k.confrelid::regclass::text FROM pg_constraint k
                WHERE k.conrelid = c.oid AND k.contype = 'f' AND a.attnum = ANY (k.conkey) LIMIT 1) AS fk_target,
               EXISTS (SELECT 1 FROM pg_constraint k WHERE k.conrelid = c.oid AND k.contype = 'p'
                       AND a.attnum = ANY (k.conkey) AND array_length(k.conkey, 1) = 1) AS single_pk
        FROM pg_class c
        JOIN pg_namespace n ON n.oid = c.relnamespace
        JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
        WHERE n.nspname IN ('ref', 'iam', 'catalog', 'inventory', 'fleet', 'procurement', 'forecast', 'audit', 'raw', 'staging', 'mart', 'util')
          AND c.relkind IN ('r', 'p') AND NOT c.relispartition
          AND col_description(c.oid, a.attnum) IS NULL
    LOOP
        v_text := CASE
            WHEN r.single_pk THEN 'Surrogate primary key.'
            WHEN r.attname IN ('created_by', 'requested_by', 'uploaded_by') THEN 'User who created the record (iam.app_user).'
            WHEN r.attname IN ('approved_by', 'dispatched_by', 'received_by', 'decided_by', 'flagged_by', 'granted_by', 'recorded_by')
                 THEN 'User who performed the step (iam.app_user).'
            WHEN r.fk_target IS NOT NULL THEN 'Reference to ' || r.fk_target || '.'
            WHEN r.attname = 'created_at' THEN 'Time the record was created.'
            WHEN r.attname = 'updated_at' THEN 'Time of the last change (maintained by trigger).'
            WHEN r.attname LIKE '%\_at' ESCAPE '\' THEN 'Time of the ' || replace(left(r.attname, -3), '_', ' ') || ' event.'
            WHEN r.attname = 'code' THEN 'Business code (natural key).'
            WHEN r.attname = 'name' THEN 'Display name.'
            WHEN r.attname = 'description' THEN 'Description.'
            WHEN r.attname = 'number' THEN 'Business document number (assigned by trigger).'
            WHEN r.attname = 'status' THEN 'Workflow status (state machine guarded by trigger).'
            WHEN r.attname = 'is_active' THEN 'FALSE = deactivated, kept for history.'
            WHEN r.attname = 'line_no' THEN 'Line number in the uploaded file.'
            WHEN r.attname IN ('quantity', 'qty_required') THEN 'Quantity in the unit of measure of the part.'
            WHEN r.attname LIKE 'is\_%' ESCAPE '\' THEN 'Flag: ' || replace(substr(r.attname, 4), '_', ' ') || '.'
            ELSE NULL END;
        IF v_text IS NOT NULL THEN
            EXECUTE format('COMMENT ON COLUMN %I.%I.%I IS %L', r.nspname, r.relname, r.attname, v_text);
        END IF;
    END LOOP;
END $$;
