# ADR 0026: Employee-derived authorization

- Status: Accepted
- Date: 2026-11-28
- Amends: [ADR 0014](0014-browser-sign-in-through-bff-endpoints.md) (user administration), [ADR 0016](0016-authentication-behind-the-gateway.md) (the `ss:warehouse` claim)
- Relates to: [STAGE_7_PLAN.md](../stage-7/STAGE_7_PLAN.md) section 1, [ADR 0024](0024-acting-user-of-integration-events-and-jobs.md), MODULE_MAPPING 3.6

## Context

Until stage 7 an administrator edited every user's rights by hand on `/admin/users`: roles (`iam.user_role`), the brands of a brand manager's approval scope (`iam.user_brand`) and one home hub (`iam.app_user.home_warehouse_id`). The same facts about a person lived nowhere else, so "who is who" in the dealership (positions, who manages which brand, who keeps which hub) and "who may do what" in SmartSpare could drift apart, and nothing explained why a user had a right. Mature ERP systems derive authorizations from the organisation instead: SAP from positions and organisational assignments, Odoo from employees and their responsibilities.

The product owner's stage 7 scope (27 November 2026) makes this a hard requirement: employees and positions are the only place where "who is who" is edited, a user account is created from an employee, and every permission is derived.

## Decision

1. **Organisation model in Identity.** `iam.position` with a role template (`iam.position_role`), `iam.employee` (number, name, contacts, position, hire and leave dates) and `iam.app_user.employee_id` (`NOT NULL`, unique). An account is created from an employee (`POST /employees/{id}/account`) and never exists without one; an employee needs no account. The six application roles stay seeded and fixed: a role is a code-defined set of permissions (policies and `util.require_role`), not data.
2. **Rights are derived, never edited.** One pure domain service, `EffectiveRightsCalculator`, computes an account's rights with their origin:
   - roles = the role template of the employee's position;
   - brand approval scope = "brand manager" assignments on brands, effective only while the position grants BRAND_MANAGER;
   - hub access = "keeper" or "service base" assignments on warehouses;
   - nothing at all for an employee who left or was anonymised, or a locked account.

   The claims, the admin screen (`/admin/users`, read-only rights with origin) and the SQL session context all come from it. No endpoint or screen edits a right directly; `POST /users` and role editing are removed, and the legacy tables are dropped once the derived rights are proven equal (STAGE_7_PLAN 1.6).
