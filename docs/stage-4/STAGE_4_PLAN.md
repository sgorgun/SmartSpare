# SmartSpare — Stage 4 Plan: Topology (Gateway, Services, RabbitMQ, Grafana)

Status: **proposed** (25 November 2026). Stage 4 of the work plan ([ARCHITECTURE_DECISIONS.md, section 13](../architecture/ARCHITECTURE_DECISIONS.md#13-work-plan)), planned in #67. It builds the production topology of [ADR 0015](../adr/0015-deployment-topology-microservices-behind-yarp-gateway.md):
- a YARP gateway that is the BFF, and one service host per module;
- RabbitMQ behind the existing outbox and inbox;
- telemetry to the Grafana stack;
- public API documentation;
- per-service images and pipelines.

Release target: **v0.4.0**. Business use cases stay in stages 5–9.

**Product owner's decisions, 25 November 2026:**
- **(B):** stage 4 builds and tests the topology locally and in CI, and writes the infrastructure and pipelines, but **deploys nothing to Azure**. The first Azure deployment is in stage 10 (#85). CloudAMQP and Grafana Cloud are still used in stage 4, from the local services topology.
- **Deadlines:** the thesis is due on 3 January 2027 and the defence is on 16 February 2027. Stage 4 runs **25 November – 3 December 2026** (milestone v0.4.0); if it runs late, its scope is cut, not the later stages moved (ARCHITECTURE_DECISIONS §13).
- **#31** (outbox back-off and poison handling) moves to stage 4 (PR 4c).

Inputs, as on `main` (v0.3.0):
- [ADR 0009](../adr/0009-azure-hosting-and-delivery.md) with its amendment, and [ADRs 0015–0019](../adr/README.md);
- [STAGE_3_PLAN.md](../stage-3/STAGE_3_PLAN.md) (the BFF, internal tokens and the single host built in stage 3);
- issues #48, #54, #56, #57, #59, #63, #64, #67.

[ADR 0020](../adr/0020-database-migrations-through-the-migration-runner-job.md) is new with this plan: database migrations in Azure run as the MigrationRunner image in a Container Apps job (section 8.4).

Every technology, version and limit below was **checked against official documentation on 25 November 2026**. The source is cited where it is used, and all sources are listed in [section 14](#14-references). Where an official source doesn't state something this plan depends on, the plan says "unverified" and puts the check into the PR that first needs it.

---

## 0. Summary of decisions

| # | Decision | Section |
|---|---|---|
| T1 | **One business codebase, two composition roots.** `SmartSpare.Host` (single host) stays as it is. New: `SmartSpare.Gateway` and six `SmartSpare.Services.<Module>`. Composition helpers in BuildingBlocks install a module, its transport, its authentication and its Contracts clients the same way in every host. An architecture test keeps YARP, RabbitMQ.Client and host projects out of modules. | 1 |
| T2 | **Gateway = BFF:** YARP 2.3.0, cookie scheme only, the stage 3 `SmartSpare.Bff` endpoints, internal ES256 tokens minted per session. Requests are proxied only on `/api/v1/{module}/{**rest}`, the unversioned operational `/api/{module}/{**rest}` (literal module names; product owner, 25 Nov 2026) and `/openapi/{module}.json`; `/internal/*` is never routed, as a route-table test proves. | 2 |
| T3 | **Services:** JwtBearer only, internal ingress only, one database login per service with `Maximum Pool Size=4`, and `/internal/v1/...` endpoints for Contracts queries. | 3 |
| T4 | **Contracts queries over HTTP:** in this stage only `ICredentialVerifier`. Its HTTP client lives in `Identity.Contracts` and calls the Identity service with the gateway's service token. | 4 |
| T5 | **RabbitMQ transport** in `SmartSpare.Infrastructure.RabbitMq` on `RabbitMQ.Client` 7.3.0, behind the existing `IIntegrationEventPublisher` seam: publisher confirms, one connection and two channels per service, durable queues, inbox consumers with stable names (#32). | 5 |
| T6 | **CloudAMQP Little Lemur** in Azure. Three features it needs aren't documented for shared plans: quorum queues, user policies and the management HTTP API. They are **checked on a real instance in PR 4c**. If they're missing, ADR 0017's classic-queue fallback applies, and the queue scale rule falls back to HTTP-only wake-up (5.6). | 5 |
| T7 | **Telemetry:** OTLP directly to Grafana Cloud Free in Azure (ADR 0018). Locally, `--topology services` adds an OpenTelemetry Collector plus Prometheus, Loki, Tempo and Grafana. Dashboards and alert rules are kept as code in `infra/observability/`. One trace spans gateway → service → broker → service. | 6 |
| T8 | **API documentation** (#54): one OpenAPI 3.1 document per module, a public Scalar UI with a **per-request CSP nonce** on `/scalar`; the strict CSP stays. | 7 |
| T9 | **Gates in every pipeline:** unit-test coverage of business logic ≥ 70 %, enforced with ReportGenerator (#63). Vulnerable packages: NuGet Audit fails the build, plus a JSON-parsed `dotnet package list --vulnerable` step (#64). | 8.2 |
| T10 | **Images:** SDK container publishing on `noble-chiseled-extra` (with ICU), one image per service, tagged with the commit SHA. | 8.1 |
| T11 | **Independent deployment:** one workflow per service with path filters; a shared reusable workflow runs the gates. A change to BuildingBlocks or Contracts triggers its dependants. | 8.3 |
| T12 | **Migrations in Azure: the MigrationRunner image as a Container Apps job** with a new `--module` filter, before each service's new revision ([ADR 0020](../adr/0020-database-migrations-through-the-migration-runner-job.md)). The seven EF bundles were compared and rejected. | 8.4 |
| T13 | **Container Apps sizing:** every app 0.25 vCPU / 0.5 GiB, `minReplicas: 0`, `maxReplicas: 1`, single revision mode. At most 28 of the 35 PostgreSQL user connections are in use. Scheduled work runs as cron jobs, plus a warm-up workflow; the cold start target is ≤ 30 s, measured. | 8.5–8.7 |
| T14 | **Forwarded headers (#48):** the gateway trusts exactly one hop (`ForwardLimit = 1`) and takes the rightmost `X-Forwarded-For`, which the Container Apps ingress provides. It uses `KnownIPNetworks`; `KnownNetworks` is obsolete in .NET 10. | 2.5 |
| T15 | **Aspire 13.6.5 → 17.0.0** (needed for `Aspire.Hosting.RabbitMQ` 17.0.0, ADR 0017). | 1.4 |
| T16 | **Decided (B):** stage 4 deploys nothing to Azure. The topology is built and tested locally and in CI; Bicep and pipelines are written and validated with what-if. CloudAMQP and Grafana Cloud are exercised from the local services topology. The first Azure deployment is in stage 10 (#85). The subscription is pay-as-you-go, so NFR07 (≤ €35/month) is the cost limit. | 9 |

---

## 1. Two topologies, one business codebase

### 1.1 Projects

| Project | Kind | Contains | References |
|---|---|---|---|
| `src/Bootstrapper/SmartSpare.Host` | exists | single host: all modules, BFF, in-process transport, Quartz later (stage 5) | all modules, `SmartSpare.Bff` |
| `src/Bootstrapper/SmartSpare.Gateway` | **new** | YARP, BFF, the WebAssembly client, Scalar, warm-up endpoint | `SmartSpare.Bff`, `SmartSpare.Web.Client`, `Identity.Contracts` (HTTP client only), ServiceDefaults. **No module implementation.** |
| `src/Bootstrapper/Services/SmartSpare.Services.<Module>` × 6 | **new** | one module, its migrations-free start-up, RabbitMQ transport, JwtBearer, `/internal` endpoints, job entry points | its module, `SmartSpare.Infrastructure.RabbitMq`, ServiceDefaults |
| `src/BuildingBlocks/SmartSpare.Infrastructure.RabbitMq` | **new** | publisher, consumer host, declarations, broker options | `SmartSpare.Infrastructure`, `RabbitMQ.Client` |
| `src/BuildingBlocks/SmartSpare.Infrastructure` | exists | + composition helpers (`ModuleComposition`), transport seam, `IJob` contract | — |

A service host is about 40 lines:
- `ServiceDefaults`;
- `ModuleComposition.AddModule(installer, Topology.Services)`;
- authentication (JwtBearer with the JWKS of the gateway);
- the RabbitMQ transport;
- `MapEndpoints`.

There is no Blazor in services.

### 1.2 Composition helpers (BuildingBlocks)

`ModuleComposition` installs a module identically in each host:
- `AddServices`;
- the unit of work;
- the outbox dispatcher with the host's transport;
- the inbox consumers;
- the authorization policies.

The host chooses:
- **the transport:** `UseInProcessTransport()` in the single host, `UseRabbitMqTransport(config)` in services;
- **the Contracts client implementation:** in-process (the module's own) or HTTP (section 4);
- **the authentication schemes** of ADR 0016 §4: single host = policy scheme `SmartSpare`; gateway = cookie; services = JwtBearer.

Module code never sees the topology (ADR 0015 §1).

### 1.3 Architecture tests (new)

- No `SmartSpare.Modules.*` assembly (implementation or Contracts) references `Yarp.ReverseProxy`, `RabbitMQ.Client`, `SmartSpare.Infrastructure.RabbitMq`, `SmartSpare.Bff`, or any `SmartSpare.Host` / `SmartSpare.Gateway` / `SmartSpare.Services.*` assembly (ADR 0015 §1, ADR 0017 §1).
- `SmartSpare.Gateway` references no module implementation project, only `Identity.Contracts` (ADR 0016 §1).
- Each `SmartSpare.Services.<Module>` references exactly one module implementation.
- The existing rules stay, including "the web client references only ApiContracts".

### 1.4 Platform upgrade

Aspire packages move from 13.6.5 to **17.0.0** (nuget.org: `Aspire.Hosting` 17.0.0, 18 Nov 2026), together with `Aspire.Hosting.RabbitMQ` 17.0.0. Our 13.6.5 reports no known vulnerability (`dotnet package list --vulnerable`, 25 Nov 2026). The move is for the RabbitMQ integration, and for staying on a supported line. Testcontainers moves to 4.17.0 and gains `Testcontainers.RabbitMq` 4.17.0.

---

## 2. Gateway (YARP BFF)

### 2.1 Routes

`Yarp.ReverseProxy` **2.3.0** (MIT). It is the latest release (27 Feb 2025). It ships net8.0 assets that run on .NET 10, which nuget.org lists as "compatible" ([NuGet](https://www.nuget.org/packages/Yarp.ReverseProxy)). Routes and clusters are built **in code** with `LoadFromMemory` ([config providers](https://learn.microsoft.com/aspnet/core/fundamentals/servers/yarp/config-providers)), one set per module, so there is no hand-written route table to drift. The gateway references no module, so it lists the module names itself (`GatewayRoutes.Modules`), and a test keeps that list equal to the ModuleCatalog.

| Route id | Match | Cluster | Notes |
|---|---|---|---|
| `api-{module}` | `/api/v1/{module}/{**rest}` | `{module}` → `http://{module}` | the versioned business API (ADR 0012). Container Apps internal DNS by app name ([connect apps](https://learn.microsoft.com/azure/container-apps/connect-apps)); locally, Aspire service discovery |
| `ops-{module}` | `/api/{module}/{**rest}` | `{module}` | the unversioned operational endpoints (module pings, the diagnostics probe), which ADR 0012 keeps out of versioning |
| `openapi-{module}` | `/openapi/{module}.json`, GET only | `{module}` | the service serves the same path, so no rewrite; anonymous, no token, output cache and rate limit `api-docs` |

Everything else is handled by the gateway itself: `/bff/*`, `/scalar`, `/warmup`, `/health/*` and the Blazor client.

**Operational routes (product owner, 25 November 2026).** The plan first proxied only `/api/v1`, but every endpoint that exists so far is operational and unversioned (ADR 0012), so the gateway could have reached none. Two conditions came with the `ops-*` routes:
- both routes name the module **literally** (`/api/inventory/{**rest}`), never as a route parameter, so `/api/v1/...` can never match an `ops-*` route;
- a test fails if a module endpoint outside `/api/v1/` lacks the explicit `OperationalEndpointMetadata` (`.AsOperational()`), so business endpoints can't bypass versioning.

Service discovery:
- `Microsoft.Extensions.ServiceDiscovery.Yarp` 10.11.0, the release line of the `Microsoft.Extensions.ServiceDiscovery` 10.11.0 the hosts already use (11.0.1 would move every host to the 11 line), `AddServiceDiscoveryDestinationResolver()` ([NuGet](https://www.nuget.org/packages/Microsoft.Extensions.ServiceDiscovery.Yarp));
- in Azure, the pass-through resolver returns the name unchanged and the environment's DNS resolves it ([service discovery](https://learn.microsoft.com/dotnet/core/extensions/service-discovery)).

### 2.2 Request transforms (ADR 0015 §3, ADR 0016 §2)

One `AddRequestTransform` per API route ([extensibility](https://learn.microsoft.com/aspnet/core/fundamentals/servers/yarp/extensibility-transforms)):
1. remove `Cookie`, `Authorization`, `X-XSRF-TOKEN` (`AddRequestHeaderRemove`);
2. get the internal token for the session (`sid`) from a per-session cache valid until 30 s before expiry, minting with the stage 3 `InternalTokenIssuer` when needed;
3. set `Authorization: Bearer <token>`.

YARP already **removes and replaces** incoming `X-Forwarded-For/Proto/Host/Prefix`, so clients can't spoof them towards services ([header guidelines](https://learn.microsoft.com/aspnet/core/fundamentals/servers/yarp/header-guidelines)).

Anonymous `/api` requests are refused by the gateway's fallback policy (401) before any proxying, so services never see a request without a token. Antiforgery runs in the gateway for unsafe requests (ADR 0016 §5).

### 2.3 Route-table test

YARP documents no helper for testing route matching. `IProxyStateLookup.GetRoutes()` only enumerates the current routes ([API](https://learn.microsoft.com/dotnet/api/yarp.reverseproxy.iproxystatelookup?view=yarp-2.2)). The test therefore combines two checks against the started gateway (`WebApplicationFactory`):
1. **Configured routes:** `GetRoutes()` returns exactly the `api-*`, `ops-*` and (from 4f) `openapi-*` routes; each names its module literally, no `ops-*` route matches `/api/v1/...`, and no route's path pattern can match `/internal`. Each pattern is checked against `/internal/v1/identity/credential-checks` and a generated set of `/internal/...` paths with every module name.
2. **Endpoint matching:** for every HTTP method and module, a request to `/internal/v1/{module}/x` through the gateway returns 404, and a recording test destination receives nothing. The same check for `/health/ready` of a service.

### 2.4 BFF in the gateway

- `SmartSpare.Bff` from stage 3, unchanged.
- `ICredentialVerifier` is the HTTP client of section 4.
- Data Protection (ADR 0016 §6):
  - in Azure: Blob Storage plus `ProtectKeysWithAzureKeyVault` through the gateway's Managed Identity;
  - locally: a file system key ring under the AppHost's data volume (the gateway has no database).
- Sign-in rate limit by client IP, after forwarded headers (2.5).
- CSP and security headers as in stage 3. `/scalar` gets a per-request nonce (section 7).
- `GET /warmup`, anonymous and rate-limited to 2 per minute: calls `/health/ready` of each service over internal ingress in parallel and returns per-service timings (8.7).

### 2.5 Forwarded headers (#48)

- The Container Apps ingress sets `X-Forwarded-Proto` (overwritten if the client sent one) and `X-Forwarded-For`. For `X-Forwarded-For`, "Only the rightmost IP is provided by Azure Container Apps. Any other values must be validated by the user" ([ingress headers](https://learn.microsoft.com/azure/container-apps/ingress-overview#http-headers)).
- The docs name no proxy IP range to trust.
- In .NET 10, `KnownNetworks` is obsolete in favour of `KnownIPNetworks` (warning ASPDEPR005, which `TreatWarningsAsErrors` turns into a build error; [breaking change](https://learn.microsoft.com/aspnet/core/breaking-changes/10/ipnetwork-knownnetworks-obsolete?view=aspnetcore-10.0)).
- Default trust is loopback only, with `ForwardLimit = 1` ([proxy servers](https://learn.microsoft.com/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0)).

Decision for the gateway in Azure:
- `ForwardedHeaders = XForwardedFor | XForwardedProto`, `ForwardLimit = 1`;
- `KnownIPNetworks` and `KnownProxies` cleared, by configuration (`ForwardedHeaders:TrustContainerAppsIngress=true`), only in the Azure environment.

Microsoft warns that clearing both lists "should be considered only when network access independently restricts callers". That condition holds here: the gateway's container has no public address, and every request reaches it through the platform's ingress. `ForwardLimit = 1` takes only the rightmost value, the one the ingress appended.

- **Single host and local:** unchanged (loopback only).
- **Services:** forwarded headers stay disabled. They are reachable only from the gateway, and they never rate-limit by IP.
- **Verification in Azure (stage 10, #85):** a request with a forged `X-Forwarded-For: 1.2.3.4` must still be rate-limited under the real client IP. The gateway logs the resolved remote IP at debug level for the check.

---

## 3. Service hosts

- **Authentication:** JwtBearer only, with `ValidIssuer`, `ValidAudience`, the JWKS from configuration, `ValidAlgorithms = [ES256]` and `ClockSkew = 30 s` (ADR 0016 §2; already used by the single host since 3b). There is no cookie and no antiforgery.
- **Ingress:** internal only. `/internal/v1/{module}/...` endpoints use the policy `GatewayOnly` (`ss:svc=gateway`, ADR 0016 §1) or a user policy, depending on the query.
- **Database login per service** (ADR 0015 §5): `smartspare_identity`, `smartspare_inventory`, `smartspare_fleet`, `smartspare_procurement`, `smartspare_forecasting`, `smartspare_reporting`.
  - They are created by the cluster bootstrap (`db/bootstrap/000_bootstrap_cluster.sql`) as members of today's capability roles: `app_write`; `app_ml` for Forecasting; `app_read` and `app_audit` for Reporting.
  - Per-module roles follow in stage 10 (#16).
  - Locally the passwords are AppHost parameters; in Azure they are Key Vault secrets until Entra logins replace them in stage 10.
- **Connection pool:** `Maximum Pool Size=4` (the Npgsql default is 100; [connection string parameters](https://www.npgsql.org/doc/connection-string-parameters.html)) and `maxReplicas: 1`.
- **Health:** `/health/live` and `/health/ready` from ServiceDefaults; ready includes the database and, in services, the broker connection. The probes are configured explicitly. The Container Apps defaults are TCP probes: startup failure threshold 240 at 1 s, readiness every 5 s ([health probes](https://learn.microsoft.com/azure/container-apps/health-probes)).
- **Jobs:** each service image accepts `--job <name>` to run one job and exit (`IJob` in BuildingBlocks). In stage 4 the only job is the **broker metrics job** (6.2), in the Reporting image. Business jobs (reservation expiry, mart refresh, audit purge, ML training) come with their stages and use the same entry point.
- **Migrations:** never at start-up (ARCHITECTURE_DECISIONS §5). In Azure they run as a job before the revision (8.4).

---

## 4. Contracts queries over HTTP

The only Contracts query today is `Identity.Contracts.ICredentialVerifier`. Stage 4 adds its HTTP implementation in `SmartSpare.Modules.Identity.Contracts`, as ADR 0015 §4 places it:
- `POST /internal/v1/identity/credential-checks` and `POST /internal/v1/identity/session-checks` on the Identity service, policy `GatewayOnly`;
- the client sends the **gateway's own service token** (`sub=gateway`, `ss:svc=gateway`), uses the standard resilience handler, and propagates W3C trace context (HttpClient instrumentation);
- failure mapping: transport failure → 503 to the browser with the trace id; never "wrong password" for an infrastructure error;
- the uniform failure semantics of `VerifyAsync` stay exactly as in stage 3; the single-host integration tests also run against the HTTP client with a fake handler.

Later Contracts queries (stages 5–9) follow the same pattern; `/internal` endpoints are excluded from OpenAPI (section 7).

---

## 5. RabbitMQ transport

### 5.1 Seam

ADR 0017 names two interfaces. The code already has the publishing half: `IIntegrationEventPublisher.PublishAsync(OutboxEnvelope)`, called by the outbox dispatcher. Its in-process implementation still has a comment about the former Azure Service Bus plan, which 4a corrects. Stage 4:
- keeps `IIntegrationEventPublisher` as ADR 0017's `IIntegrationEventTransport`, named for what it already is;
- adds `IIntegrationEventSubscriber` (start and stop the module's consumers).

The dispatcher, `EfOutboxStore`, inbox and handler registration don't change.

**Stable consumer names (#32):**
- Today the inbox `consumer` is the handler's .NET type name.
- Queue names (`<consumer name>.<EventName>.v<N>`, [ADR 0021](../adr/0021-queue-per-consumer-name.md)) and inbox names must be stable across refactorings, so consumers declare an explicit name.
- An architecture test requires one per `IIntegrationEventConsumer<T>`.

### 5.2 Publisher and consumer

`RabbitMQ.Client` **7.3.0** (23 Nov 2026, Apache-2.0 OR MPL-2.0, async API; [NuGet](https://www.nuget.org/packages/RabbitMQ.Client)).

- **One connection per service replica**, two channels:
  - publishing, with publisher confirms enabled through `CreateChannelOptions`;
  - consuming, with manual acknowledgements and prefetch 10.
- **Publish** (ADR 0017 §3):
  - `mandatory=true`; `message_id` = outbox id, persistent, `type`, `traceparent` header;
  - `processed_at` is set only after the broker confirms;
  - a nack, a return or a timeout (5 s) counts as a failed attempt in the outbox.
  - Back-off and poison handling of failed attempts are #31, in PR 4c.
- **Consume:** handler and inbox insert in the module's transaction; ack after commit; an inbox hit is acked without effect.
- **Declarations at start-up**, idempotent:
  - the publisher's topic exchange `smartspare.<module>.events`;
  - the consumer's durable queues and bindings;
  - the module's dead-letter exchange and queue.
- **Tracing:**
  - Publish and consume spans are created by our transport with an `ActivitySource` (`SmartSpare.Messaging`), and `traceparent` is propagated in headers.
  - The official `RabbitMQ.Client.OpenTelemetry` package exists only as a prerelease (1.0.0-rc.3, [NuGet](https://www.nuget.org/packages/RabbitMQ.Client.OpenTelemetry)), and we don't take prereleases into production builds. Moving to it once it's stable is a new issue (section 13).

### 5.3 Topology and budgets (ADR 0017 §2, §5)

| Resource | Budget | Enforced by |
|---|---|---|
| AMQP connections | 1 per service replica → 6 at `maxReplicas: 1`; the gateway and the KEDA scaler open none (5.6); limit 20 | configuration test + broker metrics alert at 15 |
| Channels | 2 per connection; shared plans allow 200 per connection ([FAQ](https://www.cloudamqp.com/docs/faq.html)) | — |
| Queues | ≤ 45 subscription queues + 6 dead-letter queues; limit 100 | unit test counts declared queues across all modules |
| Queued messages | alert > 5,000; limit 10,000 | broker metrics job |
| Messages per month | alert at 70 %; limit 1 M, reset on the 1st | broker metrics job; CloudAMQP also mails at 70 % and 100 % ([FAQ](https://www.cloudamqp.com/docs/faq.html)) |

In stage 4 no module has a real event yet. The transport is proven with a **probe event**: Identity publishes it from a diagnostics endpoint, and a diagnostics consumer in Inventory receives it (6.5). Reporting, named here first, has no outbox or inbox, so the product owner chose Identity → Inventory (25 November 2026). Both are removed when the first pair of business events exists, at the latest with the stub endpoints before the defence (#55). The probe uses 1 queue.

### 5.4 CloudAMQP Little Lemur: what is documented, and what PR 4c verifies

Documented on the [plans page](https://www.cloudamqp.com/plans.html) and in the [FAQ](https://www.cloudamqp.com/docs/faq.html), 25 Nov 2026:
- **Limits:** 1 M messages a month, 20 connections, 100 queues, 10,000 queued messages, 28 days maximum idle queue time; free.
- **Inactive instances:** free instances "that have not published or consumed a single message during the latest two months" are deleted after a warning e-mail.
- **When the connection limit is reached,** new connections fail and existing ones continue.
- **Message quota:** a 2015 CloudAMQP post says the instance "will be blocked" when the quota is crossed ([blog](https://www.cloudamqp.com/blog/added-limits-for-shared-plans-lemur-tiger.html)).
- **Users and vhosts:** "only possible … on dedicated plans". AMQPS on port 5671.

**Not documented for shared plans**, so each is checked on a real instance before the transport relies on it:
- the RabbitMQ version ([shared vs dedicated](https://www.cloudamqp.com/blog/shared-vs-dedicated-plan.html): "every tenant uses the same broker version");
- quorum queues;
- user-defined policies (dead-lettering and native delayed retry, which needs 4.3+; [quorum queues](https://www.rabbitmq.com/docs/quorum-queues));
- the management HTTP API (`/api/queues`) with the instance's credentials.

PR 4c runs a small verification console against a new Little Lemur instance and records the results in `docs/operations/BROKER.md`.

Outcomes:
- **Quorum queues and policies available:** ADR 0017 §2–§3 as written.
- **Not available:** classic durable queues, with TTL + dead-letter retry declared through queue arguments, behind one option (ADR 0017 §5). The code is the same.
- **Management HTTP API not available:** see 5.6.

### 5.5 Environments

| Environment | Broker |
|---|---|
| Single host, unit and integration tests | none (in-process transport) |
| Local `--topology services` | `AddRabbitMQ("messaging").WithManagementPlugin().WithDataVolume()`, image `rabbitmq:4.4-management` ([Aspire RabbitMQ](https://aspire.dev/integrations/messaging/rabbitmq/rabbitmq-host/)); RabbitMQ 4.4.1 is current ([releases](https://www.rabbitmq.com/release-information)) |
| CI | `Testcontainers.RabbitMq` 4.17.0: transport tests and the topology test |
| Azure | CloudAMQP Little Lemur, AMQPS, URL in Key Vault |

### 5.6 KEDA queue scaling without AMQP connections

- **What is documented:**
  - Container Apps accepts "a custom Container Apps scaling rule based on any ScaledObject-based KEDA scaler". Secrets go through `auth` → `triggerParameter`. Defaults: polling 30 s, cool-down 300 s, and scale to zero once the queue is empty ([scale rules](https://learn.microsoft.com/azure/container-apps/scale-app)).
  - Non-HTTP rules need `activeRevisionsMode: single` (same page).
  - The KEDA `rabbitmq` scaler supports `protocol: http`; `host` is then the management URL with the vhost ([KEDA rabbitmq scaler](https://keda.sh/docs/2.21/scalers/rabbitmq-queue/)).
- **From the source, not the docs:** in http mode KEDA calls `GET {host}/api/queues/{vhost}/{queue}` and opens an AMQP connection only for `protocol == amqp` ([scaler source](https://github.com/kedacore/keda/blob/main/pkg/scalers/rabbitmq_scaler.go)).
- **Unverified:** that an `https://` management host works, and which KEDA version Container Apps runs. Both are checked with the first deployment (stage 10, #85), by watching the connection count while a consumer scales from zero.
- **Rule per consuming service:**
  - `type: rabbitmq`, `protocol: http`, `mode: QueueLength`, `value: 1`, `activationValue: 0`, `useRegex: true` over the service's queue prefix;
  - the management URL is in a Key Vault secret.
- **If the management API isn't available on Little Lemur** (5.4), queue scaling would need AMQP, at one connection per poll target. Two options:
  1. **Recommended:** no queue rule. Consumers wake through HTTP: the `warm-up` call, or a user request, plus a 5-minute cron job `wake-consumers`, which costs replica time but no connections.
  2. AMQP mode, with the connection budget recalculated against 20.

  The plan takes option 1 in that case and records it in BROKER.md.

---

## 6. Observability

### 6.1 Production: Grafana Cloud Free (ADR 0018)

**Plan limits** (["10k active series", "50 GB" logs and traces, "14 days retention", "3 active users"](https://grafana.com/pricing/)).

**Export:** directly from each app's OpenTelemetry SDK. The endpoint, protocol and headers come from the stack's OpenTelemetry tile ([send OTLP](https://grafana.com/docs/grafana-cloud/send-data/otlp/send-data-otlp/)): `OTEL_EXPORTER_OTLP_ENDPOINT=https://otlp-gateway-<region>.grafana.net/otlp`, `OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf`, and `OTEL_EXPORTER_OTLP_HEADERS` from Key Vault.

**Known trade-off:** Grafana calls Alloy "the recommended architecture for production observability" and lists direct export with "limited reliability to handle transport issues". ADR 0018 §1 accepted that for a scale-to-zero demo environment. A collector in front stays a later option (section 13).

**Resource attributes:** `service.name`, `service.version` (SHA), `deployment.environment.name`.

**New instrumentation:**
- the Npgsql source (done in 3e);
- the `SmartSpare.Messaging` source (5.2);
- custom meters: outbox pending and oldest age, inbox duplicates, published messages.

**Cardinality views and the attribute allow-list processor** per ADR 0018 §1 and §5.

### 6.2 Broker metrics job

A Container Apps cron job (every 15 minutes, in the Reporting image, `--job broker-metrics`; cron is five-field and in UTC, [jobs](https://learn.microsoft.com/azure/container-apps/jobs)):
- reads the management API (queue depths, dead-letter counts, connections, monthly published count);
- exports gauges over OTLP;
- exits.

If the management API isn't available (5.4), the job reports only what the AMQP client can see (queue depth through passive declare), and the monthly quota relies on CloudAMQP's own e-mails.

Cost: 96 runs a day × about 30 s, roughly 24 replica-hours a month, about 12 % of the free grant (8.6).

### 6.3 Local: `--topology services`

- An OpenTelemetry Collector (contrib 0.166.0) via `CommunityToolkit.Aspire.Hosting.OpenTelemetryCollector` 17.0.0 ([Aspire collector](https://aspire.dev/integrations/observability/opentelemetry-collector/opentelemetry-collector-host/)). It forwards to:
  - **Prometheus 3.16.0:** `--web.enable-otlp-receiver`, path `/api/v1/otlp/v1/metrics`, `promote_resource_attributes` ([Prometheus OTLP](https://prometheus.io/docs/guides/opentelemetry/));
  - **Loki 3.7.10:** `/otlp` with `allow_structured_metadata: true` ([Loki OTLP](https://grafana.com/docs/loki/latest/send-data/otel/));
  - **Tempo 3.1.1:** receivers bound to `0.0.0.0:4317/4318`; metrics generator ([Tempo configuration](https://grafana.com/docs/tempo/latest/configuration/));
  - **Grafana 13.3.2** with file-provisioned data sources, dashboards and alert rules from `infra/observability/`.
- Prometheus, Loki, Tempo and Grafana run as Aspire containers (`AddContainer`), the pattern of Aspire's metrics sample ([sample](https://aspire.dev/reference/samples/metrics/)).
- The Aspire dashboard keeps receiving telemetry too. Whether the collector integration still forwards to it is unverified; PR 4g checks it and falls back to a collector exporter to the dashboard.
- `grafana/otel-lgtm` (Apache-2.0, "intended for development, demo, and testing") was considered. It is rejected because it doesn't show the separate components ADR 0018 asks to run locally ([repo](https://github.com/grafana/docker-otel-lgtm)).

### 6.4 Dashboards and alert rules as code

`infra/observability/`:
- `dashboards/`: ASP.NET Core 19924 and 19925, the SmartSpare overview, messaging, free-tier usage;
- `alerts/`: the rule set of ADR 0018 §4;
- `datasources/`: local only.

They are provisioned locally as files, and to Grafana Cloud with the Grafana Terraform provider (`grafana_rule_group`, contact points, dashboards; [Terraform provisioning](https://grafana.com/docs/grafana/latest/alerting/set-up/provision-alerting-resources/terraform-provisioning/)), run from the infra workflow.

**Ruler limits:** 35 groups, 20 rules per group ([usage limits](https://grafana.com/docs/grafana-cloud/cost-management-and-billing/understand-your-invoice/usage-limits/)). The plan uses 8 rules in 3 groups.

**Contact points:** e-mail and Telegram are both listed for Grafana Cloud ([contact points](https://grafana.com/docs/grafana-cloud/alerting-and-irm/alerting/configure-notifications/manage-contact-points/)); the docs say nothing about free-tier restrictions. PR 4j creates both on the free stack. If Telegram isn't offered, e-mail alone satisfies the rubric (ADR 0018 §4), and the result is recorded.

### 6.5 One trace across gateway → service → broker → service

The acceptance trace, demonstrated locally (Tempo) and in Azure (Grafana Cloud):
1. the browser calls `POST /api/identity/diagnostics/probe`;
2. the gateway span → the Identity service span → outbox insert (Npgsql span);
3. the dispatcher publish span (`traceparent` in headers) → the consume span in the **Inventory service's** diagnostics consumer (a second service) → its inbox insert.

The publish happens later, in the dispatcher's own activity. That activity **links** to the request's trace, and the consume span is a child of the publish span. So Tempo shows one trace from publish to consume, linked to the request trace. The outbox stores the request's `traceparent` for this (one new nullable column per outbox table, through each module's migration).

---

## 7. API documentation (#54, ADR 0019)

- **Per service:** `AddOpenApi("{module}")` (`ApiDocumentation.AddModuleDocuments`), served at `/openapi/{module}.json` as in the single host, with a document transformer that sets the title and version and clears the servers, and `ShouldInclude` that keeps only the module's `/api/v1/{module}/` business paths, so `/internal/*` and operational endpoints are left out. .NET 10 produces OpenAPI **3.1** ([OpenAPI in ASP.NET Core](https://learn.microsoft.com/aspnet/core/fundamentals/openapi/aspnetcore-openapi?view=aspnetcore-10.0)).
- **Single host:** one document per module.
- **XML documentation comments:** `GenerateDocumentationFile=true` in module projects. Handlers must be methods, not lambdas, for comments to be captured ([OpenAPI comments](https://learn.microsoft.com/aspnet/core/fundamentals/openapi/openapi-comments)). CS1591 (missing comment) stays off for internal types; public module types are only installers and Contracts.
- **Gateway:**
  - proxies `/openapi/{module}.json` (2.1), with OutputCache for 10 minutes;
  - serves **Scalar** (`Scalar.AspNetCore` 3.2.0, MIT; [NuGet](https://www.nuget.org/packages/Scalar.AspNetCore)) at `/scalar`, with one document per module (`AddDocuments`).
- **Scalar and the CSP:**
  - Scalar's assets are local by default, but fonts come from a CDN unless `DisableDefaultFonts()` is set ([Scalar ASP.NET Core](https://scalar.com/scalar/scalar-api-references/integrations/net-aspnet-core/integration)).
  - Its page has an inline `<script type="module">` that supports a nonce, `WithNonce()` ([changelog](https://github.com/scalar/scalar/blob/main/integrations/dotnet/aspnetcore/CHANGELOG.md)).
  - **Decision:** `/scalar` gets `script-src 'self' 'nonce-<per request>'` and `DisableDefaultFonts()`. Every other page keeps the stage 3 policy unchanged.
  - Whether Scalar needs inline styles is undocumented. `style-src 'self' 'unsafe-inline'` already applies site-wide (ADR 0014), so nothing changes there.
  - An integration test asserts the nonce in both header and markup, and the absence of `'unsafe-inline'` in `script-src`.
  - **Implemented (PR 4f):** Scalar also has `DisableTelemetry()`, `DisableAgent()`, `DisableMcp()` and `HideDeveloperTools()`, so the public page sends nothing to Scalar's services. A Playwright test renders `/scalar` and fails on any CSP violation or any request to another origin.
- **Content in stage 4:** every endpoint that exists is operational (pings, the diagnostics probe), which ADR 0019 §3 leaves out, so the six documents are valid but have no paths until stage 5 adds `/api/v1` endpoints. The existing endpoints have summaries and typed responses anyway.
- **Tests:** a document contains no `/internal` path and no non-`/api/v1` business path (ADR 0019 §3). `/scalar` and `/openapi/*` are anonymous; every documented operation requires authorization.

---

## 8. Delivery

### 8.1 Images

- SDK container publishing: `dotnet publish -t:PublishContainer` with `ContainerRepository=smartspare/<service>`, `ContainerImageTags=<sha>`, `ContainerFamily=noble-chiseled-extra` ([container publish configuration](https://learn.microsoft.com/dotnet/core/containers/publish-configuration)).
- .NET 10 images are Ubuntu 24.04-based. The chiseled images lack ICU and tzdata, hence the `-extra` variant: resx localization, server-side prerendering and #47 need ICU ([.NET 10 image change](https://learn.microsoft.com/dotnet/core/compatibility/containers/10.0/default-images-use-ubuntu)).
- The images run as the non-root `app` user by default.
- Seven app images plus the migration runner image, pushed to ACR Basic with OIDC sign-in.

### 8.2 Quality gates in every pipeline (#63, #64)

**Coverage (#63).**
- coverlet.collector can't enforce a threshold ("threshold validation … not provided by this integration mode", [coverlet VSTest](https://github.com/coverlet-coverage/coverlet/blob/master/Documentation/VSTestIntegration.md)).
- Instead, `tools/check.sh coverage` runs **ReportGenerator 5.6.1** (a local .NET tool, `dotnet-tools.json`) over `TestResults/**/coverage.cobertura.xml`, filtered to business logic as #63 defines it: module `Domain/` and `Features/`, SharedKernel and the handler pipeline, without migrations, EF configurations, generated code or the host's `Program`. It sets `minimumCoverageThresholds:lineCoverage=70`, and ReportGenerator "will exit unsuccessfully" below that ([settings](https://github.com/danielpalme/ReportGenerator/wiki/Settings)).
- The HTML summary is uploaded as an artefact.
- No test-platform change is needed: the repo runs xunit.v3 in VSTest mode.
- **Measured in 4b:** 89.7 % (315 of 351 lines, 25 November 2026).
- The threshold is raised to 80 % by stage 9 (#63).

**Vulnerable packages (#64).**
- NuGet Audit already fails restore on any known vulnerability: for `net10.0`, `NuGetAuditMode=all`, and `TreatWarningsAsErrors` turns NU1901–NU1904 into errors ([auditing packages](https://learn.microsoft.com/nuget/concepts/auditing-packages)).
- In addition, `tools/check.sh vulnerabilities` runs `dotnet package list --include-transitive --vulnerable --format json` ([dotnet package list](https://learn.microsoft.com/dotnet/core/tools/dotnet-package-list)). It fails if any package lists a vulnerability, because the command's exit code on findings is undocumented. It prints a table.
- A weekly scheduled run catches new advisories between pushes. Scheduled workflows run only on the default branch, at most every 5 minutes, and are disabled in public repositories after 60 days without activity ([events](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows)).

Both steps are part of the shared gate workflow that every service pipeline calls (8.3), so no image is pushed without them.

### 8.3 Pipelines and path filters

- **`ci.yml`** stays the gate for every pull request (`tools/check.sh` steps). It adds the `coverage` and `vulnerabilities` steps and the topology test job.
- **`deploy-<service>.yml` × 7** plus `deploy-migrations.yml` (only under decision A, section 9):
  - **Triggers:** `push` to `main` with `paths` for the service's host, its module, its Contracts, BuildingBlocks, `Directory.*.props` and `db/sql/<module>`. A Contracts change also lists the consuming services.
  - Workflow-level `paths` are the documented mechanism. `paths` and `paths-ignore` can't be combined for one event, and diffs above 3,000 files may be missed ([workflow syntax](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax)).
  - **Steps:**
    1. run the shared gates (build, tests, coverage, vulnerabilities) from a composite action, `.github/actions/gates`. A reusable workflow would rename the "Build and test" check to "gates / Build and test", and the `main` ruleset requires the check names "Build and test" and "Integration tests" ([reusable workflows](https://docs.github.com/en/actions/reference/workflows-and-actions/reusing-workflow-configurations));
    2. `azure/login@v2` with OIDC (`id-token: write`, federated credential; [OIDC](https://learn.microsoft.com/azure/developer/github/connect-from-azure-openid-connect));
    3. publish the image;
    4. run the migration job for the module (8.4) and wait for success;
    5. deploy the revision with `azure/container-apps-deploy-action@v1` or `az containerapp update --image` ([deploy from GitHub Actions](https://learn.microsoft.com/azure/container-apps/github-actions));
    6. smoke-test through the gateway.
- A path-filter test in CI checks that every project under `src/` is covered by at least one deploy workflow's filters, so nothing silently never deploys.

### 8.4 Migrations in Azure: MigrationRunner job vs. seven EF bundles

| | Seven EF migration bundles | MigrationRunner image as a Container Apps job (**chosen**) |
|---|---|---|
| What runs | one self-contained bundle per DbContext. EF Core 10 needs `--context` per bundle; `--context "*"` arrives in EF Core 11 ([EF CLI](https://learn.microsoft.com/ef/core/cli/dotnet#common-options), [EF 11](https://learn.microsoft.com/ef/core/what-is-new/ef-core-11.0/whatsnew#migrations)) | one image of `SmartSpare.MigrationRunner`, the program that already builds every local, AppHost and CI database |
| Cluster bootstrap (roles, database, grants) | not covered; a separate script step | `migrate` already runs `db/bootstrap/000_bootstrap_cluster.sql` once |
| Order (Platform first, then the ModuleCatalog order) | the pipeline must encode it | built in (`ModuleCatalog`) |
| Tested today | no | yes: AppHost, `verify_database.sh` (103 SQL assertions on the runner-built database), E2E fixture |
| Per-service independence | natural (one bundle per service) | new `--module <name>` filter: applies Platform (if pending) and that module only. Fails if an earlier module in the catalogue has pending migrations, so order can't be broken |
| Where it runs | the pipeline runner (needs network access to PostgreSQL) or a job | a Container Apps **manual job** inside the environment, started by the pipeline, which waits for its result. The database needs no firewall rule for GitHub runners |
| Microsoft guidance | "use a migration bundle … run it as a one-shot deployment job" with a separate identity ([applying migrations](https://learn.microsoft.com/ef/core/managing-schemas/migrations/applying#bundles)) | same principles: one-shot job, separate identity (`smartspare_migrator`), never at app start-up. Only the artefact differs |
| Artefacts per release | 7 bundles (~70 MB each, self-contained) | 1 image |

**Choice:** the MigrationRunner job, as the product owner leans to.
- It reuses the tested path, including the bootstrap and the order, and keeps per-service independence with the `--module` filter.
- `check-model` stays a CI gate.
- [ADR 0020](../adr/0020-database-migrations-through-the-migration-runner-job.md) records this. It amends ADR 0009 ("EF migration bundle as a pipeline step") and ADR 0015 §5 ("its own module's EF migration bundle").

**Unverified:** whether `az containerapp job start` can override the container arguments per execution (`--args`). If it can't, there is one manual job per module (`migrate-<module>`) with fixed arguments. That costs nothing while idle.

### 8.5 Container Apps sizing and connections

| App | CPU / memory | Replicas | Scale rules | DB connections (max) |
|---|---|---|---|---|
| gateway | 0.25 / 0.5 GiB | 0–1 | HTTP | 0 |
| identity, inventory, fleet, procurement, forecasting, reporting | 0.25 / 0.5 GiB each | 0–1 | HTTP; plus `rabbitmq` for consumers (5.6) | 4 each → 24 |
| job `migrations` | 0.5 / 1 GiB | one execution | manual | 2 |
| job `broker-metrics` | 0.25 / 0.5 GiB | one execution | cron `*/15 * * * *` | 0 |
| **Total** | | | | **≤ 26** at once, plus up to 2 for operator sessions (psql) = 28 of **35** |

- 0.25 vCPU / 0.5 GiB is the smallest size ([containers](https://learn.microsoft.com/azure/container-apps/containers)).
- B1ms has 50 connections, 15 of them reserved, which leaves 35 for users. "The reserved connection slots, presently at 15, can change" ([limits](https://learn.microsoft.com/azure/postgresql/configure-maintain/concepts-limits)).
- `maxReplicas: 1` is required by the budget, and single revision mode by the KEDA rule (5.6).
- Logs destination: **none**, as there is no Log Analytics workspace (`--logs-destination none`, [log options](https://learn.microsoft.com/azure/container-apps/log-options)).

### 8.6 Free grant and replica time

The free grant is "180,000 vCPU-seconds, 360,000 GiB-seconds, 2 million HTTP requests" per subscription per month ([billing](https://learn.microsoft.com/azure/container-apps/billing)). With `minReplicas: 0`, running replicas are billed at the active rate, and a revision at zero costs nothing.

At 0.25 vCPU / 0.5 GiB the grant is **200 replica-hours a month**. Above it:
- active vCPU is $0.000034 per second;
- active memory is $0.000004 per GiB-second;
- that is about $0.038 per replica-hour (USD Retail Prices API, West Europe).

Usage:
- A 2-hour session with all seven apps awake (plus 5-minute cool-downs) uses about 15 replica-hours.
- The broker metrics job uses about 24 replica-hours a month.
- So about 10 full sessions a month fit within the grant.

Only requests from outside the environment count as billable requests.

### 8.7 Cold start and warm-up

- **Documented factors** ([cold start](https://learn.microsoft.com/azure/container-apps/cold-start)): image pull, provisioning, app start-up. Mitigations: small images, a registry in the same region, probes that tolerate start-up, client retries, proactive wake-up.
- **Target, measured with the first deployment (stage 10, #85):** the first sign-in after all apps are at zero completes in **≤ 30 s**: gateway start, then Identity start, then the credential check. This is an acceptance target, not an NFR. NFR07 in this repository is the €35/month cost limit, so the cold-start target isn't an NFR number.
- **Measurement:**
  - `tools/measure-cold-start.sh` runs from a GitHub workflow after one hour idle;
  - it times `GET /warmup` (per-service timings) and one scripted sign-in;
  - 5 runs, median and maximum recorded in `docs/operations/COLD_START.md`.

  If the median exceeds 30 s, the plan's fallback is `minReplicas: 1` for the gateway only during demo windows (a scheduled scale rule change), whose cost is recorded.
- **`warm-up.yml`** (ADR 0009 amendment):
  - `workflow_dispatch` plus a weekly cron;
  - it calls `/warmup`;
  - under decision A it also starts the PostgreSQL server first (section 9);
  - the weekly run keeps CloudAMQP from deleting the instance for inactivity.
- **UI:** the web client shows a "starting up" state while `/bff/me` waits on a cold gateway.

### 8.8 Infrastructure as code

`infra/` in Bicep, run by `infra.yml` (manual; what-if on pull requests):
- resource group;
- Container Apps environment (workload profiles, Consumption);
- ACR Basic;
- PostgreSQL Flexible Server 17 B1ms with 32 GiB storage (17 is GA, [release notes](https://learn.microsoft.com/azure/postgresql/release-notes/release-notes));
- Key Vault;
- Storage account (gateway key ring);
- user-assigned Managed Identities per app;
- the apps and jobs with Key Vault secret references.

CloudAMQP and Grafana Cloud are created manually, with their secrets placed in Key Vault. Their APIs are outside Bicep.

---

## 9. Decision: when to deploy to Azure

Until this decision, ARCHITECTURE_DECISIONS §13 and #67 put **the first deployment of the walking skeleton into stage 4**, which is (A). Stage 10 is "Azure production" (production environment, Entra logins, per-module roles); (B) moves all Azure work there. **The product owner chose (B) on 25 November 2026.** The comparison below is kept as the basis of that decision.

### 9.1 Costs

**Prices:** USD Retail Prices API, West Europe, 25 Nov 2026:
- PostgreSQL B1MS: $0.0199/h;
- storage: $0.1369/GB-month;
- ACR Basic: $0.1666/day;
- Container Apps: within the free grant (8.6).

**Fixed baseline, always on:** 730 h × $0.0199 + 32 × $0.1369 + 30.4 × $0.1666 ≈ **$24 a month**, plus less than $1 for backup, Key Vault and Blob.

**Stopping PostgreSQL:** stopping the server stops compute billing immediately; storage is still billed. The server "remains stopped for seven days unless you restart it sooner" ([overview](https://learn.microsoft.com/azure/postgresql/overview#stop-and-start-server-to-lower-tco)), so a scheduled stop is needed to keep it stopped.

**Subscription:** a pay-as-you-go subscription (product owner, 25 November 2026; not Azure for Students). There is no credit that can run out, so the limit is **NFR07: ≤ €35 a month**. If the subscription is still within the first 12 months of an Azure free account, 750 hours of B1MS and 32 GB of storage a month are free ([free account](https://learn.microsoft.com/azure/postgresql/configure-maintain/how-to-deploy-on-azure-free-account)), which removes most of the baseline. The table doesn't assume that.

**Defence: 16 February 2027.** The totals assume the environment exists from **4 December 2026 (end of stage 4; 74 days ≈ 2.4 months)** under (A), and from **4 January 2027 (start of stage 10; 43 days ≈ 1.4 months)** under (B).

| Scenario | Per month | Total until the defence | Within NFR07 (≤ €35/month)? |
|---|---|---|---|
| A1: stage 4, PostgreSQL always on | ≈ $24.5 (≈ €22–23, the ADR 0009 amendment's figure) | ≈ $60 | Yes |
| A2: stage 4, PostgreSQL stopped outside sessions (≈ 40 h a month running) | ≈ $4.4 storage + $5.1 ACR + $0.8 compute + $0.5 other ≈ $10.8 | ≈ $26 | Yes |
| **B1: stage 10, always on (chosen)** | ≈ $24.5 | ≈ $35 | **Yes** |
| B2: stage 10, stopped outside sessions | ≈ $10.8 | ≈ $15 | Yes |

Every scenario meets NFR07.

### 9.2 Risks

| | (A) Deploy in stage 4 | (B) Deploy in stage 10 |
|---|---|---|
| **Integration risk** | Found early, while the topology is fresh: Little Lemur features (5.4), KEDA http mode (5.6), forwarded headers (2.5), Managed Identity and Key Vault references, OIDC, cold starts (8.7). Stages 5–9 are deployed through the pipeline as they land (ARCHITECTURE_DECISIONS §13) | All of those surface in stage 10, the last stage before the defence polish. A blocker there (e.g. no management API, cold start > 30 s) leaves little time. Pipelines are written in stage 4 but untested against Azure for months |
| **Cost** | ≈ $60 until the defence always on, ≈ $26 with the database stopped outside sessions; within NFR07 either way | ≈ $15–35; lowest |
| **Operational load** | Secrets, rotations and the environment must be kept alive for two months, plus the weekly warm-up | None until stage 10 |
| **Rubric** | "Production deployment reachable by external users", "CD" and "deploy only when green" can be shown from v0.4.0 on, and every later stage proves them again | Shown only from stage 10; until then only locally and in CI |
| **Free plans** | CloudAMQP and Grafana Cloud run for about three months: inactivity rules (2-month instance deletion, 28-day idle queues) must be handled by the weekly warm-up; 14-day telemetry retention means old traces disappear (irrelevant for the demo) | Shorter exposure to free-plan rules |

### 9.3 Decision and its consequences

The plan recommended (A1): the topology's risks are cloud-specific, and the always-on cost (≈ €22–23 a month) is within NFR07. **The product owner chose (B).** Consequences for this plan:

- **Unchanged in stage 4:** sections 1–8 are built and tested locally (AppHost `--topology services`) and in the CI topology job.
- **Cloud services without Azure:** the CloudAMQP Little Lemur checks (5.4) run in 4c against a real instance from a developer machine. Grafana Cloud dashboards, alert rules and the test alert (6.4) are provisioned in 4j and fed by the local services topology. So two of the cloud risks are retired in stage 4 without Azure.
- **Infrastructure and pipelines are written in stage 4 and validated, not applied** (4h):
  - Bicep with `what-if` against the pay-as-you-go subscription, which creates nothing;
  - images built in CI;
  - deploy workflows present, but only on manual dispatch until stage 10.
- **Moved to stage 10, issue #85** (the former PR 4i):
  - applying the infrastructure;
  - the first deployment of all services and the MigrationRunner job;
  - KEDA scale rules (5.6) and their AMQP-connection check;
  - forwarded headers behind the real ingress (2.5);
  - the cold-start measurement (8.7);
  - `warm-up.yml` and the broker-metrics cron job;
  - the €35 budget alert and the disabled `db-schedule.yml`.
- **Accepted risk:** KEDA http mode, the real ingress and cold starts are first seen in stage 10. Stage 10 runs 4–15 January 2027, a month before the defence on 16 February, so that a blocker still has a fallback: the AMQP scaler or HTTP wake-up for KEDA, `minReplicas: 1` for the gateway during demos.

---

## 10. Test plan

| Level | New tests |
|---|---|
| Architecture | Modules free of YARP, RabbitMQ.Client, Bff and hosts. Gateway references no module implementation. One module per service host. Every consumer declares a stable name (#32). Queue budget (≤ 45 subscriptions) |
| Unit | Transport: message properties, confirm/nack/return → outbox outcome, consumer ack after commit, duplicate ack. Route builder: one `api-*` and `openapi-*` route per module, none matching `/internal`. Forwarded-header options per environment. Token cache expiry. MigrationRunner `--module` ordering rule |
| Integration (Testcontainers) | RabbitMQ transport against `rabbitmq:4.4` (publish with confirms, return on unroutable, redelivery idempotence, dead-letter after the limit). `ICredentialVerifier` HTTP client against the Identity service host (uniform failures, gateway-only policy). Gateway route-table test (2.3). OpenAPI documents (no `/internal`, only `/api/v1`), Scalar CSP nonce |
| Topology (new CI job, `tools/check.sh topology`) | Gateway + Identity + Inventory service hosts + PostgreSQL + RabbitMQ in Testcontainers. Sign in through the gateway; call an Identity and an Inventory endpoint; probe event Identity → Inventory over RabbitMQ; one trace with gateway, service, publish and consume spans (in-memory exporter); `/internal` and service health not reachable through the gateway |
| Database | Bootstrap creates the six service logins with their capability roles; `verify_database.sh` unchanged otherwise |
| E2E | The stage 3 smoke test, plus the same scenario against the services topology in the topology job |
| Azure (stage 10, #85; manual + scripted) | Smoke through the gateway after each deploy; forwarded-header check; KEDA scale-from-zero on a probe message with no extra AMQP connection; cold-start measurement; an alert fires to e-mail (and Telegram if available) |

---

## 11. Pull requests

Each PR passes the full `tools/check.sh`, including the new steps once they exist, and is merged with a rebase. Closing keywords appear only in PR descriptions.

### 4a: Composition seams and platform upgrade
**Scope:**
- Aspire 17.0.0; Testcontainers 4.17.0;
- `ModuleComposition` and `Topology` in BuildingBlocks; the single host moved onto them (no behaviour change);
- the new architecture tests (1.3);
- stable consumer names and their test (#32);
- the stale Service Bus comment fixed.

**Acceptance:** all existing tests green unchanged; new architecture tests fail when a module references YARP or RabbitMQ.Client (mutation check). **Closes #32.**

### 4b: Quality gates
**Scope:** `tools/check.sh coverage` (ReportGenerator, 70 % on business logic) and `vulnerabilities` (JSON parse) in `ci.yml`; a weekly scheduled vulnerability run; tests added where coverage is short. The shared composite action for the deploy workflows comes with them in 4h.

**Acceptance:**
- the gate fails at 69 % (temporarily raised threshold in a mutation check) and passes on `main`;
- the vulnerability step fails on a known-vulnerable package version in a scratch branch;
- the coverage figure is recorded in BACKEND_REQUIREMENTS.

**Part of #63** (the 70 % gate; #63 stays open for its 80 % step by stage 9), **closes #64.**

### 4c: RabbitMQ transport
**Scope:** `SmartSpare.Infrastructure.RabbitMq` (5.2–5.3); the subscriber seam; the probe event; Testcontainers transport tests; the Little Lemur verification console and `docs/operations/BROKER.md` (5.4) with the chosen variant (quorum or classic); outbox back-off and poison handling (#31).

**Acceptance:**
- publish with confirms, return, redelivery and dead-letter tests green;
- the queue budget test;
- BROKER.md states every unverified item of 5.4 as verified or not, with the consequence;
- failed publishes back off and poison messages are parked with a metric (#31).

**Closes #31. Part of #57.**

### 4d: Service hosts and Contracts HTTP client
**Scope:**
- six `SmartSpare.Services.<Module>` hosts;
- JwtBearer-only authentication;
- the Identity `/internal/v1/identity/*` endpoints and `ICredentialVerifier` HTTP client (section 4);
- service logins in the bootstrap;
- `Maximum Pool Size=4`;
- `--job` entry point;
- the MigrationRunner `--module` filter (8.4).

**Acceptance:**
- each service starts with only its module;
- the HTTP verifier passes the stage 3 sign-in test matrix against a real Identity service host;
- `--module` refuses out-of-order runs;
- the bootstrap SQL assertion for the new logins.

**Part of #56.**

### 4e: Gateway
**Scope:**
- `SmartSpare.Gateway` (section 2): YARP routes from the catalogue, transforms, token cache, BFF, WebAssembly client, Data Protection per environment, forwarded headers (#48), `/warmup`;
- the route-table test.

**Acceptance:**
- sign-in through the gateway against the Identity service host;
- a module endpoint reached with a minted token;
- `/internal` and service `/health` return 404 at the gateway with nothing forwarded;
- forwarded-header unit tests per environment.

**Part of #56, part of #48** (configuration and unit tests here; the check behind the real ingress is in stage 10, #85).

### 4f: OpenAPI and Scalar
**Scope:** section 7.

**Acceptance:** document tests (ADR 0019 §3); `/scalar` with the nonce CSP and no CDN fonts; summaries and response types for the existing endpoints.

**Closes #54.**

### 4g: Local services topology, observability stack, topology test
**Scope:**
- AppHost `--topology services`: gateway, six services, RabbitMQ, collector, Prometheus, Loki, Tempo, Grafana;
- `infra/observability/` dashboards and alert rules (file-provisioned);
- the cross-service trace (6.5, outbox `traceparent` column through each module's migration);
- the topology CI job (section 10).

**Acceptance:**
- `dotnet run --project src/SmartSpare.AppHost -- --topology services` shows the dashboards with data and one trace gateway → Identity → RabbitMQ → Inventory in local Tempo;
- the same topology also runs against CloudAMQP, by configuration;
- the topology job green;
- the single host unchanged (`dotnet run` without the option).

**Closes #57. Part of #59.**

**Split in two PRs** (25 Nov 2026):
- **4g-1:** the outbox `trace_parent` column and the linked publish span (6.5), the topology test project `tests/SmartSpare.Topology.Tests` with `tools/check.sh topology` and the CI job "Topology test". Closes #57.
- **4g-2:** AppHost `--topology services`, the observability stack, dashboards and alert rules. Part of #59.

Found in 4g-1: RabbitMQ.Client 7 traces too. When its `RabbitMQ.Client.*` sources are listened to, its own publish span sits between SmartSpare's publish span and the consumer, and it writes the `traceparent` header. The consume span then descends from SmartSpare's publish span through it, so the topology test walks up the parents.

### 4h: Images, pipelines and infrastructure as code (validated, not applied)
**Scope:**
- SDK container publishing (8.1);
- `infra/` Bicep (8.8), including the KEDA rules, jobs, budget and `db-schedule.yml` for stage 10;
- the composite action `.github/actions/gates` with the gate steps, used by the "Build and test" job and every deploy workflow (8.3);
- `deploy-<service>.yml` × 7, `deploy-migrations.yml` and `infra.yml`, all on manual dispatch only until stage 10;
- the path-filter coverage test (8.3);
- ADR 0020's runner job definition.

**Acceptance:**
- images build in CI for every service;
- `infra.yml` what-if is clean against the subscription, and nothing is created;
- the path-filter test green.

**Part of #56** (it closes with the first deployment, #85).

### Moved to stage 10: first deployment (#85)
The former PR 4i: see section 9.3 and #85.

### 4j: Grafana Cloud dashboards and alerts
**Scope:**
- Terraform provisioning of dashboards, alert rules and contact points to the Grafana Cloud stack (6.4);
- the local services topology exporting OTLP to Grafana Cloud by configuration (6.1);
- the broker metrics job runnable on demand (`--job broker-metrics`) against CloudAMQP (6.2). Its cron schedule comes with #85;
- one trace across services in Grafana Cloud Tempo;
- TRACING.md stage 4 section completed.

**Acceptance:**
- a test alert reaches e-mail (and Telegram if the free stack offers it, recorded either way);
- dashboards show live data from the local services topology.

**Closes #59.**

### 4k: Documentation and release
**Scope:**
- ARCHITECTURE_DECISIONS (stage 4 implemented, ADRs 0015–0020 status);
- the solution diagram (gateway and services implemented);
- BACKEND_REQUIREMENTS rows;
- README (`--topology services`, CloudAMQP and Grafana Cloud configuration);
- STAGE_4_PLAN exit criteria with evidence;
- CHANGELOG v0.4.0.

**Acceptance:** stale-terms and docs generators green; tag `v0.4.0` after merge.

---

## 12. Exit criteria for v0.4.0

1. The single host and every existing test are unchanged and green; module code is identical in both topologies (architecture tests).
2. In the services topology (locally and in the topology CI job), a user signs in through the gateway and reaches module endpoints with an internal token. `/internal/*` is never routed (route-table test).
3. An event published by one service is consumed by another through RabbitMQ, with confirms, idempotent inbox and dead-lettering tested; the queue and connection budgets are enforced by tests.
4. One trace spans gateway → service → broker → service, in local Tempo and in Grafana Cloud (fed by the local services topology).
5. OpenAPI documents per module and the Scalar UI are public, with no `/internal` paths and a strict CSP (#54).
6. Every pipeline gates on coverage ≥ 70 % of business logic and on vulnerable packages (#63, #64).
7. A Grafana Cloud alert reaches e-mail (and Telegram where offered).
8. The Bicep templates pass what-if, every service image builds, and the per-service deploy workflows exist with the gates and the MigrationRunner job (ADR 0020), ready for stage 10.

Moved to stage 10 with the first deployment (#85):
- the seven apps on Azure Container Apps, independent deployment;
- forwarded headers behind the real ingress (#48);
- the measured cold start;
- spending within NFR07 with the budget alert.

---

## 13. Proposals and new issues

**Decided:** **#31** (outbox back-off and poison handling) moved to stage 4, PR 4c, because ADR 0017 §3 relies on it. **#30** (outbox and inbox retention) and **#17, #19** stay in stage 5.

**New issues for work deferred by this plan** (created with this PR):
- **#80**: Production telemetry through a collector (Alloy) instead of direct SDK export (ADR 0018 §1); `future`.
- **#81**: Per-service audiences for internal tokens (ADR 0016 consequences, hardening); `future`.
- **#82**: Adopt `RabbitMQ.Client.OpenTelemetry` once stable, replacing the transport's own spans; `tech-debt`, `stage-5`.
- **#83**: More than one replica per service: connection pooling (PgBouncer on Flexible Server) or a larger tier; `future`.
- **#85**: First Azure deployment of the services topology (decision B); `stage-10`.

---

## 14. References

Checked on 25 November 2026.

**Microsoft (Azure, .NET, ASP.NET Core 10)**
- Container Apps: [billing](https://learn.microsoft.com/azure/container-apps/billing), [scale rules](https://learn.microsoft.com/azure/container-apps/scale-app), [containers (sizes)](https://learn.microsoft.com/azure/container-apps/containers), [cold start](https://learn.microsoft.com/azure/container-apps/cold-start), [jobs](https://learn.microsoft.com/azure/container-apps/jobs), [connect apps](https://learn.microsoft.com/azure/container-apps/connect-apps), [health probes](https://learn.microsoft.com/azure/container-apps/health-probes), [log options](https://learn.microsoft.com/azure/container-apps/log-options), [ingress HTTP headers](https://learn.microsoft.com/azure/container-apps/ingress-overview#http-headers), [GitHub Actions](https://learn.microsoft.com/azure/container-apps/github-actions), [revisions](https://learn.microsoft.com/azure/container-apps/revisions-manage)
- PostgreSQL Flexible Server: [limits](https://learn.microsoft.com/azure/postgresql/configure-maintain/concepts-limits), [compute and storage](https://learn.microsoft.com/azure/postgresql/compute-storage/concepts-compute), [stop and start](https://learn.microsoft.com/azure/postgresql/overview#stop-and-start-server-to-lower-tco), [free account](https://learn.microsoft.com/azure/postgresql/configure-maintain/how-to-deploy-on-azure-free-account), [release notes](https://learn.microsoft.com/azure/postgresql/release-notes/release-notes)
- [Azure Retail Prices API](https://prices.azure.com/api/retail/prices); [OIDC from GitHub Actions](https://learn.microsoft.com/azure/developer/github/connect-from-azure-openid-connect)
- ASP.NET Core 10: [proxy servers and load balancers](https://learn.microsoft.com/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0), [KnownNetworks obsolete](https://learn.microsoft.com/aspnet/core/breaking-changes/10/ipnetwork-knownnetworks-obsolete?view=aspnetcore-10.0), [OpenAPI](https://learn.microsoft.com/aspnet/core/fundamentals/openapi/aspnetcore-openapi?view=aspnetcore-10.0), [OpenAPI comments](https://learn.microsoft.com/aspnet/core/fundamentals/openapi/openapi-comments)
- YARP: [config providers](https://learn.microsoft.com/aspnet/core/fundamentals/servers/yarp/config-providers), [extensibility transforms](https://learn.microsoft.com/aspnet/core/fundamentals/servers/yarp/extensibility-transforms), [header guidelines](https://learn.microsoft.com/aspnet/core/fundamentals/servers/yarp/header-guidelines), [IProxyStateLookup](https://learn.microsoft.com/dotnet/api/yarp.reverseproxy.iproxystatelookup?view=yarp-2.2)
- .NET: [container publish configuration](https://learn.microsoft.com/dotnet/core/containers/publish-configuration), [.NET 10 default images](https://learn.microsoft.com/dotnet/core/compatibility/containers/10.0/default-images-use-ubuntu), [service discovery](https://learn.microsoft.com/dotnet/core/extensions/service-discovery), [dotnet package list](https://learn.microsoft.com/dotnet/core/tools/dotnet-package-list), [NuGet auditing](https://learn.microsoft.com/nuget/concepts/auditing-packages)
- EF Core: [applying migrations and bundles](https://learn.microsoft.com/ef/core/managing-schemas/migrations/applying#bundles), [CLI common options](https://learn.microsoft.com/ef/core/cli/dotnet#common-options), [EF Core 11 migrations](https://learn.microsoft.com/ef/core/what-is-new/ef-core-11.0/whatsnew#migrations)

**Messaging**
- CloudAMQP: [plans](https://www.cloudamqp.com/plans.html), [FAQ](https://www.cloudamqp.com/docs/faq.html), [shared vs dedicated](https://www.cloudamqp.com/blog/shared-vs-dedicated-plan.html), [AMQP ports](https://www.cloudamqp.com/docs/amqp.html), [shared plan limits (2015)](https://www.cloudamqp.com/blog/added-limits-for-shared-plans-lemur-tiger.html)
- RabbitMQ: [release information](https://www.rabbitmq.com/release-information), [quorum queues](https://www.rabbitmq.com/docs/quorum-queues); [RabbitMQ.Client](https://www.nuget.org/packages/RabbitMQ.Client), [RabbitMQ.Client.OpenTelemetry](https://www.nuget.org/packages/RabbitMQ.Client.OpenTelemetry)
- KEDA: [rabbitmq scaler 2.21](https://keda.sh/docs/2.21/scalers/rabbitmq-queue/), [scaler source](https://github.com/kedacore/keda/blob/main/pkg/scalers/rabbitmq_scaler.go)
- Aspire: [RabbitMQ hosting](https://aspire.dev/integrations/messaging/rabbitmq/rabbitmq-host/), [OpenTelemetry Collector](https://aspire.dev/integrations/observability/opentelemetry-collector/opentelemetry-collector-host/), [metrics sample](https://aspire.dev/reference/samples/metrics/), [telemetry](https://aspire.dev/fundamentals/telemetry/)

**Observability**
- Grafana: [pricing](https://grafana.com/pricing/), [send OTLP](https://grafana.com/docs/grafana-cloud/send-data/otlp/send-data-otlp/), [usage limits](https://grafana.com/docs/grafana-cloud/cost-management-and-billing/understand-your-invoice/usage-limits/), [contact points](https://grafana.com/docs/grafana-cloud/alerting-and-irm/alerting/configure-notifications/manage-contact-points/), [Terraform provisioning](https://grafana.com/docs/grafana/latest/alerting/set-up/provision-alerting-resources/terraform-provisioning/), [Loki OTLP](https://grafana.com/docs/loki/latest/send-data/otel/), [Tempo configuration](https://grafana.com/docs/tempo/latest/configuration/), [docker-otel-lgtm](https://github.com/grafana/docker-otel-lgtm)
- [Prometheus OTLP guide](https://prometheus.io/docs/guides/opentelemetry/)

**Libraries and CI**
- [Yarp.ReverseProxy](https://www.nuget.org/packages/Yarp.ReverseProxy), [Microsoft.Extensions.ServiceDiscovery.Yarp](https://www.nuget.org/packages/Microsoft.Extensions.ServiceDiscovery.Yarp), [Scalar.AspNetCore](https://www.nuget.org/packages/Scalar.AspNetCore) and its [integration guide](https://scalar.com/scalar/scalar-api-references/integrations/net-aspnet-core/integration) and [changelog](https://github.com/scalar/scalar/blob/main/integrations/dotnet/aspnetcore/CHANGELOG.md), [Npgsql connection string parameters](https://www.npgsql.org/doc/connection-string-parameters.html)
- Coverage: [coverlet VSTest integration](https://github.com/coverlet-coverage/coverlet/blob/master/Documentation/VSTestIntegration.md), [ReportGenerator settings](https://github.com/danielpalme/ReportGenerator/wiki/Settings)
- GitHub Actions: [workflow syntax](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax), [events](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows), [reusable workflows](https://docs.github.com/en/actions/reference/workflows-and-actions/reusing-workflow-configurations)
