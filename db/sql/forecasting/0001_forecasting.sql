-- =============================================================================
-- Forecasting 0001 — model registry, promotion, recommendation decisions.
-- Statements copied verbatim from V001, V008, V012, V014 and V015. decide_recommendation writes
-- inventory and procurement and is TRANSITIONAL. fn_stock_position (LANGUAGE sql, reads three
-- modules) and v_recommendation_explained are created by the last Reporting migration.
-- =============================================================================

COMMENT ON SCHEMA forecast    IS 'Core layer / Forecasting module: ML model versions, forecast runs, benchmarks, recommendations.';

-- Comments of the enum types, which EF creates (V008).
COMMENT ON TYPE forecast.forecast_method IS 'fasttree = ML.NET FastTree regressor; the others are the five statistical benchmarks of KPI-02.';

-- Expression index (V008); EF cannot model an index on coalesce(...).
CREATE UNIQUE INDEX uq_ml_model_version_one_active
    ON forecast.ml_model_version ((coalesce(demand_class_code, '*'))) WHERE status = 'active';

-- Automatic model replacement: candidate must beat the best benchmark RMSSE by >= 10 % (KPI-02).
CREATE FUNCTION forecast.promote_model(p_candidate_id integer, p_min_improvement numeric DEFAULT 0.10) RETURNS boolean
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_cand  forecast.ml_model_version;
    v_score numeric;
    v_best  numeric;
BEGIN
    SELECT * INTO v_cand FROM forecast.ml_model_version WHERE id = p_candidate_id FOR UPDATE;
    IF NOT FOUND OR v_cand.status <> 'candidate' THEN
        RAISE EXCEPTION 'Model version % is not a candidate', p_candidate_id USING ERRCODE = 'SS300';
    END IF;

    -- series-weighted RMSSE over demand classes
    SELECT sum(rmsse * series_count) / sum(series_count) INTO v_score
    FROM forecast.benchmark_result WHERE model_version_id = p_candidate_id AND method = v_cand.algorithm;
    v_score := coalesce(v_score, v_cand.rmsse);

    SELECT min(w) INTO v_best FROM (
        SELECT sum(rmsse * series_count) / sum(series_count) AS w
        FROM forecast.benchmark_result
        WHERE model_version_id = p_candidate_id AND method <> v_cand.algorithm
        GROUP BY method) b;
    IF v_best IS NULL THEN
        RAISE EXCEPTION 'Model version % has no benchmark results', p_candidate_id USING ERRCODE = 'SS300';
    END IF;

    IF v_score <= v_best * (1 - p_min_improvement) THEN
        UPDATE forecast.ml_model_version SET status = 'retired', retired_at = now()
         WHERE status = 'active' AND coalesce(demand_class_code, '*') = coalesce(v_cand.demand_class_code, '*');
        UPDATE forecast.ml_model_version SET status = 'active', promoted_at = now() WHERE id = p_candidate_id;
        RETURN true;
    END IF;
    UPDATE forecast.ml_model_version SET status = 'rejected' WHERE id = p_candidate_id;
    RETURN false;
END $$;
COMMENT ON FUNCTION forecast.promote_model(integer, numeric) IS 'Promotes a candidate only if its weighted RMSSE is >= 10 % lower than the best of the 5 benchmarks; retires the previous active model atomically.';

-- TRANSITIONAL: replaced by TransferRequested / PurchaseRequested in stage 7, #14 (writes inventory and procurement; MODULE_MAPPING 3.1 X16)
-- UC01 steps 10-11 and A2 / US13: human decision on a recommendation.
CREATE FUNCTION forecast.decide_recommendation(p_recommendation_id bigint, p_decision text,
                                               p_final_qty numeric DEFAULT NULL, p_reason text DEFAULT NULL)
RETURNS bigint
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_user   uuid := util.require_role('BRAND_MANAGER', 'INVENTORY_PLANNER');
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
COMMENT ON FUNCTION forecast.decide_recommendation(bigint, text, numeric, text) IS 'Accept / override (with reason) / reject a recommendation; creates and approves a transfer or adds a line to a consolidated draft PO, in one transaction.';

CREATE PROCEDURE forecast.expire_open_recommendations(p_older_than interval DEFAULT interval '14 days')
LANGUAGE sql AS $$
    UPDATE forecast.recommendation SET status = 'expired'
    WHERE status = 'open' AND created_at < now() - p_older_than;
$$;

CREATE FUNCTION forecast.fn_classify_demand(p_adi numeric, p_cv2 numeric) RETURNS varchar
LANGUAGE sql STABLE AS $$
    SELECT dc.code FROM ref.demand_class dc
    WHERE p_adi >= dc.adi_lower AND p_adi < dc.adi_upper AND p_cv2 >= dc.cv2_lower AND p_cv2 < dc.cv2_upper
    LIMIT 1
$$;
COMMENT ON FUNCTION forecast.fn_classify_demand(numeric, numeric) IS 'Syntetos-Boylan class from ADI and CV² using the ranges in ref.demand_class.';

-- ===========================================================================
-- 2. Model status guard: 'active'/'retired' only via forecast.promote_model()
--    (which runs as the schema owner — SECURITY DEFINER).
-- ===========================================================================
CREATE FUNCTION forecast.trg_model_status_guard() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.status IS DISTINCT FROM OLD.status THEN
        IF NOT ((OLD.status = 'training'  AND NEW.status IN ('candidate', 'rejected'))
             OR (OLD.status = 'candidate' AND NEW.status IN ('active', 'rejected'))
             OR (OLD.status = 'active'    AND NEW.status = 'retired')) THEN
            RAISE EXCEPTION 'Invalid model transition % -> %', OLD.status, NEW.status USING ERRCODE = 'SS300';
        END IF;
        IF NEW.status IN ('active', 'retired')
           AND current_user <> (SELECT nspowner::regrole::name FROM pg_namespace WHERE nspname = 'forecast') THEN
            RAISE EXCEPTION 'Model activation is only possible through forecast.promote_model()' USING ERRCODE = 'insufficient_privilege';
        END IF;
    END IF;
    RETURN NEW;
END $$;

CREATE TRIGGER trg_model_status_guard BEFORE UPDATE ON forecast.ml_model_version
    FOR EACH ROW EXECUTE FUNCTION forecast.trg_model_status_guard();

-- Audit triggers (US18), V015.
CREATE TRIGGER trg_audit AFTER INSERT OR UPDATE OR DELETE ON forecast.ml_model_version
    FOR EACH ROW EXECUTE FUNCTION audit.trg_row_audit();
CREATE TRIGGER trg_audit AFTER INSERT OR UPDATE OR DELETE ON forecast.recommendation
    FOR EACH ROW EXECUTE FUNCTION audit.trg_row_audit();
