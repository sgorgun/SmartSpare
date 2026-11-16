# ADR 0009: Azure hosting and delivery

- Status: Accepted
- Date: 2026-11-16

## Context

The system is deployed to Microsoft Azure and must be able to move to microservices later.

## Decision

Ship a container image from day one. Now: App Service (Linux, Basic with Always On), PostgreSQL Flexible Server 17 (Burstable), Blob Storage, Key Vault, Azure Container Registry, Application Insights via OpenTelemetry. One Managed Identity with `SET ROLE` per module in the monolith. Later: Azure Container Apps per module and Azure Service Bus. Infrastructure in Bicep (`infra/`), CI/CD with GitHub Actions signing in via OIDC, EF migration bundle as a pipeline step. Background jobs are multi-instance safe (Quartz clustered, `SKIP LOCKED`). .NET Aspire is used for local development only.

## Consequences

- No long-lived secrets in CI or configuration.
- The same image runs on App Service and Container Apps.
- Bicep templates must be maintained alongside the code.
