# Generates EF Core entity classes + DbContext from the migrated database (database-first).
# Run after `docker compose up -d` and db/scripts/reset_local.sh. Requires: dotnet tool install -g dotnet-ef
$conn = "Host=localhost;Database=smartspare;Username=smartspare_migrator;Password=dev_migrator"
dotnet ef dbcontext scaffold $conn Npgsql.EntityFrameworkCore.PostgreSQL `
  --project src/BuildingBlocks/SmartSpare.Infrastructure `
  --output-dir Persistence/Generated --context-dir Persistence --context SmartSpareDbContext `
  --schema ref --schema iam --schema catalog --schema inventory --schema fleet `
  --schema procurement --schema forecast --schema audit --schema raw --schema staging --schema mart `
  --no-onconfiguring --use-database-names --force
