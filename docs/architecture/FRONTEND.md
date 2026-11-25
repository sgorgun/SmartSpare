# SmartSpare — Front-end

The web client of SmartSpare: what it is built with and why, how it is structured, how the main user flows work, and how
it is tested. Decisions: [ADR 0006](../adr/0006-frontend-blazor-wasm-mudblazor-bff.md) (Blazor WebAssembly, MudBlazor,
BFF) and [ADR 0014](../adr/0014-browser-sign-in-through-bff-endpoints.md) (sign-in through BFF endpoints). Status as of
stage 4: the shell, sign-in, role-based navigation and error handling are done; the business screens come with their
modules in stages 5–9.

## 1. Stack and why

| Choice | Why | Decision |
|---|---|---|
| **Blazor WebAssembly** (.NET 10, Blazor Web App template with global InteractiveWebAssembly and prerendering) | One language and one toolchain with the back-end. DTOs and validators are shared with the server through `SmartSpare.ApiContracts`. The UI runs in the browser, so no server state and no SignalR circuit, which keeps the services stateless and scalable to zero | [ADR 0006](../adr/0006-frontend-blazor-wasm-mudblazor-bff.md) |
| **MudBlazor 9** | Material components for a data-heavy admin UI (tables, forms, dialogs, snackbars), MIT, supports .NET 10 | [ADR 0006](../adr/0006-frontend-blazor-wasm-mudblazor-bff.md) |
| **BFF with an HttpOnly cookie** | No token ever reaches the browser, so an XSS can't steal one. The gateway (production) or the single host (development) owns the cookie and calls the services | [ADR 0014](../adr/0014-browser-sign-in-through-bff-endpoints.md), [ADR 0016](../adr/0016-authentication-behind-the-gateway.md) |
| **Strict CSP** | `script-src 'self' 'wasm-unsafe-eval'` plus the hash of the inline import map; no `unsafe-inline` scripts. `style-src` allows inline styles because MudBlazor computes them (accepted in ADR 0014) | `SecurityHeaders` in [`WebClientHosting.cs`](../../src/BuildingBlocks/SmartSpare.Bff/WebClientHosting.cs) |

Rejected, with reasons in ADR 0006: Fluent UI Blazor (fewer data components), Telerik, DevExpress and Syncfusion (paid),
and InteractiveServer and Auto render modes (server state and SignalR hinder scaling and splitting into services).

## 2. Component structure

[`src/Web/SmartSpare.Web.Client`](../../src/Web/SmartSpare.Web.Client) references only `SmartSpare.ApiContracts`, never a
module (architecture test `WebClient_ReferencesOnlyApiContracts`).

```
SmartSpare.Web.Client/
  Routes.razor              router: AuthorizeRouteView, redirect of anonymous users, "not authorized" message
  Program.cs, ClientServices.cs   services: MudBlazor, localization, policies, typed API clients with their handlers
  Layout/
    MainLayout.razor        app bar (menu button, title, user menu), responsive drawer, content container
    LoginLayout.razor       narrow centred layout of the sign-in page
    NavMenu.razor           the menu: areas the user's policies allow (NavigationAreas)
    NavigationAreas.cs      areas, their icons and the policies that show them
    SmartSpareTheme.cs      colours and typography (WCAG 2.1 AA contrast)
  Features/                 one folder per module, like the back-end
    Home/Home.razor         welcome and a card per visible area
    Identity/               Login.razor + LoginForm.cs (form model, shared validator), UserMenu.razor, IdentityClient.cs
    Inventory/ Fleet/ Procurement/ Forecasting/ Reporting/   area pages (placeholders until stages 5–9)
  Shared/                   reusable components
    StatusChip.razor        a status as icon + text, never colour alone
    RedirectToLogin.razor   full navigation to /login?returnUrl=...
    NotFound.razor
  Http/                     the API layer
    ApiResult.cs            ApiResult<T> and ProblemInfo: a response is a value or a ProblemDetails, never an exception
    Handlers.cs             AntiforgeryHandler, UnauthorizedHandler, LoginLinks (safe return URLs)
    ProblemMessages.cs      ProblemDetails code → readable, localized message with "Reference: <trace id>"
  Resources/SharedResource.resx   every UI string (IStringLocalizer)
```

**API services.** Each module gets one typed client, `<Module>Client`, which calls only relative URLs. Today that is
`IdentityClient` for `/bff/login`, `/bff/logout` and `/bff/me`. The clients share one handler chain, built per DI scope
in `ClientServices.CreateHttpClient`:

