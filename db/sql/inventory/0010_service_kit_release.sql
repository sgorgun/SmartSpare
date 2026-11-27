-- =============================================================================
-- Inventory 0010 — release of service-kit holds (migration ServiceKitRelease, STAGE_6_PLAN 5.5, MODULE_MAPPING X8, #12).
-- Called by Inventory's consumers of ServiceOrderCancelled and ServiceKitReleaseRequested from PR 6h3.
-- =============================================================================

-- Releases every open hold of the given service order lines: a cancelled order, or a reservation that arrived after the
-- order was closed. Locks the holds in (part_id, warehouse_id) order first, as the other kit functions. Idempotent: a
-- line without an open hold changes nothing. Returns the number of holds released.
-- Session user required (SS301), no role check: it runs in the consumer as the user who cancelled (ADR 0024).
CREATE FUNCTION inventory.release_service_kit(p_line_ids bigint[])
RETURNS integer
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_user     uuid := util.require_app_user();
    v_released integer;
BEGIN
    WITH released AS (
        UPDATE inventory.stock_reservation r
           SET status = 'released'
         WHERE r.id IN (SELECT o.id FROM inventory.stock_reservation o
                        WHERE o.service_order_line_id = ANY (p_line_ids) AND o.status IN ('soft', 'firm')
                        ORDER BY o.part_id, o.warehouse_id, o.id
                        FOR UPDATE)
           AND r.status IN ('soft', 'firm')
        RETURNING 1)
    SELECT count(*) INTO v_released FROM released;
    RETURN v_released;
END $$;
COMMENT ON FUNCTION inventory.release_service_kit(bigint[]) IS
    'Releases the open holds of service order lines (cancellation, or a reservation that reached a closed order); ordered locks, idempotent (STAGE_6_PLAN 5.5).';

REVOKE EXECUTE ON FUNCTION inventory.release_service_kit(bigint[]) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION inventory.release_service_kit(bigint[]) TO app_write;
