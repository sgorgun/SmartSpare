# ADR 0007: .NET 10 LTS as the platform

- Status: Accepted
- Date: 2026-11-16

## Context

.NET 11 was released on 10 November 2026 as a Standard Term Support release; .NET 10 is the current Long Term Support release.

## Decision

Target .NET 10 LTS for all projects. Revisit at the next LTS release.

## Consequences

- Support until November 2028, covering the defence and the period after it.
- Stable versions of MudBlazor, EF Core and Npgsql.
- .NET 11 features (e.g. the Blazor gateway for standalone WebAssembly) are not used.
