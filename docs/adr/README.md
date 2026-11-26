# Architecture Decision Records

| ADR | Title | Status |
|---|---|---|
| [0001](0001-record-architecture-decisions.md) | Record architecture decisions | Accepted |
| [0002](0002-modular-monolith.md) | Microservice-ready modular monolith | Accepted, amended by 0015 |
| [0003](0003-module-boundaries-and-integration-events.md) | Module boundaries and integration events | Accepted, amended by 0017 |
| [0004](0004-hybrid-ef-core-code-first.md) | Hybrid EF Core code-first | Accepted, amended by 0020 |
| [0005](0005-own-handlers-instead-of-mediatr.md) | Own handlers instead of MediatR | Accepted |
| [0006](0006-frontend-blazor-wasm-mudblazor-bff.md) | Frontend: Blazor WebAssembly, MudBlazor and BFF | Accepted, amended by 0015, 0016 |
| [0007](0007-dotnet-10-lts.md) | .NET 10 LTS as the platform | Accepted |
| [0008](0008-mlnet-demand-forecasting.md) | ML.NET for demand forecasting | Accepted |
| [0009](0009-azure-hosting-and-delivery.md) | Azure hosting and delivery | Accepted, amended by 0015, 0017, 0018, 0020, 0022 |
| [0010](0010-english-only-repository.md) | English-only repository | Accepted |
| [0011](0011-database-ownership-per-module.md) | Database ownership per module | Accepted |
| [0012](0012-evolvability-principles.md) | Evolvability principles | Accepted, amended by 0019 |
| [0013](0013-domain-model-and-persistence.md) | Domain model and persistence | Accepted |
| [0014](0014-browser-sign-in-through-bff-endpoints.md) | Browser sign-in through BFF endpoints | Accepted, amended by 0016; implemented in stage 3 |
| [0015](0015-deployment-topology-microservices-behind-yarp-gateway.md) | Deployment topology: microservices behind a YARP gateway | Accepted, amended by 0020 |
| [0016](0016-authentication-behind-the-gateway.md) | Authentication behind the gateway | Accepted; single-host part implemented in stage 3 |
| [0017](0017-rabbitmq-integration-event-transport.md) | RabbitMQ as the integration-event transport | Accepted, amended by 0021, 0023 |
| [0018](0018-observability-with-the-grafana-stack.md) | Observability with the Grafana stack | Accepted, amended by 0023 |
| [0019](0019-public-api-documentation.md) | Public API documentation | Accepted, amended 26 Nov 2026 (the `/bff` document) |
| [0020](0020-database-migrations-through-the-migration-runner-job.md) | Database migrations in Azure through the MigrationRunner job | Accepted |
| [0021](0021-queue-per-consumer-name.md) | One queue per consumer name | Accepted |
| [0022](0022-azure-region-north-europe.md) | Azure region North Europe | Accepted |
| [0023](0023-message-quota-watched-by-cloudamqp.md) | Message quota watched by CloudAMQP | Accepted |

New ADRs use the next free number and the same Context / Decision / Consequences format.

**Amendments.** An accepted ADR is not rewritten. When a later decision changes part of it:
- its status line says "amended by" the new ADR;
- an "Amendment" section at the end summarises the change;
- sentences that describe the current target are corrected, with the original meaning kept in the amendment section.

A decision that is replaced as a whole gets a new ADR that supersedes it.

**Stage numbers.** ADR 0015 inserted the topology stage as stage 4 of the work plan. Stage numbers 4–10 in ADRs written before 24 November 2026 (0001–0014) refer to the old plan: add one to get the current stage (for example "stage 9" in ADR 0011 is now stage 10).
