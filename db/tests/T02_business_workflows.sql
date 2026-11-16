-- =============================================================================
-- T02 — Business logic in the database: UC02 engine hours -> service order,
--       reservation expiry, transfer life cycle (UC01/US11), model promotion,
--       import deduplication, exceptional demand, audit trail, GDPR, retention.
-- =============================================================================
\ir _helpers.sql
BEGIN;
SET LOCAL ROLE smartspare_owner;

-- ---------------------------------------------------------------------------
-- UC02: register a machine, log hours, service order with soft-reserved kit
-- ---------------------------------------------------------------------------
SELECT pg_temp.act_as('mantas.petrauskas');
INSERT INTO fleet.machine (vin, model_id, customer_id, operating_condition_id, home_warehouse_id, manufacture_year)
SELECT 'TESTLX0000001', mm.id, (SELECT min(id) FROM fleet.customer), oc.id, w.id, 2024
FROM fleet.machine_model mm, ref.operating_condition oc, inventory.warehouse w
WHERE mm.model_name = 'Lexion 8700' AND oc.code = 'NORMAL' AND w.code = 'PNV';
CREATE TEMP TABLE t_m AS SELECT id FROM fleet.machine WHERE vin = 'TESTLX0000001';

SELECT pg_temp.ok((SELECT count(*) = 0 FROM fleet.log_engine_hours((SELECT id FROM t_m), 440, now() - interval '10 days')),
                  'UC02: first reading 440 h drafts nothing (60 h to the 500 h service)');
SELECT pg_temp.expect_error($$SELECT * FROM fleet.log_engine_hours((SELECT id FROM t_m), 312, now() - interval '5 days')$$,
                            'SS100', 'UC02 A1: odometer rollback 312 h < 440 h rejected');
SELECT pg_temp.expect_error($$SELECT * FROM fleet.log_engine_hours((SELECT id FROM t_m), 900, now() - interval '9 days')$$,
                            'SS101', 'UC02 step 3: +460 h within one day is physically implausible');
SELECT pg_temp.expect_error($$SELECT * FROM fleet.log_engine_hours((SELECT id FROM t_m), 500, now() - interval '11 days')$$,
                            'SS103', 'Reading older than the last reading rejected');

CREATE TEMP TABLE t_so AS SELECT * FROM fleet.log_engine_hours((SELECT id FROM t_m), 512, now() - interval '1 hour');
SELECT pg_temp.ok((SELECT count(*) = 1 AND bool_and(due_engine_hours = 500) AND bool_and(fully_reserved) FROM t_so),
                  'UC02 steps 4-7: 512 h crosses 500 h -> one service order with fully reserved kit');
SELECT pg_temp.ok((SELECT current_engine_hours = 512 FROM fleet.machine WHERE id = (SELECT id FROM t_m)),
                  'Trigger: machine odometer updated from the reading');
SELECT pg_temp.ok((SELECT count(*) = 5 AND bool_and(r.status = 'soft') AND bool_and(r.expires_at > now() + interval '6 days')
                   FROM inventory.stock_reservation r JOIN fleet.service_order_line l ON l.id = r.service_order_line_id
                   WHERE l.service_order_id = (SELECT service_order_id FROM t_so)),
                  'UC02: 5 kit parts soft-reserved for 7 days (oil filter, fuel filter, hydraulic filter, belt, oil)');
SELECT pg_temp.ok((SELECT count(*) = 0 FROM fleet.log_engine_hours((SELECT id FROM t_m), 512.5, now() - interval '20 minutes')),
                  'Idempotency: another reading does not draft the 500 h service twice');

-- Reservation expiry job
CREATE TEMP TABLE t_before AS
SELECT b.warehouse_id, b.part_id, b.reserved_qty FROM inventory.stock_balance b
JOIN inventory.stock_reservation r ON r.warehouse_id = b.warehouse_id AND r.part_id = b.part_id
JOIN fleet.service_order_line l ON l.id = r.service_order_line_id
WHERE l.service_order_id = (SELECT service_order_id FROM t_so);
UPDATE inventory.stock_reservation r SET expires_at = now() - interval '1 minute'
FROM fleet.service_order_line l WHERE l.id = r.service_order_line_id AND l.service_order_id = (SELECT service_order_id FROM t_so);
CALL inventory.expire_soft_reservations(NULL);
SELECT pg_temp.ok((SELECT status = 'draft' FROM fleet.service_order WHERE id = (SELECT service_order_id FROM t_so)),
                  'Expiry job: service order back to draft after its soft holds expired');
