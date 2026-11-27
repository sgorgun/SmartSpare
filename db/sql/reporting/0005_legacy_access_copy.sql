-- =============================================================================
-- Reporting 0005 — migration step 2 of STAGE_7_PLAN 1.6 (ADR 0026 decision 6): the one-time copy of the legacy brand
-- lists (iam.user_brand) and home hubs (iam.app_user.home_warehouse_id) into the responsibility assignments of the
-- Inventory records, and of those into Identity's projection, so an existing database keeps exactly its rights.
-- It is the only cross-module data step of the stage. It runs in the Reporting context because the migrator applies
-- Reporting last, after Identity and Inventory created both sides, and Reporting is the one context allowed to read
-- every schema (MODULE_MAPPING 3.3); no module migration reads another module's schema. Idempotent: every insert is
-- ON CONFLICT DO NOTHING, and each source is skipped once PR 7e has dropped it. Anonymised users are not copied.
-- A home hub becomes KEEPER for a warehouse keeper and SERVICE_BASE for everyone else (engineers' bases).
-- =============================================================================

DO $$
BEGIN
    IF to_regclass('iam.user_brand') IS NOT NULL THEN
        INSERT INTO catalog.brand_responsible (brand_id, employee_id, record_kind, assignment_role)
        SELECT ub.brand_id, u.employee_id, 'brand', 'BRAND_MANAGER'
        FROM iam.user_brand ub
        JOIN iam.app_user u ON u.id = ub.user_id
        WHERE u.anonymised_at IS NULL
        ON CONFLICT (brand_id, employee_id, assignment_role) DO NOTHING;
    END IF;

    IF EXISTS (SELECT 1 FROM information_schema.columns
               WHERE table_schema = 'iam' AND table_name = 'app_user' AND column_name = 'home_warehouse_id') THEN
        EXECUTE $copy$
            INSERT INTO inventory.warehouse_responsible (warehouse_id, employee_id, record_kind, assignment_role)
            SELECT u.home_warehouse_id, u.employee_id, 'warehouse',
                   CASE WHEN EXISTS (SELECT 1 FROM iam.employee e
                                     JOIN iam.position_role pr ON pr.position_id = e.position_id
                                     JOIN iam.role r ON r.id = pr.role_id
                                     WHERE e.id = u.employee_id AND r.code = 'WAREHOUSE_KEEPER')
                        THEN 'KEEPER' ELSE 'SERVICE_BASE' END
            FROM iam.app_user u
            WHERE u.home_warehouse_id IS NOT NULL AND u.anonymised_at IS NULL
              AND EXISTS (SELECT 1 FROM inventory.warehouse w WHERE w.id = u.home_warehouse_id)
            ON CONFLICT (warehouse_id, employee_id, assignment_role) DO NOTHING
        $copy$;
    END IF;

    INSERT INTO iam.employee_responsibility (record_kind, record_id, employee_id, assignment_role)
    SELECT 'brand', r.brand_id, r.employee_id, r.assignment_role
    FROM catalog.brand_responsible r
    JOIN ref.assignment_role ar ON ar.record_kind = 'brand' AND ar.code = r.assignment_role AND ar.grants_scope
    WHERE EXISTS (SELECT 1 FROM iam.employee e WHERE e.id = r.employee_id)
    UNION ALL
    SELECT 'warehouse', r.warehouse_id, r.employee_id, r.assignment_role
    FROM inventory.warehouse_responsible r
    JOIN ref.assignment_role ar ON ar.record_kind = 'warehouse' AND ar.code = r.assignment_role AND ar.grants_scope
    WHERE EXISTS (SELECT 1 FROM iam.employee e WHERE e.id = r.employee_id)
    ON CONFLICT DO NOTHING;

    INSERT INTO iam.responsibility_source (record_kind, record_id, source_occurred_at)
    SELECT DISTINCT record_kind, record_id, now() FROM iam.employee_responsibility
    ON CONFLICT DO NOTHING;
END $$;
