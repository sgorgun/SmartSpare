# ADR 0028: Permissions model

- Status: Accepted
- Date: 2026-11-28
- Amends: [ADR 0026](0026-employee-derived-authorization.md) (roles from positions now bundle permissions), [ADR 0012](0012-evolvability-principles.md) (claims-based authorization: permission claims)
- Relates to: [STAGE_7_PLAN.md](../stage-7/STAGE_7_PLAN.md) section 1.8, PRs 7x1–7x4

## Context

Until stage 7, every endpoint named a role policy (`MoveStock`, `Administration`, …) and every SQL business function named roles in `util.require_role`. The six roles were fixed in code, so a dealer could not give a purchasing clerk exactly the rights of the job, and a new screen meant editing role lists in two places. The product owner asked at the second stage 7 checkpoint for configurable permissions, using the model mature systems use: ASP.NET Core permission-based authorization, ABP permissions and Odoo groups with record rules. It should stay simple.

## Decision

Three layers.

1. **Permissions are code, and the only fixed part.**
   - `Permissions` (ApiContracts) is a catalogue of names `<Module>.<Resource>.<Action>`. Actions are `View`, `Create`, `Edit`, `Archive` and special ones (`Approve`, `Cancel`, `Dispatch`, `Receive`, `Adjust`, `Run`, `ManageAssignments`, `Decide`, `Anonymise`). Every catalogue and document has its own set.
   - `iam.permission` mirrors the catalogue (seeded by migration; a test fails if they differ).
   - A permission name is also a policy name: `PermissionPolicyProvider` builds "authenticated, with this permission claim" on demand. Endpoints call `RequireAuthorization(Permissions.Inventory.Transfer.Approve)` and never name a role.
   - From PR 7x2, SQL functions call `util.require_permission('<name>')`. A parity test keeps every endpoint's permission equal to its function's.
2. **Roles are data.**
   - A role is a named bundle of permissions (`iam.role_permission`). A position's role template holds one or more roles, and an employee's effective permissions are the union of the roles of the position.
   - Additive only, no deny rules.
   - The six seeded roles reproduce exactly the rights of the role policies they replace. The mapping table is in STAGE_7_PLAN 1.8; `AppPoliciesTests` holds a frozen copy of the old policy table and proves the bundles equal it.
   - Administrators edit roles in the UI (PR 7x3). Supplier editing, for example, belongs only to the Administrator role by default, because the first separation-of-duties pair forbids holding it together with purchase order creation. A dealer can build a "procurement master data" role later, because roles are data.
3. **Data scope stays as it is** (ADR 0026, the record-rule layer): brand assignments limit brand-scoped actions, and hub assignments limit hub writes. The exemptions that used to be written with role names are permissions too:
   - `Organisation.Scope.AllHubs` (write at every hub; the old ADMINISTRATOR exemption);
   - an "any brand" variant of each brand-scoped action (`Inventory.Transfer.ApproveAnyBrand`, `…CancelAnyBrand`, `Procurement.PurchaseOrder.ApproveAnyBrand`, `…CancelAnyBrand`, `Forecasting.Recommendation.DecideAnyBrand`).

   The scope permissions are per action on purpose. With one global "all brands" permission, a user whose roles combine brand-scoped transfer approval (brand manager) with an unscoped right on another document (sponsor) would gain every brand for transfers, which the role rules never allowed.

