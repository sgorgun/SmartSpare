# SmartSpare — API documentation

How the HTTP API is described and published: one OpenAPI document per module and the Scalar reference, both public
([ADR 0019](../adr/0019-public-api-documentation.md), decision D4 of the product owner). Every endpoint they describe
still requires a signed-in user.

## 1. Where to find it

| | Single host (development) | Gateway (production, `--topology services`) |
|---|---|---|
| Scalar reference | `https://localhost:7080/scalar` | `https://localhost:7090/scalar` locally; `<gateway>/scalar` in Azure |
| OpenAPI documents | `/openapi/identity.json`, `/openapi/inventory.json`, `/openapi/fleet.json`, `/openapi/procurement.json`, `/openapi/forecasting.json`, `/openapi/reporting.json` | the same paths, proxied to each service |

**Open it locally:**

```bash
dotnet run --project src/SmartSpare.AppHost                       # single host
# or
dotnet run --project src/SmartSpare.AppHost -- --topology services  # gateway + services
```

Then open `/scalar` on the host or the gateway (the Aspire dashboard lists the URL). No sign-in is needed to read the
documentation. To try a request from Scalar, sign in to the web app in the same browser first (section 4). The
document of one module can also be fetched directly, for example `curl -k https://localhost:7080/openapi/identity.json`.

## 2. How the documents are produced

```mermaid
flowchart LR
  subgraph service["Each service (and the single host, once per module)"]
    E["Minimal API endpoints<br/>/api/v1/{module}/..."] --> G["Microsoft.AspNetCore.OpenApi<br/>AddOpenApi(module)"]
    G --> F["ShouldInclude: only /api/v1/{module}/<br/>transformer: title, version v1, no servers"]
    F --> D["/openapi/{module}.json<br/>OpenAPI 3.1, anonymous"]
  end
  D -->|"route openapi-{module}, GET only,<br/>no token, cached 10 min"| W["Gateway<br/>/openapi/{module}.json"]
  W --> S["Scalar at /scalar<br/>one entry per module"]
```

- **Generation:** ASP.NET Core 10's built-in `Microsoft.AspNetCore.OpenApi`, no Swashbuckle. [`ApiDocumentation.AddModuleDocuments`](../../src/BuildingBlocks/SmartSpare.Infrastructure/Http/ApiDocumentation.cs) registers one document per module, named after it. The single host registers all six, and a service only its own.
- **What a document contains:** only the module's versioned business endpoints, `/api/v1/{module}/...` (`ShouldInclude`). Left out on purpose (ADR 0019 §3):
  - `/internal/*`, the service-to-service endpoints;
  - operational endpoints: module pings and the diagnostics probe, marked with `.AsOperational()`;
  - health probes;
  - server addresses.
- **Descriptions:** each endpoint declares its summary, description and typed responses (`WithSummary`, `WithDescription`, `Produces<T>`, `ProducesProblem`). Module projects generate XML documentation, so comments on handler methods appear too ([`src/Modules/Directory.Build.props`](../../src/Modules/Directory.Build.props)).
- **Publication:**
  - Each service maps `/openapi/{module}.json` anonymously.
  - The gateway proxies it through the `openapi-{module}` route: GET only, without the user's token, cached for 10 minutes and rate-limited per client IP ([`GatewayRoutes.cs`](../../src/Bootstrapper/SmartSpare.Gateway/GatewayRoutes.cs)).
  - The single host serves the same paths with the same cache and limit.
- **Scalar:** [`ApiReference.MapApiReference`](../../src/BuildingBlocks/SmartSpare.Bff/ApiReference.cs) in both BFF hosts (`Scalar.AspNetCore` 3.2, MIT).
  - Its assets are served locally and its fonts aren't loaded from a CDN.
  - Telemetry, the AI agent, MCP and the hosted developer tools are switched off, so nothing is sent to Scalar's services.
  - Its page has its own CSP, `script-src 'self' 'nonce-…'`, with a fresh 144-bit nonce per response. Every other page keeps the stricter stage 3 policy.

