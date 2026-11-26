-- =============================================================================
-- Fleet 0005 — Fleet's copy of the reserved kit quantity (migration ServiceOrderLineReservedQty, STAGE_6_PLAN 5.3,
-- MODULE_MAPPING X9, #12). service_order_line.qty_reserved is added by the EF migration before this script runs; the
-- view binds the column at creation, so it is created here and not in 0001.
-- =============================================================================

-- Kits that are not fully reserved, module-local since stage 6 (MODULE_MAPPING X9): Fleet's copy of the reserved
-- quantity (service_order_line.qty_reserved) and of the part (fleet.part_snapshot) instead of inventory and catalog;
-- the hub as an id.
CREATE VIEW fleet.v_service_kit_shortage AS
SELECT s.id AS service_order_id, s.number, s.status, s.warehouse_id,
       l.part_id, p.oem_part_number, p.description,
       l.qty_required, l.qty_issued, l.qty_reserved,
       l.qty_required - l.qty_issued - l.qty_reserved AS qty_missing
FROM fleet.service_order s
JOIN fleet.service_order_line l  ON l.service_order_id = s.id
LEFT JOIN fleet.part_snapshot p  ON p.part_id = l.part_id
WHERE s.status IN ('draft', 'parts_reserved', 'in_progress')
  AND l.qty_required - l.qty_issued - l.qty_reserved > 0;
COMMENT ON VIEW fleet.v_service_kit_shortage IS 'Open service orders whose kit could not be fully reserved (input for transfers / purchasing).';
GRANT SELECT ON fleet.v_service_kit_shortage TO app_read;
