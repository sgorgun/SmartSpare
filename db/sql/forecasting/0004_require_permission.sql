-- =============================================================================
-- Forecasting 0004 — the business functions check permissions, not role codes (ADR 0028, PR 7x2). Each function is redefined
-- as it was last defined, with util.require_role(...) replaced by util.require_permission(...) with the permission its
-- endpoint requires (PolicySqlPermissionParityTests). The signatures and grants are unchanged.
-- Generated from the latest definitions; applied scripts stay untouched (#152).
-- =============================================================================

-- forecast.decide_recommendation, as last defined in forecasting/0001_forecasting.sql, with the permission check.
CREATE OR REPLACE FUNCTION forecast.decide_recommendation(p_recommendation_id bigint, p_decision text,
                                               p_final_qty numeric DEFAULT NULL, p_reason text DEFAULT NULL)
RETURNS bigint
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_user   uuid := util.require_permission('Forecasting.Recommendation.Decide');
    v_rec    forecast.recommendation;
    v_qty    numeric;
    v_status forecast.recommendation_status;
    v_doc    bigint;
BEGIN
    IF p_decision NOT IN ('accept', 'override', 'reject') THEN
        RAISE EXCEPTION 'Unknown decision %', p_decision USING ERRCODE = 'invalid_parameter_value';
    END IF;
    SELECT * INTO v_rec FROM forecast.recommendation WHERE id = p_recommendation_id FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'Recommendation % not found', p_recommendation_id USING ERRCODE = 'no_data_found'; END IF;
    IF v_rec.status <> 'open' THEN
        RAISE EXCEPTION 'Recommendation % is already %', p_recommendation_id, v_rec.status USING ERRCODE = 'SS300';
    END IF;
    PERFORM set_config('app.reason', coalesce(p_reason, ''), true);

    IF p_decision = 'reject' THEN
        UPDATE forecast.recommendation SET status = 'rejected', decided_by = v_user, decided_at = now(), decision_reason = p_reason
         WHERE id = p_recommendation_id;
        RETURN NULL;
    END IF;

    v_qty    := CASE WHEN p_decision = 'accept' THEN v_rec.recommended_qty ELSE p_final_qty END;
    v_status := CASE WHEN p_decision = 'accept' THEN 'accepted' ELSE 'overridden' END;

    IF v_rec.kind = 'transfer' THEN
        INSERT INTO inventory.transfer_order (from_warehouse_id, to_warehouse_id, requested_by)
        VALUES (v_rec.donor_warehouse_id, v_rec.warehouse_id, v_user) RETURNING id INTO v_doc;
        INSERT INTO inventory.transfer_order_line (transfer_order_id, part_id, quantity, proposed_qty, override_reason)
        VALUES (v_doc, v_rec.part_id, v_qty, v_rec.recommended_qty, CASE WHEN v_status = 'overridden' THEN p_reason END);
        PERFORM inventory.approve_transfer(v_doc);           -- reserves at the donor (UC01 step 11)
        UPDATE forecast.recommendation
           SET status = v_status, final_qty = v_qty, decided_by = v_user, decided_at = now(),
               decision_reason = p_reason, transfer_order_id = v_doc
         WHERE id = p_recommendation_id;
    ELSE
        -- consolidate into an open draft PO of the same supplier and hub (US13)
        SELECT id INTO v_doc FROM procurement.purchase_order
        WHERE supplier_id = v_rec.supplier_id AND warehouse_id = v_rec.warehouse_id
          AND status = 'draft' AND NOT is_urgent
        ORDER BY created_at LIMIT 1 FOR UPDATE;
        IF v_doc IS NULL THEN
            INSERT INTO procurement.purchase_order (supplier_id, warehouse_id, created_by)
            VALUES (v_rec.supplier_id, v_rec.warehouse_id, v_user) RETURNING id INTO v_doc;
        END IF;
        INSERT INTO procurement.purchase_order_line AS l (purchase_order_id, part_id, quantity, proposed_qty, override_reason)
        VALUES (v_doc, v_rec.part_id, v_qty, v_rec.recommended_qty, CASE WHEN v_status = 'overridden' THEN p_reason END)
        ON CONFLICT (purchase_order_id, part_id) DO UPDATE
            SET quantity        = l.quantity + EXCLUDED.quantity,
                proposed_qty    = coalesce(l.proposed_qty, 0) + EXCLUDED.proposed_qty,
                override_reason = coalesce(EXCLUDED.override_reason, l.override_reason);
        UPDATE forecast.recommendation
           SET status = v_status, final_qty = v_qty, decided_by = v_user, decided_at = now(),
               decision_reason = p_reason, purchase_order_id = v_doc
         WHERE id = p_recommendation_id;
    END IF;
    RETURN v_doc;
END $$;