SELECT pg_temp.ok((SELECT bool_and(b.reserved_qty < t.reserved_qty) FROM t_before t
                   JOIN inventory.stock_balance b USING (warehouse_id, part_id)),
                  'Expiry job: reserved_qty released by trigger');

-- ---------------------------------------------------------------------------
-- UC01 / US11: transfer life cycle and stock effects
-- ---------------------------------------------------------------------------
SELECT pg_temp.act_as('tomas.radzevicius');
CREATE TEMP TABLE t_p AS
SELECT b.part_id, b.on_hand_qty AS klp_on_hand,
       (SELECT on_hand_qty FROM inventory.stock_balance WHERE warehouse_id = (SELECT id FROM inventory.warehouse WHERE code = 'PNV') AND part_id = b.part_id) AS pnv_on_hand
FROM inventory.stock_balance b JOIN catalog.part p ON p.id = b.part_id
WHERE b.warehouse_id = (SELECT id FROM inventory.warehouse WHERE code = 'KLP') AND p.oem_part_number = 'CP-4821';

INSERT INTO inventory.transfer_order (from_warehouse_id, to_warehouse_id, requested_by)
SELECT f.id, t.id, util.current_app_user() FROM inventory.warehouse f, inventory.warehouse t WHERE f.code = 'KLP' AND t.code = 'PNV';
CREATE TEMP TABLE t_tr AS SELECT max(id) AS id FROM inventory.transfer_order;
INSERT INTO inventory.transfer_order_line (transfer_order_id, part_id, quantity) SELECT (SELECT id FROM t_tr), part_id, 3 FROM t_p;

SELECT pg_temp.expect_error($$SELECT inventory.dispatch_transfer((SELECT id FROM t_tr))$$, 'SS301',
                            'RBAC in DB: a brand manager cannot dispatch');
SELECT inventory.approve_transfer((SELECT id FROM t_tr));
SELECT pg_temp.ok((SELECT reserved_qty = 3 FROM inventory.stock_balance WHERE warehouse_id = (SELECT id FROM inventory.warehouse WHERE code = 'KLP')
                   AND part_id = (SELECT part_id FROM t_p)), 'Approve: firm reservation of 3 at the donor');

SELECT pg_temp.act_as('lukas.vaitkus');   -- keeper of PNV, not of the donor KLP
SELECT pg_temp.expect_error($$SELECT inventory.dispatch_transfer((SELECT id FROM t_tr))$$, 'SS302',
                            'Only a keeper of the donor hub can dispatch');
SELECT pg_temp.act_as('egle.stankeviciute');
SELECT inventory.dispatch_transfer((SELECT id FROM t_tr));
SELECT pg_temp.ok((SELECT on_hand_qty = (SELECT klp_on_hand - 3 FROM t_p) AND reserved_qty = 0
                   FROM inventory.stock_balance WHERE warehouse_id = (SELECT id FROM inventory.warehouse WHERE code = 'KLP')
                   AND part_id = (SELECT part_id FROM t_p)), 'Dispatch: donor on-hand -3, reservation consumed');
SELECT pg_temp.ok((SELECT in_transit_qty = 3 FROM inventory.v_stock_overview WHERE warehouse_code = 'PNV' AND oem_part_number = 'CP-4821'),
                  'Dispatch: 3 units visible as in transit to Panevėžys (US04)');
SELECT pg_temp.act_as('lukas.vaitkus');
SELECT inventory.receive_transfer((SELECT id FROM t_tr));
SELECT pg_temp.ok((SELECT on_hand_qty = (SELECT pnv_on_hand + 3 FROM t_p) FROM inventory.stock_balance
                   WHERE warehouse_id = (SELECT id FROM inventory.warehouse WHERE code = 'PNV') AND part_id = (SELECT part_id FROM t_p)),
                  'Receive: recipient on-hand +3');
SELECT pg_temp.ok((SELECT sum(failed_rows) = 0 FROM mart.v_data_quality), 'Reconciliation: ledger = balances after the transfer');

