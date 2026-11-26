-- =============================================================================
-- Inventory 0008 — expiry of service-kit holds, module-local (migration ServiceKitExpiry, STAGE_6_PLAN 5.5,
-- MODULE_MAPPING X5, #12). Replaces the transitional procedure that also wrote fleet.service_order: the expiry job now
-- publishes ServiceKitReservationExpired with the returned lines, and Fleet's consumer reopens its orders.
-- =============================================================================

DROP PROCEDURE inventory.expire_soft_reservations(integer);

-- Expires every soft hold whose expiry lies before p_now (the job's clock, TimeProvider) and returns the service order
-- lines whose holds expired. Race with consumption (completion consumes holds): both lock the reservation row; whichever
-- commits second re-checks the row's status after the lock and skips it (status = 'soft' / IN ('soft', 'firm')), so a
-- hold is expired or consumed, never both, and trg_stock_reservation_sync lowers reserved_qty exactly once.
-- No session user is needed: the job runs without one, and the function is granted to the job and service roles only.
CREATE FUNCTION inventory.expire_soft_reservations(p_now timestamptz)
RETURNS TABLE (service_order_line_id bigint, quantity numeric)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
#variable_conflict use_column
BEGIN
    IF p_now IS NULL THEN
        RAISE EXCEPTION 'The expiry job needs its clock time' USING ERRCODE = 'invalid_parameter_value';
    END IF;

    -- Lock the overdue holds in (part_id, warehouse_id) order first, the order completions and kit reservations use,
    -- so the job and a completion never wait on each other in opposite orders.
    RETURN QUERY
    UPDATE inventory.stock_reservation r
       SET status = 'expired'
     WHERE r.id IN (SELECT o.id FROM inventory.stock_reservation o
                    WHERE o.status = 'soft' AND o.expires_at < p_now
                    ORDER BY o.part_id, o.warehouse_id, o.id
                    FOR UPDATE)
       AND r.status = 'soft'
    RETURNING r.service_order_line_id, r.quantity;
END $$;
COMMENT ON FUNCTION inventory.expire_soft_reservations(timestamptz) IS
    'Expires soft holds overdue at the given time and returns their service order lines (STAGE_6_PLAN 5.5); the job publishes ServiceKitReservationExpired.';

REVOKE EXECUTE ON FUNCTION inventory.expire_soft_reservations(timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION inventory.expire_soft_reservations(timestamptz) TO app_write, app_ml;
