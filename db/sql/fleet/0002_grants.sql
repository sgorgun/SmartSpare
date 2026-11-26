-- =============================================================================
-- Fleet 0002 — privileges on fleet objects. V015 statements restricted to fleet.
-- fleet.v_machine_maintenance_status is created in 0001 (module-local since stage 6, X10) and granted below with
-- every table; fleet.v_service_kit_shortage is created and granted in 0005 (X9).
-- =============================================================================

REVOKE EXECUTE ON ALL FUNCTIONS IN SCHEMA fleet FROM PUBLIC;

-- ---- app_read: read business data ---------------------------------------------
GRANT USAGE ON SCHEMA fleet TO app_read;
GRANT SELECT ON ALL TABLES IN SCHEMA fleet TO app_read;
GRANT EXECUTE ON FUNCTION fleet.fn_maintenance_due(bigint, numeric) TO app_read;
-- New tables in this schema become readable automatically.
ALTER DEFAULT PRIVILEGES FOR ROLE smartspare_owner IN SCHEMA fleet
    GRANT SELECT ON TABLES TO app_read;

-- ---- app_write: the ASP.NET Core API -----------------------------------------
GRANT INSERT ON fleet.customer, fleet.machine_model, fleet.machine, fleet.engine_hours_reading TO app_write;
GRANT UPDATE (region_warehouse_id, farm_size_ha, is_active) ON fleet.customer TO app_write;
GRANT UPDATE (model_id, customer_id, operating_condition_id, home_warehouse_id, status, commissioned_on) ON fleet.machine TO app_write;
GRANT INSERT, UPDATE, DELETE ON fleet.maintenance_plan, fleet.maintenance_plan_part TO app_write;
GRANT INSERT ON fleet.service_order TO app_write;                        -- unplanned repairs
GRANT UPDATE (warehouse_id) ON fleet.service_order TO app_write;
GRANT INSERT, DELETE ON fleet.service_order_line TO app_write;
GRANT UPDATE (qty_required) ON fleet.service_order_line TO app_write;

GRANT EXECUTE ON FUNCTION
    fleet.log_engine_hours(bigint, numeric, timestamptz, fleet.reading_source),
    fleet.create_service_order_draft(bigint, integer, numeric),
    fleet.complete_service_order(bigint, numeric), fleet.cancel_service_order(bigint, text)
TO app_write;
