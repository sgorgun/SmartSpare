-- =============================================================================
-- T01 — Declarative integrity: keys, CHECK, EXCLUDE, append-only tables,
--       custom business errors. Runs in a transaction that is rolled back.
-- =============================================================================
\ir _helpers.sql
BEGIN;
SET LOCAL ROLE smartspare_owner;
SELECT pg_temp.act_as('admin.smartspare');

-- Keys -----------------------------------------------------------------------
SELECT pg_temp.expect_error(
    $$INSERT INTO fleet.machine (vin, model_id, customer_id, operating_condition_id, home_warehouse_id, manufacture_year)
      SELECT vin, model_id, customer_id, operating_condition_id, home_warehouse_id, manufacture_year
      FROM fleet.machine WHERE vin = 'LX901PNV2026'$$,
    '23505', 'US01: duplicate VIN rejected (UNIQUE)');
SELECT pg_temp.expect_error(
    $$INSERT INTO catalog.part (brand_id, oem_part_number, description, category_id, uom_id, unit_cost)
      SELECT brand_id, oem_part_number, 'dup', category_id, uom_id, 1 FROM catalog.part WHERE oem_part_number = 'CP-4821'$$,
    '23505', 'US03: duplicate brand + OEM part number rejected');
SELECT pg_temp.expect_error(
    $$INSERT INTO catalog.part (brand_id, oem_part_number, description, category_id, uom_id, unit_cost)
      VALUES (999, 'X-1000', 'Unknown brand', 1, 1, 1)$$,
    '23503', 'Foreign key: unknown brand rejected');

-- CHECK constraints ---------------------------------------------------------------
SELECT pg_temp.expect_error(
    $$INSERT INTO catalog.part (brand_id, oem_part_number, description, category_id, uom_id, unit_cost)
      VALUES (1, 'X-1001', 'Negative price', 1, 1, -5)$$,
    '23514', 'CHECK: negative unit cost rejected');
SELECT pg_temp.expect_error(
    $$INSERT INTO catalog.part (brand_id, oem_part_number, description, category_id, uom_id, unit_cost)
      VALUES (1, 'lower case!', 'Bad OEM format', 1, 1, 5)$$,
    '23514', 'CHECK: OEM part number format');
SELECT pg_temp.expect_error(
    $$INSERT INTO iam.app_user (employee_id, user_name, email, full_name, password_hash) VALUES ((SELECT id FROM iam.employee ORDER BY code LIMIT 1), 'plain.text', 'p@dealer.example', 'Plain Text', 'Secret123!')$$,
    '23514', 'Security: plain-text password rejected');
SELECT pg_temp.expect_error(
    $$INSERT INTO iam.app_user (employee_id, user_name, email, full_name, password_hash) VALUES ((SELECT id FROM iam.employee ORDER BY code LIMIT 1), 'md5.user', 'm@dealer.example', 'Md Five', md5('Secret123!'))$$,
    '23514', 'Security: MD5 hash rejected (outdated hashing)');
SELECT pg_temp.expect_error(
    $$INSERT INTO iam.app_user (employee_id, user_name, email, full_name, password_hash) VALUES ((SELECT id FROM iam.employee ORDER BY code LIMIT 1), 'sha1.user', 's@dealer.example', 'Sha One', encode(sha256('x'), 'hex'))$$,
    '23514', 'Security: unsalted SHA hex digest rejected');
SELECT pg_temp.expect_error(
    $$INSERT INTO inventory.transfer_order (from_warehouse_id, to_warehouse_id) VALUES (1, 1)$$,
    '23514', 'CHECK: transfer to the same warehouse rejected');
SELECT pg_temp.expect_error(
    $$INSERT INTO inventory.stock_movement (occurred_at, warehouse_id, part_id, movement_type_code, quantity)
      VALUES (now(), 1, (SELECT id FROM catalog.part WHERE oem_part_number = 'CP-4821'), 'ISSUE_SALE', 1)$$,
    '23514', 'CHECK: counter sale without document reference rejected (lineage)');
