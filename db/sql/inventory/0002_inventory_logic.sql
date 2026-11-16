-- =============================================================================
-- Inventory 0002 — ledger, reservations, transfers, exceptional demand, import pipeline.
-- Statements copied verbatim from V001 (schema comments), V003, V005, V011, V013 and V015.
-- Not here (MODULE_MAPPING 3.2, created by the last Reporting migration in PR 2d): the LANGUAGE sql
-- function inventory.fn_in_transit_qty and the views inventory.v_in_transit / v_stock_overview,
-- which read procurement. Supplier triggers move to procurement in 2d.
-- =============================================================================

COMMENT ON SCHEMA catalog     IS 'Core layer / Inventory module: spare-parts catalogue, suppliers, supplier conditions and prices.';

COMMENT ON SCHEMA inventory   IS 'Core layer / Inventory module: warehouses, stock ledger, balances, reservations, transfers.';

COMMENT ON SCHEMA raw         IS 'Layer 1 (raw): uploaded CSV rows stored as text, 1:1 with the source file, for lineage and re-processing.';

COMMENT ON SCHEMA staging     IS 'Layer 2 (staging): typed and validated rows produced from raw; invalid rows are kept with error messages.';

-- updated_at and document-number triggers (V003, V005, V011)
CREATE TRIGGER trg_part_updated_at BEFORE UPDATE ON catalog.part
    FOR EACH ROW EXECUTE FUNCTION util.trg_set_updated_at();
CREATE TRIGGER trg_warehouse_part_updated_at BEFORE UPDATE ON inventory.warehouse_part
    FOR EACH ROW EXECUTE FUNCTION util.trg_set_updated_at();
CREATE TRIGGER trg_transfer_order_updated_at BEFORE UPDATE ON inventory.transfer_order
    FOR EACH ROW EXECUTE FUNCTION util.trg_set_updated_at();
CREATE TRIGGER trg_transfer_order_number BEFORE INSERT ON inventory.transfer_order
    FOR EACH ROW EXECUTE FUNCTION util.trg_assign_document_number('TR');

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

-- TRANSITIONAL: replaced by the ServiceKitReservationExpired event flow in stage 5 (writes fleet.service_order; MODULE_MAPPING 3.1 X5)
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

-- ---------------------------------------------------------------------------
-- Part catalogue
-- ---------------------------------------------------------------------------
CREATE FUNCTION staging.validate_part_catalog(p_batch_id bigint) RETURNS integer
LANGUAGE plpgsql AS $$
DECLARE
    v_errors integer;