-- Donor safety buffer (US10)
SELECT pg_temp.act_as('tomas.radzevicius');
INSERT INTO inventory.transfer_order (from_warehouse_id, to_warehouse_id, requested_by)
SELECT f.id, t.id, util.current_app_user() FROM inventory.warehouse f, inventory.warehouse t WHERE f.code = 'KLP' AND t.code = 'UTN';
INSERT INTO inventory.transfer_order_line (transfer_order_id, part_id, quantity)
SELECT (SELECT max(id) FROM inventory.transfer_order), part_id,
       (SELECT available_qty FROM inventory.stock_balance WHERE warehouse_id = (SELECT id FROM inventory.warehouse WHERE code = 'KLP') AND part_id = t_p.part_id)
FROM t_p;
SELECT pg_temp.expect_error($$SELECT inventory.approve_transfer((SELECT max(id) FROM inventory.transfer_order))$$, 'SS201',
                            'US10: donor may not give away its safety stock');

-- ---------------------------------------------------------------------------
-- Procurement rules
-- ---------------------------------------------------------------------------
SELECT pg_temp.act_as('rasa.kazlauskiene');
INSERT INTO procurement.purchase_order (supplier_id, warehouse_id, created_by)
SELECT s.id, w.id, util.current_app_user() FROM catalog.supplier s, inventory.warehouse w WHERE s.code = 'JD-PD' AND w.code = 'VLN';
CREATE TEMP TABLE t_po AS SELECT max(id) AS id FROM procurement.purchase_order;
SELECT pg_temp.expect_error(
    $$INSERT INTO procurement.purchase_order_line (purchase_order_id, part_id, quantity)
      SELECT (SELECT id FROM t_po), id, 7 FROM catalog.part WHERE oem_part_number = 'RE900004'$$,
    'SS400', 'US12: 7 L of oil violates MOQ 20 / pack 20');
SELECT pg_temp.expect_error(
    $$INSERT INTO procurement.purchase_order_line (purchase_order_id, part_id, quantity)
      SELECT (SELECT id FROM t_po), id, 1 FROM catalog.part WHERE oem_part_number = 'CP-4821'$$,
    'SS401', 'US12: a Claas part cannot be ordered from John Deere');
INSERT INTO procurement.purchase_order_line (purchase_order_id, part_id, quantity)
SELECT (SELECT id FROM t_po), id, 800 FROM catalog.part WHERE oem_part_number = 'RE900004';
SELECT pg_temp.ok((SELECT total_amount = 800 * l.unit_price FROM procurement.purchase_order po
                   JOIN procurement.purchase_order_line l ON l.purchase_order_id = po.id WHERE po.id = (SELECT id FROM t_po)),
                  'Trigger: price taken from the valid price list and PO total maintained');
SELECT procurement.submit_purchase_order((SELECT id FROM t_po));
SELECT pg_temp.expect_error($$SELECT procurement.approve_purchase_order((SELECT id FROM t_po))$$, 'SS301',
                            'US14: author cannot approve her own PO above EUR 5,000');
SELECT pg_temp.act_as('vida.mockute');
SELECT procurement.approve_purchase_order((SELECT id FROM t_po));
SELECT pg_temp.ok((SELECT status = 'approved' AND expected_arrival_date > current_date FROM procurement.purchase_order WHERE id = (SELECT id FROM t_po)),
                  'US14: sponsor approves; expected arrival = today + supplier lead time');

-- ---------------------------------------------------------------------------
-- Model registry (KPI-02)
-- ---------------------------------------------------------------------------
SELECT pg_temp.ok((SELECT count(*) = 1 FROM forecast.ml_model_version WHERE status = 'active'), 'Exactly one active model');
SELECT pg_temp.ok((SELECT status = 'rejected' FROM forecast.ml_model_version WHERE version_label = 'v1.1'),
                  'KPI-02: candidate only 4 % better than SES was rejected automatically');
SET LOCAL ROLE smartspare_jobs;
SELECT pg_temp.expect_error($$UPDATE forecast.ml_model_version SET status = 'retired' WHERE status = 'active'$$, '42501',
                            'Jobs cannot activate/retire models directly — only forecast.promote_model()');
SET LOCAL ROLE smartspare_owner;

-- ---------------------------------------------------------------------------
-- Import pipeline: duplicate file, exceptional demand excluded from the mart
-- ---------------------------------------------------------------------------
SELECT pg_temp.expect_error(
    $$INSERT INTO raw.import_batch (source, file_name, file_sha256) SELECT source, 'renamed.csv', file_sha256 FROM raw.import_batch LIMIT 1$$,
    '23505', 'Deduplication: the same file content (SHA-256) cannot be imported twice');
