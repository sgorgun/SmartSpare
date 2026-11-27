-- =============================================================================
-- Inventory 0009 — issue of service kits on completion (migration ServiceKitIssue, STAGE_6_PLAN 5.4, F7,
-- MODULE_MAPPING X7, #12). Called by Inventory's consumer of ServiceOrderCompleted from PR 6h2.
-- =============================================================================

-- Issues a completed order's kit at one hub, per line: consumes the line's holds and posts ISSUE_SERVICE for the open
-- quantity up to what is available once those holds are consumed (F7: reserved quantities can always be issued, an
-- unreserved shortfall is issued up to the stock and reported). Returns the issued quantity and the shortfall per line.
--  * Lock order: the kit's balance rows in (part_id, warehouse_id) order first, as reserve_service_kit and the expiry job.
--  * Idempotent per line: a line that already has an ISSUE_SERVICE movement is not issued again; its open holds (a late
--    reservation, 6h3) are released and the earlier issue is reported.
--  * Session user required (SS301), no role check: it runs in the consumer as the engineer who completed the order
--    (ADR 0024); the role gate was fleet.complete_service_order.
CREATE FUNCTION inventory.issue_service_kit(
    p_warehouse_id smallint,
    p_line_ids     bigint[],
    p_part_ids     bigint[],
    p_quantities   numeric[])
RETURNS TABLE (service_order_line_id bigint, issued_qty numeric, shortfall_qty numeric)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
#variable_conflict use_column
DECLARE
    v_user   uuid := util.require_app_user();
    v_line   record;
    v_done   numeric;
    v_avail  numeric;
    v_qty    numeric;
BEGIN
    IF cardinality(p_line_ids) IS DISTINCT FROM cardinality(p_part_ids)
       OR cardinality(p_line_ids) IS DISTINCT FROM cardinality(p_quantities) THEN
        RAISE EXCEPTION 'Kit arrays differ in length' USING ERRCODE = 'invalid_parameter_value';
    END IF;

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
        SELECT sum(m.quantity) INTO v_done
        FROM inventory.stock_movement m
        WHERE m.service_order_line_id = v_line.line_id AND m.movement_type_code = 'ISSUE_SERVICE';

        IF v_done IS NOT NULL THEN
            -- Issued before: release a late hold and report the earlier issue.
            UPDATE inventory.stock_reservation r SET status = 'released'
             WHERE r.service_order_line_id = v_line.line_id AND r.status IN ('soft', 'firm');
            service_order_line_id := v_line.line_id;
            issued_qty := v_done;
            shortfall_qty := greatest(v_line.quantity - v_done, 0);
            RETURN NEXT;
            CONTINUE;
        END IF;

        UPDATE inventory.stock_reservation r SET status = 'consumed'
         WHERE r.service_order_line_id = v_line.line_id AND r.status IN ('soft', 'firm');

        SELECT b.available_qty INTO v_avail
        FROM inventory.stock_balance b
        WHERE b.warehouse_id = p_warehouse_id AND b.part_id = v_line.part_id;

        v_qty := least(coalesce(v_avail, 0), v_line.quantity);
        IF v_qty > 0 THEN
            INSERT INTO inventory.stock_movement (occurred_at, warehouse_id, part_id, movement_type_code, quantity,
                                                  unit_cost, service_order_line_id, created_by)
            SELECT now(), p_warehouse_id, v_line.part_id, 'ISSUE_SERVICE', v_qty, p.unit_cost, v_line.line_id, v_user
            FROM catalog.part p WHERE p.id = v_line.part_id;
        END IF;

        service_order_line_id := v_line.line_id;
        issued_qty := greatest(v_qty, 0);
        shortfall_qty := v_line.quantity - greatest(v_qty, 0);
        RETURN NEXT;
    END LOOP;
END $$;
COMMENT ON FUNCTION inventory.issue_service_kit(smallint, bigint[], bigint[], numeric[]) IS
    'Consumes the holds of a completed service order and issues its kit up to the available stock, idempotent per line; returns issued and shortfall (STAGE_6_PLAN 5.4, F7).';

REVOKE EXECUTE ON FUNCTION inventory.issue_service_kit(smallint, bigint[], bigint[], numeric[]) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION inventory.issue_service_kit(smallint, bigint[], bigint[], numeric[]) TO app_write;
