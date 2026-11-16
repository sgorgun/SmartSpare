-- =============================================================================
-- V011 — Inventory business logic in the database
--   * ledger -> balance trigger (single source of truth, no negative stock)
--   * reservation guard + sync triggers (reserved_qty always = active holds)
--   * transfer workflow functions approve / dispatch / receive / cancel (UC01, US11)
--   * exceptional-demand detection (US08) and in-transit calculation
--
-- Custom SQLSTATE codes (mapped to HTTP 409/422 problem details by the API):
--   SS100 engine-hour rollback        SS101 implausible engine hours
--   SS102 machine not active          SS103 reading older than last reading
--   SS200 insufficient stock          SS201 donor safety buffer violated
--   SS300 invalid status transition   SS301 missing role / no app user
--   SS302 user not assigned to hub    SS400 MOQ / pack-size violation
--   SS401 part not offered by supplier SS402 no valid supplier price
-- =============================================================================

-- ---------------------------------------------------------------------------
-- Acting user (from the API session) — required for user-initiated actions
-- ---------------------------------------------------------------------------
CREATE FUNCTION util.require_app_user() RETURNS uuid
LANGUAGE plpgsql STABLE AS $$
DECLARE
    v_user uuid := util.current_app_user();
BEGIN
    IF v_user IS NULL THEN
        RAISE EXCEPTION 'No application user in session (app.user_id is not set)' USING ERRCODE = 'SS301';
    END IF;
    RETURN v_user;
END $$;

CREATE FUNCTION util.require_role(VARIADIC p_roles text[]) RETURNS uuid
LANGUAGE plpgsql STABLE AS $$
DECLARE
    v_user uuid := util.require_app_user();
BEGIN
    IF NOT iam.user_has_role(v_user, VARIADIC p_roles) THEN
        RAISE EXCEPTION 'User % lacks the required role (one of %)', v_user, p_roles USING ERRCODE = 'SS301';
    END IF;
    RETURN v_user;
END $$;
COMMENT ON FUNCTION util.require_role(text[]) IS 'Returns the session user if it has one of the roles, otherwise raises SS301 (defence in depth behind API authorisation).';

-- ---------------------------------------------------------------------------
-- Document numbers assigned by trigger (EF Core inserts without a number)
-- ---------------------------------------------------------------------------
CREATE FUNCTION util.trg_assign_document_number() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
BEGIN
    IF NEW.number IS NULL THEN
        NEW.number := util.next_document_number(TG_ARGV[0], NEW.created_at);
    END IF;
    RETURN NEW;
END $$;

CREATE TRIGGER trg_transfer_order_number BEFORE INSERT ON inventory.transfer_order
    FOR EACH ROW EXECUTE FUNCTION util.trg_assign_document_number('TR');
CREATE TRIGGER trg_service_order_number BEFORE INSERT ON fleet.service_order
    FOR EACH ROW EXECUTE FUNCTION util.trg_assign_document_number('SO');
CREATE TRIGGER trg_purchase_order_number BEFORE INSERT ON procurement.purchase_order
    FOR EACH ROW EXECUTE FUNCTION util.trg_assign_document_number('PO');

-- ---------------------------------------------------------------------------
-- Stock ledger -> balance
-- ---------------------------------------------------------------------------
CREATE FUNCTION inventory.trg_stock_movement_apply() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_sign       smallint;
    v_available  numeric;
BEGIN
    SELECT mt.balance_sign INTO STRICT v_sign
    FROM ref.movement_type mt WHERE mt.code = NEW.movement_type_code;

    IF v_sign = 0 THEN
        RETURN NULL;                         -- imported history: no physical effect
    END IF;

    IF v_sign < 0 THEN
        -- Row lock serialises concurrent issues of the same SKU at the same hub.
        SELECT b.available_qty INTO v_available
        FROM inventory.stock_balance b
        WHERE b.warehouse_id = NEW.warehouse_id AND b.part_id = NEW.part_id
        FOR UPDATE;
        IF coalesce(v_available, 0) < NEW.quantity THEN
            RAISE EXCEPTION 'Insufficient stock of part % in warehouse %: available %, requested % (%)',
                  NEW.part_id, NEW.warehouse_id, coalesce(v_available, 0), NEW.quantity, NEW.movement_type_code
                  USING ERRCODE = 'SS200',
                        HINT = 'Reserved stock must be consumed through its reservation.';
        END IF;
    END IF;

    IF v_sign < 0 THEN
        -- The row exists and is locked (checked above). Plain UPDATE: an upsert would
        -- evaluate the CHECK constraints on the negative *proposed* row first.
        UPDATE inventory.stock_balance b
           SET on_hand_qty      = b.on_hand_qty - NEW.quantity,
               last_movement_at = greatest(b.last_movement_at, NEW.occurred_at),
               updated_at       = now()
         WHERE b.warehouse_id = NEW.warehouse_id AND b.part_id = NEW.part_id;
    ELSE
        INSERT INTO inventory.warehouse_part (warehouse_id, part_id)
        VALUES (NEW.warehouse_id, NEW.part_id)
        ON CONFLICT DO NOTHING;

        INSERT INTO inventory.stock_balance AS b (warehouse_id, part_id, on_hand_qty, last_movement_at)
        VALUES (NEW.warehouse_id, NEW.part_id, NEW.quantity, NEW.occurred_at)
        ON CONFLICT (warehouse_id, part_id) DO UPDATE
            SET on_hand_qty      = b.on_hand_qty + EXCLUDED.on_hand_qty,
                last_movement_at = greatest(b.last_movement_at, EXCLUDED.last_movement_at),
                updated_at       = now();
    END IF;
    RETURN NULL;
