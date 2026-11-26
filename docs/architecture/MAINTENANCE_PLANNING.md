# SmartSpare — Maintenance Planning: the Due Calculation

Status: implemented in stage 6 (PR 6c, [STAGE_6_PLAN.md](../stage-6/STAGE_6_PLAN.md) section 2). Code:
`src/Modules/Fleet/SmartSpare.Modules.Fleet/Domain/Maintenance/MaintenanceDueCalculator.cs` and `UsageRate.cs`;
tests: `tests/SmartSpare.Modules.Fleet.UnitTests/Domain/MaintenanceDueCalculatorTests.cs` (every rule below) and the
parity integration test against `fleet.fn_maintenance_due`.

The question the calculation answers for a dealer's service department: **which customer machines need a planned
service soon, and when?** Agricultural machines are serviced by engine hours (an OEM plan says "engine service every
500 h") and often also by calendar ("at least once a year"), whichever comes first. A dealer sees only the readings the
engineers log at visits or that arrive from telematics, so the calculation projects forward from the reading history.

---

## 1. Inputs

| Symbol | Meaning | Source |
|---|---|---|
| *H* | OEM interval in engine hours | `fleet.maintenance_plan.interval_hours` |
| *M* | OEM calendar interval in months (optional) | `fleet.maintenance_plan.interval_months` |
| *f* | factor of the machine's operating condition (1.0 normal; < 1 for severe dust, high load, non-OEM oil — BA finding F5) | `ref.operating_condition.interval_factor` |
| *h* | current engine hours (latest reading) | `fleet.machine.current_engine_hours` |
| *h₀* | first known reading | `fleet.engine_hours_reading` |
| *L* | highest threshold already covered by a non-cancelled (open or completed) service order of the plan | `fleet.service_order.due_engine_hours` |
| readings | (time, hours) pairs | `fleet.engine_hours_reading` |
| *A* | calendar anchor: last completion of the plan, else commissioning date, else first reading | `fleet.service_order.completed_at`, `fleet.machine.commissioned_on` |

## 2. The formula

**Effective interval.** E = round(H · f, 1).

**Next due threshold.** Without a served threshold, L = ⌊h₀ / E⌋ · E (thresholds before the first known reading are
unknown history: a machine taken over at 1,230 h is not asked for its 500 h and 1,000 h services). Then

  D = max((⌊L / E⌋ + 1) · E, ⌊h / E⌋ · E),

so if several thresholds were skipped, only the latest crossed one is due.

**Remaining hours.** R = D − h; R ≤ 0 means the service is overdue by −R hours.

**Average daily usage.** Over the readings of the 365 days before the latest reading,

  u = (h_last − h_first) / days(t_first, t_last),

known only when two readings in the window are at least 14 days apart; otherwise the whole history is used under the
same rule; otherwise *u* is unknown. The window covers a whole year on purpose: machines work in campaigns (sowing,
harvest), and a short window would project a harvest-week rate over the winter. Seasonal profiles are a possible
refinement (#119).

**Projected due date by hours.** t_h = t_last + ⌈R / u⌉ days when *u* > 0 (the last reading's day when R ≤ 0); no date
when *u* is unknown or zero.

**Calendar due date.** t_c = A + M months, when the plan has a calendar interval.

**Due date.** t_due = min(t_h, t_c), and the trigger is the interval that gives it.

**Status**, with the planning horizon P (default 14 days):

| Status | Rule |
|---|---|
| Planned | an open service order of the plan exists (it counts as served in *L*, so D is already the threshold after it) |
| Overdue | R ≤ 0, or t_c ≤ today |
| Due soon | R ≤ 50 h (the drafting horizon of US05), or t_due ≤ today + P |
| OK | otherwise |

## 3. Worked example

A combine with an engine service every 500 h and every 12 months (normal conditions, f = 1.0), commissioned on
15 June 2025. Readings: 300 h on 28 August 2026 and 440 h on 6 November 2026. Today is 26 November 2026.

| Step | Value |
|---|---|
| E | round(500 · 1.0, 1) = 500 h |
| L | no order yet: ⌊300 / 500⌋ · 500 = 0 |
| D | max(500, ⌊440 / 500⌋ · 500 = 0) = 500 h |
| R | 500 − 440 = 60 h |
| u | (440 − 300) / 70 days = 2.0 h/day |
| t_h | 6 Nov + ⌈60 / 2⌉ = 30 days → 6 December 2026 |
| t_c | 15 Jun 2025 + 12 months → 15 June 2026 |
| t_due | min(6 Dec 2026, 15 Jun 2026) = 15 June 2026, trigger: calendar |
| Status | t_c ≤ today → **overdue** (by calendar, although 60 h remain) |

Without the calendar interval the same machine is **due soon**: t_h = 6 December is within 14 days of today. When the
engineer logs 512 h (UC02), R = −12 h, `fleet.log_engine_hours` drafts the 500 h service (R ≤ 50 h), and the plan shows
**planned** while the order is open. The open order counts as served, so the next threshold is already 1,000 h
(R = 488 h); once the order is completed, its completion date becomes the calendar anchor.

## 4. Two places, one rule

`fleet.fn_maintenance_due` (SQL) and `MaintenanceDueCalculator` (C#) compute the same E, L, D and R:

- **SQL decides the automatic drafts.** `fleet.log_engine_hours` inserts the reading and drafts every plan with
  R ≤ 50 h in the same transaction, under the machine row lock, with the partial unique index
  `uq_service_order_machine_plan_due` against duplicates. This is the last line of defence and stays in the database
  (ARCHITECTURE_DECISIONS §5).
- **C# plans ahead.** Usage, projected and calendar dates and the status are planning information for the due list and
  the machine card; they never write.
- **Parity is tested**: an integration test runs both over the whole demo fleet (500 machines, two plans each) and
  requires the same D and R for every machine and plan.

Calendar-due plans are not drafted automatically (a reading is not their trigger); the engineer creates the order from
the due list, with D from the calculation, so a later automatic draft never duplicates it.

## 5. Limits

- The projection assumes the average rate continues; it does not know the next campaign. A machine idle since its last
  reading keeps its old projected date until a new reading arrives.
- Readings are only as good as the visits and telematics feeds; the odometer rules (SS100–SS103) reject decreasing,
  implausible and out-of-order values, but not an honest typo within the plausible range.
- The planning horizon P is a parameter of the due list (`horizonDays`), not a property of the plan.