SELECT pg_temp.expect_error(
    $$INSERT INTO inventory.stock_movement (occurred_at, warehouse_id, part_id, movement_type_code, quantity, note)
      VALUES (now(), 1, (SELECT id FROM catalog.part WHERE oem_part_number = 'CP-4821'), 'ADJUSTMENT_IN', 0, 'zero')$$,
    '23514', 'CHECK: zero quantity movement rejected');
SELECT pg_temp.expect_error(
    $$INSERT INTO forecast.ml_model_version (algorithm, version_label, training_from, training_to, holdout_from, holdout_to)
      VALUES ('fasttree', 'v9.0', '2025-01-01', '2025-12-31', '2025-06-01', '2026-01-31')$$,
    '23514', 'CHECK: holdout period must be after the training period (R1)');
SELECT pg_temp.expect_error(
    $$INSERT INTO forecast.ml_model_version (algorithm, version_label, training_from, training_to, holdout_from, holdout_to, hyperparameters)
      VALUES ('fasttree', 'v9.1', '2025-01-01', '2025-06-30', '2025-07-01', '2025-12-31', '[1,2,3]')$$,
    '23514', 'CHECK: hyperparameters must be a JSON object (structured JSON only)');
SELECT pg_temp.expect_error(
    $$INSERT INTO forecast.recommendation (forecast_run_id, kind, warehouse_id, part_id, donor_warehouse_id,
        base_demand_qty, maintenance_demand_qty, lead_time_buffer_qty, safety_stock_qty, available_qty, in_transit_qty,
        net_position_qty, recommended_qty, estimated_cost_eur)
      SELECT forecast_run_id, kind, warehouse_id, part_id, donor_warehouse_id, base_demand_qty, maintenance_demand_qty,
             lead_time_buffer_qty, safety_stock_qty, available_qty, in_transit_qty, net_position_qty - 5, recommended_qty, estimated_cost_eur
      FROM forecast.recommendation WHERE kind = 'transfer' LIMIT 1$$,
    '23514', 'NFR08: explanation drivers must add up to the net position');
SELECT pg_temp.expect_error(
    $$UPDATE inventory.stock_balance SET reserved_qty = on_hand_qty + 1
      WHERE (warehouse_id, part_id) = (SELECT warehouse_id, part_id FROM inventory.stock_balance WHERE on_hand_qty > 0 LIMIT 1)$$,
    '23514', 'NFR03: reserved quantity can never exceed on-hand stock');
SELECT pg_temp.expect_error(
    $$UPDATE inventory.transfer_order_line SET quantity = quantity + 1, override_reason = NULL
      WHERE proposed_qty IS NOT NULL AND id = (SELECT min(id) FROM inventory.transfer_order_line WHERE proposed_qty IS NOT NULL)$$,
    'SS300', 'UC01 A2: lines of an approved transfer are frozen');

-- EXCLUDE constraint ---------------------------------------------------------------
SELECT pg_temp.expect_error(
    $$INSERT INTO procurement.supplier_price (supplier_part_id, valid_during, unit_price)
      SELECT supplier_part_id, daterange(current_date, current_date + 30), 1 FROM procurement.supplier_price LIMIT 1$$,
    '23P01', 'EXCLUDE: overlapping supplier price periods rejected');

-- Four-eyes rule as a CHECK -----------------------------------------------------------
SELECT pg_temp.expect_error(
    $$UPDATE procurement.purchase_order SET approved_by = created_by WHERE number = 'PO-2026-000001'$$,
    '23514', 'US14: PO > EUR 5,000 cannot be approved by its author (CHECK)');

