-- =============================================================================
-- Inventory 0015 — the business functions check permissions, not role codes (ADR 0028, PR 7x2). Each function is redefined
-- as it was last defined, with util.require_role(...) replaced by util.require_permission(...) with the permission its
-- endpoint requires (PolicySqlPermissionParityTests). The signatures and grants are unchanged.
-- Generated from the latest definitions; applied scripts stay untouched (#152).
-- =============================================================================

-- inventory.approve_transfer, as last defined in inventory/0002_inventory_logic.sql, with the permission check.
CREATE OR REPLACE FUNCTION inventory.approve_transfer(p_transfer_id bigint) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_user   uuid := util.require_permission('Inventory.Transfer.Approve');
    v_order  inventory.transfer_order;
    v_line   record;
    v_avail  numeric;
    v_safety numeric;
BEGIN
    SELECT * INTO v_order FROM inventory.transfer_order WHERE id = p_transfer_id FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'Transfer order % not found', p_transfer_id USING ERRCODE = 'no_data_found'; END IF;
    IF v_order.status <> 'proposed' THEN
        RAISE EXCEPTION 'Transfer order % is %, only proposed orders can be approved', v_order.number, v_order.status USING ERRCODE = 'SS300';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM inventory.transfer_order_line WHERE transfer_order_id = p_transfer_id) THEN
        RAISE EXCEPTION 'Transfer order % has no lines', v_order.number USING ERRCODE = 'SS300';
    END IF;

    -- Lock balances in a deterministic order (part_id) to avoid deadlocks.
    FOR v_line IN
        SELECT l.id, l.part_id, l.quantity
        FROM inventory.transfer_order_line l
        WHERE l.transfer_order_id = p_transfer_id
        ORDER BY l.part_id
    LOOP
        SELECT b.available_qty, wp.safety_stock_qty INTO v_avail, v_safety
        FROM inventory.stock_balance b
        JOIN inventory.warehouse_part wp USING (warehouse_id, part_id)
        WHERE b.warehouse_id = v_order.from_warehouse_id AND b.part_id = v_line.part_id
        FOR UPDATE OF b;

        -- US10: a donor only gives stock above its safety buffer (urgent orders may use the buffer).
        IF NOT v_order.is_urgent AND coalesce(v_avail, 0) - v_line.quantity < coalesce(v_safety, 0) THEN
            RAISE EXCEPTION 'Donor warehouse % would fall below its safety stock for part % (available %, safety %, transfer %)',
                  v_order.from_warehouse_id, v_line.part_id, coalesce(v_avail, 0), coalesce(v_safety, 0), v_line.quantity
                  USING ERRCODE = 'SS201';
        END IF;

        INSERT INTO inventory.stock_reservation (warehouse_id, part_id, quantity, status, transfer_order_line_id, created_by)
        VALUES (v_order.from_warehouse_id, v_line.part_id, v_line.quantity, 'firm', v_line.id, v_user);
    END LOOP;

    UPDATE inventory.transfer_order
       SET status = 'approved', approved_by = v_user, approved_at = now()
     WHERE id = p_transfer_id;
END $$;

-- inventory.cancel_transfer, as last defined in inventory/0002_inventory_logic.sql, with the permission check.
CREATE OR REPLACE FUNCTION inventory.cancel_transfer(p_transfer_id bigint, p_reason text) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_user   uuid := util.require_permission('Inventory.Transfer.Cancel');
    v_status inventory.transfer_status;
BEGIN
    SELECT status INTO v_status FROM inventory.transfer_order WHERE id = p_transfer_id FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'Transfer order % not found', p_transfer_id USING ERRCODE = 'no_data_found'; END IF;
    IF v_status NOT IN ('proposed', 'approved') THEN
        RAISE EXCEPTION 'A % transfer order cannot be cancelled', v_status USING ERRCODE = 'SS300';
    END IF;
    UPDATE inventory.stock_reservation r SET status = 'released'
      FROM inventory.transfer_order_line l
     WHERE l.transfer_order_id = p_transfer_id AND r.transfer_order_line_id = l.id AND r.status = 'firm';
    UPDATE inventory.transfer_order SET status = 'cancelled', cancel_reason = p_reason WHERE id = p_transfer_id;
END $$;