```mermaid
flowchart LR
  C["IdentityClient<br/>(and later one client per module)"] --> A["AntiforgeryHandler<br/>adds X-XSRF-TOKEN to POST/PUT/PATCH/DELETE"]
  A --> U["UnauthorizedHandler<br/>401 → reload to sign-in<br/>403 → snackbar with reference"]
  U --> H["HttpClientHandler<br/>same origin, the cookie goes along"]
  H --> B["BFF: gateway or single host"]
```

**State.** Plain scoped services and the cascading `AuthenticationState`; no Fluxor or Redux (ARCHITECTURE_DECISIONS §6).
The server prerenders the page and hands the browser an allow-listed principal: user id, name, roles, brands and
warehouse, never the security stamp (`ClientAuthenticationState`).

## 3. User flows

### 3.1 Sign-in

```mermaid
sequenceDiagram
  actor U as User
  participant B as Browser (Blazor WASM)
  participant F as BFF (gateway / host)
  participant I as Identity (service or module)
  U->>B: open any page
  B->>F: GET /inventory (no cookie)
  F-->>B: prerendered page, anonymous
  B->>B: RedirectToLogin → /login?returnUrl=%2Finventory
  B->>F: GET /bff/antiforgery
  F-->>B: request token (bound to the anonymous user)
  U->>B: user name, password, Sign in
  B->>B: validate with LoginRequestValidator (shared with the server)
  B->>F: POST /bff/login + X-XSRF-TOKEN
  F->>I: check credentials (HTTP with the gateway's service token, or in process)
  I-->>F: claims
  F-->>B: 204 + Set-Cookie __Host-SmartSpare (HttpOnly, Secure, SameSite=Strict)
  B->>F: full reload of /inventory (only local return URLs are followed)
  F-->>B: prerendered for the signed-in user, new antiforgery token
```

### 3.2 Wrong password, with a reference

```mermaid
sequenceDiagram
  actor U as User
  participant B as Browser
  participant F as BFF
  U->>B: wrong password, Sign in
  B->>F: POST /bff/login
  F-->>B: 400 ProblemDetails { code: SS-AUTH-001, traceId }
  B->>B: ProblemMessages: "The user name or password is incorrect. Reference: 4bf9…0e47"
  B-->>U: error alert under the form (role=alert)
  Note over U,F: Every failure (unknown user, wrong password, locked, inactive) gives the same message.<br/>The reference is the W3C trace id; operations find the request with it (TRACING.md).
```

### 3.3 Role-based navigation

```mermaid
flowchart TD
  S["Signed-in principal<br/>(roles, brands, warehouse)"] --> N["NavigationAccess.VisibleAsync"]
  N -->|for each area| P{"user passes any of<br/>the area's policies?"}
  P -->|yes| M["shown in the menu and as a card on Home"]
  P -->|no| H["hidden"]
  M --> R["route guarded by AuthorizeRouteView"]
  R --> E["every API endpoint checks its own policy on the server:<br/>the menu is convenience, not security"]
```

The policies come from `SmartSpare.ApiContracts` (`AppPolicies`), the same definitions the server enforces. The bUnit
test `The_menu_shows_the_areas_the_roles_may_work_in` checks every role.

### 3.4 Session expiry and 401

```mermaid
sequenceDiagram
  participant B as Browser
  participant F as BFF
  participant I as Identity
  Note over F: every 5 minutes the cookie is re-validated
  B->>F: GET /api/v1/... (cookie)
  F->>I: session check (security stamp)
  I-->>F: revoked (password changed, user deactivated or anonymised)
  F-->>B: 401 ProblemDetails (cookie removed)
  B->>B: UnauthorizedHandler → full reload of /login?returnUrl=<current page>
  Note over B: after sign-in the user returns to the same page.<br/>A changed role re-issues the cookie with the new claims instead.
```

A 403 keeps the user on the page and shows "You are not allowed to do this." with the reference in a snackbar.

### 3.5 Sign-out

```mermaid
sequenceDiagram
  actor U as User
  participant B as Browser
  participant F as BFF
  U->>B: user menu → Sign out
  B->>F: POST /bff/logout + X-XSRF-TOKEN
  F-->>B: 204, cookie deleted
  B->>B: full reload of /login
  Note over B: the prerendered page is anonymous again;<br/>GET /bff/me answers isAuthenticated: false
```

## 4. Forms and validation

