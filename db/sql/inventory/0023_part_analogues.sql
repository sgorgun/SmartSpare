-- =============================================================================
-- Inventory 0023 — part analogues as typed links (ADR 0027, PR 7i2a). Migration PartAnalogueLinks adds the surrogate
-- key, link_type (existing links become supersessions: the V016 meaning "new SKU -> predecessor"), note and audit
-- columns and the one-successor index. This script adds what EF cannot express:
--   * one link per unordered pair, whatever its type (unique index on least/greatest);
--   * trg_part_analogue_rules: self-link, duplicate pair, second successor, supersession cycle (SS502) and archived
--     parts (SS500), serialised by an advisory lock so that two concurrent writers cannot close a cycle together;
--     interchangeable links form groups, not cycles, and are not part of the cycle check;
--   * the stage 8 views in the order of ADR 0027 decision 5: resolve each part to the end of its supersession chain,
--     then pool by the interchangeability group of that final part (connected components; over-pooling accepted).
-- =============================================================================

ALTER TABLE catalog.part_analogue ALTER COLUMN link_type DROP DEFAULT;

CREATE UNIQUE INDEX uq_part_analogue_pair ON catalog.part_analogue (least(part_id, analogue_part_id), greatest(part_id, analogue_part_id));

CREATE FUNCTION catalog.trg_part_analogue_rules() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    PERFORM pg_advisory_xact_lock(hashtext('catalog.part_analogue'));

    IF NEW.part_id = NEW.analogue_part_id THEN
        RAISE EXCEPTION 'A part cannot be its own analogue' USING ERRCODE = 'SS502';
    END IF;

    IF TG_OP = 'INSERT' OR NEW.part_id <> OLD.part_id OR NEW.analogue_part_id <> OLD.analogue_part_id THEN
        IF EXISTS (SELECT 1 FROM catalog.part p WHERE p.id IN (NEW.part_id, NEW.analogue_part_id) AND p.is_archived) THEN
            RAISE EXCEPTION 'An archived part cannot be linked' USING ERRCODE = 'SS500';
        END IF;
    END IF;

    IF EXISTS (SELECT 1 FROM catalog.part_analogue a
               WHERE a.id <> NEW.id
                 AND least(a.part_id, a.analogue_part_id) = least(NEW.part_id, NEW.analogue_part_id)
                 AND greatest(a.part_id, a.analogue_part_id) = greatest(NEW.part_id, NEW.analogue_part_id)) THEN
        RAISE EXCEPTION 'Parts % and % are already linked', NEW.part_id, NEW.analogue_part_id USING ERRCODE = 'SS502';
    END IF;

    IF NEW.link_type = 'supersedes' THEN
        IF EXISTS (SELECT 1 FROM catalog.part_analogue a
                   WHERE a.id <> NEW.id AND a.link_type = 'supersedes' AND a.analogue_part_id = NEW.analogue_part_id) THEN
            RAISE EXCEPTION 'Part % already has a successor', NEW.analogue_part_id USING ERRCODE = 'SS502';
        END IF;

        -- "new replaces old" closes a cycle if the old part already replaces the new one through the chain.
        IF EXISTS (WITH RECURSIVE older(part_id) AS (
                       SELECT NEW.analogue_part_id
                       UNION
                       SELECT a.analogue_part_id FROM catalog.part_analogue a JOIN older o ON a.part_id = o.part_id
                       WHERE a.link_type = 'supersedes' AND a.id <> NEW.id)
                   SELECT 1 FROM older WHERE part_id = NEW.part_id) THEN
            RAISE EXCEPTION 'Part % superseding % would close a cycle', NEW.part_id, NEW.analogue_part_id USING ERRCODE = 'SS502';
        END IF;
    END IF;

    RETURN NEW;
END $$;
COMMENT ON FUNCTION catalog.trg_part_analogue_rules() IS
    'ADR 0027: no self-link, one link per pair, one successor, no supersession cycle (SS502), no archived part (SS500); serialised by an advisory lock.';

CREATE TRIGGER trg_part_analogue_rules BEFORE INSERT OR UPDATE ON catalog.part_analogue
    FOR EACH ROW EXECUTE FUNCTION catalog.trg_part_analogue_rules();
CREATE TRIGGER trg_part_analogue_updated_at BEFORE UPDATE ON catalog.part_analogue
    FOR EACH ROW EXECUTE FUNCTION util.trg_set_updated_at();
CREATE TRIGGER trg_audit AFTER INSERT OR UPDATE OR DELETE ON catalog.part_analogue
    FOR EACH ROW EXECUTE FUNCTION audit.trg_row_audit();

GRANT USAGE ON SEQUENCE catalog.part_analogue_id_seq TO app_write;

-- Step 1 of ADR 0027 decision 5: every part resolved to the end of its supersession chain, with the product of the
-- chain's scaling factors (units of the final part per unit of the part's history).
CREATE VIEW catalog.v_part_successor AS
WITH RECURSIVE walk (part_id, current_part_id, chain_length, factor) AS (
    SELECT p.id, p.id, 0, 1.000::numeric FROM catalog.part p
    UNION ALL
    SELECT w.part_id, a.part_id, w.chain_length + 1, w.factor * a.scaling_factor
    FROM walk w
    JOIN catalog.part_analogue a ON a.analogue_part_id = w.current_part_id AND a.link_type = 'supersedes'
    WHERE w.chain_length < 100
)
SELECT DISTINCT ON (part_id) part_id, current_part_id, chain_length, factor
FROM walk
ORDER BY part_id, chain_length DESC;
COMMENT ON VIEW catalog.v_part_successor IS
    'ADR 0027: each part resolved to the end of its supersession chain (current_part_id), with the chain length and the product of its scaling factors.';

-- Step 2: interchangeability groups, the connected components of the interchangeable links (transitive by decision 2;
-- real interchangeability is not always transitive, over-pooling is accepted). group_id is the smallest part id.
CREATE VIEW catalog.v_part_analogue_group AS
WITH RECURSIVE edge (a, b) AS (
    SELECT part_id, analogue_part_id FROM catalog.part_analogue WHERE link_type = 'interchangeable'
    UNION ALL
    SELECT analogue_part_id, part_id FROM catalog.part_analogue WHERE link_type = 'interchangeable'
), reach (part_id, member) AS (
    SELECT p.id, p.id FROM catalog.part p
    UNION
    SELECT r.part_id, e.b FROM reach r JOIN edge e ON e.a = r.member
)
SELECT part_id, min(member) AS group_id
FROM reach
GROUP BY part_id;
COMMENT ON VIEW catalog.v_part_analogue_group IS
    'ADR 0027: each part with its interchangeability group (connected component of interchangeable links; group_id = smallest part id).';

-- Both steps in order: the series a part's demand belongs to. Resolved conservatively: a part's demand goes to the end
-- of its chain and pools with that part's group only (with A ≈ B and A -> C, B is not inferred to be interchangeable
-- with C), so every movement is counted exactly once.
CREATE VIEW catalog.v_part_demand_key AS
SELECT s.part_id, s.current_part_id, g.group_id, s.factor
FROM catalog.v_part_successor s
JOIN catalog.v_part_analogue_group g ON g.part_id = s.current_part_id;
COMMENT ON VIEW catalog.v_part_demand_key IS
    'ADR 0027 decision 5 for stage 8: resolve to the end of the supersession chain, then pool by that part''s interchangeability group; each part maps to exactly one key.';

GRANT SELECT ON catalog.v_part_successor, catalog.v_part_analogue_group, catalog.v_part_demand_key TO app_read;
