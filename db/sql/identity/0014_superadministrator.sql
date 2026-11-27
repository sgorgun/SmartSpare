-- =============================================================================
-- Identity 0014 — the Superadministrator (ADR 0028, decided by the owner on 27 November 2026, PR 7x3b).
-- A locked position whose locked role template holds every permission, with no brand or hub scope limit (the scope
-- permissions are part of "every permission"). Superadministrators are ordinary employees in that position, signing in
-- with their own accounts; rights still come only from the position. The application refuses to change, archive or
-- delete the position and the role, to put the role in any other template, to move anyone into or out of the position
-- except by another superadministrator (never oneself), and to remove the last active one. It is not exempt from the
-- database invariants or from the four-eyes rule. For emergencies, not daily work.
-- =============================================================================

-- iam.position.is_locked is added by the EF migration of this step.

INSERT INTO iam.role (code, name, description, is_locked) VALUES
    ('SUPERADMINISTRATOR', 'Superadministrator', 'Every permission, no scope limit; held only by the locked Superadministrator position. For emergencies.', true);
INSERT INTO iam.role_permission (role_id, permission)
SELECT r.id, p.code FROM iam.role r CROSS JOIN iam.permission p WHERE r.code = 'SUPERADMINISTRATOR';

INSERT INTO iam.position (code, name, description, is_locked) VALUES
    ('SUPERADMINISTRATOR', 'Superadministrator', 'Emergency access with every permission; assigned only by another superadministrator.', true);
INSERT INTO iam.position_role (position_id, role_id)
SELECT p.id, r.id FROM iam.position p, iam.role r WHERE p.code = 'SUPERADMINISTRATOR' AND r.code = 'SUPERADMINISTRATOR';
