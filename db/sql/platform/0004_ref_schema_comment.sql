-- Reference data is seeded by the Platform migrations since stage 2 (PR 2e); the comment written by
-- 0001_util_helpers.sql still named the removed SQL seed scripts. Applied by UpdateRefSchemaComment.
COMMENT ON SCHEMA ref IS 'Core layer: fixed reference (lookup) data, seeded by the Platform migrations (db/sql/platform/seed_ref.sql).';