END $$;
COMMENT ON FUNCTION inventory.trg_stock_movement_apply() IS 'AFTER INSERT on the ledger: applies +/- quantity to stock_balance with a row lock; rejects issues exceeding available stock (SS200).';

CREATE TRIGGER trg_stock_movement_apply AFTER INSERT ON inventory.stock_movement
    FOR EACH ROW EXECUTE FUNCTION inventory.trg_stock_movement_apply();
CREATE TRIGGER trg_stock_movement_immutable BEFORE UPDATE OR DELETE ON inventory.stock_movement
    FOR EACH ROW EXECUTE FUNCTION util.trg_forbid_modification();

-- ---------------------------------------------------------------------------
-- Reservations
-- ---------------------------------------------------------------------------
CREATE FUNCTION inventory.trg_stock_reservation_guard() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT' THEN
        IF NEW.status NOT IN ('soft', 'firm') THEN
            RAISE EXCEPTION 'A reservation must be created as soft or firm, not %', NEW.status USING ERRCODE = 'SS300';
        END IF;
        RETURN NEW;
    END IF;

    IF (NEW.warehouse_id, NEW.part_id) IS DISTINCT FROM (OLD.warehouse_id, OLD.part_id)
       OR NEW.service_order_line_id  IS DISTINCT FROM OLD.service_order_line_id
       OR NEW.transfer_order_line_id IS DISTINCT FROM OLD.transfer_order_line_id THEN
        RAISE EXCEPTION 'Location and source document of reservation % cannot be changed', OLD.id USING ERRCODE = 'SS300';
    END IF;

    IF OLD.status NOT IN ('soft', 'firm')
       AND (NEW.status IS DISTINCT FROM OLD.status OR NEW.quantity IS DISTINCT FROM OLD.quantity) THEN
        RAISE EXCEPTION 'Reservation % is already closed (%)', OLD.id, OLD.status USING ERRCODE = 'SS300';
    END IF;

    IF NEW.status IS DISTINCT FROM OLD.status THEN
        IF NOT ((OLD.status = 'soft' AND NEW.status IN ('firm', 'consumed', 'released', 'expired'))
             OR (OLD.status = 'firm' AND NEW.status IN ('consumed', 'released'))) THEN
            RAISE EXCEPTION 'Invalid reservation transition % -> %', OLD.status, NEW.status USING ERRCODE = 'SS300';
        END IF;
        IF NEW.status IN ('consumed', 'released', 'expired') THEN
            NEW.closed_at := now();
        END IF;
    END IF;
    RETURN NEW;
END $$;

CREATE FUNCTION inventory.trg_stock_reservation_sync() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_delta      numeric := 0;
    v_wh         smallint := coalesce(NEW.warehouse_id, OLD.warehouse_id);
    v_part       bigint   := coalesce(NEW.part_id, OLD.part_id);
    v_available  numeric;
BEGIN
    IF TG_OP IN ('UPDATE', 'DELETE') AND OLD.status IN ('soft', 'firm') THEN v_delta := v_delta - OLD.quantity; END IF;
    IF TG_OP IN ('INSERT', 'UPDATE') AND NEW.status IN ('soft', 'firm') THEN v_delta := v_delta + NEW.quantity; END IF;
    IF v_delta = 0 THEN RETURN NULL; END IF;

    SELECT b.available_qty INTO v_available
    FROM inventory.stock_balance b
    WHERE b.warehouse_id = v_wh AND b.part_id = v_part
    FOR UPDATE;

    IF v_delta > 0 AND coalesce(v_available, 0) < v_delta THEN
        RAISE EXCEPTION 'Insufficient available stock of part % in warehouse %: available %, to reserve %',
              v_part, v_wh, coalesce(v_available, 0), v_delta USING ERRCODE = 'SS200';
    END IF;

    UPDATE inventory.stock_balance
       SET reserved_qty = reserved_qty + v_delta, updated_at = now()
     WHERE warehouse_id = v_wh AND part_id = v_part;
    RETURN NULL;
