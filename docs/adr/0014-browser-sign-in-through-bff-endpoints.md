# ADR 0014: Browser sign-in through BFF endpoints

- Status: Accepted, amended by [ADR 0016](0016-authentication-behind-the-gateway.md) (2026-11-24)
- Date: 2026-11-24

## Context

ADR 0006 fixes the frontend (Blazor Web App, global InteractiveWebAssembly, MudBlazor 9.x) and the BFF pattern: an HttpOnly, SameSite=Strict cookie, with no tokens in the browser. It doesn't say how the cookie is issued or how ASP.NET Core Identity is attached to the existing `iam` tables. Several facts of the platform narrow the choice ([STAGE_3_PLAN.md](../stage-3/STAGE_3_PLAN.md), section 1.1):

- `SignInManager` and `UserManager` aren't supported in Razor components, and a WebAssembly component can't set an HttpOnly cookie.
- The template's Identity UI renders with static SSR, but MudBlazor needs interactivity.
- The authentication state deserialized in WASM is fixed for the lifetime of the app.
- The `iam` schema (uuid security stamp, citext user name, no normalized columns, CHECK-protected V3 hashes, audit triggers) is already ported and covered by the SQL test suite.
- ADR 0013 forbids framework types in `Domain/`.

Alternatives considered:

1. **Template Identity UI (static SSR account pages with `SignInManager`).** Fits the framework best, but the sign-in page couldn't use MudBlazor. It would bring a second rendering model and scaffolded pages we don't need (registration, e-mail confirmation).
2. **`MapIdentityApi` with `useCookies=true`.** Ready-made JSON endpoints, but it also exposes registration, bearer-token issuance and refresh endpoints. It requires `IdentityUser`-shaped stores, and tokens in the browser are what ADR 0006 rules out.
3. **Own OpenID Connect server (OpenIddict, Duende) with the BFF as client.** The textbook BFF, but a token server is out of scope for one web client. Kept for mobile and third-party clients (#25, #26).
4. **WASM sign-in page posting JSON to Host BFF endpoints, Identity with custom stores over `iam`.**

## Decision

Option 4.

1. **Endpoints.** The BFF maps `POST /bff/login`, `POST /bff/logout`, `GET /bff/me` and `GET /bff/antiforgery`, unversioned because they are browser-session plumbing, not public API. Originally the BFF was the Identity module, with `SignInManager<AppUser>` running only there. Since [ADR 0016](0016-authentication-behind-the-gateway.md), the endpoints are a `SmartSpare.Bff` building block used by the single host and the gateway; they call `Identity.Contracts.ICredentialVerifier`, and `SignInManager` runs inside the Identity module. The cookie `__Host-SmartSpare` is HttpOnly, Secure and SameSite=Strict, with an 8-hour sliding expiry. APIs answer 401/403 instead of redirecting.
2. **Auth state.** `AddAuthenticationStateSerialization` with an allow-listed `SerializationCallback` (id, name, roles, brands, warehouse). After sign-in or sign-out the client does a full reload, so the server re-renders with the new principal and a new antiforgery token.
3. **CSRF.** Every unsafe `/bff` and `/api` request, including login, is validated with the token-based antiforgery service through an endpoint filter, with the token sent in a header. SameSite=Strict is a second layer, not the only one.
4. **Identity over `iam`.** `AddIdentityCore<AppUser>` with custom stores (`IUserPasswordStore`, `IUserSecurityStampStore`, `IUserLockoutStore`, `IUserRoleStore`) in the module's `Infrastructure/`. `AppUser` is the domain aggregate and doesn't derive from `IdentityUser`. Seeded V3 hashes verify unchanged. The security stamp is validated every 5 minutes. Lockout is 5 attempts and 15 minutes. Deactivated or anonymised users are refused through `IUserConfirmation`. Sign-in is rate-limited per client IP.
5. **Shared HTTP contract.** `SmartSpare.ApiContracts` holds the DTOs, validators, role, claim and policy names. Module implementation projects may reference it, so endpoints and the client share one definition. ApiContracts itself references no module and depends only on FluentValidation and `Microsoft.AspNetCore.Authorization`.

## Consequences

- No tokens ever reach the browser, and XSS can't steal the session (NFR04).
- One rendering model (WASM with MudBlazor) for every page, including sign-in.
- No Identity schema change: the existing CHECKs, grants, audit triggers and 97 SQL tests stay valid.
- We own about 200 lines of store code and must keep it consistent with Identity's manager contracts. Integration tests cover sign-in, lockout and the stamp.
- The CSP allows `style-src 'unsafe-inline'`, because MudBlazor computes inline `style` attributes at runtime and no hash or nonce can match them. The accepted risk is CSS injection (defacement, UI redressing, attribute reads through CSS selectors), limited by `img-src` and `connect-src 'self'`. `script-src` stays strict (`'self' 'wasm-unsafe-eval'` plus the import-map hash), so injected markup still can't run script ([STAGE_3_PLAN.md](../stage-3/STAGE_3_PLAN.md), section 8.7).
- A full page reload after sign-in and sign-out is a visible but accepted UX cost.
- SameSite=Strict means a first navigation from another site looks signed-out until the sign-in page checks `/bff/me` and reloads.
- New client types (mobile, third-party) need a token issuer later. Policies are claims-based (ADR 0012), so endpoints don't change.
- After the split into services, the `/bff` endpoints move behind the YARP gateway (ADR 0006).

## Amendment (2026-11-24)

[ADR 0016](0016-authentication-behind-the-gateway.md) extends this decision to the services topology:
- The `/bff` endpoints move into a BFF building block shared by the single host and the gateway.
- Credentials are checked by the Identity module through `ICredentialVerifier`, in-process or through the Identity service's internal endpoint.
- The gateway forwards the user to services as a 2-minute ES256 internal token, validated with JwtBearer.
- Module endpoints accept the cookie or the internal token through one policy scheme.

Everything this ADR says about the browser still holds: cookie flags, antiforgery, full reload, `iam` custom stores, lockout and stamp validation.