BEGIN
    DELETE FROM staging.import_error WHERE import_batch_id = p_batch_id;
    DELETE FROM staging.part_catalog WHERE import_batch_id = p_batch_id;

    -- typed + normalised projection of the raw rows
    DROP TABLE IF EXISTS tmp_pc;
    CREATE TEMP TABLE tmp_pc ON COMMIT DROP AS
    SELECT r.import_batch_id, r.line_no,
           b.id                                                    AS brand_id,
           upper(regexp_replace(btrim(r.oem_part_number), '\s+', '', 'g')) AS oem,
           left(regexp_replace(btrim(r.description), '\s+', ' ', 'g'), 300) AS description,
           c.id                                                    AS category_id,
           u.id                                                    AS uom_id,
           util.try_numeric(r.weight_kg)                           AS weight_kg,
           util.try_numeric(r.unit_cost)                           AS unit_cost,
           lower(btrim(coalesce(r.condition_based, ''))) IN ('1', 'true', 'yes', 'y') AS condition_based,
           r.brand_code, r.category_code, r.uom_code, r.weight_kg AS weight_raw, r.unit_cost AS cost_raw
    FROM raw.part_catalog_row r
    LEFT JOIN ref.brand b            ON b.code = upper(btrim(r.brand_code))
    LEFT JOIN ref.part_category c    ON c.code = upper(btrim(r.category_code))
    LEFT JOIN ref.unit_of_measure u  ON u.code = upper(btrim(r.uom_code))
    WHERE r.import_batch_id = p_batch_id;
    ANALYZE tmp_pc;

    INSERT INTO staging.import_error (import_batch_id, line_no, column_name, error_code, message)
    SELECT t.import_batch_id, t.line_no, e.col, e.code, e.msg
    FROM tmp_pc t
    CROSS JOIN LATERAL (VALUES
        ('brand_code',      CASE WHEN nullif(btrim(t.brand_code), '') IS NULL THEN 'REQUIRED' WHEN t.brand_id IS NULL THEN 'UNKNOWN_REF' END,
                            format('Unknown or missing brand "%s"', t.brand_code)),
        ('oem_part_number', CASE WHEN nullif(t.oem, '') IS NULL THEN 'REQUIRED' WHEN t.oem !~ '^[A-Z0-9][A-Z0-9.\-/]{2,39}$' THEN 'FORMAT' END,
                            format('Invalid OEM part number "%s"', t.oem)),
        ('description',     CASE WHEN length(coalesce(t.description, '')) < 3 THEN 'REQUIRED' END,
                            'Description is missing or shorter than 3 characters'),
        ('category_code',   CASE WHEN t.category_id IS NULL THEN 'UNKNOWN_REF' END,
                            format('Unknown category "%s"', t.category_code)),
        ('uom_code',        CASE WHEN t.uom_id IS NULL THEN 'UNKNOWN_REF' END,
                            format('Unknown unit of measure "%s"', t.uom_code)),
        ('weight_kg',       CASE WHEN nullif(btrim(t.weight_raw), '') IS NOT NULL AND t.weight_kg IS NULL THEN 'TYPE'
                                 WHEN t.weight_kg < 0 OR t.weight_kg > 999999 THEN 'RANGE' END,
                            format('Invalid weight "%s"', t.weight_raw)),
        ('unit_cost',       CASE WHEN nullif(btrim(t.cost_raw), '') IS NULL THEN 'REQUIRED'
                                 WHEN t.unit_cost IS NULL THEN 'TYPE'
                                 WHEN t.unit_cost < 0 OR t.unit_cost > 9999999999 THEN 'RANGE' END,
                            format('Invalid unit cost "%s"', t.cost_raw))
    ) AS e(col, code, msg)
    WHERE e.code IS NOT NULL
    UNION ALL
    -- duplicates inside the file: the first occurrence wins
    SELECT d.import_batch_id, d.line_no, 'oem_part_number', 'DUPLICATE', format('Duplicate of line %s', d.first_line)
    FROM (SELECT t.import_batch_id, t.line_no, t.brand_id, t.oem,
                 min(t.line_no) OVER (PARTITION BY t.brand_id, t.oem) AS first_line
          FROM tmp_pc t) d
    WHERE d.brand_id IS NOT NULL AND d.oem <> '' AND d.line_no > d.first_line;

    INSERT INTO staging.part_catalog (import_batch_id, line_no, brand_id, oem_part_number, description, category_id,
                                      uom_id, weight_kg, unit_cost, is_condition_based, is_valid)
    SELECT t.import_batch_id, t.line_no, t.brand_id, left(nullif(t.oem, ''), 40), nullif(t.description, ''),
           t.category_id, t.uom_id,
           CASE WHEN t.weight_kg BETWEEN 0 AND 999999 THEN round(t.weight_kg, 3) END,
           CASE WHEN t.unit_cost BETWEEN 0 AND 9999999999 THEN round(t.unit_cost, 2) END,
           t.condition_based,
           NOT EXISTS (SELECT 1 FROM staging.import_error e WHERE e.import_batch_id = t.import_batch_id AND e.line_no = t.line_no)
    FROM tmp_pc t;

    SELECT count(DISTINCT line_no) INTO v_errors FROM staging.import_error WHERE import_batch_id = p_batch_id;
    RETURN v_errors;
END $$;

CREATE FUNCTION staging.load_part_catalog(p_batch_id bigint) RETURNS integer
LANGUAGE plpgsql AS $$
DECLARE
    v_rows integer;