END $$;
COMMENT ON FUNCTION inventory.trg_stock_reservation_sync() IS 'Keeps stock_balance.reserved_qty equal to the sum of active (soft/firm) reservations.';

CREATE TRIGGER trg_stock_reservation_guard BEFORE INSERT OR UPDATE ON inventory.stock_reservation
    FOR EACH ROW EXECUTE FUNCTION inventory.trg_stock_reservation_guard();
CREATE TRIGGER trg_stock_reservation_sync AFTER INSERT OR UPDATE OR DELETE ON inventory.stock_reservation
    FOR EACH ROW EXECUTE FUNCTION inventory.trg_stock_reservation_sync();

-- Background job (Quartz.NET, every 15 min): expire 7-day soft holds of service kits.
CREATE PROCEDURE inventory.expire_soft_reservations(INOUT p_expired integer DEFAULT 0)
LANGUAGE plpgsql AS $$
BEGIN
    WITH expired AS (
        UPDATE inventory.stock_reservation
           SET status = 'expired'
         WHERE status = 'soft' AND expires_at < now()
        RETURNING service_order_line_id
    ), reopened AS (
        UPDATE fleet.service_order so
           SET status = 'draft'
         WHERE so.status = 'parts_reserved'
           AND so.id IN (SELECT l.service_order_id
                         FROM fleet.service_order_line l
                         JOIN expired e ON e.service_order_line_id = l.id)
        RETURNING so.id
    )
    SELECT count(*) INTO p_expired FROM expired;
END $$;
COMMENT ON PROCEDURE inventory.expire_soft_reservations(integer) IS 'Expires overdue soft reservations and returns affected service orders to draft.';

-- ---------------------------------------------------------------------------
-- In-transit quantity: dispatched transfers + open purchase orders
-- ---------------------------------------------------------------------------
CREATE FUNCTION inventory.fn_in_transit_qty(p_warehouse_id smallint, p_part_id bigint) RETURNS numeric
LANGUAGE sql STABLE AS $$
    SELECT coalesce((SELECT sum(l.quantity)
                     FROM inventory.transfer_order_line l
                     JOIN inventory.transfer_order o ON o.id = l.transfer_order_id
                     WHERE o.to_warehouse_id = p_warehouse_id AND l.part_id = p_part_id
                       AND o.status = 'dispatched'), 0)
         + coalesce((SELECT sum(pl.quantity - pl.received_qty)
                     FROM procurement.purchase_order_line pl
                     JOIN procurement.purchase_order po ON po.id = pl.purchase_order_id
                     WHERE po.warehouse_id = p_warehouse_id AND pl.part_id = p_part_id
                       AND po.status IN ('approved', 'exported', 'partially_received')), 0)
$$;
COMMENT ON FUNCTION inventory.fn_in_transit_qty(smallint, bigint) IS 'Inbound quantity: dispatched transfers + not yet received approved purchase orders.';

-- ---------------------------------------------------------------------------
-- Transfer order workflow
-- ---------------------------------------------------------------------------
CREATE FUNCTION inventory.trg_transfer_order_status_guard() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.status IS DISTINCT FROM OLD.status AND NOT (
          (OLD.status = 'proposed'   AND NEW.status IN ('approved', 'cancelled'))
       OR (OLD.status = 'approved'   AND NEW.status IN ('dispatched', 'cancelled'))
       OR (OLD.status = 'dispatched' AND NEW.status = 'received')) THEN
        RAISE EXCEPTION 'Invalid transfer order transition % -> % (order %)', OLD.status, NEW.status, OLD.number
              USING ERRCODE = 'SS300';
    END IF;
    IF OLD.status <> 'proposed' AND (NEW.from_warehouse_id, NEW.to_warehouse_id) IS DISTINCT FROM (OLD.from_warehouse_id, OLD.to_warehouse_id) THEN
        RAISE EXCEPTION 'Route of transfer order % cannot change after approval', OLD.number USING ERRCODE = 'SS300';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER trg_transfer_order_status_guard BEFORE UPDATE ON inventory.transfer_order
    FOR EACH ROW EXECUTE FUNCTION inventory.trg_transfer_order_status_guard();

-- Lines can only be edited while the order is still a proposal.
CREATE FUNCTION inventory.trg_transfer_line_editable() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    v_status inventory.transfer_status;
BEGIN
    SELECT status INTO v_status FROM inventory.transfer_order
    WHERE id = coalesce(NEW.transfer_order_id, OLD.transfer_order_id);
    IF v_status IS DISTINCT FROM 'proposed' THEN
        RAISE EXCEPTION 'Lines of a % transfer order cannot be changed', v_status USING ERRCODE = 'SS300';
    END IF;
    RETURN coalesce(NEW, OLD);
