# ADR 0027: Part analogue model

- Status: Accepted
- Date: 2026-11-28
- Relates to: [STAGE_7_PLAN.md](../stage-7/STAGE_7_PLAN.md) sections 3.3 (C2) and 8, [ADR 0025](0025-generic-master-data-mechanism.md), [ADR 0008](0008-mlnet-demand-forecasting.md)

## Context

`catalog.part_analogue` (V016) links a new part to a historical one with a demand scaling factor (US15), keyed by the pair, with one meaning: "new SKU → predecessor". Dealers know two different relations between parts:

- **interchangeable**: either part may be fitted for the other, typically an OEM part and its aftermarket equivalent, often of another brand or manufacturer;
- **supersedes**: the manufacturer replaced an old part number with a new one, and the old one is no longer sold.

Stage 8 pools demand across analogues when it builds the training series (STAGE_7_PLAN section 8), so the model must say exactly which parts share a series. The rules must hold in the database, not only in C#, because CSV imports and future integrations write links too.

## Decision

1. **One link per pair, typed.** `catalog.part_analogue` gets a surrogate id and `link_type` (`catalog.analogue_link_type`: `interchangeable` | `supersedes`), keeps `scaling_factor` (units of the analogue per unit of the part, 0 < f ≤ 10) and `valid_from`, and gains `note` and audit columns. Links across brands are allowed; OEM ↔ aftermarket is the main real-world case. The existing T001 row becomes a supersession.
2. **INTERCHANGEABLE is symmetric and transitive.** It is stored once, with `part_id < analogue_part_id` (CHECK), and works both ways. A group is the **connected component** of the interchangeable links: an "interchangeability group", as many ERP systems model it. If A ≈ B and B ≈ C, then A, B and C are one group.
   **Limitation:** real interchangeability is not always transitive. A fits B and B fits C does not prove A fits C. Pooling a whole component may therefore pool parts that a fitter would not swap ("over-pooling"). This is accepted for the diploma scope: the groups are small, a planner sees each group on the part page, and removing a link splits it.
3. **SUPERSEDES points one way, and only its chains are checked for cycles.** `part_id` (new) replaces `analogue_part_id` (old). A part may have several predecessors, but **at most one successor** (partial unique index on `analogue_part_id` where `supersedes`). A supersession chain must not loop back (A → B → A). Interchangeable links form groups, not cycles, so they are not part of this check.
4. **Rules enforced in the database (SS502 `Catalog.AnalogueRule`).**
   - **No self-link:** CHECK.
   - **One link per unordered pair, whatever its type:** unique index on `least(part_id, analogue_part_id)`, `greatest(…)`.
   - **At most one successor:** the partial unique index of decision 3.
   - **No supersession cycle:** a `BEFORE INSERT OR UPDATE` trigger with a recursive walk, serialised by `pg_advisory_xact_lock` on the table, so two concurrent inserts cannot close a cycle together.
   - **No archived part** in a new or changed link (SS500).

   The C# `AnalogueRules` applies the same rules first, for readable messages and per-row CSV errors.
   - **Archiving (PR 7i3):** a part that is another part's successor, or has interchangeable links, cannot be archived (SS501 with the reason, in the API guard and in `trg_part_catalogue_guard`). Otherwise a chain could end in an archived part, which documents cannot choose (SS500), or a planner could be offered an archived stand-in. An old, superseded part may be archived: that is how a supersession normally ends.
5. **Mixed case, the order stage 8 uses.** For a part P:
   1. **Resolve** P to the end of its supersession chain: `current(P)`, with the product of the chain's scaling factors.
   2. **Pool** by the interchangeability group of that final part: `group(current(P))`.

   Two views implement exactly this order:
   - `catalog.v_part_successor(part_id, current_part_id, chain_length, factor)` resolves every part;
   - `catalog.v_part_analogue_group(part_id, group_id)` gives each part its interchangeability component, with `group_id` the smallest part id.

   `catalog.v_part_demand_key(part_id, current_part_id, group_id, factor)` joins them, giving each part the series its demand belongs to.
   **Example:** with A ≈ B and A → C (C supersedes A):
   - A's demand goes to C, and C's group is C alone;
   - B stays in the group {A, B}, but only with its own demand, because A already resolved to C.

   Every movement is counted exactly once: neither doubled nor lost. A test asserts that summing a demand table over the demand key gives the total.
   **The mixed case is resolved conservatively: B is not inferred to be interchangeable with C.**
6. **History is never rewritten.** Links are used at forecast time only; `inventory.stock_movement` is never re-keyed. Removing or retyping a link changes future forecasts, so it is audited with a **mandatory reason given by the user**, as part of the API contract:
   - **Retyping:** a required `reason` in the body of `PUT /part-analogues/{id}` when `linkType` changes.
   - **Removing:** `POST /part-analogues/{id}/remove` with the body `{ "reason": "…" }`.
   - A missing or empty reason is `400 Inventory.ReasonRequired`.
   - Inside the write's transaction, the database helper only passes the user's text into `app.reason`, which the audit trigger writes to `audit.audit_log.reason`. Nothing is invented.

   The screen asks for the reason in the confirmation dialog.
   **Why a POST action, not DELETE with a body or `?reason=`:**
   - it follows the catalogue convention for state changes (`POST /{id}/archive`, `/restore`), and an employee's archive already takes its reason in a body;
   - a DELETE body is ignored by some proxies and clients;
   - a free-text reason in a query string would land in access logs and traces.
7. **API and import.**
   - `GET /parts/{id}/analogues` returns the part's links, its group and its supersession chain.
   - `POST /part-analogues`, `PUT /part-analogues/{id}` (factor, valid from, note; a type change needs a reason) and `POST /part-analogues/{id}/remove` (with a reason) need `Inventory.Part.Edit`.
   - A CSV of pairs by brand and OEM number has a dry run. Each row's error names the pair and the rule (PR 7i2b).

## Consequences

- Stage 8 reads `v_part_demand_key` and needs no knowledge of the link table.
- A planner can split an over-pooled group by removing one link; the reason is in the audit trail and the next forecast follows.
- Substituting an analogue in a reservation is not built in stage 7 (#220). Analogues are shown read-only on a service order's kit shortage (PR 7i3) through `GET /parts/{id}/direct-analogues` and its batch form `GET /direct-analogues`. They show only direct interchangeable links and the current successor, never indirect group members, because transitivity (decision 2) is a forecasting assumption, not confirmed compatibility. Availability is on hand minus reserved at the hub. The web client composes this with Fleet's order through the gateway; the Fleet service never calls Inventory.

## Considered and rejected

- **Non-transitive interchangeability (pairwise only):** it is closer to reality, but stage 8 would need a pooling rule per pair, and groups could overlap, so one movement could land in two series.
- **One table per link type:** the "one link per pair whatever its type" rule would need a cross-table check.
- **Cycle check in C# only:** two concurrent writers, or a CSV import alongside the screen, could close a cycle between them.
