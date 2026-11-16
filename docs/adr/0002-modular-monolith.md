# ADR 0002: Microservice-ready modular monolith

- Status: Accepted
- Date: 2026-11-16

## Context

The system must be production-grade yet simple, cheap to run as an individual project, and easy to split into microservices after the defence. The BA (section 2.4) rejected microservices for cost and DevOps overhead.

## Decision

Build a modular monolith with six business modules (Identity, Inventory, Fleet, Procurement, Forecasting, Reporting) plus shared BuildingBlocks. Each module is two projects (implementation and `Contracts`) with its own PostgreSQL schema, DbContext and migrations history. Inside a module: Domain / Features (vertical slices) / Infrastructure folders, DDD tactical patterns, CQRS-lite (commands via aggregates, queries projected to DTOs) and the Result pattern for expected errors.

## Consequences

- One deployment and one database today; extracting a module is mechanical.
- Requires discipline on boundaries, enforced by architecture tests.
- Reporting reads other modules through marts as a deliberate, documented exception.