END $$;
CREATE TRIGGER trg_transfer_line_editable BEFORE INSERT OR UPDATE OR DELETE ON inventory.transfer_order_line
    FOR EACH ROW EXECUTE FUNCTION inventory.trg_transfer_line_editable();

CREATE FUNCTION inventory.approve_transfer(p_transfer_id bigint) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_user   uuid := util.require_role('BRAND_MANAGER', 'INVENTORY_PLANNER');
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
COMMENT ON FUNCTION inventory.approve_transfer(bigint) IS 'UC01 step 10-11: approves a proposed transfer and places firm reservations at the donor (atomic, all lines or none).';

CREATE FUNCTION inventory.dispatch_transfer(p_transfer_id bigint) RETURNS void
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
    SELECT home_warehouse_id INTO v_home FROM iam.app_user WHERE id = v_user;
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

CREATE FUNCTION inventory.receive_transfer(p_transfer_id bigint) RETURNS void
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
    SELECT home_warehouse_id INTO v_home FROM iam.app_user WHERE id = v_user;
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

CREATE FUNCTION inventory.cancel_transfer(p_transfer_id bigint, p_reason text) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_user   uuid := util.require_role('BRAND_MANAGER', 'INVENTORY_PLANNER', 'ADMINISTRATOR');
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

-- ---------------------------------------------------------------------------
-- Exceptional demand (US08)
-- ---------------------------------------------------------------------------
CREATE FUNCTION inventory.trg_demand_exception_validate() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF NOT EXISTS (SELECT 1
                   FROM inventory.stock_movement m
                   JOIN ref.movement_type t ON t.code = m.movement_type_code
                   WHERE m.id = NEW.movement_id AND m.occurred_at = NEW.movement_occurred_at AND t.is_demand) THEN
        RAISE EXCEPTION 'Movement % is not a demand movement and cannot be flagged as exceptional', NEW.movement_id
              USING ERRCODE = 'check_violation';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER trg_demand_exception_validate BEFORE INSERT OR UPDATE ON inventory.demand_exception
    FOR EACH ROW EXECUTE FUNCTION inventory.trg_demand_exception_validate();

CREATE FUNCTION inventory.flag_exceptional_demand(p_movement_id bigint, p_occurred_at timestamptz, p_reason text,
                                                  p_method text DEFAULT 'manual') RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_user uuid := util.require_role('INVENTORY_PLANNER', 'BRAND_MANAGER');
BEGIN
    PERFORM set_config('app.reason', p_reason, true);
    INSERT INTO inventory.demand_exception (movement_id, movement_occurred_at, reason, detection_method, flagged_by)
    VALUES (p_movement_id, p_occurred_at, p_reason, p_method, v_user)
    ON CONFLICT (movement_id, movement_occurred_at) DO UPDATE
        SET reason = EXCLUDED.reason, flagged_by = EXCLUDED.flagged_by, flagged_at = now();
END $$;

CREATE FUNCTION inventory.find_exceptional_demand_candidates(
        p_sigma numeric DEFAULT 3, p_lookback interval DEFAULT interval '24 months', p_min_events integer DEFAULT 8)
RETURNS TABLE (movement_id bigint, occurred_at timestamptz, warehouse_id smallint, part_id bigint,
               quantity numeric, mean_qty numeric, stddev_qty numeric, z_score numeric)
LANGUAGE sql STABLE AS $$
    WITH demand AS (
        SELECT m.id, m.occurred_at, m.warehouse_id, m.part_id, m.quantity,
               avg(m.quantity)         OVER w AS mean_qty,
               stddev_samp(m.quantity) OVER w AS sd,
               count(*)                OVER w AS n
        FROM inventory.stock_movement m
        JOIN ref.movement_type t ON t.code = m.movement_type_code AND t.is_demand
        WHERE m.occurred_at >= now() - p_lookback
        WINDOW w AS (PARTITION BY m.part_id)
    )
    SELECT d.id, d.occurred_at, d.warehouse_id, d.part_id, d.quantity,
           round(d.mean_qty, 3), round(d.sd, 3), round((d.quantity - d.mean_qty) / d.sd, 2)
    FROM demand d
    WHERE d.n >= p_min_events AND d.sd > 0 AND d.quantity > d.mean_qty + p_sigma * d.sd
      AND NOT EXISTS (SELECT 1 FROM inventory.demand_exception e
                      WHERE e.movement_id = d.id AND e.movement_occurred_at = d.occurred_at)
    ORDER BY 8 DESC
$$;
COMMENT ON FUNCTION inventory.find_exceptional_demand_candidates(numeric, interval, integer) IS 'US08: demand lines above mean + 3 sigma of the SKU (window functions), not yet flagged.';