3. **Assignments live with the record they are about.** "Brand manager of CLAAS" is edited on the brand, "keeper of KLP" on the warehouse, each in the module that owns the record, in one table per record kind with a real foreign key to the record. The rights-bearing ones reach Identity as `ResponsibilitiesChanged` (state transfer, idempotent and order-safe), and Identity derives from a read-only projection. Granting or revoking a rights-bearing assignment requires the `Administration` policy, whichever module owns the record. (Implemented in PR 7d; until then brands and hub come from the account's own lists, shown as their origin.)
4. **Immediate effect, with a deliberate 30-second window.** Every change that alters an account's rights rotates its security stamp: a new position, a changed role template (every holder), an assignment, leaving the company. Sign-in checks the employee's status directly, so an employee who left is refused **at once**. An open session re-validates at the BFF's session check, which becomes **30 seconds** (`Bff:SessionCheckInterval`, PR 7e; 5 minutes before). This window is a conscious trade-off (product owner, 27 November 2026): checking on every request would add a database or HTTP call to every proxied request in both topologies, while 30 seconds bounds the exposure of a revoked right and blocks new sign-ins immediately.
5. **Hub access is a list and guards writes only.** `ss:warehouse` may repeat; the session GUC `app.warehouse_ids` replaces `app.home_warehouse_id`, and `util.require_hub(warehouse)` (SS302) replaces the inline checks (PR 7d). Only ADMINISTRATOR is unrestricted; a keeper without a hub assignment has no hub access (least privilege; before, a missing home hub meant unrestricted). **Reads are never scoped** by hub or brand, as decided in stage 3: planners and brand managers see stock across every hub and brand, and brand managers approve only their own brands.
6. **The one cross-module data step.** Moving the existing brand lists and home hubs into the assignment tables of Inventory (PR 7d) is a one-time copy run by the **Reporting** migration. It is the only exception to "a module's migration touches only its own schema" in this stage, for three reasons: the migrator applies Reporting last, after Identity and Inventory have created both sides; Reporting is already the one context allowed to read every schema (MODULE_MAPPING 3.3); and no module migration has to read another module's schema. The copy is idempotent (`ON CONFLICT DO NOTHING`, skipped once the legacy objects are gone), covered by the migration test (run twice), and runs before any production database exists (stage 10).

**As built (PR 7e):** the legacy tables and the home hub are dropped behind a drift guard. The account's copies of the employee's name, e-mail and phone are emptied rather than dropped, because an applied Reporting script reads `app_user.full_name` and applied scripts are never edited (#152); nothing writes them, and the audit trail names the actor from the employee.

## Superseded parts of earlier decisions

Marked in place, not rewritten:

| Document | Part | Now |
|---|---|---|
| [STAGE_3_PLAN.md](../stage-3/STAGE_3_PLAN.md) | 3.1 claims from `iam.user_role`, `iam.user_brand` and one `home_warehouse_id`; 3.3 brands of a brand manager from `iam.user_brand` | Rights are derived from the position and assignments (decision 2); hubs are a list (decision 5) |
| [ADR 0016](0016-authentication-behind-the-gateway.md) | `ss:warehouse` as one home hub | A list of hubs, write access only (decision 5) |
| [MODULE_MAPPING.md](../database/MODULE_MAPPING.md) | 2.2 `iam.app_user.home_warehouse_id`; 3.6 `app.home_warehouse_id` | Warehouse assignments; `app.warehouse_ids` (decision 5) |
| [DATABASE_DESIGN.md](../database/DATABASE_DESIGN.md) | 8.3 session context, 8.5 personal data of users | `app.warehouse_ids`; personal data lives on the employee, erased by `iam.anonymise_employee` |

## Verification

| Rule | Where | Test |
|---|---|---|
| Who may do what does not change by accident | — | `AccessMatrixTests`: the golden matrix of every T001 user recorded on `main` before the rework (claims, policies, brand-scoped actions, hub writes, reads) |
| Roles come from the position, with origin | `EffectiveRightsCalculator`, `AppUserClaimsPrincipalFactory` | `OrganisationTests`, `ClaimsPrincipalFactoryTests` |
| No account without an employee; no direct editing path | `iam.app_user.employee_id NOT NULL`, removed endpoints | `EndpointTableTests.No_endpoint_edits_roles_on_a_user`, `UserAdministrationTests.No_endpoint_creates_a_user_without_an_employee` |
| Changes take effect; leaving blocks sign-in at once | `AccountStamps`, `ActiveUserConfirmation` | `UserAdministrationTests`, `OrganisationRulesTests` |
| The last administrator stays | `AdministratorGuard` | `OrganisationRulesTests.The_last_administrator_can_neither_leave_nor_lose_the_role` |
| Existing data keeps exactly its rights | `identity/0007_organisation.sql` | `AccessMigrationTests` |

## Consequences

- Every right on the admin screen says where it comes from, and is changed where the organisation changes: a position, a template, an assignment, an employee leaving.
- A position's template change applies to all its holders at once; an unusual combination of roles needs a position of its own (the migration creates one per unusual role set).
- Rights-bearing assignments arrive in Identity with event latency (milliseconds over RabbitMQ, one dispatcher cycle in the single host), well inside the 30-second window; `inventory.responsibility-publish` repairs a lost event.
- Personal data of staff lives on the employee; anonymising an employee also erases the account, and the audit trail never holds the personal columns.
