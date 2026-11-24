# ADR 0016: Authentication behind the gateway

- Status: Accepted
- Date: 2026-11-24
- Implementation: single-host part done in stage 3, release v0.3.0 (PRs 3b–3c: credential verifier, policy scheme, internal tokens, session context, shared key ring); gateway and services in stage 4 (#67)
- Amends: [ADR 0014](0014-browser-sign-in-through-bff-endpoints.md)

## Context

[ADR 0014](0014-browser-sign-in-through-bff-endpoints.md) signs the browser in through `/bff` endpoints in the host, with an HttpOnly, SameSite=Strict cookie and ASP.NET Core Identity over the `iam` tables. With [ADR 0015](0015-deployment-topology-microservices-behind-yarp-gateway.md), production has a gateway (the BFF) and six services, but local development and the tests keep a single host. Four questions have to be answered for both topologies:

1. Where does sign-in run, when the gateway has no database and Identity is its own service?
2. How do the services learn who the user is?
3. How is the security stamp re-validated, so deactivation and role changes take effect?
4. How does the single host accept the same principal, so module endpoints don't care which topology they run in?

The fixed constraints: no tokens in the browser (NFR04, ADR 0006), no home-made cryptography, claims-based authorization (ADR 0012), and no business-code fork (ADR 0015).

## Decision

### 1. Sign-in: the BFF issues the cookie, the Identity service checks the credentials

- The `/bff/login`, `/bff/logout`, `/bff/me` and `/bff/antiforgery` endpoints live in a BFF building block, `SmartSpare.Bff`, used by both `SmartSpare.Host` and `SmartSpare.Gateway`. They are not part of the Identity module.
- They call **`Identity.Contracts.ICredentialVerifier`**:
  - `VerifyAsync(userName, password, clientIp)` returns a result: success with the user's claims and security stamp, or a uniform failure (wrong credentials, locked out or inactive, without saying which).
  - `ValidateSessionAsync(userId, stamp)` returns the current claims, or "revoked".
- The Identity module implements `ICredentialVerifier` with `SignInManager.CheckPasswordSignInAsync(lockoutOnFailure: true)`. That runs the password check, lockout, `IUserConfirmation`, rehashing and `last_login_at` against its own data, without issuing a cookie.
- The BFF then signs in with `HttpContext.SignInAsync` (cookie scheme) using exactly those claims.
- In the single host, `ICredentialVerifier` is the in-process implementation.
- In production, it is the HTTP client of the Identity Contracts, calling `POST /internal/v1/identity/credential-checks` and `POST /internal/v1/identity/session-checks` on the Identity service.

Those two internal endpoints require a **gateway service token**: the gateway's own internal token (section 2), with subject `gateway` and claim `ss:svc=gateway`. The policy `GatewayOnly` checks it. They are reachable only inside the Container Apps environment.

### 2. From the gateway to the services: a short-lived internal token

For every proxied `/api/*` request, the gateway's YARP request transform:
- removes the `Cookie` header and any client-supplied `Authorization` header;
- sets `Authorization: Bearer <internal token>`.

The token is minted by the gateway from the cookie principal and cached per session until 30 seconds before it expires. Note that the YARP docs describe swapping authentication types in the proxy by creating a JWT from the user information ([YARP authentication and authorization](https://learn.microsoft.com/aspnet/core/fundamentals/servers/yarp/authn-authz?view=aspnetcore-10.0#flowing-credentials)).

| Property | Value |
|---|---|
| Format | JWS compact (RFC 7515/7519), created with `JsonWebTokenHandler` from `Microsoft.IdentityModel.JsonWebTokens` 8.23.0 ([API](https://learn.microsoft.com/dotnet/api/microsoft.identitymodel.jsonwebtokens.jsonwebtokenhandler.createtoken?view=msal-web-dotnet-latest)) |
| Algorithm | **ES256** (ECDSA P-256), asymmetric |
| Claims | `iss=smartspare-gateway`, `aud=smartspare-internal`, `sub`, `name`, `role` (n), `ss:brand` (n), `ss:warehouse`, `sid` (cookie session id), `jti`, `iat`, `exp` |
| Lifetime | **2 minutes** |
| Private key | Held only by the gateway: Key Vault in Azure (read with the gateway's Managed Identity), user-secrets locally. It is never in configuration files or images. |
| Public keys | Given to the services as a JWKS document in configuration (with `kid`). Two keys during rotation. |
| Validation in services | `AddJwtBearer` (`Microsoft.AspNetCore.Authentication.JwtBearer` 10.0.x) with `ValidIssuer`, `ValidAudience`, `IssuerSigningKeys` from the JWKS, `ValidAlgorithms = [ES256]`, `RequireExpirationTime`, `ClockSkew = 30 s` |

**`/internal/*` is never routed by the gateway.** No YARP route matches it: the routes are `/api/v1/{module}/{**rest}` and `/openapi/{module}.json` only. A **route-table test** loads the gateway's YARP configuration and asserts that no route matches `/internal/...` (any method, any module). A request to `/internal/...` at the gateway gets 404 and is never forwarded. Services use **internal ingress only**, so `/internal` endpoints, like everything else on a service, are reachable only inside the Container Apps environment.

Rules:
- Services hold **only the public key**. A compromised service can't mint tokens.
- Tokens never leave the server side. The browser only ever has the HttpOnly cookie.
- A service that calls another service's Contracts query while handling a user request forwards the same token. The audience is shared by all internal services for that reason.
- Event consumers and jobs make no user-context calls (ADR 0015 §4).

This is standard JWT signing and validation with Microsoft's libraries. No cryptography is implemented by hand.

### 3. Security stamp and revocation

- The BFF's cookie `OnValidatePrincipal` runs every **5 minutes**, like `SecurityStampValidator` in STAGE_3_PLAN §2.5. It calls `ICredentialVerifier.ValidateSessionAsync(userId, stamp)`:
  - **revoked** (stamp rotated, user deactivated or anonymised) → the cookie is rejected and the response is 401;
  - **changed roles or brands** → the cookie is re-issued with the new claims.
- Internal tokens live 2 minutes, so a revoked session stops reaching services after at most about **7 minutes** (5 + 2) in the services topology. In the single host there is no internal token, so it is at most 5 minutes.
- Logout removes the cookie and the gateway's cached token for that `sid`.

### 4. One principal in both topologies

- Module endpoints authorize with policies over claims only (ADR 0012, STAGE_3_PLAN §3). They never name an authentication scheme.
- Authentication schemes per host:

  | Host | Schemes | Default |
  |---|---|---|
  | **Gateway** | Cookie only. It **mints** internal tokens but registers **no JwtBearer scheme**, so it accepts no bearer token from outside. | Cookie |
  | **Services** | JwtBearer only (internal tokens). No cookie. | JwtBearer |
  | **Single host** (local development, tests) | Cookie and JwtBearer, behind a **policy scheme `SmartSpare`**: `Authorization: Bearer` → JwtBearer, anything else → cookie | `SmartSpare` |

  The policy scheme exists only in the single host, where the browser-facing BFF and the module endpoints share one process. There, the internal-token path can be tested without a gateway.
- The cookie principal and the internal-token principal carry the same claim types, defined in `SmartSpare.ApiContracts.Authorization` (`AppClaimTypes`). So `ICurrentUser`, the session GUCs (`app.user_id`, `app.user_roles`, `app.home_warehouse_id`) and the policies behave identically.
- The single host also accepts internal tokens. An integration test calls module endpoints in the single host with a minted internal token and with the cookie, and expects the same results. That way the service path is tested without deploying.

### 5. CSRF, rate limiting, forwarded headers

- Antiforgery validation (STAGE_3_PLAN §1.5) runs where the cookie is accepted: the gateway, or the single host.
- Requests authenticated by bearer token skip it, as the plan already specifies. Services accept no cookies.
- The sign-in rate limit and forwarded-headers handling (#48) run in the BFF, which sees the client.

### 6. Data Protection keys

The cookie and the antiforgery tokens exist only in the BFF.
- **Single host and tests:** the key ring stays in `iam.data_protection_keys` (#22).
- **Gateway:** it has no database, so the key ring goes to Azure Blob Storage with `Azure.Extensions.AspNetCore.DataProtection.Blobs`, protected at rest with `ProtectKeysWithAzureKeyVault`, both accessed with its Managed Identity ([Key storage providers](https://learn.microsoft.com/aspnet/core/security/data-protection/implementation/key-storage-providers?view=aspnetcore-10.0#azure-storage)).
- Key storage is a host concern (ADR 0012 seam), so modules are unaffected.

## Alternatives considered

| Question | Alternative | Why not |
|---|---|---|
| Where sign-in runs | **Gateway contains the Identity module** (the gateway is also the Identity service) | `SignInManager` would work directly, but the internet-facing process would hold database credentials and personal data. It would contradict D1 (Identity as its own service) and couple gateway deployments to Identity changes. |
| | **OpenID Connect server** (e.g. OpenIddict) in the Identity service, with the gateway as an OIDC client | The standard pattern for many clients, but it adds redirects, a server-rendered login UI outside MudBlazor, a token endpoint and client registration. That is more than one first-party web client needs. Kept for mobile and third-party clients (#25, #26). |
| How services learn the user | **Shared Data Protection key ring, cookie forwarded to services** ([cookie sharing](https://learn.microsoft.com/aspnet/core/security/cookie-sharing?view=aspnetcore-10.0)) | Every service would hold a symmetric key able to forge browser cookies, and the browser credential would travel into every service. |
| | **Trusted headers** (`X-User-Id`, `X-Roles`) set by the gateway | No integrity: anything that can reach a service, such as another compromised service or a misrouted request, can claim any user. |
| | **HMAC-signed (symmetric) internal JWT** | Every service would need the signing secret and could mint tokens. ES256 keeps minting in the gateway. |
| | **Entra ID / Managed Identity tokens** | They authenticate the calling workload, not the end user and their roles and brands. They could be added later for service identity. |

## Consequences

- No tokens in the browser, and the cookie stays in one place, the BFF. Services authenticate every request cryptographically and can't forge users.
- Module code and policies are identical in both topologies, and the single host tests the internal-token path.
- Sign-in and session checks cost one internal call each to the Identity service in production. Stamp checks happen every 5 minutes per session, not per request.
- Revocation in production takes up to about 7 minutes instead of 5.
- **One shared audience** (`smartspare-internal`): a token minted for a request to one service is accepted by every service for its 2-minute lifetime. That lets a service forward the caller's token on Contracts queries (§2), but it also means a token leaked from one service could be replayed against another. This is accepted because tokens exist only inside the private Container Apps network, never reach the browser, and expire after 2 minutes. **Possible hardening:** per-service audiences (`aud=smartspare-<service>`). The gateway would mint one token per destination cluster, and service-to-service queries would need either multi-audience tokens or an exchange at the gateway.
- Two operational secrets to manage: the gateway signing key with its rotation, and the gateway's Data Protection keys. Both live in Key Vault.
- STAGE_3_PLAN PR 3b changes accordingly: a BFF building block, `ICredentialVerifier`, the policy scheme, and internal-token validation and minting with tests. The gateway itself is built in stage 4.