-- inventory.dispatch_transfer, as last defined in inventory/0012_hub_access.sql, with the permission check.
CREATE OR REPLACE FUNCTION inventory.dispatch_transfer(p_transfer_id bigint) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_user   uuid := util.require_permission('Inventory.Transfer.Dispatch');
    v_order  inventory.transfer_order;
    v_line   record;
BEGIN
    SELECT * INTO v_order FROM inventory.transfer_order WHERE id = p_transfer_id FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'Transfer order % not found', p_transfer_id USING ERRCODE = 'no_data_found'; END IF;
    IF v_order.status <> 'approved' THEN
        RAISE EXCEPTION 'Transfer order % is %, only approved orders can be dispatched', v_order.number, v_order.status USING ERRCODE = 'SS300';
    END IF;
    PERFORM util.require_hub(v_order.from_warehouse_id);   -- a keeper of the donor hub (ADR 0026), SS302

    FOR v_line IN
        SELECT l.id, l.part_id, l.quantity, p.unit_cost
        FROM inventory.transfer_order_line l JOIN catalog.part p ON p.id = l.part_id
        WHERE l.transfer_order_id = p_transfer_id ORDER BY l.part_id
    LOOP
        -- 1) release the hold, 2) post the physical issue (order matters for reserved <= on_hand)
        UPDATE inventory.stock_reservation SET status = 'consumed'
         WHERE transfer_order_line_id = v_line.id AND status = 'firm';
        INSERT INTO inventory.stock_movement (occurred_at, warehouse_id, part_id, movement_type_code, quantity,
                                              unit_cost, transfer_order_line_id, created_by)
        VALUES (now(), v_order.from_warehouse_id, v_line.part_id, 'TRANSFER_OUT', v_line.quantity,
                v_line.unit_cost, v_line.id, v_user);
    END LOOP;

    UPDATE inventory.transfer_order
       SET status = 'dispatched', dispatched_by = v_user, dispatched_at = now()
     WHERE id = p_transfer_id;
END $$;

-- inventory.flag_exceptional_demand, as last defined in inventory/0002_inventory_logic.sql, with the permission check.
CREATE OR REPLACE FUNCTION inventory.flag_exceptional_demand(p_movement_id bigint, p_occurred_at timestamptz, p_reason text,
                                                  p_method text DEFAULT 'manual') RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_user uuid := util.require_permission('Inventory.Demand.Flag');
BEGIN
    PERFORM set_config('app.reason', p_reason, true);
    INSERT INTO inventory.demand_exception (movement_id, movement_occurred_at, reason, detection_method, flagged_by)
    VALUES (p_movement_id, p_occurred_at, p_reason, p_method, v_user)
    ON CONFLICT (movement_id, movement_occurred_at) DO UPDATE
        SET reason = EXCLUDED.reason, flagged_by = EXCLUDED.flagged_by, flagged_at = now();
END $$;

-- inventory.receive_transfer, as last defined in inventory/0012_hub_access.sql, with the permission check.
CREATE OR REPLACE FUNCTION inventory.receive_transfer(p_transfer_id bigint) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_user   uuid := util.require_permission('Inventory.Transfer.Receive');
    v_order  inventory.transfer_order;
BEGIN
    SELECT * INTO v_order FROM inventory.transfer_order WHERE id = p_transfer_id FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'Transfer order % not found', p_transfer_id USING ERRCODE = 'no_data_found'; END IF;
    IF v_order.status <> 'dispatched' THEN
        RAISE EXCEPTION 'Transfer order % is %, only dispatched orders can be received', v_order.number, v_order.status USING ERRCODE = 'SS300';
    END IF;
    PERFORM util.require_hub(v_order.to_warehouse_id);     -- a keeper of the recipient hub (ADR 0026), SS302

    INSERT INTO inventory.stock_movement (occurred_at, warehouse_id, part_id, movement_type_code, quantity,
                                          unit_cost, transfer_order_line_id, created_by)
    SELECT greatest(now(), v_order.dispatched_at), v_order.to_warehouse_id, l.part_id, 'TRANSFER_IN', l.quantity,
           p.unit_cost, l.id, v_user
    FROM inventory.transfer_order_line l JOIN catalog.part p ON p.id = l.part_id
    WHERE l.transfer_order_id = p_transfer_id
    ORDER BY l.part_id;

    UPDATE inventory.transfer_order
       SET status = 'received', received_by = v_user, received_at = greatest(now(), v_order.dispatched_at)
     WHERE id = p_transfer_id;
END $$;
