# ADR 0006: Frontend: Blazor WebAssembly, MudBlazor and BFF

- Status: Accepted
- Date: 2026-11-16

## Context

The topic application and the BA fix Blazor WebAssembly and MudBlazor. The BA (NFR04) mentions JWT; storing tokens in the browser exposes them to XSS.

## Decision

Use the .NET 10 Blazor Web App template with global InteractiveWebAssembly render mode (MudBlazor does not support static rendering) and MudBlazor 9.x. Authenticate with the Backend-for-Frontend pattern: ASP.NET Core Identity issues an HttpOnly, SameSite=Strict cookie with CSRF protection; after the split a YARP gateway becomes the BFF. Organise the client by module (`Features/<Module>`) and share DTOs and FluentValidation validators through `SmartSpare.ApiContracts`. Rejected: Fluent UI Blazor v5, commercial suites (Telerik, DevExpress, Syncfusion), InteractiveServer and Auto render modes.

## Consequences

- Tokens never reach the browser; NFR04 is reworded to "cookie in the browser, tokens between services".
- Stateless server that scales horizontally; the frontend is unchanged by the move to microservices.
- Larger first download than server rendering.
