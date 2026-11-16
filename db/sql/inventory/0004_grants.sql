-- =============================================================================
-- Inventory 0004 — privileges on catalog, inventory, raw and staging objects.
-- V015 statements restricted to the Inventory schemas. Supplier tables (procurement in 2d)
-- and inventory.fn_in_transit_qty / the transitional views (Reporting, 2d) are granted there.
-- NO write privilege on inventory.stock_balance / stock_reservation / demand_exception:
-- they change only through triggers and SECURITY DEFINER functions.
-- =============================================================================

REVOKE EXECUTE ON ALL FUNCTIONS IN SCHEMA catalog, inventory, raw, staging FROM PUBLIC;
REVOKE EXECUTE ON ALL PROCEDURES IN SCHEMA inventory FROM PUBLIC;

-- ---- app_read: read business data ---------------------------------------------
GRANT USAGE ON SCHEMA catalog, inventory TO app_read;
GRANT SELECT ON ALL TABLES IN SCHEMA catalog, inventory TO app_read;
-- New tables in these schemas become readable automatically.
ALTER DEFAULT PRIVILEGES FOR ROLE smartspare_owner IN SCHEMA catalog, inventory
    GRANT SELECT ON TABLES TO app_read;

-- ---- app_write: the ASP.NET Core API -----------------------------------------
GRANT USAGE ON SCHEMA raw, staging TO app_write;
GRANT SELECT ON ALL TABLES IN SCHEMA raw, staging TO app_write;

GRANT INSERT, UPDATE ON catalog.part TO app_write;
GRANT INSERT, UPDATE, DELETE ON catalog.part_analogue TO app_write;

GRANT INSERT ON inventory.warehouse_part TO app_write;
GRANT UPDATE (bin_location, safety_stock_qty, max_stock_qty, is_stocked) ON inventory.warehouse_part TO app_write;
GRANT INSERT ON inventory.stock_movement TO app_write;                   -- sales, adjustments; balance via trigger
GRANT INSERT, DELETE ON inventory.transfer_order TO app_write;
GRANT UPDATE (is_urgent, from_warehouse_id, to_warehouse_id) ON inventory.transfer_order TO app_write;
GRANT INSERT, DELETE ON inventory.transfer_order_line TO app_write;
GRANT UPDATE (quantity, override_reason) ON inventory.transfer_order_line TO app_write;

GRANT INSERT ON raw.import_batch, raw.part_catalog_row, raw.demand_history_row TO app_write;

GRANT EXECUTE ON FUNCTION
    inventory.approve_transfer(bigint), inventory.dispatch_transfer(bigint), inventory.receive_transfer(bigint),
    inventory.cancel_transfer(bigint, text),
    inventory.flag_exceptional_demand(bigint, timestamptz, text, text),
    inventory.find_exceptional_demand_candidates(numeric, interval, integer),
    staging.process_batch(bigint, numeric)
TO app_write;

-- ---- app_ml: Quartz.NET jobs -----------------------------------------------------
GRANT EXECUTE ON PROCEDURE inventory.expire_soft_reservations(integer) TO app_ml;

-- ---- app_admin: application administration ---------------------------------------
GRANT INSERT, UPDATE ON inventory.warehouse, inventory.warehouse_route TO app_admin;
