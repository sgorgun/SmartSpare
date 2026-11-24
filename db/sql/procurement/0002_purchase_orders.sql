-- =============================================================================
-- Procurement 0002 — purchase orders: line rules, totals, approval workflow.
-- Statements copied verbatim from V007, V011, V012 and V015; references to the supplier tables
-- now name procurement.* (ADR 0011 D2). The goods receipt writes inventory and is TRANSITIONAL.
-- procurement.v_purchase_order_overview reads inventory and iam: last Reporting migration.
-- =============================================================================

-- Comment of the enum type, which EF creates (V007).
COMMENT ON TYPE procurement.po_status IS 'PO life cycle; transitions are validated by trigger procurement.trg_po_status_transition.';

-- updated_at and document-number triggers (V007, V011)
CREATE TRIGGER trg_purchase_order_updated_at BEFORE UPDATE ON procurement.purchase_order
    FOR EACH ROW EXECUTE FUNCTION util.trg_set_updated_at();
CREATE TRIGGER trg_purchase_order_number BEFORE INSERT ON procurement.purchase_order
    FOR EACH ROW EXECUTE FUNCTION util.trg_assign_document_number('PO');

-- US12: supplier conditions are hard constraints of every PO line.
CREATE FUNCTION procurement.trg_po_line_validate() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    v_po    procurement.purchase_order;
    v_sp    procurement.supplier_part;
    v_price numeric;
BEGIN
    SELECT * INTO v_po FROM procurement.purchase_order
    WHERE id = coalesce(NEW.purchase_order_id, OLD.purchase_order_id);

    IF v_po.status NOT IN ('draft', 'pending_approval') THEN
        RAISE EXCEPTION 'Lines of purchase order % (%) cannot be changed', v_po.number, v_po.status USING ERRCODE = 'SS300';
    END IF;
    IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;

    SELECT * INTO v_sp FROM procurement.supplier_part WHERE supplier_id = v_po.supplier_id AND part_id = NEW.part_id;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Part % is not offered by supplier %', NEW.part_id, v_po.supplier_id USING ERRCODE = 'SS401';
    END IF;
    IF NEW.quantity < v_sp.moq OR mod(NEW.quantity, v_sp.pack_size) <> 0 THEN
        RAISE EXCEPTION 'Quantity % violates supplier conditions (MOQ %, pack size %)', NEW.quantity, v_sp.moq, v_sp.pack_size
              USING ERRCODE = 'SS400',
                    HINT = format('Nearest valid quantity: %s', ceil(greatest(NEW.quantity, v_sp.moq) / v_sp.pack_size) * v_sp.pack_size);
    END IF;
    IF NEW.unit_price IS NULL THEN   -- default price from the price list valid today
        SELECT sp.unit_price INTO v_price FROM procurement.supplier_price sp
        WHERE sp.supplier_part_id = v_sp.id AND sp.valid_during @> current_date;
        IF v_price IS NULL THEN
            RAISE EXCEPTION 'No valid price of supplier % for part % on %', v_po.supplier_id, NEW.part_id, current_date USING ERRCODE = 'SS402';
        END IF;
        NEW.unit_price := v_price;
    END IF;
    RETURN NEW;
END $$;

CREATE TRIGGER trg_po_line_validate BEFORE INSERT OR DELETE OR UPDATE OF part_id, quantity, unit_price
    ON procurement.purchase_order_line FOR EACH ROW EXECUTE FUNCTION procurement.trg_po_line_validate();

CREATE FUNCTION procurement.trg_po_line_total() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_po bigint := coalesce(NEW.purchase_order_id, OLD.purchase_order_id);
BEGIN
    UPDATE procurement.purchase_order po
       SET total_amount = (SELECT coalesce(sum(l.line_amount), 0)
                           FROM procurement.purchase_order_line l WHERE l.purchase_order_id = v_po)
     WHERE po.id = v_po;
    RETURN NULL;
END $$;

CREATE TRIGGER trg_po_line_total AFTER INSERT OR DELETE OR UPDATE OF quantity, unit_price
    ON procurement.purchase_order_line FOR EACH ROW EXECUTE FUNCTION procurement.trg_po_line_total();

