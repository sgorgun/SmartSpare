-- =============================================================================
-- Inventory 0014 — adjustment reasons, the first catalogue of the shared master-data mechanism (ADR 0025,
-- STAGE_7_PLAN 3.1). The table itself is EF Fluent (AdjustmentReasonConfiguration); this script adds:
--   * the triggers every catalogue has (updated_at, audit) and its registration for the record history;
--   * grants: every role reads, app_write maintains (the endpoint policy ManageCatalog is the control);
--   * the standard reasons;
--   * inventory.stock_movement.adjustment_reason_id: every new ADJUSTMENT_IN/OUT names its reason (and needs a note
--     only if the reason says so, so ck_stock_movement_source_document accepts the reason instead). The ledger is
--     immutable, so the rule is checked on INSERT by a trigger rather than a CHECK over the history: older
--     adjustments keep only their note. The reason must allow the direction of the movement.
-- =============================================================================

CREATE TRIGGER trg_adjustment_reason_updated_at BEFORE UPDATE ON inventory.adjustment_reason
    FOR EACH ROW EXECUTE FUNCTION util.trg_set_updated_at();
CREATE TRIGGER trg_audit AFTER INSERT OR UPDATE OR DELETE ON inventory.adjustment_reason
    FOR EACH ROW EXECUTE FUNCTION audit.trg_row_audit();

INSERT INTO audit.history_table (table_name) VALUES ('inventory.adjustment_reason');

GRANT SELECT ON inventory.adjustment_reason TO app_read;
GRANT INSERT, UPDATE ON inventory.adjustment_reason TO app_write;

INSERT INTO inventory.adjustment_reason (code, name, direction, requires_note) VALUES
    ('COUNT_CORRECTION', 'Stock count correction', 'BOTH', true),
    ('DAMAGED',          'Damaged in storage',     'OUT',  false),
    ('LOST',             'Lost or missing',        'OUT',  false),
    ('EXPIRED',          'Expired or obsolete',    'OUT',  false),
    ('FOUND',            'Found in storage',       'IN',   false);

ALTER TABLE inventory.stock_movement ADD COLUMN adjustment_reason_id smallint;
ALTER TABLE inventory.stock_movement
    ADD CONSTRAINT fk_stock_movement_adjustment_reason FOREIGN KEY (adjustment_reason_id) REFERENCES inventory.adjustment_reason (id);
-- An adjustment documents itself by its reason now; the note is required only when the reason says so (checked by
-- the application). Older adjustments all have a note, so the new CHECK holds for the history.
ALTER TABLE inventory.stock_movement DROP CONSTRAINT ck_stock_movement_source_document;
ALTER TABLE inventory.stock_movement ADD CONSTRAINT ck_stock_movement_source_document CHECK (
    CASE movement_type_code
        WHEN 'RECEIPT_PO'        THEN purchase_order_line_id IS NOT NULL
        WHEN 'TRANSFER_OUT'      THEN transfer_order_line_id IS NOT NULL
        WHEN 'TRANSFER_IN'       THEN transfer_order_line_id IS NOT NULL
        WHEN 'ISSUE_SERVICE'     THEN service_order_line_id  IS NOT NULL
        WHEN 'ISSUE_SALE'        THEN external_doc_ref       IS NOT NULL
        WHEN 'HISTORICAL_DEMAND' THEN import_batch_id        IS NOT NULL
        WHEN 'ADJUSTMENT_IN'     THEN note IS NOT NULL OR adjustment_reason_id IS NOT NULL
        WHEN 'ADJUSTMENT_OUT'    THEN note IS NOT NULL OR adjustment_reason_id IS NOT NULL
        ELSE true
    END);
COMMENT ON COLUMN inventory.stock_movement.adjustment_reason_id IS
    'Why the stock was corrected (inventory.adjustment_reason); required for new ADJUSTMENT_IN/OUT, empty on older ones.';

CREATE FUNCTION inventory.trg_stock_movement_adjustment_reason() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp AS $$
DECLARE
    v_direction varchar(4);
    v_needed    varchar(4);
BEGIN
    IF NEW.movement_type_code IN ('ADJUSTMENT_IN', 'ADJUSTMENT_OUT') THEN
        IF NEW.adjustment_reason_id IS NULL THEN
            RAISE EXCEPTION 'A stock adjustment needs a reason' USING ERRCODE = 'check_violation',
                CONSTRAINT = 'ck_stock_movement_adjustment_reason';
        END IF;
        SELECT r.direction INTO v_direction FROM inventory.adjustment_reason r WHERE r.id = NEW.adjustment_reason_id;
        v_needed := CASE NEW.movement_type_code WHEN 'ADJUSTMENT_IN' THEN 'IN' ELSE 'OUT' END;
        IF v_direction IS DISTINCT FROM 'BOTH' AND v_direction IS DISTINCT FROM v_needed THEN
            RAISE EXCEPTION 'The adjustment reason does not allow %', NEW.movement_type_code USING ERRCODE = 'check_violation',
                CONSTRAINT = 'ck_stock_movement_adjustment_reason';
        END IF;
    ELSIF NEW.adjustment_reason_id IS NOT NULL THEN
        RAISE EXCEPTION 'Only a stock adjustment has an adjustment reason' USING ERRCODE = 'check_violation',
            CONSTRAINT = 'ck_stock_movement_adjustment_reason';
    END IF;
    RETURN NEW;
END $$;
COMMENT ON FUNCTION inventory.trg_stock_movement_adjustment_reason() IS
    'BEFORE INSERT on the ledger: a new adjustment names a reason that allows its direction; other movements have none (ADR 0025).';
CREATE TRIGGER trg_stock_movement_adjustment_reason BEFORE INSERT ON inventory.stock_movement
    FOR EACH ROW EXECUTE FUNCTION inventory.trg_stock_movement_adjustment_reason();
