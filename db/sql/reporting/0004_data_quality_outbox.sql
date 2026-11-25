-- =============================================================================
-- Reporting 0004 — mart.v_data_quality also counts integration events parked in an outbox (#31).
-- An event that ran out of delivery attempts (outbox_message.exhausted_at) is a lost cross-module workflow until an
-- operator replays it (docs/operations/OUTBOX.md), so the reconciliation reports it with the other checks. Reporting
-- reads the other modules' outboxes like its marts read their tables (ARCHITECTURE_DECISIONS §2). The view keeps its
-- columns, so CREATE OR REPLACE is enough; the checks above the new one are unchanged from 0001.
-- =============================================================================

CREATE OR REPLACE VIEW mart.v_data_quality AS
WITH ledger AS (
    SELECT m.warehouse_id, m.part_id, sum(t.balance_sign * m.quantity) AS qty
    FROM inventory.stock_movement m JOIN ref.movement_type t ON t.code = m.movement_type_code
    WHERE t.balance_sign <> 0
    GROUP BY m.warehouse_id, m.part_id
), holds AS (
    SELECT warehouse_id, part_id, sum(quantity) AS qty
    FROM inventory.stock_reservation WHERE status IN ('soft', 'firm')
    GROUP BY warehouse_id, part_id
)
SELECT 'stock_balance = SUM(ledger)' AS check_name,
       (SELECT count(*) FROM inventory.stock_balance b FULL JOIN ledger l USING (warehouse_id, part_id)
        WHERE coalesce(b.on_hand_qty, 0) <> coalesce(l.qty, 0)) AS failed_rows
UNION ALL
SELECT 'reserved_qty = SUM(active reservations)',
       (SELECT count(*) FROM inventory.stock_balance b FULL JOIN holds h USING (warehouse_id, part_id)
        WHERE coalesce(b.reserved_qty, 0) <> coalesce(h.qty, 0))
UNION ALL
SELECT 'PO total = SUM(lines)',
       (SELECT count(*) FROM procurement.purchase_order po
        WHERE po.total_amount <> (SELECT coalesce(sum(line_amount), 0) FROM procurement.purchase_order_line l WHERE l.purchase_order_id = po.id))
UNION ALL
SELECT 'machine hours = last reading',
       (SELECT count(*) FROM fleet.machine m
        JOIN LATERAL (SELECT reading_hours FROM fleet.engine_hours_reading r WHERE r.machine_id = m.id ORDER BY read_at DESC LIMIT 1) r ON true
        WHERE m.current_engine_hours <> r.reading_hours)
UNION ALL
SELECT 'active parts without supplier',
       (SELECT count(*) FROM catalog.part p WHERE p.is_active
        AND NOT EXISTS (SELECT 1 FROM procurement.supplier_part sp WHERE sp.part_id = p.id))
UNION ALL
SELECT 'duplicate demand rows in staging (loaded)',
       (SELECT count(*) FROM (SELECT row_hash FROM staging.demand_history WHERE is_valid AND NOT is_duplicate
                              GROUP BY row_hash HAVING count(*) > 1) d)
UNION ALL
SELECT 'expired soft reservations still active',
       (SELECT count(*) FROM inventory.stock_reservation WHERE status = 'soft' AND expires_at < now() - interval '1 hour')
UNION ALL
SELECT 'integration events parked in an outbox',
       (SELECT count(*) FROM (
            SELECT id FROM iam.outbox_message         WHERE exhausted_at IS NOT NULL AND processed_at IS NULL
            UNION ALL SELECT id FROM inventory.outbox_message   WHERE exhausted_at IS NOT NULL AND processed_at IS NULL
            UNION ALL SELECT id FROM fleet.outbox_message       WHERE exhausted_at IS NOT NULL AND processed_at IS NULL
            UNION ALL SELECT id FROM procurement.outbox_message WHERE exhausted_at IS NOT NULL AND processed_at IS NULL
            UNION ALL SELECT id FROM forecast.outbox_message    WHERE exhausted_at IS NOT NULL AND processed_at IS NULL) parked);
COMMENT ON VIEW mart.v_data_quality IS 'Automated reconciliation checks (ledger vs. balance, reservations, PO totals, odometer, duplicates, parked outbox events). Expected: all failed_rows = 0.';
