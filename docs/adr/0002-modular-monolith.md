# ADR 0002: Microservice-ready modular monolith

- Status: Accepted, amended by [ADR 0015](0015-deployment-topology-microservices-behind-yarp-gateway.md) (2026-11-24)
- Date: 2026-11-16

## Context

The system must be production-grade yet simple, cheap to run as an individual project, and easy to split into microservices after the defence. The BA (section 2.4) rejected microservices for cost and DevOps overhead.

## Decision

Build a modular monolith with six business modules (Identity, Inventory, Fleet, Procurement, Forecasting, Reporting) plus shared BuildingBlocks. Each module is two projects (implementation and `Contracts`) with its own PostgreSQL schema, DbContext and migrations history. Inside a module: Domain / Features (vertical slices) / Infrastructure folders, DDD tactical patterns, CQRS-lite (commands via aggregates, queries projected to DTOs) and the Result pattern for expected errors.

## Consequences

- One codebase and one database. Development and tests run all modules in one host; production runs each module as its own service (see the amendment).
- Requires discipline on boundaries, enforced by architecture tests.
- Reporting reads other modules through marts as a deliberate, documented exception.

## Amendment (2026-11-24)

The product owner decided on 24 November 2026 (D1) that **production runs every module as its own independently deployable service**, behind a YARP gateway ([ADR 0015](0015-deployment-topology-microservices-behind-yarp-gateway.md)). The modular-monolith structure of this ADR stays unchanged:
- as the way the code is organised;
- as the development and test topology ("one codebase, two topologies").

The Context sentence about the BA rejecting microservices for cost and DevOps overhead describes the situation before D1. The cost consequence is recorded in ADR 0015 and #66.
