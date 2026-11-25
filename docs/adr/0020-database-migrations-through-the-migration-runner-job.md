# ADR 0020: Database migrations in Azure through the MigrationRunner job

- Status: Accepted
- Date: 2026-11-25
- Amends: [ADR 0004](0004-hybrid-ef-core-code-first.md), [ADR 0009](0009-azure-hosting-and-delivery.md), [ADR 0015](0015-deployment-topology-microservices-behind-yarp-gateway.md) §5

## Context

ADR 0004, ADR 0009 and ADR 0015 §5 planned to deploy schema changes to Azure with **EF Core migration bundles**:
- one self-contained executable per DbContext, built in CI;
- run by the pipeline under `smartspare_migrator` before the new revision;
- in the services topology, only the service's own module's bundle.

Since then, `SmartSpare.MigrationRunner` has become the one path that builds every database:
- it runs the cluster bootstrap (`db/bootstrap/000_bootstrap_cluster.sql`: roles, database, grants) when the migrator login or the database is missing;
- it applies every context in the order of `SmartSpare.ModuleCatalog` (Platform first);
- it checks model drift (`check-model`);
- it is used by the AppHost, by `tools/verify_database.sh` (103 SQL assertions on the runner-built database), by the integration tests and by the end-to-end test.

The stage 4 plan compared both options for Azure ([STAGE_4_PLAN.md](../stage-4/STAGE_4_PLAN.md), section 8.4). Facts checked on 25 November 2026:
- EF Core 10 bundles take one `--context` each. `--context "*"` arrives with EF Core 11 ([EF CLI](https://learn.microsoft.com/ef/core/cli/dotnet#common-options), [EF Core 11](https://learn.microsoft.com/ef/core/what-is-new/ef-core-11.0/whatsnew#migrations)). The bundles don't run the bootstrap and don't know the catalogue order.
- Microsoft's guidance is to run migrations "as a one-shot deployment job after the database is healthy", with a separate deployment identity, and never from every replica's entry point ([applying migrations](https://learn.microsoft.com/ef/core/managing-schemas/migrations/applying#bundles)).

The product owner prefers the runner (25 November 2026).

## Decision

1. **Artefact.** CI builds one container image of `SmartSpare.MigrationRunner` per release (SDK container publishing, like the services), tagged with the commit SHA. No EF bundles are built.
2. **Where it runs.** A **manual Container Apps job** `migrations` in the same environment as the services. The deployment pipeline starts an execution and waits for its result. The job reaches PostgreSQL from inside the environment, so GitHub runners need no database firewall rule.
3. **What it runs.**
   - **First deployment of a server:** `migrate` with the server admin connection from Key Vault, which runs the bootstrap once and then all contexts in catalogue order.
   - **Afterwards:** `migrate --module <name>` under `smartspare_migrator`. It applies pending Platform migrations, then the named module's.
     - It **fails** if any module earlier in the catalogue has pending migrations, so per-service deployments can't apply changes out of order.
     - It stops at the first failure; each migration is transactional, so the database stays at the previous version, and the pipeline doesn't deploy the new revision.
4. **Per-service independence (ADR 0015 §6).** Each service's pipeline runs the job for its own module only. A Platform change runs with whichever service deploys first.
5. **Unchanged:**
   - migrations never run at application start-up;
   - `check-model` remains a CI gate;
   - the migration history stays per module (`<schema>.__ef_migrations_history`);
   - the single host and local development keep using the runner as today.
6. **Unverified, decided in PR 4h:** whether `az containerapp job start` can override the arguments of one execution. If it can't, there is one manual job per module (`migrate-<module>`) with fixed arguments, which costs nothing while idle.

## Consequences

- One tested program builds the database everywhere, including the bootstrap and the order; Azure is not a separate path.
- One image per release instead of seven bundles (about 70 MB each, self-contained).
- A new `--module` option and its ordering rule, covered by unit tests and by the topology job.
- The runner, until now "development and CI only", becomes a deployable artefact. It holds no secrets: connections come from Key Vault references, and from stage 10 the migrator's Entra token.
- The job is billed like any Container Apps job, for seconds per deployment, within the free grant.

## Alternatives considered

| Alternative | Why not |
|---|---|
| Seven EF migration bundles run by the pipeline (ADR 0004, 0009, 0015 as written) | Microsoft's documented default, and naturally per service. But a second, untested path: the bootstrap would need a separate step, the catalogue order would be encoded in YAML, and the GitHub runner would need network access to the database. |
| Aspire's `PublishAsMigrationBundle` | Aspire is used for local development only (ADR 0009); deployment is Bicep and GitHub Actions. |
| Migrate on application start-up | Rejected by ADR 0004: every replica would race, and the application login would need DDL rights. |
