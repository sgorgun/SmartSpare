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
  architecture/ARCHITECTURE.md          solution architecture
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

## Rules for changing the database

1. Never edit an applied `V`-script — add `V016__…sql` (the runner detects checksum drift and stops).
2. Grant privileges for new objects in the same migration; `db/tests/T03` fails if `util.v_privilege_findings` returns rows.
3. Regenerate docs: `python3 tools/generate_db_docs.py && tools/export_ddl.sh`.
4. `*.sql` files are checked out with LF line endings (`.gitattributes`) so checksums are identical on Windows and Linux.
