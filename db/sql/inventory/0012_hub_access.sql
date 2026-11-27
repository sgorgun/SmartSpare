-- =============================================================================
-- Inventory 0012 — dispatch and receive check write access to the hub through util.require_hub (ADR 0026,
-- STAGE_7_PLAN 1.2): ADMINISTRATOR or the hub among the user's hubs (app.warehouse_ids). A keeper without a
-- hub assignment is refused (SS302); before, a user without a home hub was not restricted. Bodies otherwise
-- unchanged from 0006, which stays as applied (#152).
-- =============================================================================

CREATE OR REPLACE FUNCTION inventory.dispatch_transfer(p_transfer_id bigint) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_user   uuid := util.require_role('WAREHOUSE_KEEPER', 'ADMINISTRATOR');
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
COMMENT ON FUNCTION inventory.dispatch_transfer(bigint) IS 'US11: consumes the firm reservations, posts TRANSFER_OUT movements, status -> dispatched (stock is now in transit).';

CREATE OR REPLACE FUNCTION inventory.receive_transfer(p_transfer_id bigint) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_user   uuid := util.require_role('WAREHOUSE_KEEPER', 'ADMINISTRATOR');
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
COMMENT ON FUNCTION inventory.receive_transfer(bigint) IS 'US11: posts TRANSFER_IN movements at the recipient hub, status -> received.';
