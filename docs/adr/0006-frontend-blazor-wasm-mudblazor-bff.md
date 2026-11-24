# ADR 0006: Frontend: Blazor WebAssembly, MudBlazor and BFF

- Status: Accepted, amended by [ADR 0015](0015-deployment-topology-microservices-behind-yarp-gateway.md) and [ADR 0016](0016-authentication-behind-the-gateway.md) (2026-11-24)
- Date: 2026-11-16

## Context

The topic application and the BA fix Blazor WebAssembly and MudBlazor. The BA (NFR04) mentions JWT; storing tokens in the browser exposes them to XSS.

## Decision

Use the .NET 10 Blazor Web App template with global InteractiveWebAssembly render mode (MudBlazor does not support static rendering) and MudBlazor 9.x. Authenticate with the Backend-for-Frontend pattern: ASP.NET Core Identity issues an HttpOnly, SameSite=Strict cookie with CSRF protection; in production the YARP gateway is the BFF ([ADR 0015](0015-deployment-topology-microservices-behind-yarp-gateway.md)); in development the single host plays that role. Organise the client by module (`Features/<Module>`) and share DTOs and FluentValidation validators through `SmartSpare.ApiContracts`. Rejected: Fluent UI Blazor v5, commercial suites (Telerik, DevExpress, Syncfusion), InteractiveServer and Auto render modes.

## Consequences

- Tokens never reach the browser; NFR04 is reworded to "cookie in the browser, tokens between services".
- Stateless server that scales horizontally; the frontend is unchanged by the move to microservices.
- Larger first download than server rendering.

## Amendment (2026-11-24)

In production the BFF is the **YARP gateway** (`SmartSpare.Gateway`, [ADR 0015](0015-deployment-topology-microservices-behind-yarp-gateway.md)). It:
- hosts the WebAssembly client;
- owns the HttpOnly cookie and antiforgery;
- proxies `/api/*` to the services, swapping the cookie for a short-lived internal token ([ADR 0016](0016-authentication-behind-the-gateway.md)).

The single host keeps the BFF role in development and tests. The browser side of this ADR is unchanged: cookie only, no tokens, MudBlazor, `Features/<Module>`, ApiContracts.
