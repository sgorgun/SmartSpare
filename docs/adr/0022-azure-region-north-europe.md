# ADR 0022: Azure region North Europe

- Status: Accepted
- Date: 2026-11-25
- Amends: [ADR 0009](0009-azure-hosting-and-delivery.md) (the region of its cost estimate, West Europe)

## Context

ADR 0009 and the stage 4 plan priced the environment in West Europe, and the Bicep templates of PR 4h defaulted to it. The first what-if against the product owner's pay-as-you-go subscription (25 November 2026, read-only) refused every resource there: "The selected region is currently not accepting new customers" ([location eligibility](https://aka.ms/locationineligible)). Nothing else in the templates was rejected.

## Decision

All SmartSpare resources in Azure go to **North Europe** (`northeurope`, Ireland), chosen by the product owner on 25 November 2026. The templates' `location` parameter and the infrastructure workflow use it.

The what-if in North Europe succeeded: 45 resources to create, nothing modified or deleted, no errors.

## Consequences

- **Cost:** North Europe is cheaper than the West Europe figures of ADR 0009 for every meter the estimate uses. USD Retail Prices API, 25 November 2026:

  | Meter | West Europe | North Europe |
  |---|---|---|
  | PostgreSQL Flexible Server B1ms, per hour | $0.0199 | $0.0180 |
  | PostgreSQL storage, per GB-month | $0.1369 | $0.1265 |
  | Container Apps active vCPU, per second | $0.000034 | $0.000024 |
  | Container Apps active memory, per GiB-second | $0.000004 | $0.000003 |
  | Container Registry Basic | same | same |

  The ADR 0009 estimate therefore remains an upper bound, and NFR07 (≤ €35 a month) holds with more margin. The estimate isn't recomputed; the first months' actual cost is checked against it in stage 10 (#85).
- **Latency:** Ireland is about as far from Lithuania as the Netherlands; no requirement depends on the difference.
- **Neighbours:** ACR is in the same region as the apps, as cold start needs (STAGE_4_PLAN 8.7). CloudAMQP and Grafana Cloud stay where their instances were created. Traffic to them crosses the internet in either region.
- **If North Europe also closes to new customers before stage 10,** the region is again only the `location` parameter. The choice is made the same way: a what-if, then a short ADR.

## Alternatives considered

| Alternative | Why not |
|---|---|
| Sweden Central | Open and close to the Baltics, but its prices would need re-checking, and nothing argued for it over North Europe |
| Germany West Central | Slightly higher prices for some services, and sometimes restricted for new subscriptions too |
| Poland Central | Closest to Lithuania, but a newer region, where not every SKU is always offered |
| Ask Azure support for West Europe access | No guarantee, and nothing in SmartSpare depends on West Europe |