**Content today.** Every endpoint that exists in stage 4 is operational, so the six documents are valid but list no
paths yet. They fill as stages 5–9 add `/api/v1` endpoints, each with its summary and response types (#54).

## 3. Conventions

**Versioning** ([ADR 0012](../adr/0012-evolvability-principles.md)):
- Business endpoints live under `/api/v1/{module}/...`. A breaking change gets `/api/v2/...` beside v1.
- Operational endpoints (`/api/{module}/ping`, diagnostics) are unversioned and must carry `.AsOperational()`. The test `Every_unversioned_module_endpoint_is_marked_operational` fails otherwise, so no business endpoint escapes versioning.
- The gateway routes both with literal module names: `/api/v1/inventory/{**rest}` and `/api/inventory/{**rest}`.
- `/internal/v1/{module}/...` is for service-to-service calls only. It is never routed by the gateway (route-table test) and never documented.

**Errors** (RFC 9457 ProblemDetails, `application/problem+json`). Every error response carries:
- `status`, `title` and `detail`;
- `traceId`, the W3C trace id, shown to users as "Reference" ([TRACING.md](../operations/TRACING.md));
- a stable `code`:

| Code | Status | Meaning |
|---|---|---|
| `SS-AUTH-001` | 400 | Sign-in failed (the same for every cause) |
| `SS-AUTH-002` | 400 | Missing or invalid antiforgery token on an unsafe request |
| `SS-AUTH-003` | 401 | No valid session |
| `SS-AUTH-004` | 403 | Signed in, but the policy or brand scope doesn't allow it |
| `SS-AUTH-005` | 429 | Too many attempts; see `Retry-After` |
| `SS-SYS-001` | 503 | A service the request needs didn't answer (gateway topology) |
| `SS100`–`SS402` | 400–422 | Business rules enforced by the database, mapped by `DatabaseErrorMapper` |
| `<Module>.<Error>` | 400–422 | Module errors from `Result<T>`, for example `Identity.InvalidCredentials` |

The codes are defined in [`ProblemCodes`](../../src/Web/SmartSpare.ApiContracts/ProblemCodes.cs) and the module error classes; `ProducesProblem` declares them per endpoint.

**Status codes:** `Result<T>` maps to 200 with a body or 204 without one; its errors to 400 (validation), 403, 404, 409 (conflict) and 422 (business rule) ([`ResultHttpExtensions`](../../src/BuildingBlocks/SmartSpare.Infrastructure/Http/ResultHttpExtensions.cs)); authentication gives 401, the rate limiter 429, an unreachable service 503. Creating endpoints (stages 5–9) answer 201 with `Location`.

**JSON:** the ASP.NET Core web defaults (camelCase properties, ISO 8601 dates); request and response types are the records of `SmartSpare.ApiContracts`, shared with the web client.

## 4. Authentication

The API has no API keys and no bearer tokens for browsers ([ADR 0014](../adr/0014-browser-sign-in-through-bff-endpoints.md), [ADR 0016](../adr/0016-authentication-behind-the-gateway.md)):

1. `GET /bff/antiforgery` returns the antiforgery request token and its header name (`X-XSRF-TOKEN`).
2. `POST /bff/login` with `{ "userName", "password", "rememberMe" }` and that header sets the HttpOnly cookie `__Host-SmartSpare`.
3. Every API call then sends the cookie automatically. Unsafe methods (POST, PUT, PATCH, DELETE) also send `X-XSRF-TOKEN`; fetch a new token after sign-in, because it is bound to the user.
4. `GET /bff/me` tells whether the session is signed in, and `POST /bff/logout` ends it.

Behind the gateway, each request reaches the services with a 2-minute ES256 internal token that only the gateway can
mint; clients never see it. Try-it calls from Scalar are same-origin, so they work in a browser that has signed in to the
web app. The `/bff` endpoints themselves are part of the BFF, not of a module document; they are described here and in
the [README](../../README.md#sign-in-endpoints-bff).

## 5. Tests

| Test | What it checks |
|---|---|
| `ApiDocumentationTests` (host integration) | Each module document is anonymous, OpenAPI 3.1, titled, without servers, and lists only that module's `/api/v1` paths; the path filter on business, operational, internal, other-module and health paths; every `/api/v1` endpoint requires authorization; `/scalar` runs only its nonce script, with a fresh nonce per response and no CDN; other pages keep the stage 3 policy |
| `ServiceStartTests` | Each service publishes only its own document (404 for others) |
| `GatewayTests` | The gateway proxies documents anonymously without a token, GET only; `/internal` is never forwarded; the route table can't match `/internal` |
| `GatewayUnitTests.Every_unversioned_module_endpoint_is_marked_operational` | No business endpoint outside `/api/v1` |
| `ApiReferenceTest` (Playwright) | `/scalar` renders in Chromium with no CSP violation and no request to another origin |
