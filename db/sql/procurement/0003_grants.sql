-- =============================================================================
-- Procurement 0003 — privileges on procurement objects (incl. the supplier tables
-- moved from catalog, ADR 0011 D2). V015 statements restricted to Procurement.
-- procurement.v_purchase_order_overview is created and granted by the last Reporting migration.
-- =============================================================================

REVOKE EXECUTE ON ALL FUNCTIONS IN SCHEMA procurement FROM PUBLIC;

-- ---- app_read: read business data ---------------------------------------------
GRANT USAGE ON SCHEMA procurement TO app_read;
GRANT SELECT ON ALL TABLES IN SCHEMA procurement TO app_read;
-- New tables in this schema become readable automatically.
ALTER DEFAULT PRIVILEGES FOR ROLE smartspare_owner IN SCHEMA procurement
    GRANT SELECT ON TABLES TO app_read;

-- ---- app_write: the ASP.NET Core API -----------------------------------------
GRANT INSERT, UPDATE ON procurement.supplier, procurement.supplier_part TO app_write;
GRANT INSERT, UPDATE, DELETE ON procurement.supplier_price TO app_write;

GRANT INSERT ON procurement.purchase_order TO app_write;
GRANT UPDATE (warehouse_id, is_urgent, urgent_reason, supplier_id) ON procurement.purchase_order TO app_write;
GRANT INSERT, DELETE ON procurement.purchase_order_line TO app_write;
GRANT UPDATE (quantity, unit_price, override_reason) ON procurement.purchase_order_line TO app_write;

GRANT EXECUTE ON FUNCTION
    procurement.submit_purchase_order(bigint), procurement.approve_purchase_order(bigint),
    procurement.export_purchase_order(bigint), procurement.receive_purchase_order_line(bigint, numeric),
    procurement.cancel_purchase_order(bigint, text)
TO app_write;

-- Outbox and inbox (ADR 0003, MODULE_MAPPING 4.3 and 5.6): the API (app_write) writes events with its
-- data and records handled messages; the jobs (app_write + app_ml) claim and mark them. No DELETE.
GRANT SELECT, INSERT, UPDATE ON procurement.outbox_message TO app_write, app_ml;
GRANT SELECT, INSERT ON procurement.inbox_message TO app_write, app_ml;
