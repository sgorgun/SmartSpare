# ADR 0004: Hybrid EF Core code-first

- Status: Accepted
- Date: 2026-11-16

## Context

The database was designed and tested as SQL migrations (V001–V016) with triggers, SECURITY DEFINER functions, partitions, roles and materialized views. EF Core code-first is the expected .NET approach, but EF cannot model all of these objects.

## Decision

Use EF Core code-first per module: tables, keys, intra-module FKs, indexes, CHECKs and enums in Fluent API; triggers, functions, views, roles, grants and EXCLUDE constraints as embedded SQL via `migrationBuilder.Sql`; partitioned tables created in SQL and marked `ExcludeFromMigrations()`. snake_case via `EFCore.NamingConventions`. Deploy with an EF migration bundle under the `smartspare_migrator` login; never migrate on application startup. Verify the port by diffing against `db/ddl/smartspare_schema.sql` and running the 97 SQL tests.

## Consequences

- Keeps every database-rubric point and the database as the last line of defence.
- `SmartSpare.DbMigrator` is retired.
- Part of the schema remains SQL that EF does not track, so those scripts need their own review.
