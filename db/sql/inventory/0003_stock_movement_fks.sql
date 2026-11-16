-- =============================================================================
-- Inventory 0003 — intra-module FKs of inventory.stock_movement.
-- Added after EF has created the referenced tables. Definitions from V005 (inline in CREATE TABLE
-- there: warehouse, part; ALTER: transfer line) and V010 (import batch).
-- =============================================================================

ALTER TABLE inventory.stock_movement
    ADD CONSTRAINT fk_stock_movement_warehouse FOREIGN KEY (warehouse_id) REFERENCES inventory.warehouse (id);

ALTER TABLE inventory.stock_movement
    ADD CONSTRAINT fk_stock_movement_part      FOREIGN KEY (part_id)      REFERENCES catalog.part (id);

ALTER TABLE inventory.stock_movement
    ADD CONSTRAINT fk_stock_movement_transfer_line FOREIGN KEY (transfer_order_line_id)
        REFERENCES inventory.transfer_order_line (id);

ALTER TABLE inventory.stock_movement
    ADD CONSTRAINT fk_stock_movement_import_batch FOREIGN KEY (import_batch_id) REFERENCES raw.import_batch (id);