**Transport.**
- The cookie keeps id, name, stamp and role codes; permissions never go into it.
- `PermissionClaimsTransformation` in each BFF host (the single host and the gateway) adds the permissions per request. It reads the role → permissions map through `IRolePermissions`: in process from Identity, or over HTTP from the Identity service. The map is cached for the 30-second session check, the same window in which any rights change reaches a session.
- The 2-minute internal token to the services carries the permissions, so the services need no lookup.
- **Compact encoding, decided before it was needed (PR 7x1b).** As names, every permission cost about 40 bytes of token. The worst case was 3,062 bytes at 55 permissions; extrapolated, it reaches about 6.9 KB at 150 permissions (the expected size at the end of stage 7) and about 8.9 KB at 200, over the 8 KB budget. So the token carries a **bitset** instead.
  - Every permission has a stable numeric code, assigned once and never renumbered or reused; a test pins the list.
  - `ss:perms` holds the base64url bitset, where bit *n* is code *n*; `ss:permv` holds the catalogue version (the highest code).
  - Token validation expands the bitset back into permission claims, so policies are unchanged.
  - A service with an older catalogue ignores bits it does not know, because none of its endpoints can require them, so services deploy independently.
  - Measured worst case now: **886 bytes**. The bitset costs about 26 characters at 150 permissions and 51 at 300.
  - The 8 KB budget test stays.
  - **Re-measured with the superadministrator (28 November 2026, after PR 7x3b).** Its token is the largest: every permission, every role code including SUPERADMINISTRATOR, 12 brands and 6 hubs. Measured with today's 56 permissions: **916 bytes**. Only the bitset grows with the catalogue, so a test projects the size: **931 bytes at 120 permissions, 936 at 150 and 970 at 300**, against about 6 KB for names at 150. That is far below the ~6 KB line at which the product owner required a compact encoding, so no further change is needed. `InternalTokenTests` asserts the projection (< 1.5 KB) as well as the 8 KB budget.
- The web client receives the permissions in its serialized authentication state and in `/bff/me`.

**Guardrails** (PR 7x3):
- **Separation of duties:** a declarative list of conflicting permission pairs. A role or a position template that breaks a pair cannot be saved (409 with the pair). The first pair is `Procurement.PurchaseOrder.Create` + `Procurement.Supplier.Edit`. Self-approval stays forbidden.
- **No self-granting:** nobody edits a role they hold, the template of their own position, or their own position or assignments (403).
- **Administrator** is system administration under normal rules. The built-in Administrator role holds today's administrator rights plus the organisation permissions, not every permission. It is locked: it cannot be edited, archived or deleted. The last active administrator cannot be removed.
- **Superadministrator** is a locked position whose locked role template holds every permission, with no brand or hub scope limit. It is exempt from separation-of-duties validation.
  - Superadministrators are ordinary employees in that position, signing in with their own accounts, so rights still come only from the position and the model needs no exception.
  - Only an existing superadministrator moves an employee into or out of the position, never themselves. The last active one cannot be deactivated or moved out (409).
  - It does not bypass database invariants (stock never below zero, the append-only ledger, CHECKs) or the self-approval ban.
  - It is meant for emergencies, not daily work.
- **Audit and refresh:** every change to a role, its permissions or a position template is audited and rotates the security stamp of every affected account, so sessions take the new rights within 30 seconds.

## Considered and rejected

- **Role inheritance:** bundles are small and additive; inheritance hides where a right comes from, and the effective rights screen shows every origin instead.
- **Field-level permissions:** no requirement needs them, and personal data is already masked by the database.
- **Deny rules:** they make the effective rights depend on order and precedence, so additive-only stays explainable.
- **Time-limited grants:** an expiring grant needs a job and an audit trail of its own; a changed position or assignment covers the cases met so far.
- **External policy engines (OPA, Cedar, OpenFGA):** another runtime, language and deployment for a model that fits in ASP.NET Core policies and one SQL function.
- **Permissions in the cookie:** a cookie with every permission grows past browser limits as the catalogue grows, and would go stale until the next sign-in.

## Consequences

- A new screen or document adds permission constants, seeds them in a migration and gives them to the default roles that should have them, with a test. No role list is edited in code.
- The golden access matrix records permissions instead of policies. Its endpoint probes stayed identical through PR 7x1.
- Until PR 7x2, SQL functions still check role codes from the session; the parity test keeps those roles equal to the default holders of each permission.
