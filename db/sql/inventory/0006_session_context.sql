-- =============================================================================
-- Inventory 0006 — the warehouse check of dispatch and receive reads the session context (#11, MODULE_MAPPING X2).
-- The home warehouse comes from app.home_warehouse_id (the ss:warehouse claim, set by SessionContextInterceptor)
-- instead of iam.app_user, so Inventory's SQL no longer reads the Identity schema. A user without a home
-- warehouse (an administrator) is not restricted, as before (SS302). Bodies otherwise unchanged from 0002.
-- =============================================================================

CREATE OR REPLACE FUNCTION inventory.dispatch_transfer(p_transfer_id bigint) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_user   uuid := util.require_role('WAREHOUSE_KEEPER', 'ADMINISTRATOR');
    v_order  inventory.transfer_order;
    v_home   smallint;
    v_line   record;
BEGIN
    SELECT * INTO v_order FROM inventory.transfer_order WHERE id = p_transfer_id FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'Transfer order % not found', p_transfer_id USING ERRCODE = 'no_data_found'; END IF;
    IF v_order.status <> 'approved' THEN
        RAISE EXCEPTION 'Transfer order % is %, only approved orders can be dispatched', v_order.number, v_order.status USING ERRCODE = 'SS300';
    END IF;
    v_home := util.current_home_warehouse_id();      -- claim of the signed-in keeper (X2)
    IF v_home IS NOT NULL AND v_home <> v_order.from_warehouse_id THEN
        RAISE EXCEPTION 'Only a keeper of the donor warehouse can dispatch order %', v_order.number USING ERRCODE = 'SS302';
    END IF;

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
COMMENT ON FUNCTION inventory.dispatch_transfer(bigint) IS 'US11: consumes the firm reservations, posts TRANSFER_OUT movements, status -> dispatched (stock is now in transit).';

CREATE OR REPLACE FUNCTION inventory.receive_transfer(p_transfer_id bigint) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_user   uuid := util.require_role('WAREHOUSE_KEEPER', 'ADMINISTRATOR');
    v_order  inventory.transfer_order;
    v_home   smallint;
BEGIN
    SELECT * INTO v_order FROM inventory.transfer_order WHERE id = p_transfer_id FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'Transfer order % not found', p_transfer_id USING ERRCODE = 'no_data_found'; END IF;
    IF v_order.status <> 'dispatched' THEN
        RAISE EXCEPTION 'Transfer order % is %, only dispatched orders can be received', v_order.number, v_order.status USING ERRCODE = 'SS300';
    END IF;
    v_home := util.current_home_warehouse_id();      -- claim of the signed-in keeper (X2)
    IF v_home IS NOT NULL AND v_home <> v_order.to_warehouse_id THEN
        RAISE EXCEPTION 'Only a keeper of the recipient warehouse can receive order %', v_order.number USING ERRCODE = 'SS302';
    END IF;

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
COMMENT ON FUNCTION inventory.receive_transfer(bigint) IS 'US11: posts TRANSFER_IN movements at the recipient hub, status -> received.';
