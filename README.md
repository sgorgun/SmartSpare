# SmartSpare

Agricultural machinery maintenance planning and spare-parts demand forecasting (EHU bachelor's diploma project, Sergei Gorgun, 2026/27).

Stack: ASP.NET Core 10 · Blazor WebAssembly + MudBlazor · EF Core 10 + Npgsql · **PostgreSQL 17** · ML.NET · Quartz.NET · Microsoft Azure.

## Repository layout

```
db/
  bootstrap/000_bootstrap_cluster.sql   roles + database (once per server, server admin)
  migrations/V001…V016__*.sql           versioned schema migrations (source of truth)
  seed/R001__reference_data.sql         repeatable reference data
  testdata/T001__demo_dataset.sql       deterministic synthetic dataset (dev/test only)
  tests/                                SQL test suite (97 assertions) + concurrency test
  scripts/migrate.sh, reset_local.sh    migration runner (journal, checksums, advisory lock)
  ddl/smartspare_schema.sql             complete DDL (generated)
docs/
  architecture/ARCHITECTURE_DECISIONS.md approved target architecture (start here)
  architecture/ARCHITECTURE.md          database-stage architecture baseline (superseded)
  adr/                                  architecture decision records
  database/DATABASE_DESIGN.md           database design + requirements compliance matrix
  database/DATA_DICTIONARY.md           data dictionary (generated from the catalogue)
  database/diagrams/                    logical schema (colour-coded), data flow
src/
  SmartSpare.DbMigrator/                C# migrator for the Azure pipeline
  SmartSpare.Infrastructure/Persistence EF Core integration (session context, error mapping, enums)
tools/                                  doc/diagram/DDL generators, EF Core scaffolding
```

## Quick start (database)

```bash
docker compose up -d
db/scripts/reset_local.sh                         # bootstrap + migrations + reference data
PGHOST=localhost PGDATABASE=smartspare PGUSER=smartspare_migrator PGPASSWORD=dev_migrator \
  psql -v parts=2000 -f db/testdata/T001__demo_dataset.sql
PGHOST=localhost PGDATABASE=smartspare PGUSER=postgres PGPASSWORD=postgres db/tests/run_tests.sh
```

On Windows run the `.sh` scripts in Git Bash or WSL. Demo users (synthetic) log in with the password `SmartSpare#Demo2026`, e.g. `tomas.radzevicius` (brand manager) or `mantas.petrauskas` (service engineer).

## Diagrams

| Diagram | Image | Editable (draw.io) |
|---|---|---|
| Logical schema — overview (keys) | `docs/database/diagrams/logical_schema_overview.svg` | `logical_schema_overview.drawio` |
| Logical schema — OLTP core (all columns) | `docs/database/diagrams/logical_schema_core.svg` | `logical_schema_core.drawio` |
| Import data layers raw → staging → core | `docs/database/diagrams/logical_schema_import_layers.svg` | `logical_schema_import_layers.drawio` |
| Data flow through the layers | `docs/database/diagrams/data_flow.svg` | `data_flow.drawio` |
| Solution architecture | `docs/architecture/diagrams/architecture.svg` | `architecture.drawio` |

* **All five diagrams in one file (one page each):** `docs/SmartSpare_diagrams.drawio`.
  Open with [app.diagrams.net](https://app.diagrams.net) (File → Open from → Device), the draw.io desktop app,
  or VS Code with the *Draw.io Integration* extension. Export to PNG/SVG/PDF/VSDX from File → Export as.
* **Schema as code (DBML):** `docs/database/diagrams/smartspare_core.dbml` — 43 tables, 11 enums, 80 relationships,
  8 colour-coded table groups (modules). Open [dbdiagram.io](https://dbdiagram.io), create a diagram and paste the file
  (or Import → DBML). Free dbdiagram diagrams are public — do not upload anything confidential.
* All diagram files are **generated** from the database: after a migration run
  `python3 tools/generate_db_docs.py && python3 tools/generate_editable_diagrams.py`.
  Manual layout changes made in draw.io are lost on regeneration — save such versions under a different name.

## Rules for changing the database

1. Never edit an applied `V`-script — add `V016__…sql` (the runner detects checksum drift and stops).
2. Grant privileges for new objects in the same migration; `db/tests/T03` fails if `util.v_privilege_findings` returns rows.
3. Regenerate docs: `python3 tools/generate_db_docs.py && python3 tools/generate_editable_diagrams.py && tools/export_ddl.sh`.
4. `*.sql` files are checked out with LF line endings (`.gitattributes`) so checksums are identical on Windows and Linux.