CREATE FUNCTION procurement.trg_po_status_guard() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.status IS DISTINCT FROM OLD.status AND NOT (
          (OLD.status = 'draft'              AND NEW.status IN ('pending_approval', 'cancelled'))
       OR (OLD.status = 'pending_approval'   AND NEW.status IN ('approved', 'draft', 'cancelled'))
       OR (OLD.status = 'approved'           AND NEW.status IN ('exported', 'partially_received', 'received', 'cancelled'))
       OR (OLD.status = 'exported'           AND NEW.status IN ('partially_received', 'received', 'cancelled'))
       OR (OLD.status = 'partially_received' AND NEW.status = 'received')) THEN
        RAISE EXCEPTION 'Invalid purchase order transition % -> % (order %)', OLD.status, NEW.status, OLD.number USING ERRCODE = 'SS300';
    END IF;
    IF OLD.status NOT IN ('draft', 'pending_approval') AND NEW.supplier_id IS DISTINCT FROM OLD.supplier_id THEN
        RAISE EXCEPTION 'Supplier of an approved purchase order cannot change' USING ERRCODE = 'SS300';
    END IF;
    RETURN NEW;
END $$;

CREATE TRIGGER trg_po_status_guard BEFORE UPDATE ON procurement.purchase_order
    FOR EACH ROW EXECUTE FUNCTION procurement.trg_po_status_guard();

CREATE FUNCTION procurement.submit_purchase_order(p_po_id bigint) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_user uuid := util.require_role('BRAND_MANAGER', 'INVENTORY_PLANNER');
BEGIN
    PERFORM 1 FROM procurement.purchase_order WHERE id = p_po_id FOR UPDATE;
    IF NOT EXISTS (SELECT 1 FROM procurement.purchase_order_line WHERE purchase_order_id = p_po_id) THEN
        RAISE EXCEPTION 'Purchase order % has no lines', p_po_id USING ERRCODE = 'SS300';
    END IF;
    UPDATE procurement.purchase_order SET status = 'pending_approval', submitted_at = now() WHERE id = p_po_id;
END $$;

CREATE FUNCTION procurement.approve_purchase_order(p_po_id bigint) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_user uuid := util.require_role('BRAND_MANAGER', 'SPONSOR');
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
COMMENT ON FUNCTION procurement.approve_purchase_order(bigint) IS 'US14: approves a submitted PO; > EUR 5,000 requires an approver other than the author (also a CHECK constraint).';

CREATE FUNCTION procurement.export_purchase_order(p_po_id bigint) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_user uuid := util.require_role('BRAND_MANAGER', 'INVENTORY_PLANNER');
BEGIN
    UPDATE procurement.purchase_order SET status = 'exported', exported_at = now() WHERE id = p_po_id;
END $$;

-- TRANSITIONAL: replaced by PurchaseOrderLineReceived and inventory.expected_receipt in stage 7, #13 (writes inventory.stock_movement; MODULE_MAPPING 3.1 X12)
CREATE FUNCTION procurement.receive_purchase_order_line(p_line_id bigint, p_quantity numeric) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_user uuid := util.require_role('WAREHOUSE_KEEPER', 'ADMINISTRATOR');
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

CREATE FUNCTION procurement.cancel_purchase_order(p_po_id bigint, p_reason text) RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_user uuid := util.require_role('BRAND_MANAGER', 'SPONSOR', 'ADMINISTRATOR');
BEGIN
    IF EXISTS (SELECT 1 FROM procurement.purchase_order_line WHERE purchase_order_id = p_po_id AND received_qty > 0) THEN
        RAISE EXCEPTION 'Purchase order % already has receipts and cannot be cancelled', p_po_id USING ERRCODE = 'SS300';
    END IF;
    PERFORM set_config('app.reason', p_reason, true);
    UPDATE procurement.purchase_order SET status = 'cancelled', cancel_reason = p_reason WHERE id = p_po_id;
END $$;

-- Audit triggers (US18), V015.
CREATE TRIGGER trg_audit AFTER INSERT OR UPDATE OR DELETE ON procurement.purchase_order
    FOR EACH ROW EXECUTE FUNCTION audit.trg_row_audit();
CREATE TRIGGER trg_audit AFTER INSERT OR UPDATE OR DELETE ON procurement.purchase_order_line
    FOR EACH ROW EXECUTE FUNCTION audit.trg_row_audit();
