-- =============================================================================
-- Inventory 0007 — reservation of service kits (STAGE_6_PLAN 5.3 and 5.7, MODULE_MAPPING X6, #12).
-- Migration ServiceKitReservation. Called by Inventory's consumer of ServiceOrderDrafted from PR 6g2; until then the
-- transitional fleet.create_service_order_draft still reserves by itself.
-- =============================================================================

-- Soft-reserves a service order's kit at one hub: per line the smaller of the required and the available quantity
-- (partial reservation, STAGE_6_PLAN 5.7), held until p_expires_at. Returns the reserved quantity per line.
--  * Deadlocks: every stock_balance row of the kit is locked in one statement in (part_id, warehouse_id) order before
--    any reservation is written, the order approve_transfer uses too, so two kits that share parts never wait on each
--    other in opposite orders.
--  * Idempotent per line: a line that already has a reservation (any status) is not reserved again and returns what it
--    holds now, so a redelivered ServiceOrderDrafted cannot reserve twice even without the inbox.
--  * The invariant 0 <= reserved_qty <= on_hand_qty of stock_balance holds: trg_stock_reservation_sync refuses (SS200)
--    a hold above the available quantity, and this function never asks for more than is available.
--  * No role check: it runs in Inventory's event consumer as the user who drafted the order, without roles; the role
--    gate was applied when that user acted in Fleet (STAGE_6_PLAN 5.5). It requires a session user (SS301).
CREATE FUNCTION inventory.reserve_service_kit(
    p_warehouse_id smallint,
    p_line_ids     bigint[],
    p_part_ids     bigint[],
    p_quantities   numeric[],
    p_expires_at   timestamptz)
RETURNS TABLE (service_order_line_id bigint, reserved_qty numeric)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
#variable_conflict use_column
DECLARE
    v_user  uuid := util.require_app_user();
    v_line  record;
    v_avail numeric;
    v_held  numeric;
    v_seen  boolean;
    v_qty   numeric;
BEGIN
    IF cardinality(p_line_ids) IS DISTINCT FROM cardinality(p_part_ids)
       OR cardinality(p_line_ids) IS DISTINCT FROM cardinality(p_quantities) THEN
        RAISE EXCEPTION 'Kit arrays differ in length' USING ERRCODE = 'invalid_parameter_value';
    END IF;
    IF p_expires_at IS NULL OR p_expires_at <= now() THEN
        RAISE EXCEPTION 'A soft reservation needs a future expiry, not %', p_expires_at USING ERRCODE = 'invalid_parameter_value';
    END IF;

    -- Lock the kit's balances in a deterministic order before any write.
    PERFORM 1
    FROM inventory.stock_balance b
    WHERE b.warehouse_id = p_warehouse_id AND b.part_id = ANY (p_part_ids)
    ORDER BY b.part_id, b.warehouse_id
    FOR UPDATE;

    FOR v_line IN
        SELECT k.line_id, k.part_id, k.quantity
        FROM unnest(p_line_ids, p_part_ids, p_quantities) AS k (line_id, part_id, quantity)
        ORDER BY k.part_id, k.line_id
    LOOP
        SELECT coalesce(sum(r.quantity) FILTER (WHERE r.status IN ('soft', 'firm')), 0), count(*) > 0
          INTO v_held, v_seen
        FROM inventory.stock_reservation r
        WHERE r.service_order_line_id = v_line.line_id;

        IF v_seen THEN
            service_order_line_id := v_line.line_id;     -- already handled: report what it holds now
            reserved_qty := v_held;
            RETURN NEXT;
            CONTINUE;
        END IF;

        SELECT b.available_qty INTO v_avail
        FROM inventory.stock_balance b
        WHERE b.warehouse_id = p_warehouse_id AND b.part_id = v_line.part_id;

        v_qty := least(coalesce(v_avail, 0), v_line.quantity);
        IF v_qty > 0 THEN
            INSERT INTO inventory.stock_reservation (warehouse_id, part_id, quantity, status, expires_at, service_order_line_id, created_by)
            VALUES (p_warehouse_id, v_line.part_id, v_qty, 'soft', p_expires_at, v_line.line_id, v_user);
        END IF;

        service_order_line_id := v_line.line_id;
        reserved_qty := greatest(v_qty, 0);
        RETURN NEXT;
    END LOOP;
END $$;
COMMENT ON FUNCTION inventory.reserve_service_kit(smallint, bigint[], bigint[], numeric[], timestamptz) IS
    'Soft-reserves a service kit at one hub, partially if stock is short, idempotent per line; balances locked in (part_id, warehouse_id) order (STAGE_6_PLAN 5.3, 5.7).';

REVOKE EXECUTE ON FUNCTION inventory.reserve_service_kit(smallint, bigint[], bigint[], numeric[], timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION inventory.reserve_service_kit(smallint, bigint[], bigint[], numeric[], timestamptz) TO app_write;
