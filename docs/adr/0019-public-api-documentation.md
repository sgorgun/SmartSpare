# ADR 0019: Public API documentation

- Status: Accepted
- Date: 2026-11-24
- Amends: [ADR 0012](0012-evolvability-principles.md) (stable contracts: the HTTP API is described by OpenAPI)

## Context

ADR 0012 makes the versioned HTTP API a stable contract described by OpenAPI, but doesn't say who may read the description. `Program.cs` maps OpenAPI only in Development, as the ASP.NET Core template does. Microsoft's docs restrict it there "to minimize the risk of exposing sensitive information and reduce the vulnerabilities in production" ([OpenAPI overview](https://learn.microsoft.com/aspnet/core/fundamentals/openapi/overview?view=aspnetcore-10.0)).

The backend rubric deducts a point for missing API documentation (#54). On 24 November 2026 the product owner decided (D4) that the OpenAPI documents and the Scalar UI are published **without sign-in** in production. Every endpoint they describe still requires authorization.

This deliberately reverses a framework default for security reasons, so it is recorded as its own ADR rather than as an edit to ADR 0012.

## Decision

1. **Generation.** Each service generates its own document with `Microsoft.AspNetCore.OpenApi` (OpenAPI 3.1 in .NET 10; [generate documents](https://learn.microsoft.com/aspnet/core/fundamentals/openapi/aspnetcore-openapi?view=aspnetcore-10.0)), named after its module (`identity`, `inventory`, …).
   - **In the single host:** one document per module from the same endpoint groups.
   - **Content:** summaries and descriptions (XML documentation comments are supported for minimal APIs in .NET 10), typed success and error responses with ProblemDetails, examples, tags per module.
2. **Publication.**
   - The gateway serves `/openapi/{module}.json` by proxying each service's document.
   - It also serves the Scalar UI (`Scalar.AspNetCore` 3.2.0, MIT; [use OpenAPI documents](https://learn.microsoft.com/aspnet/core/fundamentals/openapi/using-openapi-documents?view=aspnetcore-10.0)) at `/scalar`, with one entry per module.
   - The single host serves the same paths.
   - All of these are anonymous in every environment.
3. **What is excluded:**
   - `/internal/*` endpoints (ADR 0015), via a document transformer;
   - health and operational endpoints;
   - servers other than the public base URL;
   - examples containing real or personal data (examples use the synthetic T001 personas);
   - server error details.

   An integration test fails if an `/internal` path or a non-`/api/v1` business path appears in a document.
4. **What does not change:** authentication and authorization of the endpoints themselves (fallback policy, STAGE_3_PLAN §3). Rate limiting and caching (`OutputCache`, 10 minutes) apply to the documentation endpoints. CSP and security headers apply to the Scalar page as to the rest of the site.
5. **Try-it calls** from Scalar are same-origin. They work only for a signed-in browser session and are subject to antiforgery like any other client. No API keys or tokens are entered in the UI.

## Alternatives considered

| Alternative | Why not |
|---|---|
| Development only (status quo) | A deployed instance has no API documentation, which the rubric penalises, and examiners and integrators can't read the contract. |
| Published behind sign-in | Safer by default, but the product owner wants the contract readable without an account (examiners, future integrators; #26). The descriptions contain no secrets, so hiding them adds little protection: authorization is enforced on the endpoints, not by obscurity. |
| A static documentation site built in CI | It could drift from the running version. Serving the live document keeps them identical, and a build-time copy can still be added for releases. |

## Consequences

- Anyone can read the public API surface: routes, parameters, schemas and error codes. That is accepted, because security never relies on hiding the API. The OWASP-style risk of revealing endpoint structure is reduced by excluding internal endpoints and by authorization on every operation.
- The documentation becomes a reviewed artefact: PRs that add endpoints add summaries, response types and examples (#54).
- Scalar is one more MIT dependency served by the gateway.