- Form models (`LoginForm`) convert to the API request (`LoginRequest`) and are validated with the **same
  FluentValidation validator** the server uses, from `SmartSpare.ApiContracts`. The rules can't drift apart, and the
  server validates again: the client's check is for the user, not for security.
- `MudForm` validates per field as the user types (`Immediate`, `ValidationDelay = 0`). Submit validates the whole
  form and sends nothing when it is invalid (bUnit `Empty_fields_show_the_shared_validator_messages_and_send_nothing`).
- Inputs carry `autocomplete` hints (`username`, `current-password`) so password managers work.

## 5. Error handling

| Response | What the user sees | Where |
|---|---|---|
| 400 with a known `code` (`SS-AUTH-001`, `SS-AUTH-002`, …) | A specific, localized message with "Reference: \<trace id\>" | `ProblemMessages` |
| 400/404/409/422 business error | The server's `detail` with the reference | `ProblemMessages` |
| 401 | Full reload to the sign-in page with the current page as return URL | `UnauthorizedHandler` |
| 403 | Snackbar "not allowed" with the reference; the page stays | `UnauthorizedHandler` |
| 429 | "Too many attempts. Wait a minute and try again." | `ProblemMessages` |
| 500/503 | A generic message with the reference, never internal details | `ProblemMessages` |

Responses are values (`ApiResult<T>`), not exceptions, so a page always decides what to show. The reference is the W3C
trace id the server puts into every ProblemDetails (#60); [TRACING.md](../operations/TRACING.md) follows it to the logs
and the trace.

## 6. Loading states

- **Before WebAssembly runs**, the server-prerendered page is visible at once. Interactive elements expose
  `data-interactive="true"` once the client has taken over, which the end-to-end tests wait for.
- **Sign-in page:** a progress bar while it checks for an existing session (`/bff/me`), and a busy, disabled button
  with a spinner while signing in (bUnit `While_signing_in_the_button_is_busy_and_disabled`), so there is no double
  submit.
- **Business pages** (convention for stages 5–9, not built yet): `MudProgressLinear` or skeletons while loading, and an
  empty state when there is no data.
- **Cold start** (production scales to zero): the gateway's `/warmup` wakes the services before a session (STAGE_4_PLAN
  8.7).

## 7. Responsive layout

- MudBlazor's breakpoints: `MudContainer` widths, `MudGrid` items that stack on small screens. The drawer is
  *responsive*: docked beside the content on wide screens, an overlay opened from the app bar's menu button on phones.
- The sign-in page uses a narrow centred layout (`LoginLayout`) that fits a phone.
- **Checked on a phone viewport:** Playwright `MobileLayoutTest` at 390 × 844 (touch, 3× scale). The sign-in page,
  the home page and an area page have no horizontal scroll, and the menu opens from the app bar and leads to an area.
  Screenshots are in `TestResults/e2e/mobile-*.png`.
- The engineer's tablet view of a machine card (WF03) comes with Fleet in stage 6.

**Accessibility** (WCAG 2.1 AA target): ARIA labels on icon buttons, a visible focus outline (`:focus-visible`), focus
moved to the page heading on navigation (`FocusOnNavigate`), colours with sufficient contrast, and statuses shown as
icon + text, not colour alone (`StatusChip`, bUnit-tested).

## 8. Tests

| Level | Project | What it covers |
|---|---|---|
| Components (bUnit) | [`tests/SmartSpare.Web.Tests`](../../tests/SmartSpare.Web.Tests) | Sign-in form: shared validator messages, busy state, the generic failure with its reference, return URL and open-redirect protection; menu per role; redirect to sign-in; 401 and 403 handling; antiforgery header on unsafe methods; problem messages; status chip |
| End-to-end (Playwright, Chromium) | [`tests/SmartSpare.E2E`](../../tests/SmartSpare.E2E) | `SignInSmokeTest`: redirect, wrong password with reference, sign-in, role menu, no credential readable by scripts, sign-out. `MobileLayoutTest`: phone viewport. `ApiReferenceTest`: `/scalar` under its CSP |
| Server side of the client | [`tests/SmartSpare.Host.IntegrationTests`](../../tests/SmartSpare.Host.IntegrationTests) | Cookie flags, antiforgery, CSP and the import-map hash, the principal handed to the client, rate limiting |

The bUnit tests run in the "Build and test" CI job and the Playwright tests in the "End-to-end smoke test" job (`tools/check.sh e2e`, real host on Kestrel, PostgreSQL in Testcontainers, demo data, headless Chromium).