BEGIN
    -- Upsert by natural key; only rows whose values actually change are updated.
    INSERT INTO catalog.part AS p (brand_id, oem_part_number, description, category_id, uom_id, weight_kg, unit_cost, is_condition_based)
    SELECT s.brand_id, s.oem_part_number, s.description, s.category_id, s.uom_id, s.weight_kg, s.unit_cost, s.is_condition_based
    FROM staging.part_catalog s
    WHERE s.import_batch_id = p_batch_id AND s.is_valid
    ON CONFLICT (brand_id, oem_part_number) DO UPDATE
        SET description = EXCLUDED.description, category_id = EXCLUDED.category_id, uom_id = EXCLUDED.uom_id,
            weight_kg = EXCLUDED.weight_kg, unit_cost = EXCLUDED.unit_cost, is_condition_based = EXCLUDED.is_condition_based
        WHERE (p.description, p.category_id, p.uom_id, p.weight_kg, p.unit_cost, p.is_condition_based)
              IS DISTINCT FROM
              (EXCLUDED.description, EXCLUDED.category_id, EXCLUDED.uom_id, EXCLUDED.weight_kg, EXCLUDED.unit_cost, EXCLUDED.is_condition_based);
    GET DIAGNOSTICS v_rows = ROW_COUNT;
    RETURN v_rows;
END $$;

-- ---------------------------------------------------------------------------
-- Demand history (incremental: new rows only, deduplicated by business hash)
-- ---------------------------------------------------------------------------
CREATE FUNCTION staging.validate_demand_history(p_batch_id bigint) RETURNS integer
LANGUAGE plpgsql AS $$
DECLARE
    v_errors integer;
