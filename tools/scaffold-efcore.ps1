# Generates EF Core entity classes + DbContext from the migrated database (database-first).
# Run after `docker compose up -d` and db/scripts/reset_local.sh. Requires: dotnet tool install -g dotnet-ef
# The migrator password comes from the environment or from .env (gitignored, see .env.example).
$password = $env:SMARTSPARE_MIGRATOR_PASSWORD
if (-not $password -and (Test-Path .env)) {
    $line = Get-Content .env | Where-Object { $_ -match '^\s*SMARTSPARE_MIGRATOR_PASSWORD\s*=' } | Select-Object -First 1
    if ($line) { $password = ($line -split '=', 2)[1].Trim().Trim('"', "'") }
}
if (-not $password) { throw "Set SMARTSPARE_MIGRATOR_PASSWORD in .env (copy .env.example)." }
$conn = "Host=localhost;Database=smartspare;Username=smartspare_migrator;Password=$password"
dotnet ef dbcontext scaffold $conn Npgsql.EntityFrameworkCore.PostgreSQL `
  --project src/BuildingBlocks/SmartSpare.Infrastructure `
  --output-dir Persistence/Generated --context-dir Persistence --context SmartSpareDbContext `
  --schema ref --schema iam --schema catalog --schema inventory --schema fleet `
  --schema procurement --schema forecast --schema audit --schema raw --schema staging --schema mart `
  --no-onconfiguring --use-database-names --force
