# ADR 0008: ML.NET for demand forecasting

- Status: Accepted
- Date: 2026-11-16

## Context

The diploma topic requires machine-learning demand forecasting for spare parts, where demand is often intermittent. Options: ML.NET inside the application or a separate Python service.

## Decision

Use ML.NET 5.x inside the Forecasting module. The primary model is one global gradient-boosted regression (LightGBM / FastTreeTweedie) over lag, rolling-mean, calendar, engine-hour and demand-class features. Baselines: Croston, SBA and TSB for intermittent demand; seasonal naive and SSA (`ForecastBySsa`) for regular demand. Select the model per demand class using `fn_classify_demand` and `benchmark_result` on a chronological holdout. Models are stored in Blob Storage, versioned in `ml_model_version` and promoted with `promote_model`; training is a scheduled job under the `app_ml` role, inference uses `PredictionEnginePool`.

## Consequences

- One language and one deployment.
- ML.NET's own time-series support is limited to SSA, so intermittent-demand methods are implemented in C#.
- The time-series package depends on Intel MKL; images are built for linux-x64.