BEGIN
    DELETE FROM staging.import_error   WHERE import_batch_id = p_batch_id;
    DELETE FROM staging.demand_history WHERE import_batch_id = p_batch_id;

    DROP TABLE IF EXISTS tmp_dh;
    CREATE TEMP TABLE tmp_dh ON COMMIT DROP AS
    SELECT r.import_batch_id, r.line_no,
           util.try_date(r.doc_date)         AS doc_date,
           w.id                              AS warehouse_id,
           p.id                              AS part_id,
           util.try_numeric(r.quantity)      AS quantity,
           left(nullif(btrim(r.doc_ref), ''), 40) AS doc_ref,
           r.doc_date AS date_raw, r.warehouse_code, r.brand_code, r.oem_part_number, r.quantity AS qty_raw
    FROM raw.demand_history_row r
    LEFT JOIN inventory.warehouse w ON w.code = upper(btrim(r.warehouse_code))
    LEFT JOIN ref.brand b           ON b.code = upper(btrim(r.brand_code))
    LEFT JOIN catalog.part p        ON p.brand_id = b.id
                                   AND p.oem_part_number = upper(regexp_replace(btrim(r.oem_part_number), '\s+', '', 'g'))
    WHERE r.import_batch_id = p_batch_id;
    ANALYZE tmp_dh;

    INSERT INTO staging.import_error (import_batch_id, line_no, column_name, error_code, message)
    SELECT t.import_batch_id, t.line_no, e.col, e.code, e.msg
    FROM tmp_dh t
    CROSS JOIN LATERAL (VALUES
        ('doc_date', CASE WHEN t.doc_date IS NULL THEN 'FORMAT'
                          WHEN t.doc_date > current_date OR t.doc_date < DATE '2000-01-01' THEN 'RANGE' END,
                     format('Invalid date "%s" (expected YYYY-MM-DD, not in the future)', t.date_raw)),
        ('warehouse_code', CASE WHEN t.warehouse_id IS NULL THEN 'UNKNOWN_REF' END,
                     format('Unknown warehouse "%s"', t.warehouse_code)),
        ('oem_part_number', CASE WHEN t.part_id IS NULL THEN 'UNKNOWN_REF' END,
                     format('Unknown part %s/%s (import the catalogue first)', t.brand_code, t.oem_part_number)),
        ('quantity', CASE WHEN t.quantity IS NULL THEN 'TYPE' WHEN t.quantity <= 0 OR t.quantity > 999999 THEN 'RANGE' END,
                     format('Invalid quantity "%s"', t.qty_raw))
    ) AS e(col, code, msg)
    WHERE e.code IS NOT NULL;

    INSERT INTO staging.demand_history (import_batch_id, line_no, occurred_at, warehouse_id, part_id, quantity, doc_ref, row_hash, is_valid)
    SELECT t.import_batch_id, t.line_no,
           (t.doc_date + time '12:00') AT TIME ZONE 'Europe/Vilnius',
           t.warehouse_id, t.part_id,
           CASE WHEN t.quantity > 0 AND t.quantity <= 999999 THEN round(t.quantity, 3) END,   -- out-of-range stays NULL (error logged)
           t.doc_ref,
           CASE WHEN t.doc_date IS NOT NULL AND t.warehouse_id IS NOT NULL AND t.part_id IS NOT NULL
                     AND t.quantity > 0 AND t.quantity <= 999999
                THEN public.digest(concat_ws('|', t.doc_date, t.warehouse_id, t.part_id, coalesce(t.doc_ref, ''), round(t.quantity, 3)), 'sha256') END,
           NOT EXISTS (SELECT 1 FROM staging.import_error e WHERE e.import_batch_id = t.import_batch_id AND e.line_no = t.line_no)
    FROM tmp_dh t;

    ANALYZE staging.demand_history;

    -- Deduplication: within the file (window function, first line wins) and
    -- against batches loaded earlier (partial index ix_staging_demand_history_hash).
    UPDATE staging.demand_history d SET is_duplicate = true
    FROM (
        SELECT x.line_no
        FROM (SELECT s.line_no, row_number() OVER (PARTITION BY s.row_hash ORDER BY s.line_no) AS rn
              FROM staging.demand_history s
              WHERE s.import_batch_id = p_batch_id AND s.is_valid) x
        WHERE x.rn > 1
        UNION
        SELECT s.line_no
        FROM staging.demand_history s
        WHERE s.import_batch_id = p_batch_id AND s.is_valid
          AND EXISTS (SELECT 1 FROM staging.demand_history d2
                      JOIN raw.import_batch b2 ON b2.id = d2.import_batch_id AND b2.status = 'loaded'
                      WHERE d2.row_hash = s.row_hash AND d2.is_valid AND NOT d2.is_duplicate)
    ) dup
    WHERE d.import_batch_id = p_batch_id AND d.line_no = dup.line_no;

    INSERT INTO staging.import_error (import_batch_id, line_no, column_name, error_code, message)
    SELECT import_batch_id, line_no, 'row', 'DUPLICATE', 'Record already imported (same date, hub, part, document, quantity)'
    FROM staging.demand_history WHERE import_batch_id = p_batch_id AND is_duplicate;

    SELECT count(DISTINCT line_no) INTO v_errors FROM staging.import_error
    WHERE import_batch_id = p_batch_id AND error_code <> 'DUPLICATE';
    RETURN v_errors;
END $$;

CREATE FUNCTION staging.load_demand_history(p_batch_id bigint) RETURNS integer
LANGUAGE plpgsql AS $$
DECLARE
    v_rows integer;
BEGIN
    INSERT INTO inventory.stock_movement (occurred_at, warehouse_id, part_id, movement_type_code, quantity,
                                          import_batch_id, external_doc_ref)
    SELECT s.occurred_at, s.warehouse_id, s.part_id, 'HISTORICAL_DEMAND', s.quantity, p_batch_id, s.doc_ref
    FROM staging.demand_history s
    WHERE s.import_batch_id = p_batch_id AND s.is_valid AND NOT s.is_duplicate
    ORDER BY s.occurred_at;
    GET DIAGNOSTICS v_rows = ROW_COUNT;
    RETURN v_rows;
END $$;

-- ---------------------------------------------------------------------------
-- Orchestration with a data-quality gate and load report
-- ---------------------------------------------------------------------------
CREATE FUNCTION staging.process_batch(p_batch_id bigint, p_max_error_ratio numeric DEFAULT 0.05)
RETURNS raw.import_batch
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_batch   raw.import_batch;
    v_total   integer;
    v_errors  integer;
    v_dups    integer := 0;
    v_loaded  integer := 0;
