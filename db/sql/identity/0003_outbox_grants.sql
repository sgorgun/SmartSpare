-- =============================================================================
-- Identity 0003 — privileges on the outbox and inbox of iam (migration AddOutboxInbox).
-- =============================================================================

-- Outbox and inbox (ADR 0003, MODULE_MAPPING 4.3 and 5.6): the API (app_write) writes events with its
-- data and records handled messages; the jobs (app_write + app_ml) claim and mark them. No DELETE.
GRANT SELECT, INSERT, UPDATE ON iam.outbox_message TO app_write, app_ml;
GRANT SELECT, INSERT ON iam.inbox_message TO app_write, app_ml;