SELECT pg_temp.ok((SELECT error_rows = 10 AND loaded_rows = 40 AND status = 'loaded' FROM raw.import_batch WHERE file_name = 'erp_demand_corrections.csv'),
                  'Data quality: 10 invalid rows reported, 150 duplicates skipped, 40 new rows loaded');
SELECT pg_temp.ok((SELECT status = 'failed' AND loaded_rows = 0 FROM raw.import_batch WHERE file_name = 'catalogue_price_update_bad.csv'),
                  'Quality gate: batch with 7.9 % errors rejected as a whole');
SELECT pg_temp.ok((SELECT count(DISTINCT error_code) >= 4 FROM staging.import_error), 'Validation error codes recorded per row and column');
SELECT pg_temp.ok(
    (SELECT coalesce(sum(d.demand_qty), 0) FROM mart.mv_weekly_demand d) =
    (SELECT sum(m.quantity) FROM inventory.stock_movement m JOIN ref.movement_type t ON t.code = m.movement_type_code AND t.is_demand
     WHERE NOT EXISTS (SELECT 1 FROM inventory.demand_exception e WHERE e.movement_id = m.id AND e.movement_occurred_at = m.occurred_at)
       AND m.occurred_at < (SELECT max(finished_at) FROM mart.refresh_log)),
    'US08: weekly demand mart = all demand except flagged exceptional lines');

-- ---------------------------------------------------------------------------
-- Audit trail (US18), GDPR anonymisation, retention
-- ---------------------------------------------------------------------------
SELECT pg_temp.act_as('rasa.kazlauskiene');
SELECT set_config('app.reason', 'Supplier price increase 2026', true);
UPDATE catalog.part SET unit_cost = unit_cost + 1 WHERE oem_part_number = 'RE900001';
SELECT pg_temp.ok((SELECT changed_columns = ARRAY['unit_cost'] AND reason = 'Supplier price increase 2026'
                          AND user_id = util.current_app_user() AND old_values ? 'unit_cost'
                   FROM audit.audit_log WHERE table_name = 'catalog.part' ORDER BY id DESC LIMIT 1),
                  'US18: audit row with user, previous/new value and reason');
SELECT pg_temp.act_as('admin.smartspare');
UPDATE iam.app_user SET full_name = 'Rasa Kazlauskienė-Test', email = 'rasa.test@dealer.example' WHERE user_name = 'rasa.kazlauskiene';
SELECT pg_temp.ok((SELECT NOT (coalesce(new_values, '{}') ? 'email') AND NOT (coalesce(new_values, '{}') ? 'full_name')
                   FROM audit.audit_log WHERE table_name = 'iam.app_user' ORDER BY id DESC LIMIT 1) IS NOT FALSE,
                  'GDPR: personal data columns are never copied into the audit trail');
SELECT pg_temp.ok((SELECT count(*) FROM audit.audit_log WHERE table_name = 'iam.app_user' AND (new_values ? 'email' OR old_values ? 'email')) = 0,
                  'GDPR: no e-mail address anywhere in the audit trail');
SELECT pg_temp.ok((SELECT email LIKE 'anon-%@anonymised.invalid' AND password_hash IS NULL AND NOT is_active AND phone IS NULL
                   FROM iam.app_user WHERE id = '00000000-0000-4000-8000-000000000012'),
                  'GDPR Art. 17: former employee anonymised, row kept');
SELECT pg_temp.ok((SELECT count(*) > 0 FROM audit.audit_log WHERE user_id IS NOT NULL), 'Audit references stay valid after anonymisation');

CREATE TEMP TABLE t_parts AS SELECT count(*) AS n FROM pg_inherits WHERE inhparent = 'audit.audit_log'::regclass;
CALL audit.purge_expired(interval '180 days');
SELECT pg_temp.ok((SELECT count(*) FROM pg_inherits WHERE inhparent = 'audit.audit_log'::regclass) < (SELECT n FROM t_parts),
                  'Retention: audit partitions older than 180 days dropped');
SELECT pg_temp.ok((SELECT min(occurred_at) >= date_trunc('month', now() - interval '180 days') - interval '1 month' FROM audit.audit_log),
                  'Retention: no audit record older than the retention window');

ROLLBACK;
