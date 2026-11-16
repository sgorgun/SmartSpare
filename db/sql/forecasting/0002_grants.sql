-- =============================================================================
-- Forecasting 0002 — privileges on forecast objects. V015 statements restricted to
-- Forecasting. fn_stock_position and v_recommendation_explained are created and granted
-- by the last Reporting migration; app_ml's USAGE on mart is granted by Reporting.
-- =============================================================================

REVOKE EXECUTE ON ALL FUNCTIONS IN SCHEMA forecast FROM PUBLIC;
REVOKE EXECUTE ON ALL PROCEDURES IN SCHEMA forecast FROM PUBLIC;

-- ---- app_read: read business data ---------------------------------------------
GRANT USAGE ON SCHEMA forecast TO app_read;
GRANT SELECT ON ALL TABLES IN SCHEMA forecast TO app_read;
GRANT EXECUTE ON FUNCTION forecast.fn_classify_demand(numeric, numeric) TO app_read;
-- New tables in this schema become readable automatically.
ALTER DEFAULT PRIVILEGES FOR ROLE smartspare_owner IN SCHEMA forecast
    GRANT SELECT ON TABLES TO app_read;

-- ---- app_write: the ASP.NET Core API -----------------------------------------
GRANT EXECUTE ON FUNCTION forecast.decide_recommendation(bigint, text, numeric, text) TO app_write;

-- ---- app_ml: Quartz.NET forecasting / retraining jobs ------------------------
GRANT USAGE ON SCHEMA forecast TO app_ml;
GRANT INSERT ON forecast.ml_model_version, forecast.benchmark_result, forecast.forecast_run,
                forecast.demand_forecast, forecast.recommendation TO app_ml;
GRANT UPDATE (status, rmsse, mase, mae, rmse, hyperparameters, artifact_uri, artifact_sha256, trained_at)
    ON forecast.ml_model_version TO app_ml;
GRANT UPDATE (status, finished_at, error_message) ON forecast.forecast_run TO app_ml;
GRANT DELETE ON forecast.demand_forecast TO app_ml;                      -- re-run of a failed run
GRANT EXECUTE ON FUNCTION forecast.promote_model(integer, numeric) TO app_ml;
GRANT EXECUTE ON PROCEDURE forecast.expire_open_recommendations(interval) TO app_ml;

-- Outbox and inbox (ADR 0003, MODULE_MAPPING 4.3 and 5.6): the API (app_write) writes events with its
-- data and records handled messages; the jobs (app_write + app_ml) claim and mark them. No DELETE.
GRANT SELECT, INSERT, UPDATE ON forecast.outbox_message TO app_write, app_ml;
GRANT SELECT, INSERT ON forecast.inbox_message TO app_write, app_ml;
