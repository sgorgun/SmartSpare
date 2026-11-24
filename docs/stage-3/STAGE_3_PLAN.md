# SmartSpare — Stage 3 Plan: Identity, BFF and Blazor Skeleton

Status: **proposed** (24 November 2026). Stage 3 of the work plan ([ARCHITECTURE_DECISIONS.md, section 13](../architecture/ARCHITECTURE_DECISIONS.md#13-work-plan)): cookie sign-in, roles and policies, and the Blazor client skeleton with MudBlazor and the sign-in page. Release target: **v0.3.0**.

Inputs: ARCHITECTURE_DECISIONS sections 6, 7 and 13; [ADR 0006](../adr/0006-frontend-blazor-wasm-mudblazor-bff.md), [ADR 0012](../adr/0012-evolvability-principles.md), [ADR 0013](../adr/0013-domain-model-and-persistence.md); the `iam` schema in [DATA_DICTIONARY.md](../database/DATA_DICTIONARY.md#3-schema-iam--identity) and the trust model in [MODULE_MAPPING.md, section 3.6](../database/MODULE_MAPPING.md); issues #7, #11, #22, #33, #38. The sign-in design is recorded in [ADR 0014](../adr/0014-browser-sign-in-through-bff-endpoints.md) (**Accepted** 24 November 2026; PRs 3b–3d implement it). [ADR 0016](../adr/0016-authentication-behind-the-gateway.md) extends it to the production topology of [ADR 0015](../adr/0015-deployment-topology-microservices-behind-yarp-gateway.md). PR 3b already builds the sign-in so that it works unchanged behind the gateway in stage 4 (section 1.6).

Every technical choice below was checked against the official documentation for **ASP.NET Core 10.0** (links use `?view=aspnetcore-10.0`; see [section 11](#11-references)). Learn now opens on the ASP.NET Core 11 preview by default, and some of what it shows there does not exist in .NET 10 (for example, the automatic `Sec-Fetch-Site` CSRF middleware). The plan uses only .NET 10 features.

---

## 0. Summary of decisions

| # | Decision | Section |
|---|---|---|
| S1 | The WASM sign-in page posts JSON to **BFF endpoints** `/bff/login`, `/bff/logout` and `/bff/me`. They live in a `SmartSpare.Bff` building block, used by `SmartSpare.Host` now and by the gateway in stage 4. They check credentials through `Identity.Contracts.ICredentialVerifier`. `SignInManager` runs only inside the Identity module, never in Razor components or in the BFF. | 1, ADR 0014, ADR 0016 |
| S2 | Auth cookie `__Host-SmartSpare`: HttpOnly, Secure, SameSite=Strict, 8 h sliding expiry. No tokens reach the browser. | 1.3 |
| S3 | Auth state reaches WASM through `AddAuthenticationStateSerialization`, with a custom callback that sends only id, name, roles, brands and warehouse. After sign-in and sign-out the page does a **full reload**. | 1.4 |
| S4 | Token-based antiforgery on **every unsafe `/bff` and `/api` request**, including login, plus SameSite=Strict as a second layer. | 1.5 |
| S5 | **`AddIdentityCore<AppUser>` with custom stores** over the existing `iam` tables. `AppUser` is the domain aggregate from `Domain/`, not an `IdentityUser`. No schema changes for Identity itself. | 2 |
| S6 | Seeded V3 hashes (PBKDF2-HMAC-SHA512, 100,000 iterations) verify unchanged and are rehashed with **220,000 iterations** (OWASP) on the next sign-in. The demo password comes from `.env` at load time (#7). | 2.3 |
| S7 | Session (security stamp) re-validated by the BFF every 5 minutes through `ICredentialVerifier`. Lockout after 5 failures for 15 minutes. Deactivated users cannot sign in, and their cookies stop working at the next stamp check. | 2.4–2.6 |
| S8 | Claims: `sub`, `name`, `role` (n), `ss:brand` (n), `ss:warehouse`. Policies use claims only (ADR 0012). Reads are never brand-scoped. Approvals by a `BRAND_MANAGER` require every line's brand to be in their claims, unless another role grants the action unscoped. | 3 |
| S9 | Fixed-window rate limit on `/bff/login`, partitioned by client IP. Returns 429 with `Retry-After`. | 4 |
| S10 | Data Protection key ring in `iam.data_protection_keys` via `PersistKeysToDbContext`, application name `SmartSpare`. Key protection at rest is configured in the host per environment (#22). This store is for the single host; the gateway uses Blob Storage + Key Vault (ADR 0016 §6, stage 4). | 5 |
| S11 | `SessionContextInterceptor` also sets `app.user_roles` and `app.home_warehouse_id`. The SQL role and warehouse checks read these GUCs, and `iam.v_app_user_masked` becomes module-local (#11). | 6 |
| S12 | Identity aggregates move into `Domain/`, and the Identity classes in `PersistenceModel.cs` are deleted (ADR 0013, #38). | 7 |
| S13 | New `SmartSpare.Web.Client` (Blazor WASM, MudBlazor **9.12.0**), hosted by `SmartSpare.Host` (and by the gateway from stage 4). Typed HTTP clients per module. `IStringLocalizer` with en-US only in this stage. | 8 |
| S14 | Module implementation projects may reference `SmartSpare.ApiContracts`, the public HTTP contract. Role, claim and policy names live there, so host and client share one definition. | 8.4, ADR 0014 |
| S15 | **One principal in both topologies.** In the single host the default scheme is the policy scheme `SmartSpare` (services: JwtBearer only; gateway: cookie only, ADR 0016 §4): an internal ES256 bearer token goes to JwtBearer, anything else to the cookie. Module endpoints never name a scheme. The internal-token issuer is part of `SmartSpare.Bff`, and integration tests call module endpoints with the cookie and with an internal token. | 1.6, ADR 0016 |

---

## 1. Sign-in flow (Blazor Web App, global InteractiveWebAssembly)

### 1.1 Constraints from the platform

- `SignInManager<TUser>` and `UserManager<TUser>` **aren't supported in Razor components** ([Blazor authentication and authorization](https://learn.microsoft.com/aspnet/core/blazor/security/?view=aspnetcore-10.0)). In a WASM component they can't run at all: there is no server, no `HttpContext` and no way to set an HttpOnly cookie.
- The template's Identity UI renders **statically** (static SSR) because Identity components don't support interactivity. MudBlazor requires interactivity and has no static rendering ([ADR 0006](../adr/0006-frontend-blazor-wasm-mudblazor-bff.md); [MudBlazor on NuGet](https://www.nuget.org/packages/MudBlazor/)). A template-style sign-in page could therefore not use MudBlazor.
- With CSR, the deserialized authentication state is **fixed for the lifetime of the WebAssembly app** ([Blazor authentication and authorization, "Manage authentication state in Blazor Web Apps"](https://learn.microsoft.com/aspnet/core/blazor/security/?view=aspnetcore-10.0#manage-authentication-state-in-blazor-web-apps)). Any change to who is signed in must restart the app, so the page does a full reload.
- Authorization in WASM is UX only. **Every check is repeated on the server endpoint** ([Secure ASP.NET Core Blazor WebAssembly, "Authorization"](https://learn.microsoft.com/aspnet/core/blazor/security/webassembly/?view=aspnetcore-10.0#authorization)).

### 1.2 Endpoints

The endpoints belong to the **BFF building block `SmartSpare.Bff`** ([ADR 0016](../adr/0016-authentication-behind-the-gateway.md) §1). The host maps them now; in stage 4 the YARP gateway maps the same code under an unversioned `/bff` group. They are browser-session plumbing, not part of the versioned public API (ADR 0012).

The BFF never touches `iam`. It calls **`Identity.Contracts.ICredentialVerifier`**:
- `VerifyAsync(userName, password, clientIp)` returns the user's claims and security stamp on success, or a uniform failure.
- `ValidateSessionAsync(userId, stamp)` returns the current claims, or "revoked".

The Identity module, which owns `AppUser`, the stores and `SignInManager<AppUser>`, implements it in process. In stage 4 an HTTP client implements it against the Identity service's internal endpoints.

| Endpoint | Auth | Antiforgery | Rate limit | Behaviour |
|---|---|---|---|---|
| `POST /bff/login` (`LoginRequest { UserName, Password, RememberMe }`) | anonymous | required | `login` policy | `ICredentialVerifier.VerifyAsync`, which in the Identity module runs `SignInManager.CheckPasswordSignInAsync(lockoutOnFailure: true)`, then `HttpContext.SignInAsync` with the returned claims. **204** and `Set-Cookie` on success. On failure **400 ProblemDetails** with code `SS-AUTH-001` for every case (wrong user, wrong password, deactivated, locked out), so the response can't be used to enumerate accounts. Lockout is logged on the server. |
| `POST /bff/logout` | authenticated | required | — | `SignOutAsync()`, **204**. A cross-site POST is stopped by antiforgery and SameSite, so another site can't sign the user out. |
| `GET /bff/me` | anonymous allowed | — | — | `CurrentUserResponse { IsAuthenticated, UserId, UserName, Roles[], BrandIds[], HomeWarehouseId }` from claims, with no database call. The client uses it to recover after SameSite=Strict navigations (1.3) and after a 401. |
| `GET /bff/antiforgery` | anonymous allowed | — | — | Returns a fresh request token for the current principal (fallback; normally the token comes with the prerendered page, see 1.5). |

`RememberMe` = persistent cookie. Without it the cookie lives for the browser session, still capped by the 8 h ticket expiry.

### 1.3 Cookie

`AddAuthentication("SmartSpare")` registers three schemes: the policy scheme `SmartSpare` (1.6) as the default, the cookie scheme below (`AddCookie`), and the internal JwtBearer scheme. Identity's own `AddIdentityCookies` is not used, because the cookie belongs to the BFF, which has no `SignInManager`. The cookie scheme is configured as:

| Setting | Value | Why |
|---|---|---|
| `Cookie.Name` | `__Host-SmartSpare` | The `__Host-` prefix makes the browser require Secure, `Path=/` and no `Domain`. |
| `HttpOnly` | true | Script can't read the cookie (NFR04, ADR 0006). |
| `SecurePolicy` | `Always` | |
| `SameSite` | `Strict` | ADR 0006. Cross-site requests never carry the cookie ([Work with SameSite cookies](https://learn.microsoft.com/aspnet/core/security/samesite?view=aspnetcore-10.0)). |
| `ExpireTimeSpan` / `SlidingExpiration` | 8 h / true | One work shift. |
| `LoginPath` | `/login` | For full page navigations to protected Blazor routes during prerendering. |
| `Events.OnRedirectToLogin` / `OnRedirectToAccessDenied` | **401** / **403** for `/api` and `/bff` paths | APIs must not redirect to HTML. |
| `Events.OnValidatePrincipal` | Every 5 minutes: `ICredentialVerifier.ValidateSessionAsync(userId, stamp)`. Revoked → reject (401); changed claims → renew the cookie | Replaces Identity's `SecurityStampValidator`, so the host and the gateway behave the same (2.5) |

**SameSite=Strict caveat.** The first top-level navigation from another site, such as a link in an e-mail, doesn't carry the cookie, so the prerendered page shows the user as signed out. The `/login` page first calls `GET /bff/me`. That request is same-site, so it carries the cookie. If the user turns out to be signed in, the page reloads the return URL straight away. This is covered by a Playwright check (section 9.4).

### 1.4 Authentication state in WASM

- Host: `AddRazorComponents().AddInteractiveWebAssemblyComponents().AddAuthenticationStateSerialization(o => o.SerializationCallback = ...)`.
- Client: `AddAuthorizationCore()`, `AddCascadingAuthenticationState()`, `AddAuthenticationStateDeserialization()` ([Blazor authentication and authorization](https://learn.microsoft.com/aspnet/core/blazor/security/?view=aspnetcore-10.0); [`AuthenticationStateSerializationOptions`](https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.components.webassembly.server.authenticationstateserializationoptions?view=aspnetcore-10.0)).
- The default serializes only name and role claims, and `SerializeAllClaims = true` would also send the Identity security-stamp claim into the page. We use a custom **`SerializationCallback`** that emits an explicit allow-list: `sub`, `name`, `role`, `ss:brand`, `ss:warehouse`. No personal data apart from the user name.
- After `/bff/login` or `/bff/logout` succeeds, the client calls `NavigationManager.NavigateTo(returnUrl, forceLoad: true)`. The server prerenders with the new principal, then serializes the new state and a new antiforgery token.
- A `DelegatingHandler` in the client treats **401** as "session ended" (cookie expired or security stamp rejected) and does a full reload to `/login?returnUrl=…`. It treats **403** as a UI error, shown in MudSnackbar.

### 1.5 Antiforgery (CSRF)

.NET 10 has no header-based automatic CSRF middleware (that arrives in .NET 11). The token-based `UseAntiforgery()` middleware only validates endpoints with antiforgery metadata, normally form-binding ones. It doesn't validate JSON endpoints or DELETE ([Prevent CSRF attacks, "Antiforgery with Minimal APIs"](https://learn.microsoft.com/aspnet/core/security/anti-request-forgery?view=aspnetcore-10.0#antiforgery-with-minimal-apis)). SameSite=Strict alone is not enough: it doesn't protect `/bff/login` against login CSRF (there is no session cookie yet), and some old or embedded browsers ignore it. So:

1. `AddAntiforgery(o => { o.HeaderName = "X-XSRF-TOKEN"; o.Cookie.Name = "__Host-SmartSpare-AF"; o.Cookie.SameSite = Strict; o.Cookie.SecurePolicy = Always; })`.
2. An **endpoint filter** (`AntiforgeryValidationFilter`, in `SmartSpare.Infrastructure.Http`) on the `/bff` and `/api` route groups. For POST, PUT, PATCH and DELETE it calls `IAntiforgery.ValidateRequestAsync`, and a failure becomes **400 ProblemDetails** `SS-AUTH-002`. The filter skips requests authenticated by a non-cookie scheme, which keeps bearer-token clients possible later (ADR 0012, #25, #26).
3. The client gets the request token from **`AntiforgeryStateProvider`**. Blazor persists it with the prerendered component state ([Call a web API from Blazor, "Antiforgery support"](https://learn.microsoft.com/aspnet/core/blazor/call-web-api?view=aspnetcore-10.0#antiforgery-support)). An `AntiforgeryHandler : DelegatingHandler` adds the header to unsafe requests. The token is bound to the principal, which is one more reason for the full reload after sign-in.
4. `app.UseAntiforgery()` stays in the pipeline (required by `MapRazorComponents`), after `UseAuthentication`/`UseAuthorization`.

### 1.6 Behind the gateway ([ADR 0016](../adr/0016-authentication-behind-the-gateway.md))

PR 3b builds the sign-in so that stage 4 only has to add a gateway host and HTTP clients, without touching module code:

| Piece | Built in 3b (single host) | Added in stage 4 (services) |
|---|---|---|
| `/bff` endpoints, cookie, antiforgery, sign-in rate limit | `SmartSpare.Bff`, mapped by `SmartSpare.Host` | The same `SmartSpare.Bff`, mapped by `SmartSpare.Gateway` |
| Credential and session checks | `ICredentialVerifier`, in-process implementation in the Identity module | HTTP client to `POST /internal/v1/identity/credential-checks` and `/session-checks`, policy `GatewayOnly` |
| Internal token | `InternalTokenIssuer` in `SmartSpare.Bff`: `JsonWebTokenHandler`, ES256, `iss=smartspare-gateway`, `aud=smartspare-internal`, the claims of 3.1 plus `sid` and `jti`, 2-minute lifetime. Key from user-secrets | Used by the YARP request transform for every proxied `/api/*` call. Key from Key Vault |
| Token validation | JwtBearer scheme with the public JWKS, `ValidAlgorithms = [ES256]`, issuer, audience, lifetime, 30 s clock skew | The same registration in every service host (JwtBearer only) |
| Default scheme | Policy scheme `SmartSpare`: `Authorization: Bearer` → JwtBearer, otherwise → cookie (single host only) | Services register JwtBearer only. The gateway registers the cookie only and accepts no bearer token from outside (ADR 0016 §4) |

Rules carried into the code from 3b on:
- Module endpoints and policies never name an authentication scheme.
- `ICurrentUser` and the session GUCs read the same claim types (`AppClaimTypes`) from either principal.
- Antiforgery runs only for cookie-authenticated requests (1.5).
- Contracts queries are called only while handling a user request (ADR 0015 §4).

---

## 2. ASP.NET Core Identity over the existing `iam` tables

### 2.1 Custom stores, not `IdentityUser` (decision S5)

| Option | Verdict |
|---|---|
| `AddIdentity<IdentityUser>().AddEntityFrameworkStores<…>()` with the default schema | Rejected. It needs `normalized_user_name`, `normalized_email`, `concurrency_stamp`, `email_confirmed`, `lockout_enabled`, a string `security_stamp`, and `AspNetUserClaims`/`Logins`/`Tokens` tables. That changes a schema already covered by 97 SQL tests, the CHECKs and the audit trigger. |
| `AppUser : IdentityUser<Guid>` mapped onto `iam.app_user` | Rejected. `Domain/` may not depend on framework packages (ADR 0013, architecture tests), and `IdentityUser` brings public setters for every field. |
| **`AddIdentityCore<AppUser>()` + custom `IUserStore` implementations in `Infrastructure/Identity`** | **Chosen.** Identity's managers work with any `TUser : class` and only the store interfaces we implement ([Custom storage providers for ASP.NET Core Identity](https://learn.microsoft.com/aspnet/core/security/authentication/identity-custom-storage-providers?view=aspnetcore-10.0)). The store translates between Identity's calls and the aggregate's behaviour methods. |

Registration inside `IdentityModuleInstaller.AddServices`. The module uses this machinery only behind `ICredentialVerifier` (1.2). It never issues cookies.

```csharp
services.AddIdentityCore<AppUser>(o => { /* 2.4–2.6 */ })
        .AddUserStore<AppUserStore>()
        .AddClaimsPrincipalFactory<AppUserClaimsPrincipalFactory>()
        .AddSignInManager()
        .AddDefaultTokenProviders();   // only for UpdateSecurityStampAsync / future password reset
services.AddScoped<IUserConfirmation<AppUser>, ActiveUserConfirmation>();
```

`AppUserStore` implements `IUserStore<AppUser>`, `IUserPasswordStore<AppUser>`, `IUserSecurityStampStore<AppUser>`, `IUserLockoutStore<AppUser>` and `IUserRoleStore<AppUser>` (read-only in stage 3). `RoleManager` and `IRoleStore` are not used: roles are seeded reference data, and claims come from the principal factory.

### 2.2 Column mapping

| Identity concept | `iam` column | Notes |
|---|---|---|
| `Id` | `id uuid` | |
| `UserName` / normalized name | `user_name citext` | `FindByNameAsync(normalized)` compares on `citext`, which is case-insensitive, so no normalized column is needed. The CHECK's regular expression is case-insensitive on `citext`, so it alone would let upper-case letters through. `UserName.Create` therefore stores the canonical lower-case form (equality stays case-insensitive, like `citext`), and PR 3c tightens the CHECK with `user_name = lower(user_name)` (#69). A `LowerInvariantLookupNormalizer` makes the normalized value equal to the stored one. |
| Email | `email citext` | Not used for sign-in in stage 3 (`IUserEmailStore` not implemented). |
| `PasswordHash` | `password_hash text` | V3 format, protected by `ck_app_user_password_hash_format`. |
| `SecurityStamp` | `security_stamp uuid` | See 2.5. |
| `AccessFailedCount` | `failed_login_count smallint` | CHECK 0..100; reset on success and when lockout starts. |
| `LockoutEnd` | `lockout_end timestamptz` | |
| `LockoutEnabled` | — | Always true for active users. Returned by the store with no column. |
| Roles | `iam.user_role` → `iam.role.code` | |
| Brand claims | `iam.user_brand.brand_id` | |
| Warehouse claim | `home_warehouse_id smallint` | |
| Sign-in allowed | `is_active`, `anonymised_at` | `IUserConfirmation` (2.6). |
| Last login | `last_login_at` | Set on successful sign-in (excluded from the audit diff by the trigger). |

The API login (`smartspare_api` → `app_write`) already has exactly the column privileges this needs: `SELECT` on `iam.app_user`, and `UPDATE (password_hash, security_stamp, last_login_at, failed_login_count, lockout_end, full_name, phone)` ([db/sql/identity/0002_grants.sql](../../db/sql/identity/0002_grants.sql)). EF Core updates only changed columns, so no grant change is needed. Creating users needs `app_admin` and comes with user administration (#46).

Sign-in writes run in a transaction where `app.user_id` is the **user who is signing in**, with `app.reason = 'sign-in'`, so the audit row for a lockout or a password rehash names the right actor. `ICurrentUser` gets an explicit override for the sign-in handler (section 6).

### 2.3 Password hash compatibility (#7)

- The seeded hashes start with `AQAAAAIAAYag`: format marker `0x01` (V3), PRF `2` = HMAC-SHA512, iteration count `0x000186A0` = 100,000. These are the defaults of `PasswordHasher<TUser>` in .NET 10 (`CompatibilityMode = IdentityV3`, `IterationCount = 100000`; [Configure ASP.NET Core Identity, "Password Hasher options"](https://learn.microsoft.com/aspnet/core/security/authentication/identity-configuration?view=aspnetcore-10.0#password-hasher-options)).
- **Work factor (PR 3a).** 100,000 is below the current OWASP recommendation for PBKDF2-HMAC-SHA512, which is **220,000 iterations** ([OWASP Password Storage Cheat Sheet, "PBKDF2"](https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html#pbkdf2)). PR 3a sets `Configure<PasswordHasherOptions>(o => o.IterationCount = 220_000)`, in one place shared by the host and `set-demo-passwords`. New hashes then start with `AQAAAAIAA1tg`. Existing hashes keep working: `VerifyHashedPassword` reads the algorithm and iteration count from the hash itself. When the stored count is lower than the configured one, it returns `PasswordVerificationResult.SuccessRehashNeeded`, so `UserManager` rehashes on the next successful sign-in. That write uses the `password_hash` grant and rotates the stamp. This is the OWASP approach of upgrading the work factor "when the user next authenticates".
- The T03 SQL assertion changes from the literal prefix `AQAAAAIAAYag` to "V3, HMAC-SHA512, iteration count ≥ 220,000", decoded from the first 13 bytes of the hash. DATABASE_DESIGN §8.4 and the compliance matrix (100,000 iterations) are updated in the same PR.
- An integration test pins compatibility: a user seeded with a 100,000-iteration hash signs in, and the stored hash is then a 220,000-iteration hash. A unit test checks that `PasswordHasher<AppUser>` output decodes to V3 / SHA512 / 220,000.
- **#7:** T001 no longer contains literal hashes. Because `ck_app_user_active_has_password` requires a hash for active users, T001 stores a random placeholder per user: a valid V3 header (marker `0x01`, HMAC-SHA512, 220,000 iterations, salt length 16) with random salt and subkey from `pgcrypto`, so the format CHECK and the header test pass on plain T001 but no password verifies. A new `MigrationRunner set-demo-passwords` command reads `SMARTSPARE_DEMO_USER_PASSWORD`, hashes it with the shared `PasswordHasherOptions` and updates the demo users (by the fixed ids of T001) as `smartspare_migrator`. `db/scripts/load_testdata.sh` runs T001 and then the command; `tools/verify_database.sh` runs T001 with the container's psql and the command from the host with a throw-away password. `.env.example` gets a placeholder, and the README states that any password works for the demo users.
- **#33:** the AppHost gets a resource command "Load demo data" on the database resource (`WithCommand`). It runs T001 and `set-demo-passwords` with the connection and parameters Aspire already holds, so nothing has to be copied from the dashboard.

### 2.4 Lockout

`o.Lockout.AllowedForNewUsers = true`, `MaxFailedAccessAttempts = 5`, `DefaultLockoutTimeSpan = 15 min`. `CheckPasswordSignInAsync(..., lockoutOnFailure: true)` increments `failed_login_count`, sets `lockout_end` and resets the counter, so the value never approaches the CHECK's 100. A locked-out user gets the same generic 400 response (1.2). Unlocking early is an administrator action (#46).

**One source of truth (PR 3b).** ASP.NET Core Identity runs the lockout algorithm; the domain does not duplicate it. `AppUserStore` maps `IncrementAccessFailedCount`, `SetLockoutEndDate` and `ResetAccessFailedCount` onto the `AppUser` behaviour methods `RecordFailedSignIn()`, `SetLockoutEnd(end)` and `ResetFailedSignIns()`. The policy values come from one place, the domain's `LockoutPolicy.Default` (5 attempts, 15 minutes, validated against the CHECK's limit), which the Identity installer copies into `IdentityOptions.Lockout`.

**Clock.** `UserManager<TUser>` in ASP.NET Core 10 compares `lockout_end` with `DateTimeOffset.UtcNow`; it takes no `TimeProvider` (its constructor has none, and `Microsoft.Extensions.Identity.Core` 10.0 doesn't reference the type). Lockout therefore runs on the system clock. `TimeProvider` is used for the cookie and the session checks (1.3, 2.5) and for `last_login_at`.

### 2.5 Security stamp validation

- The BFF's cookie `OnValidatePrincipal` (1.3) checks the session every **5 minutes** through `ICredentialVerifier.ValidateSessionAsync`. It replaces Identity's `SecurityStampValidator` (default interval 30 minutes), so the same code runs in the host and the gateway. Each check costs one database query per user per interval ([Blazor authentication and authorization, "Sign out for ASP.NET Core Identity"](https://learn.microsoft.com/aspnet/core/blazor/security/?view=aspnetcore-10.0); [Configure Identity, "ISecurityStampValidator and SignOut everywhere"](https://learn.microsoft.com/aspnet/core/security/authentication/identity-configuration?view=aspnetcore-10.0#isecuritystampvalidator-and-signout-everywhere)).
- `security_stamp` is a `uuid`, while `UserManager` generates Base32 strings. `SetSecurityStampAsync` therefore ignores the proposed string and calls `AppUser.RotateSecurityStamp()`, which stores a new `Guid`. `GetSecurityStampAsync` returns the stored Guid as `"N"` text. The validator only compares the claim with the stored value, so the stamp only has to change. It doesn't have to match Identity's format.
- The stamp rotates on password change, role or brand change, deactivation and anonymisation. Anonymisation already does this in SQL: `iam.anonymise_user` sets `security_stamp = gen_random_uuid()`. In each case the user's cookies stop working within 5 minutes in the single host. Behind the gateway it takes up to about 7 minutes, because of the 2-minute internal token (ADR 0016 §3). The principal is then rebuilt, so new roles take effect within the same time.

### 2.6 Deactivated and anonymised users

`o.SignIn.RequireConfirmedAccount = true` together with `ActiveUserConfirmation : IUserConfirmation<AppUser>` returning `user.IsActive && user.AnonymisedAt is null`. `SignInManager.CanSignInAsync` (called by `CheckPasswordSignInAsync`) then refuses those users, and the BFF's session check refuses their existing cookies, because both deactivation and anonymisation rotate the stamp. This uses only documented extension points, with no `SignInManager` subclass.

### 2.7 Password policy

Applies to new passwords only (administration, #46): `RequiredLength = 12`, `RequireNonAlphanumeric = false`, `RequireUppercase = false`, `RequireDigit = false`, `RequiredUniqueChars = 4`. Length matters more than character classes. The seeded demo password still verifies, because policy isn't checked at sign-in.

---

## 3. Claims and authorization

### 3.1 Claims (built by `AppUserClaimsPrincipalFactory`)

| Claim type | Value | Count |
|---|---|---|
| `sub` (`ClaimTypes.NameIdentifier`) | `app_user.id` | 1 |
| `name` | `user_name` | 1 |
| `role` | `iam.role.code`, e.g. `BRAND_MANAGER` | 0..n |
| `ss:brand` | `ref.brand.id` from `iam.user_brand` | 0..n |
| `ss:warehouse` | `home_warehouse_id` | 0..1 |
| Identity security stamp | added by Identity, **never serialized to the browser** | 1 |

Claim type and role names are constants in `SmartSpare.ApiContracts.Authorization` (S14), shared by host and client.

### 3.2 Policies

Policies are claims-only (ADR 0012) and registered by one shared method, `AppPolicies.Register(AuthorizationOptions)`, which the host and the client both call.

**Parity rule.** A policy that fronts a SQL business function allows **exactly** the roles of that function's `util.require_role(...)` call, no more and no fewer. Each such policy is named after the action and lists its functions below. Two layers that disagree would either reject valid users in the database (SS301) or let the API accept requests the database refuses, so the two must not drift. A test enforces it (9.1). A new SQL function with a different role list gets its own policy rather than reusing a "close enough" one. Brand scope (3.3) and warehouse match are resource checks on top of the role gate. They never widen it.

| Policy | Roles | Fronts (`util.require_role` in `db/sql/`) | Resource check |
|---|---|---|---|
| (fallback) | any authenticated user | — | — |
| `Administration` | `ADMINISTRATOR` | `iam.anonymise_user` | — |
| `PlanInventory` | `BRAND_MANAGER`, `INVENTORY_PLANNER` | `inventory.flag_exceptional_demand`, `procurement.submit_purchase_order`, `procurement.export_purchase_order` | — |
| `ApproveTransfer` | `BRAND_MANAGER`, `INVENTORY_PLANNER` | `inventory.approve_transfer` | brand scope (3.3) |
| `CancelTransfer` | `BRAND_MANAGER`, `INVENTORY_PLANNER`, `ADMINISTRATOR` | `inventory.cancel_transfer` | brand scope (3.3) |
| `MoveStock` | `WAREHOUSE_KEEPER`, `ADMINISTRATOR` | `inventory.dispatch_transfer`, `inventory.receive_transfer`, `procurement.receive_purchase_order_line` | warehouse match (below) |
| `RecordServiceWork` | `SERVICE_ENGINEER`, `ADMINISTRATOR` | `fleet.log_engine_hours`, `fleet.complete_service_order` | — |
| `ManageServiceOrders` | `SERVICE_ENGINEER`, `BRAND_MANAGER`, `ADMINISTRATOR` | `fleet.create_service_order_draft`, `fleet.cancel_service_order` | — |
| `ApprovePurchaseOrder` | `BRAND_MANAGER`, `SPONSOR` | `procurement.approve_purchase_order` | brand scope (3.3) |
| `CancelPurchaseOrder` | `BRAND_MANAGER`, `SPONSOR`, `ADMINISTRATOR` | `procurement.cancel_purchase_order` | brand scope (3.3) |
| `DecideRecommendation` | `BRAND_MANAGER`, `INVENTORY_PLANNER` | `forecast.decide_recommendation` (and its event-based successor, MODULE_MAPPING 3.4) | brand scope (3.3) |
| `ReadAudit` | `SPONSOR`, `ADMINISTRATOR` | — (read only) | — |

Notes:

- Policies that front no function (fallback, `ReadAudit`, and the read policies stages 5–9 add) are outside the parity rule. Every read policy is **never brand-scoped** (3.3 rule 1).
- **Warehouse match** for `MoveStock` mirrors SS302 in `dispatch_transfer` / `receive_transfer`: a user with an `ss:warehouse` claim may only dispatch from or receive into that warehouse; a user without one (an administrator) is not restricted. The role gate is therefore role-only. The earlier draft required `ss:warehouse` in the policy, which would have locked out `ADMINISTRATOR`, whom SQL allows.
- `RecordServiceWork` and `ManageServiceOrders` replace the earlier single `ServiceMachines` (`SERVICE_ENGINEER` only), which didn't match the SQL role lists.
- The matrix is fixed for stage 3. Stages 5–9 add rows next to the endpoints that need them, under the same parity rule.

### 3.3 Brand scope (resource-based)

**Business rule** (set by the product owner on 24 November 2026; it refines the `iam.user_brand` description "brand manager ↔ brands"):

1. **Reads are not brand-scoped.** Every role allowed to view stock, transfers, purchase orders or recommendations sees all brands, `BRAND_MANAGER` included.
2. **Brand scope applies only to approval and decision actions:** approve or cancel a transfer, approve or cancel a purchase order, accept or reject a recommendation. Brand lives on `catalog.part`, not on the document, so a `BRAND_MANAGER` may act only if the brands of **all** lines of the document are in their `ss:brand` claims. A single line of another brand → **403**.
3. **An unscoped role overrides the scope.** If the user also holds a role that grants the same action without brand scope, that role decides. For example, a user with `BRAND_MANAGER` + `SPONSOR` may approve any purchase order as `SPONSOR`.
4. **All other roles are not brand-scoped.**

**Model.**

```csharp
// SmartSpare.Infrastructure.Security
public interface IBrandScoped
{
    IReadOnlySet<short> BrandIds { get; }   // distinct brands of all lines of the document
}

public sealed record BrandScopedActionRequirement(
    IReadOnlySet<string> UnscopedRoles,     // e.g. { SPONSOR } for ApprovePurchaseOrder
    string ScopedRole)                       // BRAND_MANAGER
    : IAuthorizationRequirement;

internal sealed class BrandScopedActionHandler
    : AuthorizationHandler<BrandScopedActionRequirement, IBrandScoped> { ... }
```

The handler evaluates in a fixed order:

1. If the user has **any unscoped role** of the requirement → succeed. Brands are not looked at.
2. Else, if the user has the scoped role **and** `BrandIds` is non-empty **and** every id in `BrandIds` is in the user's `ss:brand` claims → succeed.
3. Otherwise → not satisfied (403).

An empty `BrandIds` (a document without lines) does not pass step 2. The handler denies by default, and the domain rejects empty documents anyway.

Five named requirements are registered next to the policies of 3.2. Each one's unscoped roles plus its scoped role equal the roles of the policy it refines, and therefore of the SQL function (parity rule, 3.2):

| Requirement | Unscoped roles | Scoped role |
|---|---|---|
| `ApproveTransferScope` | `INVENTORY_PLANNER` | `BRAND_MANAGER` |
| `CancelTransferScope` | `INVENTORY_PLANNER`, `ADMINISTRATOR` | `BRAND_MANAGER` |
| `ApprovePurchaseOrderScope` | `SPONSOR` | `BRAND_MANAGER` |
| `CancelPurchaseOrderScope` | `SPONSOR`, `ADMINISTRATOR` | `BRAND_MANAGER` |
| `DecideRecommendationScope` | `INVENTORY_PLANNER` | `BRAND_MANAGER` |

**Use in endpoints.** The endpoint first passes its policy (3.2). The handler then loads the document and builds the `IBrandScoped` view from the brands of its lines, read from module-local data (Inventory: `catalog.part`; Procurement and Forecasting: their local part copies with brand, ARCHITECTURE_DECISIONS section 4) or through an `Inventory.Contracts` query. Brand ids never come from the request body. It then calls `IAuthorizationService.AuthorizeAsync(user, document, requirement)` and returns **403 ProblemDetails** on failure, before any state change. Stage 3 proves the handler with test endpoints in the integration test host. The first real uses are transfer approval and cancellation (stage 5), purchase-order approval and cancellation (stage 7) and recommendation decisions (stage 8).

**Database.** The SQL business functions repeat the **role** check (`util.require_role`, section 6), so a bug in a policy can't bypass that second line of defence (ARCHITECTURE_DECISIONS section 7). They do **not** enforce brand scope. A bug in the resource check above would not be caught by the database. A database-level brand check is tracked as #53.

---

## 4. Rate limiting on sign-in

- `AddRateLimiter` with a named policy `login`: `RateLimitPartition.GetFixedWindowLimiter` partitioned by `HttpContext.Connection.RemoteIpAddress`, **10 requests per minute**, no queue. `RejectionStatusCode = 429`, and `OnRejected` copies `MetadataName.RetryAfter` into `Retry-After` ([Rate limiting middleware](https://learn.microsoft.com/aspnet/core/performance/rate-limit?view=aspnetcore-10.0)). `/bff/login` uses `.RequireRateLimiting("login")`, and `app.UseRateLimiter()` runs after routing.
- Account-level brute force is handled by lockout (2.4), so partitioning by user name, which would mean reading the body in the limiter, isn't needed.
**Known limits:**

- **Per-instance limiter.** The limiter is in memory, so each replica has its own budget. Accepted while the app runs as one instance (#49).
- **Client IP behind a proxy.** Behind the gateway's Container Apps ingress, `RemoteIpAddress` is the platform's proxy. The client IP must come from `X-Forwarded-For` via the forwarded-headers middleware, trusted only from known proxies; otherwise every user shares one partition and one user can block sign-in for everyone. The docs also warn that IP partitions can be spoofed ([Rate limiter samples](https://learn.microsoft.com/aspnet/core/performance/rate-limit-samples?view=aspnetcore-10.0)). **#48 must be done before the first Azure deployment** (stage 4).
- **Lockout as a denial-of-service vector.** Anyone who knows or guesses a user name can lock that account for 15 minutes with five wrong passwords, repeatedly. Accepted for now: the user names aren't published, the IP rate limit slows the attack, a lockout ends on its own, and an administrator can unlock early (#46). Detecting lockout spikes is part of the security event log (#51). MFA (#50) would let lockout be relaxed for second-factor failures only.

---

## 5. Data Protection keys in PostgreSQL (#22)

- Package `Microsoft.AspNetCore.DataProtection.EntityFrameworkCore` (10.0.x, central version). `IdentityDbContext` implements `IDataProtectionKeyContext` (`DbSet<DataProtectionKey> DataProtectionKeys`), mapped to **`iam.data_protection_keys`** (`id`, `friendly_name`, `xml`). The table is created by a new Identity migration ([Key storage providers, "Entity Framework Core"](https://learn.microsoft.com/aspnet/core/security/data-protection/implementation/key-storage-providers?view=aspnetcore-10.0#entity-framework-core)).
- Grants (SQL part of the migration, ADR 0004): `SELECT, INSERT` on the table and `USAGE` on its sequence to `app_write`, nothing to `app_read`. Key XML is secret material. The audit trigger is **not** attached.
- **The host owns the configuration (PR 3c).** It calls `AddDataProtection().SetApplicationName("SmartSpare")`, decides where keys live and how they are protected. For the storage it calls `IdentityModuleInstaller.PersistDataProtectionKeys(IDataProtectionBuilder)`, which wraps `PersistKeysToDbContext<IdentityDbContext>()`: `IdentityDbContext` stays internal, and an architecture test allows this installer method explicitly. The Identity installer's `AddServices` only requires Data Protection (`AddDataProtection()`, for the token providers). Protection at rest ([Configure ASP.NET Core Data Protection](https://learn.microsoft.com/aspnet/core/security/data-protection/configuration/overview?view=aspnetcore-10.0)):
  - Azure: `ProtectKeysWithAzureKeyVault` with the Managed Identity. Host only, because modules may not reference cloud SDKs (ADR 0012, architecture tests). The single host is not deployed, so this switch stays configuration-driven and off. In production the cookie lives in the gateway, whose key ring goes to Blob Storage, protected with Key Vault, in stage 4 (ADR 0016 §6).
  - Other environments: `ProtectKeysWithCertificate` when `DataProtection:CertificateThumbprint`/`Path` is configured.
  - Local development: unprotected keys (accepted in #22).
- The Data Protection key table is technical state, not a domain aggregate. ADR 0013 rule 3 lets it stay a framework class.

---

## 6. Session context and database checks (#11)

- `ICurrentUser` (BuildingBlocks) gains `IReadOnlyCollection<string> Roles` and `short? HomeWarehouseId`. A new `ClaimsCurrentUser` in `SmartSpare.Infrastructure` reads the claims through `IHttpContextAccessor`, whichever scheme authenticated the request (cookie or internal token, 1.6), and replaces `NoCurrentUser` in the host. Background jobs keep `NoCurrentUser`. The sign-in handler uses a scoped override for the user who is signing in (2.2).
- `SessionContextInterceptor` sets four transaction-local GUCs: `app.user_id`, `app.reason`, **`app.user_roles`** (comma-separated role codes) and **`app.home_warehouse_id`**, all with `set_config(..., true)`. The trust model is unchanged ([MODULE_MAPPING 3.6](../database/MODULE_MAPPING.md)).
- SQL, applied through new migrations of the owning modules (ADR 0004):
  - Platform: `util.require_role` checks `app.user_roles` instead of calling `iam.user_has_role` (X1).
  - Inventory: `dispatch_transfer` and `receive_transfer` read `app.home_warehouse_id` instead of `iam.app_user` (X2).
  - Identity: `iam.v_app_user_masked` returns `home_warehouse_id` instead of the code (X18). The view is removed from the transitional Reporting script (`9000_transitional_cross_module.sql`, MODULE_MAPPING 3.2) and created by a new Identity migration (`ModuleLocalMaskedUserView`) with `DROP VIEW IF EXISTS` + `CREATE VIEW`, so a database built before PR 3c is upgraded too. No Reporting migration: Reporting runs after Identity in the migration order, so a Reporting migration that dropped the view would drop the Identity one. `pg_depend` showed no dependent objects (no CASCADE).
  - `db/tests/_helpers.sql`: `pg_temp.act_as` also sets `app.user_roles` and `app.home_warehouse_id`.
- Checks: the SQL assertions stay green on the EF-built database (`tools/verify_database.sh`; 97, plus 5 new ones in PR 3c = 102). `tools/schema_diff.sh` gets allow-list entries for the changed function bodies and view columns, each explained in the PR.

---

## 7. Identity domain model (ADR 0013, #38)

`src/Modules/Identity/SmartSpare.Modules.Identity/Domain/`:

| Type | Kind | Behaviour |
|---|---|---|
| `AppUser : AggregateRoot<Guid>` | aggregate | Private state. `RecordSuccessfulSignIn(now)`; `RecordFailedSignIn()`, `SetLockoutEnd(end)` and `ResetFailedSignIns()`, driven by Identity's lockout algorithm (2.4); `ChangePasswordHash(hash)` (rotates stamp), `RotateSecurityStamp()`, `Deactivate()` (rotates stamp, raises `UserDeactivated`), `AssignRole` / `RevokeRole` and `AssignBrand` / `RemoveBrand` (rotate stamp). Invariants mirror the CHECKs: an active user has a password, an anonymised user is inactive, the failure counter stays in 0..100. |
| `UserRoleAssignment : Entity<…>` | child entity | `RoleId`, `GrantedAt`, `GrantedBy` |
| `UserBrandAssignment` | child entity / owned | `BrandId` |
| `Role : Entity<short>` | reference entity | Read-only, seeded (`Code`, `Name`). |
| `UserName`, `EmailAddress` | value objects | Validation equal to `ck_app_user_user_name` / `ck_app_user_email`. |

- EF configurations in `Infrastructure/Persistence/Configurations` map these directly: private constructors, backing fields for the collections, value converters for the value objects, and `xmin` concurrency on `AppUser`. The Identity classes in `PersistenceModel.cs` (`AppUser`, `Role`, `UserBrand`, `UserRole`) are deleted. `RefBrand` stays as the read-only FK principal.
- The remap itself must not change the model: apart from `xmin`, `MigrationRunner check-model` reports no pending changes. Mapping `xmin` needs **one schema-free migration** (`AddAppUserXminConcurrency`): Npgsql emits no DDL for the system column, so up and down only touch the migrations history. The only Identity schema changes in stage 3 are deliberate migrations in PR 3c: the `data_protection_keys` table (section 5) and the SQL for #11 (section 6).
- **Rule for later aggregates (stages 5–9):** mapping `xmin` as the concurrency token of an aggregate is always a schema-free migration of the same kind, reviewed with its generated SQL (history insert only).
- Only stage 3 needs `UserDeactivated` as a domain event. Publishing it as an integration event waits for a consumer (#46).

---

## 8. Web client

### 8.1 Hosting

The Blazor Web App template has a server project and a `.Client` project. `SmartSpare.Host` takes the server role: `App.razor`, `Routes` with global `@rendermode="InteractiveWebAssembly"`, `MapRazorComponents<App>().AddInteractiveWebAssemblyRenderMode().AddAdditionalAssemblies(typeof(Client._Imports).Assembly)`, `UseStaticFiles`/`MapStaticAssets`. Prerendering stays on: it carries the auth state and the antiforgery token (1.4, 1.5) and gives a faster first paint. Components avoid work that must not run twice during prerendering.

The host pipeline order is: `UseExceptionHandler` → `UseHsts` (non-Development) → `UseHttpsRedirection` → security headers / CSP → `UseStaticFiles`/`MapStaticAssets` → `UseRouting` → `UseRateLimiter` → `UseAuthentication` → `UseAuthorization` → `UseAntiforgery` → endpoints.

### 8.2 `src/Web/SmartSpare.Web.Client` layout

```
SmartSpare.Web.Client/
├─ Program.cs                    # MudBlazor, auth state deserialization, policies, localization, typed clients
├─ _Imports.razor
├─ Routes.razor                  # AuthorizeRouteView, NotAuthorized → RedirectToLogin
├─ Layout/                       # MainLayout (MudLayout, AppBar, NavMenu by policy), LoginLayout
├─ Shared/                       # RedirectToLogin, ProblemDetails → message mapping, StatusChip
├─ Http/                         # AntiforgeryHandler, UnauthorizedHandler, ApiClientBase
├─ Features/
│  ├─ Identity/                  # Login.razor, IdentityClient (/bff), UserMenu
│  ├─ Inventory/  Fleet/  Procurement/  Forecasting/  Reporting/   # empty placeholders with a home page tile each
├─ Resources/                    # SharedResource.resx (en-US)
└─ wwwroot/
```

The client references **only `SmartSpare.ApiContracts`** (existing architecture rule). An architecture test checks that it doesn't reference any `SmartSpare.Modules.*` assembly.

### 8.3 MudBlazor 9.12

- Package `MudBlazor` **9.12.0** (latest 9.x on NuGet; 9.x fully supports .NET 10). Central version in `Directory.Packages.props`.
- Setup per the [installation guide](https://mudblazor.com/getting-started/installation): `builder.Services.AddMudServices(...)` in the client, `@using MudBlazor` in `_Imports.razor`, the MudBlazor CSS and JS references in `App.razor`, and `MudThemeProvider`, `MudPopoverProvider`, `MudDialogProvider` and `MudSnackbarProvider` in `MainLayout`. Snackbar position and a theme with WCAG 2.1 AA contrast (section 6 of the architecture).
- The sign-in page is a `MudForm` with `MudTextField` (user name, password with `InputType.Password`, `autocomplete="username"` / `"current-password"`), `MudCheckBox` (remember me) and a `MudButton` with a busy state. Validation messages come from the FluentValidation `LoginRequestValidator` in ApiContracts. The server runs the same validator.

### 8.4 ApiContracts (decision S14)

`SmartSpare.ApiContracts` gets `Identity/LoginRequest`, `Identity/CurrentUserResponse`, `Identity/LoginRequestValidator`, plus `Authorization/AppRoles`, `AppClaimTypes`, `AppPolicies.Register`, and `ProblemCodes` (`SS-AUTH-001…`). It references `FluentValidation` and `Microsoft.AspNetCore.Authorization` only. Module implementation projects may reference it so endpoints bind and return the shared DTOs. ApiContracts must never reference a module (existing test), and a new test limits its framework dependencies to that list.

### 8.5 Typed HTTP clients

One typed `HttpClient` per module (`IdentityClient`, later `InventoryClient`, …), registered with `AddHttpClient<T>(c => c.BaseAddress = new(builder.HostEnvironment.BaseAddress))` and the `AntiforgeryHandler` + `UnauthorizedHandler` chain. Same origin, so the cookie is sent automatically, with no CORS and no tokens. Errors: `ApiClientBase` reads `application/problem+json` into a `Result<T>`-like `ApiResult<T>`, and pages show `ProblemDetails` codes SS1xx–SS4xx and SS-AUTH-xxx as localized messages in MudSnackbar ([Call a web API from Blazor](https://learn.microsoft.com/aspnet/core/blazor/call-web-api?view=aspnetcore-10.0)).

### 8.6 Localization

`<BlazorWebAssemblyLoadAllGlobalizationData>true</BlazorWebAssemblyLoadAllGlobalizationData>`, `AddLocalization()`, `IStringLocalizer<SharedResource>` and per-feature resx files. The culture is set at startup (`CultureInfo.DefaultThreadCurrentCulture`/`UICulture`) and the host uses `UseRequestLocalization` with the same list for prerendering ([Blazor globalization and localization](https://learn.microsoft.com/aspnet/core/blazor/globalization-localization?view=aspnetcore-10.0)). Stage 3 ships **en-US only**. All UI text goes through the localizer from the first page, so adding a culture is a translation task (#47).

### 8.7 Security headers

The host sends `Content-Security-Policy` as a header, built from the docs' client-side baseline ([Enforce a CSP for Blazor](https://learn.microsoft.com/aspnet/core/blazor/security/content-security-policy?view=aspnetcore-10.0)):

```
default-src 'self';
script-src 'self' 'wasm-unsafe-eval' 'sha256-<import map>';
style-src 'self' 'unsafe-inline';
img-src 'self' data:;
connect-src 'self';
object-src 'none';
base-uri 'self';
frame-ancestors 'none';
upgrade-insecure-requests
```

**Why `style-src 'unsafe-inline'`.** MudBlazor sets inline `style` attributes at runtime (positions of popovers, menus and tooltips, widths, colours from the theme). Hashes and nonces in `style-src` cover `<style>` elements, but browsers apply them to `style` attributes only together with `'unsafe-hashes'`, and only for fixed values. These values are computed at runtime, so no hash can match them. Without `'unsafe-inline'` the components would render broken.

**Accepted risk.** An attacker who can inject markup could add CSS. That allows defacement, UI redressing inside the page, or reading attribute values through CSS selectors that load background images; the last is limited by `img-src 'self' data:` and `connect-src 'self'`. Injected CSS cannot run script. `script-src` stays strict, with no `'unsafe-inline'` and no `'unsafe-eval'`, and Blazor's rendering encodes text, so the main XSS defence is unchanged. If MudBlazor moves away from inline styles, or browsers allow per-attribute style nonces, we revisit this.

The host also sends `X-Content-Type-Options: nosniff` and `Referrer-Policy: strict-origin-when-cross-origin`, and keeps `UseHsts` outside Development. An integration test asserts the headers on `/`, including that `script-src` contains neither `'unsafe-inline'` nor `'unsafe-eval'`.

---

## 9. Test plan

All new tests follow the pyramid (ARCHITECTURE_DECISIONS section 11). Testcontainers tests are `Category=Integration`.

### 9.1 Unit (`tests/SmartSpare.Modules.Identity.UnitTests`, new)

- `AppUser`: sign-in success sets `last_login_at` (refused for inactive users). Failed sign-ins are counted one by one and never exceed the CHECK's 100; lockout end and counter reset are plain state changes, because Identity decides when to lock (2.4). `LockoutPolicy` rejects values outside the CHECK's range. Deactivation rotates the stamp and raises `UserDeactivated`. Role or brand change rotates the stamp. Invariants match the CHECKs (active ⇒ password, anonymised ⇒ inactive, counter range).
- `UserName` / `EmailAddress` accept and reject the same values as the SQL CHECKs (shared test cases).
- `AppUserStore` security-stamp translation (Base32 proposal → new Guid). `ActiveUserConfirmation`.
- `AppUserClaimsPrincipalFactory`: claim set, and no personal data.
- `PasswordHasher<AppUser>` output decodes to V3 / HMAC-SHA512 / 220,000 iterations (prefix `AQAAAAIAA1tg`). A 100,000-iteration hash verifies with `SuccessRehashNeeded`.
- `LoginRequestValidator` (ApiContracts).
- `AppPolicies` evaluated with `DefaultAuthorizationService` for each role. Read policies never consider brands.
- **Policy ↔ SQL role parity** (`PolicySqlRoleParityTests` in `tests/SmartSpare.ArchitectureTests`, no database). The test reads the repository like the other architecture tests:
  - It holds the table of 3.2, mapping each policy to the SQL functions it fronts.
  - For each function it extracts the role list from the `util.require_role(...)` call in its `CREATE FUNCTION` body under `db/sql/`, and compares it as a set with the policy's roles in `AppPolicies`.
  - For each brand-scoped policy it also checks that the requirement's unscoped roles plus its scoped role equal the policy's roles.
  - It fails when a function in `db/sql/` calls `util.require_role` but isn't mapped to any policy, so a new SQL function can't slip in unnoticed.
  - The expected rows are data (`[Theory]` with `MemberData`), so the test output names the policy, the function and the differing roles.
- `BrandScopedActionHandler`, for each of the three requirements:
  - only own brands → succeed;
  - one foreign line among own lines → fail;
  - empty `BrandIds` → fail;
  - `BRAND_MANAGER` without `ss:brand` claims → fail;
  - unscoped role alone (e.g. `SPONSOR`) with foreign brands → succeed;
  - `BRAND_MANAGER` + `SPONSOR` with foreign brands on `ApprovePurchaseOrderScope` → succeed;
  - `BRAND_MANAGER` + `SPONSOR` with foreign brands on `ApproveTransferScope` → fail, because `SPONSOR` doesn't grant that action;
  - `INVENTORY_PLANNER` with foreign brands on `ApproveTransferScope` → succeed (unscoped);
  - `ADMINISTRATOR` with foreign brands on `CancelTransferScope` and `CancelPurchaseOrderScope` → succeed (unscoped);
  - `BRAND_MANAGER` with a mixed-brand document on `CancelTransferScope` and `CancelPurchaseOrderScope` → fail;
  - a role outside the requirement (e.g. `WAREHOUSE_KEEPER`) → fail.

### 9.2 Integration (`tests/SmartSpare.Host.IntegrationTests`, Testcontainers PostgreSQL 17 + `WebApplicationFactory<Program>`)

| Test | Expectation |
|---|---|
| Sign-in with a seeded V3 user | 204 and `Set-Cookie` with `__Host-SmartSpare`, `HttpOnly`, `Secure`, `SameSite=Strict`, `Path=/`, no `Domain`. Then `/bff/me` returns the roles and brands. `last_login_at` is set. |
| Wrong password, unknown user, deactivated user | Identical 400 `SS-AUTH-001` bodies. |
| Lockout | The 5th wrong password locks the user for 15 minutes and resets the counter; the lockout is audited with the user signing in as actor and the reason `sign-in`. The correct password is then still rejected. The test then moves `lockout_end` into the past in the database (the same effect as an early unlock by an administrator, #46), and sign-in works. `UserManager` uses `DateTimeOffset.UtcNow`, not `TimeProvider`, for lockout (2.4), so the test can't move the clock instead. |
| Antiforgery | `POST /bff/login` and an authenticated `POST /api/...` without `X-XSRF-TOKEN` → 400 `SS-AUTH-002`. With a token from `/bff/antiforgery` → success. A token issued to anonymous is rejected after sign-in. |
| Logout | After `POST /bff/logout`, the old cookie no longer reaches protected endpoints (cleared). |
| Security stamp | Rotate the stamp in the database. After the validation interval (`TimeProvider`), the old cookie → 401, through `ICredentialVerifier.ValidateSessionAsync`. A role change renews the cookie with the new roles. |
| Internal token (ADR 0016) | A module endpoint called with a minted internal token returns the same result as with the cookie (same policies, same `ICurrentUser`, same session GUCs). Rejected with 401: expired token, wrong audience, wrong issuer, signed with another key, `alg` HS256 or `none`. A bearer request needs no antiforgery token; a cookie request still does. |
| BFF independence | The BFF endpoints run against a fake `ICredentialVerifier` with no Identity database, which proves the gateway can use them as they are. |
| 401 vs 403 | Anonymous `/api/v1/...` → 401 (no redirect). Wrong role → 403. |
| Brand scope (test endpoints with documents whose lines carry brands) | `BRAND_MANAGER` (Claas, Lemken) approves a document with only Claas lines → **200**. Approves a document with Claas and one foreign-brand line → **403**, and the document is unchanged. Reads a foreign-brand document → **200**. `BRAND_MANAGER` + `SPONSOR` approves a foreign-brand purchase order → **200**. `BRAND_MANAGER` cancels a mixed-brand transfer or purchase order → **403**. `INVENTORY_PLANNER` approves a foreign-brand transfer → **200**. `ADMINISTRATOR` cancels a foreign-brand transfer → **200**. |
| Rate limiting | The 11th login in a minute from one IP → 429 with `Retry-After`. |
| Data Protection (#22) | Two `WebApplicationFactory` hosts share one database. A cookie issued by host A is accepted by host B. Keys are in `iam.data_protection_keys`. |
| Session GUCs (#11) | Inside a request, `current_setting('app.user_roles')` and `app.home_warehouse_id` match the claims. `util.require_role` passes or fails accordingly. |
| Headers | `Content-Security-Policy`, `X-Content-Type-Options` and `Referrer-Policy` on `/`. |
| Remap safety | `MigrationRunner check-model` reports no pending changes. `tools/verify_database.sh` (97 SQL assertions, concurrency test, demo data) passes in CI. |

### 9.3 bUnit (`tests/SmartSpare.Web.Tests`, new)

- `Login` page: required-field validation from the shared validator, busy state, a 400 `SS-AUTH-001` shows a generic message, and success calls `NavigateTo(..., forceLoad: true)`.
- `MainLayout` / `NavMenu`: items are shown or hidden by policy for a fake `AuthenticationState`.
- `RedirectToLogin` keeps the return URL. `UnauthorizedHandler` triggers a reload on 401.
- MudBlazor needs `Services.AddMudServices()` and JS interop in loose mode in the test context.

### 9.4 Playwright (`tests/SmartSpare.E2E`, new): one smoke test

This test is cheap because the host already runs in-process with Testcontainers for the integration tests. A Kestrel-bound `WebApplicationFactory` serves the app, and Playwright for .NET (Chromium only) runs:

1. open `/` → redirected to `/login`;
2. sign in as `tomas.radzevicius` (BRAND_MANAGER) → home page with the user menu;
3. sign out → back to `/login`.

It runs in a separate CI job (`Category=E2E`) that installs Chromium. The job doesn't block local `dotnet test --filter "Category!=Integration"`. The full UC01/UC02 scenarios and tablet viewports stay in stage 11 (work plan).

---

## 10. Pull requests and exit criteria

Each PR is merged with a green build (`-warnaserror`), all tests, `dotnet format --verify-no-changes` and the stale-terms check.

### 3a: Identity domain and persistence

Scope: section 7 (aggregates, configurations, delete the persistence classes). `MigrationRunner set-demo-passwords` and the T001/`.env.example` change (2.3). AppHost "Load demo data" command.

Acceptance:
- One schema-free migration, `AddAppUserXminConcurrency`, for the `xmin` concurrency token; its generated SQL is only the migrations-history insert, `tools/schema_diff.sh` shows no schema change, and `check-model` reports no further pending changes.
- `verify_database.sh` green, including the T03 hash-format assertion (now: V3, SHA512, ≥ 220,000 iterations).
- `PasswordHasherOptions.IterationCount = 220_000`. Old hashes are rehashed on sign-in (2.3).
- Unit tests 9.1 for the aggregate and the value objects.
- `.env.example` contains no real password, and demo users get the hash of the password chosen in `.env`.
- Demo data loads into the AppHost database without copying a port or password, and the README is updated.

Closes **#38, #7, #33**.

### 3b: Cookie authentication, BFF endpoints and authorization

Scope: sections 1.2, 1.3, 1.5, 1.6, 2, 3 and 4.
- Identity core with the custom stores, behind `Identity.Contracts.ICredentialVerifier` with its in-process implementation.
- The `SmartSpare.Bff` building block: `/bff/login`, `/bff/logout`, `/bff/me` and `/bff/antiforgery`, the cookie with the session check, the internal-token issuer.
- The policy scheme `SmartSpare` with the cookie and internal JwtBearer schemes.
- The antiforgery endpoint filter, `AppPolicies` and the brand-scoped action requirements with their handler (3.3), the `login` rate limit. ApiContracts gets the Identity DTOs, validator and authorization constants, and the architecture test is updated for S14. Data Protection keeps the default per-instance key ring in this PR.

Acceptance:
- Integration tests 9.2: sign-in and cookie flags, uniform failures, lockout, antiforgery, logout, security stamp, 401 vs 403 and brand scope, rate limiting.
- Internal-token and BFF-independence tests (9.2, ADR 0016): module endpoints behave the same with the cookie and with an internal token, and invalid tokens are rejected.
- The BFF references no module implementation, only `Identity.Contracts` (architecture test).
- Unit tests for the stores, the principal factory, the policies and `BrandScopedActionHandler`; `PolicySqlRoleParityTests` green.
- The implementation matches ADR 0014 and ADR 0016 (accepted). Any deviation needs a new ADR.

Closes **#60** (trace id in every ProblemDetails; the client snackbar and the walkthrough moved to #72). It implements ADR 0014 on the server, and the single-host half of ADR 0016.

### 3c: Session GUCs and Data Protection keys

Scope: section 6 (`ICurrentUser` with roles and warehouse, `ClaimsCurrentUser`, `SessionContextInterceptor`, the Platform, Inventory, Identity and Reporting SQL migrations, `pg_temp.act_as`) and section 5 (`iam.data_protection_keys` table, migration and grants, `PersistKeysToDbContext` in the installer, application name and per-environment key protection in the host).

Acceptance:
- The integration tests for the session GUCs and for a cookie shared by two host instances.
- SQL suite (102 assertions) green on the EF-built database with the GUC-based checks; `schema_diff` allow-list entries explained in the PR.
- #71: audit rows of UPDATEs carry the record id (Platform migration), with SQL assertions for a single-column and a composite key.
- `check-model` shows only the deliberate migrations of this PR.
- #69 in the Identity migration: `ck_app_user_user_name` also requires `user_name = lower(user_name)`, and the `password_hash` comment states 220,000 iterations.
- Key protection is configurable per environment and documented (README or `docs/`).

Closes **#11, #22, #69, #71**.

### 3d: Blazor client skeleton

Scope: section 8 (hosting in `SmartSpare.Host`, `SmartSpare.Web.Client`, MudBlazor 9.12, layout, sign-in page, typed clients, localization, CSP and security headers) and section 1.4. bUnit project 9.3.

Acceptance:
- `dotnet run --project src/SmartSpare.AppHost`, then sign in as a demo user, see the role-dependent menu and sign out. A 401 after stamp rotation sends the user back to `/login`.
- bUnit tests green. The security-header test is green, and `script-src` contains no `'unsafe-inline'`.
- An architecture test checks that the client references only ApiContracts.

### 3e: End-to-end smoke test, documentation and release

Scope: Playwright smoke test (9.4) and its CI job. ARCHITECTURE_DECISIONS sections 6/7 and the solution diagram marked implemented for stage 3. README sign-in instructions. `docs/database` regenerated for the new table and the changed view. Release notes.

Acceptance:
- E2E job green.
- Docs generators and the stale-terms check green.
- Tag `v0.3.0`.

User administration (#46) is an optional PR 3f after the release.

### Exit criteria for v0.3.0

1. A demo user signs in through the MudBlazor sign-in page with the password chosen in `.env`, sees a menu that matches their roles and signs out. Nothing token-like is stored in the browser (the cookie is HttpOnly and `localStorage`/`sessionStorage` hold no credentials).
2. The cookie flags, antiforgery, lockout, rate limiting, security stamp revocation, 401/403 behaviour and the brand scope are proven by integration tests against PostgreSQL 17.
3. A cookie issued by one host instance works on another (#22).
4. The database role and warehouse checks use the session GUCs. No module's SQL reads `iam` except Identity and Reporting, as MODULE_MAPPING allows. The 97 SQL assertions are green on the EF-built database.
5. Identity has a real domain model, and its persistence classes are gone (ADR 0013).
6. bUnit tests and one Playwright smoke test are green in CI.
7. Issues #7, #11, #22, #33 and #38 are closed. Follow-ups are tracked: #46 (user administration), #47 (second UI culture), #48 (forwarded headers behind the platform's ingress, stage 4), #49 (distributed rate limiting), #50 (MFA for privileged roles), #51 (security event log), #53 (database-level brand check).

---

## 11. References

Official documentation (ASP.NET Core 10.0), checked 24 November 2026:

- [ASP.NET Core Blazor authentication and authorization](https://learn.microsoft.com/aspnet/core/blazor/security/?view=aspnetcore-10.0): `SignInManager`/`UserManager` not supported in Razor components; `AddAuthenticationStateSerialization` / `AddAuthenticationStateDeserialization`; state fixed for the WASM app's lifetime; security stamp validation interval.
- [AuthenticationStateSerializationOptions](https://learn.microsoft.com/dotnet/api/microsoft.aspnetcore.components.webassembly.server.authenticationstateserializationoptions?view=aspnetcore-10.0): `SerializeAllClaims`, `SerializationCallback`.
- [Secure ASP.NET Core Blazor WebAssembly](https://learn.microsoft.com/aspnet/core/blazor/security/webassembly/?view=aspnetcore-10.0): authorization must be enforced on the server.
- [Prevent Cross-Site Request Forgery (XSRF/CSRF) attacks](https://learn.microsoft.com/aspnet/core/security/anti-request-forgery?view=aspnetcore-10.0): antiforgery with Minimal APIs, `IAntiforgery.ValidateRequestAsync`, header name.
- [Call a web API from ASP.NET Core Blazor](https://learn.microsoft.com/aspnet/core/blazor/call-web-api?view=aspnetcore-10.0): `AntiforgeryStateProvider` and `RequestVerificationToken`, typed clients.
- [Work with SameSite cookies in ASP.NET Core](https://learn.microsoft.com/aspnet/core/security/samesite?view=aspnetcore-10.0)
- [Custom storage providers for ASP.NET Core Identity](https://learn.microsoft.com/aspnet/core/security/authentication/identity-custom-storage-providers?view=aspnetcore-10.0): store interfaces.
- [Configure ASP.NET Core Identity](https://learn.microsoft.com/aspnet/core/security/authentication/identity-configuration?view=aspnetcore-10.0): password hasher V3 defaults, lockout, sign-in options, security stamp.
- [Rate limiting middleware in ASP.NET Core](https://learn.microsoft.com/aspnet/core/performance/rate-limit?view=aspnetcore-10.0) and [samples](https://learn.microsoft.com/aspnet/core/performance/rate-limit-samples?view=aspnetcore-10.0)
- [Key storage providers](https://learn.microsoft.com/aspnet/core/security/data-protection/implementation/key-storage-providers?view=aspnetcore-10.0) and [Configure ASP.NET Core Data Protection](https://learn.microsoft.com/aspnet/core/security/data-protection/configuration/overview?view=aspnetcore-10.0): `PersistKeysToDbContext`, `SetApplicationName`, `ProtectKeysWithAzureKeyVault`, `ProtectKeysWithCertificate`.
- [Enforce a Content Security Policy for ASP.NET Core Blazor](https://learn.microsoft.com/aspnet/core/blazor/security/content-security-policy?view=aspnetcore-10.0)
- [ASP.NET Core Blazor globalization and localization](https://learn.microsoft.com/aspnet/core/blazor/globalization-localization?view=aspnetcore-10.0)
- [MudBlazor installation](https://mudblazor.com/getting-started/installation) and [MudBlazor on NuGet](https://www.nuget.org/packages/MudBlazor/) (9.12.0; 9.x supports .NET 8, 9 and 10; no static rendering).
