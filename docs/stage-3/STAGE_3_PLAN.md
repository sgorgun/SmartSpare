# SmartSpare — Stage 3 Plan: Identity, BFF and Blazor Skeleton

Status: **proposed** (24 November 2026). Stage 3 of the work plan ([ARCHITECTURE_DECISIONS.md, section 12](../architecture/ARCHITECTURE_DECISIONS.md#12-work-plan)): cookie sign-in, roles and policies, and the Blazor client skeleton with MudBlazor and the sign-in page. Release target: **v0.3.0**.

Inputs: ARCHITECTURE_DECISIONS sections 6, 7 and 12; [ADR 0006](../adr/0006-frontend-blazor-wasm-mudblazor-bff.md), [ADR 0012](../adr/0012-evolvability-principles.md), [ADR 0013](../adr/0013-domain-model-and-persistence.md); the `iam` schema in [DATA_DICTIONARY.md](../database/DATA_DICTIONARY.md#3-schema-iam--identity) and the trust model in [MODULE_MAPPING.md, section 3.6](../database/MODULE_MAPPING.md); issues #7, #11, #22, #33, #38. The sign-in design is recorded in [ADR 0014](../adr/0014-browser-sign-in-through-bff-endpoints.md) (**Accepted** 24 November 2026; PRs 3b–3d implement it).

Every technical choice below was checked against the official documentation for **ASP.NET Core 10.0** (links use `?view=aspnetcore-10.0`; see [section 11](#11-references)). Learn now opens on the ASP.NET Core 11 preview by default, and some of what it shows there does not exist in .NET 10 (for example, the automatic `Sec-Fetch-Site` CSRF middleware). The plan uses only .NET 10 features.

---

## 0. Summary of decisions

| # | Decision | Section |
|---|---|---|
| S1 | The WASM sign-in page posts JSON to **Host endpoints** `/bff/login`, `/bff/logout` and `/bff/me`. `SignInManager` runs only in those endpoints, never in Razor components. | 1, ADR 0014 |
| S2 | Auth cookie `__Host-SmartSpare`: HttpOnly, Secure, SameSite=Strict, 8 h sliding expiry. No tokens reach the browser. | 1.3 |
| S3 | Auth state reaches WASM through `AddAuthenticationStateSerialization`, with a custom callback that sends only id, name, roles, brands and warehouse. After sign-in and sign-out the page does a **full reload**. | 1.4 |
| S4 | Token-based antiforgery on **every unsafe `/bff` and `/api` request**, including login, plus SameSite=Strict as a second layer. | 1.5 |
| S5 | **`AddIdentityCore<AppUser>` with custom stores** over the existing `iam` tables. `AppUser` is the domain aggregate from `Domain/`, not an `IdentityUser`. No schema changes for Identity itself. | 2 |
| S6 | Seeded V3 hashes (PBKDF2-HMAC-SHA512, 100,000 iterations) verify unchanged. The demo password comes from `.env` at load time (#7). | 2.3 |
| S7 | Security stamp checked every 5 minutes. Lockout after 5 failures for 15 minutes. Deactivated users cannot sign in, and their cookies stop working at the next stamp check. | 2.4–2.6 |
| S8 | Claims: `sub`, `name`, `role` (n), `ss:brand` (n), `ss:warehouse`. Policies use claims only (ADR 0012). Brand scope is checked against the resource. | 3 |
| S9 | Fixed-window rate limit on `/bff/login`, partitioned by client IP. Returns 429 with `Retry-After`. | 4 |
| S10 | Data Protection key ring in `iam.data_protection_keys` via `PersistKeysToDbContext`, application name `SmartSpare`. Key protection at rest is configured in the host per environment (#22). | 5 |
| S11 | `SessionContextInterceptor` also sets `app.user_roles` and `app.home_warehouse_id`. The SQL role and warehouse checks read these GUCs, and `iam.v_app_user_masked` becomes module-local (#11). | 6 |
| S12 | Identity aggregates move into `Domain/`, and the Identity classes in `PersistenceModel.cs` are deleted (ADR 0013, #38). | 7 |
| S13 | New `SmartSpare.Web.Client` (Blazor WASM, MudBlazor **9.12.0**), hosted by `SmartSpare.Host`. Typed HTTP clients per module. `IStringLocalizer` with en-US only in this stage. | 8 |
| S14 | Module implementation projects may reference `SmartSpare.ApiContracts`, the public HTTP contract. Role, claim and policy names live there, so host and client share one definition. | 8.4, ADR 0014 |

---

## 1. Sign-in flow (Blazor Web App, global InteractiveWebAssembly)

### 1.1 Constraints from the platform

- `SignInManager<TUser>` and `UserManager<TUser>` **aren't supported in Razor components** ([Blazor authentication and authorization](https://learn.microsoft.com/aspnet/core/blazor/security/?view=aspnetcore-10.0)). In a WASM component they can't run at all: there is no server, no `HttpContext` and no way to set an HttpOnly cookie.
- The template's Identity UI renders **statically** (static SSR) because Identity components don't support interactivity. MudBlazor requires interactivity and has no static rendering ([ADR 0006](../adr/0006-frontend-blazor-wasm-mudblazor-bff.md); [MudBlazor on NuGet](https://www.nuget.org/packages/MudBlazor/)). A template-style sign-in page could therefore not use MudBlazor.
- With CSR, the deserialized authentication state is **fixed for the lifetime of the WebAssembly app** ([Blazor authentication and authorization, "Manage authentication state in Blazor Web Apps"](https://learn.microsoft.com/aspnet/core/blazor/security/?view=aspnetcore-10.0#manage-authentication-state-in-blazor-web-apps)). Any change to who is signed in must restart the app, so the page does a full reload.
- Authorization in WASM is UX only. **Every check is repeated on the server endpoint** ([Secure ASP.NET Core Blazor WebAssembly, "Authorization"](https://learn.microsoft.com/aspnet/core/blazor/security/webassembly/?view=aspnetcore-10.0#authorization)).

### 1.2 Endpoints

The endpoints belong to the Identity module, which owns `AppUser`, the stores and `SignInManager<AppUser>`. They are mapped by `IdentityModuleInstaller.MapEndpoints` under an unversioned `/bff` group. They are browser-session plumbing of the BFF, not part of the versioned public API (ADR 0012). After the split they move to the YARP gateway's identity service.

| Endpoint | Auth | Antiforgery | Rate limit | Behaviour |
|---|---|---|---|---|
| `POST /bff/login` (`LoginRequest { UserName, Password, RememberMe }`) | anonymous | required | `login` policy | `PasswordSignInAsync(lockoutOnFailure: true)`. **204** and `Set-Cookie` on success. On failure **400 ProblemDetails** with code `SS-AUTH-001` for every case (wrong user, wrong password, deactivated, locked out), so the response can't be used to enumerate accounts. Lockout is logged on the server. |
| `POST /bff/logout` | authenticated | required | — | `SignOutAsync()`, **204**. A cross-site POST is stopped by antiforgery and SameSite, so another site can't sign the user out. |
| `GET /bff/me` | anonymous allowed | — | — | `CurrentUserResponse { IsAuthenticated, UserId, UserName, Roles[], BrandIds[], HomeWarehouseId }` from claims, with no database call. The client uses it to recover after SameSite=Strict navigations (1.3) and after a 401. |
| `GET /bff/antiforgery` | anonymous allowed | — | — | Returns a fresh request token for the current principal (fallback; normally the token comes with the prerendered page, see 1.5). |

`RememberMe` = persistent cookie. Without it the cookie lives for the browser session, still capped by the 8 h ticket expiry.

### 1.3 Cookie

`AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies()` with `ConfigureApplicationCookie`:

| Setting | Value | Why |
|---|---|---|
| `Cookie.Name` | `__Host-SmartSpare` | The `__Host-` prefix makes the browser require Secure, `Path=/` and no `Domain`. |
| `HttpOnly` | true | Script can't read the cookie (NFR04, ADR 0006). |
| `SecurePolicy` | `Always` | |
| `SameSite` | `Strict` | ADR 0006. Cross-site requests never carry the cookie ([Work with SameSite cookies](https://learn.microsoft.com/aspnet/core/security/samesite?view=aspnetcore-10.0)). |
| `ExpireTimeSpan` / `SlidingExpiration` | 8 h / true | One work shift. |
| `LoginPath` | `/login` | For full page navigations to protected Blazor routes during prerendering. |
| `Events.OnRedirectToLogin` / `OnRedirectToAccessDenied` | **401** / **403** for `/api` and `/bff` paths | APIs must not redirect to HTML. |

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

---

## 2. ASP.NET Core Identity over the existing `iam` tables

### 2.1 Custom stores, not `IdentityUser` (decision S5)

| Option | Verdict |
|---|---|
| `AddIdentity<IdentityUser>().AddEntityFrameworkStores<…>()` with the default schema | Rejected. It needs `normalized_user_name`, `normalized_email`, `concurrency_stamp`, `email_confirmed`, `lockout_enabled`, a string `security_stamp`, and `AspNetUserClaims`/`Logins`/`Tokens` tables. That changes a schema already covered by 97 SQL tests, the CHECKs and the audit trigger. |
| `AppUser : IdentityUser<Guid>` mapped onto `iam.app_user` | Rejected. `Domain/` may not depend on framework packages (ADR 0013, architecture tests), and `IdentityUser` brings public setters for every field. |
| **`AddIdentityCore<AppUser>()` + custom `IUserStore` implementations in `Infrastructure/Identity`** | **Chosen.** Identity's managers work with any `TUser : class` and only the store interfaces we implement ([Custom storage providers for ASP.NET Core Identity](https://learn.microsoft.com/aspnet/core/security/authentication/identity-custom-storage-providers?view=aspnetcore-10.0)). The store translates between Identity's calls and the aggregate's behaviour methods. |

Registration (inside `IdentityModuleInstaller.AddServices`):

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
| `UserName` / normalized name | `user_name citext` | `FindByNameAsync(normalized)` compares on `citext`, which is case-insensitive, so no normalized column is needed. The CHECK keeps stored names lower-case. A `LowerInvariantLookupNormalizer` makes the normalized value equal to the stored one. |
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

- The seeded hashes start with `AQAAAAIAAYag`: format marker `0x01` (V3), PRF `2` = HMAC-SHA512, iteration count `0x000186A0` = 100,000. These are the defaults of `PasswordHasher<TUser>` in .NET 10 (`CompatibilityMode = IdentityV3`, `IterationCount = 100000`; [Configure ASP.NET Core Identity, "Password Hasher options"](https://learn.microsoft.com/aspnet/core/security/authentication/identity-configuration?view=aspnetcore-10.0#password-hasher-options)). `VerifyHashedPassword` picks the algorithm from the format marker, so the seeded users sign in without any rehash. If the defaults ever change, `PasswordVerificationResult.SuccessRehashNeeded` makes `SignInManager` rehash on the next sign-in. That writes through the `password_hash` grant and rotates the stamp.
- An integration test pins the format: a user seeded with a known V3 hash signs in. A unit test checks that `PasswordHasher<AppUser>` output still matches `^AQAAAAIAAYag`, the same pattern as the T03 SQL assertion.
- **#7:** T001 no longer contains literal hashes. A new `MigrationRunner set-demo-passwords` command reads `SMARTSPARE_DEMO_USER_PASSWORD`, hashes it with `PasswordHasher` and updates the demo users (by the fixed ids of T001) as `smartspare_migrator`. `db/scripts/load_testdata.sh` calls it after loading T001. `.env.example` gets a placeholder, and the README states that any password works for the demo users.
- **#33:** the AppHost gets a resource command "Load demo data" on the database resource (`WithCommand`). It runs T001 and `set-demo-passwords` with the connection and parameters Aspire already holds, so nothing has to be copied from the dashboard.

### 2.4 Lockout

`o.Lockout.AllowedForNewUsers = true`, `MaxFailedAccessAttempts = 5`, `DefaultLockoutTimeSpan = 15 min`. `PasswordSignInAsync(..., lockoutOnFailure: true)` increments `failed_login_count`, sets `lockout_end` and resets the counter, so the value never approaches the CHECK's 100. A locked-out user gets the same generic 400 response (1.2). Unlocking early is an administrator action (#46).

### 2.5 Security stamp validation

- `Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(5))`. The default is 30 minutes, and each check costs one database query per user per interval ([Blazor authentication and authorization, "Sign out for ASP.NET Core Identity"](https://learn.microsoft.com/aspnet/core/blazor/security/?view=aspnetcore-10.0); [Configure Identity, "ISecurityStampValidator and SignOut everywhere"](https://learn.microsoft.com/aspnet/core/security/authentication/identity-configuration?view=aspnetcore-10.0#isecuritystampvalidator-and-signout-everywhere)).
- `security_stamp` is a `uuid`, while `UserManager` generates Base32 strings. `SetSecurityStampAsync` therefore ignores the proposed string and calls `AppUser.RotateSecurityStamp()`, which stores a new `Guid`. `GetSecurityStampAsync` returns the stored Guid as `"N"` text. The validator only compares the claim with the stored value, so the stamp only has to change. It doesn't have to match Identity's format.
- The stamp rotates on password change, role or brand change, deactivation and anonymisation. Anonymisation already does this in SQL: `iam.anonymise_user` sets `security_stamp = gen_random_uuid()`. In each case the user's cookies stop working within 5 minutes. The principal is then rebuilt, so new roles also take effect within 5 minutes.

### 2.6 Deactivated and anonymised users

`o.SignIn.RequireConfirmedAccount = true` together with `ActiveUserConfirmation : IUserConfirmation<AppUser>` returning `user.IsActive && user.AnonymisedAt is null`. `SignInManager.CanSignInAsync` then refuses those users, and the stamp validator refuses their existing cookies, because both deactivation and anonymisation rotate the stamp. This uses only documented extension points, with no `SignInManager` subclass.

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

Policies are claims-only (ADR 0012) and registered by one shared method, `AppPolicies.Register(AuthorizationOptions)`, which the host and the client both call:

| Policy | Rule |
|---|---|
| (fallback) | Authenticated user. Every `/api` endpoint requires authorization unless it explicitly allows anonymous access (health and module pings stay anonymous). |
| `Administration` | role `ADMINISTRATOR` |
| `PlanInventory` | role `INVENTORY_PLANNER` or `BRAND_MANAGER` |
| `ManageWarehouse` | role `WAREHOUSE_KEEPER` and an `ss:warehouse` claim |
| `ServiceMachines` | role `SERVICE_ENGINEER` |
| `ApprovePurchases` | role `BRAND_MANAGER` or `SPONSOR` |
| `ReadAudit` | role `SPONSOR` or `ADMINISTRATOR` |

The role-to-policy matrix is fixed for stage 3. Stages 4–8 add rows next to the endpoints that need them.

### 3.3 Brand scope (resource-based)

`BrandScopeRequirement` with `BrandScopeHandler : AuthorizationHandler<BrandScopeRequirement, IBrandScoped>` in `SmartSpare.Infrastructure.Security`. A user **with the `BRAND_MANAGER` role** passes only if the resource's `BrandId` is in their `ss:brand` claims. Other roles aren't brand-scoped. This matches `iam.user_brand`, which is documented as "brand manager ↔ brands", and the rule has been confirmed against the BA. Endpoints call `IAuthorizationService.AuthorizeAsync(user, resource, BrandScope)` after loading the resource, and get **403** on failure. Stage 3 proves it with a test endpoint in the integration test host. The first real use is in stage 4.

The database repeats the role check (`util.require_role`, section 6), so a bug in a policy can't bypass the SQL second line of defence (ARCHITECTURE_DECISIONS section 7).

---

## 4. Rate limiting on sign-in

- `AddRateLimiter` with a named policy `login`: `RateLimitPartition.GetFixedWindowLimiter` partitioned by `HttpContext.Connection.RemoteIpAddress`, **10 requests per minute**, no queue. `RejectionStatusCode = 429`, and `OnRejected` copies `MetadataName.RetryAfter` into `Retry-After` ([Rate limiting middleware](https://learn.microsoft.com/aspnet/core/performance/rate-limit?view=aspnetcore-10.0)). `/bff/login` uses `.RequireRateLimiting("login")`, and `app.UseRateLimiter()` runs after routing.
- Account-level brute force is handled by lockout (2.4), so partitioning by user name, which would mean reading the body in the limiter, isn't needed.
**Known limits:**

- **Per-instance limiter.** The limiter is in memory, so each replica has its own budget. Accepted while the app runs as one instance (#49).
- **Client IP behind a proxy.** Behind App Service, `RemoteIpAddress` is the front end. The client IP must come from `X-Forwarded-For` via the forwarded-headers middleware, trusted only from known proxies; otherwise every user shares one partition and one user can block sign-in for everyone. The docs also warn that IP partitions can be spoofed ([Rate limiter samples](https://learn.microsoft.com/aspnet/core/performance/rate-limit-samples?view=aspnetcore-10.0)). **#48 must be done before the first Azure deployment** (stage 9).
- **Lockout as a denial-of-service vector.** Anyone who knows or guesses a user name can lock that account for 15 minutes with five wrong passwords, repeatedly. Accepted for now: the user names aren't published, the IP rate limit slows the attack, a lockout ends on its own, and an administrator can unlock early (#46). Detecting lockout spikes is part of the security event log (#51). MFA (#50) would let lockout be relaxed for second-factor failures only.

---

## 5. Data Protection keys in PostgreSQL (#22)

- Package `Microsoft.AspNetCore.DataProtection.EntityFrameworkCore` (10.0.x, central version). `IdentityDbContext` implements `IDataProtectionKeyContext` (`DbSet<DataProtectionKey> DataProtectionKeys`), mapped to **`iam.data_protection_keys`** (`id`, `friendly_name`, `xml`). The table is created by a new Identity migration ([Key storage providers, "Entity Framework Core"](https://learn.microsoft.com/aspnet/core/security/data-protection/implementation/key-storage-providers?view=aspnetcore-10.0#entity-framework-core)).
- Grants (SQL part of the migration, ADR 0004): `SELECT, INSERT` on the table and `USAGE` on its sequence to `app_write`, nothing to `app_read`. Key XML is secret material. The audit trigger is **not** attached.
- Wiring is split across the boundary. `IdentityDbContext` is internal, so **the Identity installer** calls `services.AddDataProtection().PersistKeysToDbContext<IdentityDbContext>()`. **The host** adds `.SetApplicationName("SmartSpare")` and protection at rest ([Configure ASP.NET Core Data Protection](https://learn.microsoft.com/aspnet/core/security/data-protection/configuration/overview?view=aspnetcore-10.0)):
  - Azure: `ProtectKeysWithAzureKeyVault` with the Managed Identity. Host only, because modules may not reference cloud SDKs (ADR 0012, architecture tests). The Key Vault key is created in stage 9 (#16). Until then the switch is configuration-driven and off.
  - Other environments: `ProtectKeysWithCertificate` when `DataProtection:CertificateThumbprint`/`Path` is configured.
  - Local development: unprotected keys (accepted in #22).
- The Data Protection key table is technical state, not a domain aggregate. ADR 0013 rule 3 lets it stay a framework class.

---

## 6. Session context and database checks (#11)

- `ICurrentUser` (BuildingBlocks) gains `IReadOnlyCollection<string> Roles` and `short? HomeWarehouseId`. A new `ClaimsCurrentUser` in `SmartSpare.Infrastructure` reads the claims through `IHttpContextAccessor` and replaces `NoCurrentUser` in the host. Background jobs keep `NoCurrentUser`. The sign-in handler uses a scoped override for the user who is signing in (2.2).
- `SessionContextInterceptor` sets four transaction-local GUCs: `app.user_id`, `app.reason`, **`app.user_roles`** (comma-separated role codes) and **`app.home_warehouse_id`**, all with `set_config(..., true)`. The trust model is unchanged ([MODULE_MAPPING 3.6](../database/MODULE_MAPPING.md)).
- SQL, applied through new migrations of the owning modules (ADR 0004):
  - Platform: `util.require_role` checks `app.user_roles` instead of calling `iam.user_has_role` (X1).
  - Inventory: `dispatch_transfer` and `receive_transfer` read `app.home_warehouse_id` instead of `iam.app_user` (X2).
  - Identity: `iam.v_app_user_masked` returns `home_warehouse_id` instead of the code (X18), and the transitional copy in the Reporting migration is dropped. Reporting gets a new migration that drops it.
  - `db/tests/_helpers.sql`: `pg_temp.act_as` also sets `app.user_roles` and `app.home_warehouse_id`.
- Checks: the 97 SQL assertions stay green on the EF-built database (`tools/verify_database.sh`). `tools/schema_diff.sh` gets allow-list entries for the changed function bodies and view columns, each explained in the PR.

---

## 7. Identity domain model (ADR 0013, #38)

`src/Modules/Identity/SmartSpare.Modules.Identity/Domain/`:

| Type | Kind | Behaviour |
|---|---|---|
| `AppUser : AggregateRoot<Guid>` | aggregate | Private state. `RecordSuccessfulSignIn(now)`, `RecordFailedSignIn(now, policy)` → lockout, `ChangePasswordHash(hash)` (rotates stamp), `RotateSecurityStamp()`, `Deactivate()` (rotates stamp, raises `UserDeactivated`), `AssignRole` / `RevokeRole` and `AssignBrand` / `RemoveBrand` (rotate stamp). Invariants mirror the CHECKs: an active user has a password, an anonymised user is inactive, the failure counter stays in 0..100. |
| `UserRoleAssignment : Entity<…>` | child entity | `RoleId`, `GrantedAt`, `GrantedBy` |
| `UserBrandAssignment` | child entity / owned | `BrandId` |
| `Role : Entity<short>` | reference entity | Read-only, seeded (`Code`, `Name`). |
| `UserName`, `EmailAddress` | value objects | Validation equal to `ck_app_user_user_name` / `ck_app_user_email`. |

- EF configurations in `Infrastructure/Persistence/Configurations` map these directly: private constructors, backing fields for the collections, value converters for the value objects, and `xmin` concurrency on `AppUser`. The Identity classes in `PersistenceModel.cs` (`AppUser`, `Role`, `UserBrand`, `UserRole`) are deleted. `RefBrand` stays as the read-only FK principal.
- `MigrationRunner check-model` must report **no pending changes** for `IdentityDbContext` after the remap (PR 3a). The only Identity schema changes in stage 3 are deliberate migrations in PR 3c: the `data_protection_keys` table (section 5) and the SQL for #11 (section 6).
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

All new tests follow the pyramid (ARCHITECTURE_DECISIONS section 10). Testcontainers tests are `Category=Integration`.

### 9.1 Unit (`tests/SmartSpare.Modules.Identity.UnitTests`, new)

- `AppUser`: sign-in success resets the counter and sets `last_login_at`. Five failures lock the user and reset the counter. Deactivation rotates the stamp and raises `UserDeactivated`. Role or brand change rotates the stamp. Invariants match the CHECKs (active ⇒ password, anonymised ⇒ inactive, counter range).
- `UserName` / `EmailAddress` accept and reject the same values as the SQL CHECKs (shared test cases).
- `AppUserStore` security-stamp translation (Base32 proposal → new Guid). `ActiveUserConfirmation`.
- `AppUserClaimsPrincipalFactory`: claim set, and no personal data.
- `PasswordHasher<AppUser>` output starts with `AQAAAAIAAYag` (V3 / SHA512 / 100k).
- `LoginRequestValidator` (ApiContracts).
- `AppPolicies` evaluated with `DefaultAuthorizationService` for each role, plus `BrandScopeHandler`.

### 9.2 Integration (`tests/SmartSpare.Host.IntegrationTests`, Testcontainers PostgreSQL 17 + `WebApplicationFactory<Program>`)

| Test | Expectation |
|---|---|
| Sign-in with a seeded V3 user | 204 and `Set-Cookie` with `__Host-SmartSpare`, `HttpOnly`, `Secure`, `SameSite=Strict`, `Path=/`, no `Domain`. Then `/bff/me` returns the roles and brands. `last_login_at` is set. |
| Wrong password, unknown user, deactivated user | Identical 400 `SS-AUTH-001` bodies. |
| Lockout | The 5th wrong password locks the user. The correct password is then still rejected. After `lockout_end` passes (injected `TimeProvider`) sign-in works. |
| Antiforgery | `POST /bff/login` and an authenticated `POST /api/...` without `X-XSRF-TOKEN` → 400 `SS-AUTH-002`. With a token from `/bff/antiforgery` → success. A token issued to anonymous is rejected after sign-in. |
| Logout | After `POST /bff/logout`, the old cookie no longer reaches protected endpoints (cleared). |
| Security stamp | Rotate the stamp in the database. After the validation interval (`TimeProvider`), the old cookie → 401. |
| 401 vs 403 | Anonymous `/api/v1/...` → 401 (no redirect). Wrong role → 403. `BRAND_MANAGER` on another brand's resource → 403, on their own → 200 (test endpoint). |
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

It runs in a separate CI job (`Category=E2E`) that installs Chromium. The job doesn't block local `dotnet test --filter "Category!=Integration"`. The full UC01/UC02 scenarios and tablet viewports stay in stage 10 (work plan).

---

## 10. Pull requests and exit criteria

Each PR is merged with a green build (`-warnaserror`), all tests, `dotnet format --verify-no-changes` and the stale-terms check.

### 3a: Identity domain and persistence

Scope: section 7 (aggregates, configurations, delete the persistence classes). `MigrationRunner set-demo-passwords` and the T001/`.env.example` change (2.3). AppHost "Load demo data" command.

Acceptance:
- `check-model` reports no pending changes (no migration in this PR).
- `verify_database.sh` green, including the T03 hash-format assertion.
- Unit tests 9.1 for the aggregate and the value objects.
- `.env.example` contains no real password, and demo users get the hash of the password chosen in `.env`.
- Demo data loads into the AppHost database without copying a port or password, and the README is updated.

Closes **#38, #7, #33**.

### 3b: Cookie authentication, BFF endpoints and authorization

Scope: sections 1.2, 1.3, 1.5, 2, 3 and 4. Identity core with the custom stores, the cookie, `/bff/login`, `/bff/logout`, `/bff/me`, `/bff/antiforgery`, the antiforgery endpoint filter, `AppPolicies` and the brand scope handler, the `login` rate limit. ApiContracts gets the Identity DTOs, validator and authorization constants, and the architecture test is updated for S14. Data Protection keeps the default per-instance key ring in this PR.

Acceptance:
- Integration tests 9.2: sign-in and cookie flags, uniform failures, lockout, antiforgery, logout, security stamp, 401 vs 403 and brand scope, rate limiting.
- Unit tests for the stores, the principal factory, the policies and `BrandScopeHandler`.
- The implementation matches ADR 0014 (accepted). Any deviation needs a new ADR.

Closes no issue on its own; it implements ADR 0014 on the server.

### 3c: Session GUCs and Data Protection keys

Scope: section 6 (`ICurrentUser` with roles and warehouse, `ClaimsCurrentUser`, `SessionContextInterceptor`, the Platform, Inventory, Identity and Reporting SQL migrations, `pg_temp.act_as`) and section 5 (`iam.data_protection_keys` table, migration and grants, `PersistKeysToDbContext` in the installer, application name and per-environment key protection in the host).

Acceptance:
- The integration tests for the session GUCs and for a cookie shared by two host instances.
- SQL suite (97 assertions) green on the EF-built database with the GUC-based checks; `schema_diff` allow-list entries explained in the PR.
- `check-model` shows only the deliberate migrations of this PR.
- Key protection is configurable per environment and documented (README or `docs/`).

Closes **#11, #22**.

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
7. Issues #7, #11, #22, #33 and #38 are closed. Follow-ups are tracked: #46 (user administration), #47 (second UI culture), #48 (forwarded headers behind App Service), #49 (distributed rate limiting), #50 (MFA for privileged roles), #51 (security event log).

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