BEGIN
    SELECT * INTO v_batch FROM raw.import_batch WHERE id = p_batch_id FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'Import batch % not found', p_batch_id USING ERRCODE = 'no_data_found'; END IF;
    IF v_batch.status NOT IN ('uploaded', 'failed') THEN
        RAISE EXCEPTION 'Import batch % is already %', p_batch_id, v_batch.status USING ERRCODE = 'SS300';
    END IF;

    IF v_batch.source = 'part_catalog' THEN
        SELECT count(*) INTO v_total FROM raw.part_catalog_row WHERE import_batch_id = p_batch_id;
        v_errors := staging.validate_part_catalog(p_batch_id);
    ELSE
        SELECT count(*) INTO v_total FROM raw.demand_history_row WHERE import_batch_id = p_batch_id;
        v_errors := staging.validate_demand_history(p_batch_id);
        SELECT count(*) INTO v_dups FROM staging.demand_history WHERE import_batch_id = p_batch_id AND is_duplicate;
    END IF;

    IF v_total = 0 OR v_errors::numeric / v_total > p_max_error_ratio THEN
        UPDATE raw.import_batch
           SET status = 'failed', total_rows = v_total, valid_rows = v_total - v_errors, error_rows = v_errors,
               loaded_rows = 0, processed_at = now(),
               message = CASE WHEN v_total = 0 THEN 'Empty file'
                              ELSE format('%s of %s rows invalid (%s%%) > threshold %s%%: nothing loaded',
                                          v_errors, v_total, round(100.0 * v_errors / v_total, 1), round(100 * p_max_error_ratio, 1)) END
         WHERE id = p_batch_id
        RETURNING * INTO v_batch;
        RETURN v_batch;
    END IF;

    v_loaded := CASE v_batch.source WHEN 'part_catalog' THEN staging.load_part_catalog(p_batch_id)
                                    ELSE staging.load_demand_history(p_batch_id) END;

    UPDATE raw.import_batch
       SET status = 'loaded', total_rows = v_total, valid_rows = v_total - v_errors, error_rows = v_errors,
           loaded_rows = v_loaded, processed_at = now(),
           message = format('%s rows: %s loaded/updated, %s invalid, %s duplicates skipped', v_total, v_loaded, v_errors, v_dups)
     WHERE id = p_batch_id
    RETURNING * INTO v_batch;
    RETURN v_batch;
END $$;
COMMENT ON FUNCTION staging.process_batch(bigint, numeric) IS 'Validates a raw batch into staging and loads core tables if the share of invalid rows <= threshold (default 5 %). Returns the load report.';

-- Audit triggers (US18), V015.
CREATE TRIGGER trg_audit AFTER INSERT OR UPDATE OR DELETE ON catalog.part
    FOR EACH ROW EXECUTE FUNCTION audit.trg_row_audit();
CREATE TRIGGER trg_audit AFTER INSERT OR UPDATE OR DELETE ON inventory.warehouse_part
    FOR EACH ROW EXECUTE FUNCTION audit.trg_row_audit();
CREATE TRIGGER trg_audit AFTER INSERT OR UPDATE OR DELETE ON inventory.stock_reservation
    FOR EACH ROW EXECUTE FUNCTION audit.trg_row_audit();
CREATE TRIGGER trg_audit AFTER INSERT OR UPDATE OR DELETE ON inventory.demand_exception
    FOR EACH ROW EXECUTE FUNCTION audit.trg_row_audit();
CREATE TRIGGER trg_audit AFTER INSERT OR UPDATE OR DELETE ON inventory.transfer_order
    FOR EACH ROW EXECUTE FUNCTION audit.trg_row_audit();
CREATE TRIGGER trg_audit AFTER INSERT OR UPDATE OR DELETE ON inventory.transfer_order_line
    FOR EACH ROW EXECUTE FUNCTION audit.trg_row_audit();