-- Append-only tables ---------------------------------------------------------------
SELECT pg_temp.expect_error(
    $$UPDATE inventory.stock_movement SET quantity = quantity + 1 WHERE id = (SELECT min(id) FROM inventory.stock_movement)$$,
    '42501', 'Ledger is append-only (UPDATE blocked by trigger, even for the owner)');
SELECT pg_temp.expect_error(
    $$DELETE FROM inventory.stock_movement WHERE id = (SELECT min(id) FROM inventory.stock_movement)$$,
    '42501', 'Ledger is append-only (DELETE blocked)');
SELECT pg_temp.expect_error(
    $$DELETE FROM audit.audit_log WHERE id = (SELECT min(id) FROM audit.audit_log)$$,
    '42501', 'US18: audit trail is immutable (DELETE blocked)');
SELECT pg_temp.expect_error(
    $$UPDATE fleet.engine_hours_reading SET reading_hours = 1 WHERE id = (SELECT min(id) FROM fleet.engine_hours_reading)$$,
    '42501', 'Engine-hour readings cannot be edited');

-- Business rules with custom SQLSTATEs ------------------------------------------------
SELECT pg_temp.expect_error(
    $$INSERT INTO inventory.stock_movement (occurred_at, warehouse_id, part_id, movement_type_code, quantity, external_doc_ref)
      SELECT now(), b.warehouse_id, b.part_id, 'ISSUE_SALE', b.available_qty + 1, 'POS-TEST'
      FROM inventory.stock_balance b WHERE b.on_hand_qty > 0 LIMIT 1$$,
    'SS200', 'NFR03: issue exceeding available stock rejected (no negative stock)');
SELECT pg_temp.expect_error(
    $$INSERT INTO inventory.demand_exception (movement_id, movement_occurred_at, reason)
      SELECT id, occurred_at, 'not a demand line' FROM inventory.stock_movement WHERE movement_type_code = 'OPENING_BALANCE' LIMIT 1$$,
    '23514', 'US08: only demand movements can be flagged exceptional');
SELECT pg_temp.expect_error(
    $$UPDATE inventory.transfer_order SET status = 'approved' WHERE status = 'received'$$,
    'SS300', 'State machine: received -> approved is not a valid transition');
SELECT pg_temp.expect_error(
    $$UPDATE procurement.purchase_order SET status = 'received' WHERE status = 'draft'$$,
    'SS300', 'State machine: draft PO cannot jump to received');

-- Partitioning ----------------------------------------------------------------------
INSERT INTO inventory.stock_movement (occurred_at, warehouse_id, part_id, movement_type_code, quantity, note)
SELECT TIMESTAMPTZ '2027-03-15 10:00+02', 1, (SELECT id FROM catalog.part WHERE oem_part_number = 'CP-4821'), 'ADJUSTMENT_IN', 1, 'partition routing test';
SELECT pg_temp.ok(
    (SELECT tableoid::regclass::text FROM inventory.stock_movement WHERE note = 'partition routing test') = 'inventory.stock_movement_202703',
    'Partitioning: a March 2027 movement is stored in partition stock_movement_202703');

-- Document numbers ---------------------------------------------------------------------
SELECT pg_temp.ok((SELECT bool_and(number ~ '^TR-\d{4}-\d{6}$') FROM inventory.transfer_order), 'Document numbers: TR-YYYY-NNNNNN format');
SELECT pg_temp.ok(
    (SELECT count(*) = max(substr(number, 9)::int) FROM inventory.transfer_order WHERE number LIKE 'TR-' || extract(year FROM now()) || '-%'),
    'Document numbers are gap-less within a year');

-- #69: the regex of ck_app_user_user_name matches case-insensitively on citext; the CHECK also keeps names lower-case.
SELECT pg_temp.expect_error($$UPDATE iam.app_user SET user_name = 'Tomas.Radzevicius' WHERE user_name = 'tomas.radzevicius'$$,
                            '23514', 'CHECK: user names are stored in lower case (#69)');

ROLLBACK;
