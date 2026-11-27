-- =============================================================================
-- Procurement 0005 — the business functions check permissions, not role codes (ADR 0028, PR 7x2). Each function is redefined
-- as it was last defined, with util.require_role(...) replaced by util.require_permission(...) with the permission its
-- endpoint requires (PolicySqlPermissionParityTests). The signatures and grants are unchanged.
-- Generated from the latest definitions; applied scripts stay untouched (#152).
-- =============================================================================

-- procurement.approve_purchase_order, as last defined in procurement/0002_purchase_orders.sql, with the permission check.
CREATE OR REPLACE FUNCTION procurement.approve_purchase_order(p_po_id bigint) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_user uuid := util.require_permission('Procurement.PurchaseOrder.Approve');
    v_po   procurement.purchase_order;
    v_lead integer;
BEGIN
    SELECT * INTO v_po FROM procurement.purchase_order WHERE id = p_po_id FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'Purchase order % not found', p_po_id USING ERRCODE = 'no_data_found'; END IF;
    IF v_po.status <> 'pending_approval' THEN
        RAISE EXCEPTION 'Purchase order % is %, expected pending_approval', v_po.number, v_po.status USING ERRCODE = 'SS300';
    END IF;
    IF v_po.total_amount > 5000 AND v_po.created_by = v_user THEN
        RAISE EXCEPTION 'Four-eyes rule: % (EUR %) must be approved by another manager', v_po.number, v_po.total_amount USING ERRCODE = 'SS301';
    END IF;

    SELECT max(sp.lead_time_days) INTO v_lead
    FROM procurement.purchase_order_line l
    JOIN procurement.supplier_part sp ON sp.part_id = l.part_id AND sp.supplier_id = v_po.supplier_id
    WHERE l.purchase_order_id = p_po_id;

    UPDATE procurement.purchase_order
       SET status = 'approved', approved_by = v_user, approved_at = now(),
           expected_arrival_date = current_date + coalesce(v_lead, 0)
     WHERE id = p_po_id;
END $$;

-- procurement.cancel_purchase_order, as last defined in procurement/0002_purchase_orders.sql, with the permission check.
CREATE OR REPLACE FUNCTION procurement.cancel_purchase_order(p_po_id bigint, p_reason text) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_user uuid := util.require_permission('Procurement.PurchaseOrder.Cancel');
BEGIN
    IF EXISTS (SELECT 1 FROM procurement.purchase_order_line WHERE purchase_order_id = p_po_id AND received_qty > 0) THEN
        RAISE EXCEPTION 'Purchase order % already has receipts and cannot be cancelled', p_po_id USING ERRCODE = 'SS300';
    END IF;
    PERFORM set_config('app.reason', p_reason, true);
    UPDATE procurement.purchase_order SET status = 'cancelled', cancel_reason = p_reason WHERE id = p_po_id;
END $$;

-- procurement.export_purchase_order, as last defined in procurement/0002_purchase_orders.sql, with the permission check.
CREATE OR REPLACE FUNCTION procurement.export_purchase_order(p_po_id bigint) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_user uuid := util.require_permission('Procurement.PurchaseOrder.Create');
BEGIN
    UPDATE procurement.purchase_order SET status = 'exported', exported_at = now() WHERE id = p_po_id;
END $$;

-- procurement.receive_purchase_order_line, as last defined in procurement/0002_purchase_orders.sql, with the permission check.
CREATE OR REPLACE FUNCTION procurement.receive_purchase_order_line(p_line_id bigint, p_quantity numeric) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_user uuid := util.require_permission('Procurement.PurchaseOrder.Receive');
    v_line procurement.purchase_order_line;
    v_po   procurement.purchase_order;
BEGIN
    SELECT * INTO v_line FROM procurement.purchase_order_line WHERE id = p_line_id;
    IF NOT FOUND THEN RAISE EXCEPTION 'PO line % not found', p_line_id USING ERRCODE = 'no_data_found'; END IF;
    SELECT * INTO v_po FROM procurement.purchase_order WHERE id = v_line.purchase_order_id FOR UPDATE;
    IF v_po.status NOT IN ('approved', 'exported', 'partially_received') THEN
        RAISE EXCEPTION 'Purchase order % is % and cannot be received', v_po.number, v_po.status USING ERRCODE = 'SS300';
    END IF;
    IF p_quantity <= 0 OR v_line.received_qty + p_quantity > v_line.quantity THEN
        RAISE EXCEPTION 'Receipt of % exceeds the open quantity % of line %', p_quantity, v_line.quantity - v_line.received_qty, p_line_id
              USING ERRCODE = 'check_violation';
    END IF;

    INSERT INTO inventory.stock_movement (occurred_at, warehouse_id, part_id, movement_type_code, quantity,
                                          unit_cost, purchase_order_line_id, created_by)
    VALUES (now(), v_po.warehouse_id, v_line.part_id, 'RECEIPT_PO', p_quantity, v_line.unit_price, p_line_id, v_user);
    UPDATE procurement.purchase_order_line SET received_qty = received_qty + p_quantity WHERE id = p_line_id;

    UPDATE procurement.purchase_order
       SET status = CASE WHEN EXISTS (SELECT 1 FROM procurement.purchase_order_line
                                      WHERE purchase_order_id = v_po.id AND received_qty < quantity)
                         THEN 'partially_received'::procurement.po_status
                         ELSE 'received'::procurement.po_status END
     WHERE id = v_po.id;
END $$;

-- procurement.submit_purchase_order, as last defined in procurement/0002_purchase_orders.sql, with the permission check.
CREATE OR REPLACE FUNCTION procurement.submit_purchase_order(p_po_id bigint) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_user uuid := util.require_permission('Procurement.PurchaseOrder.Create');
BEGIN
    PERFORM 1 FROM procurement.purchase_order WHERE id = p_po_id FOR UPDATE;
    IF NOT EXISTS (SELECT 1 FROM procurement.purchase_order_line WHERE purchase_order_id = p_po_id) THEN
        RAISE EXCEPTION 'Purchase order % has no lines', p_po_id USING ERRCODE = 'SS300';
    END IF;
    UPDATE procurement.purchase_order SET status = 'pending_approval', submitted_at = now() WHERE id = p_po_id;
END $$;
