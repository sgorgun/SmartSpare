using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using SmartSpare.Infrastructure.Persistence;

namespace SmartSpare.Modules.Identity.Infrastructure.Persistence;

/// <summary>Migrations of the Identity module, applied after the Platform (ModuleCatalog order).</summary>
internal sealed class IdentityDatabase : ModuleDatabase<IdentityDbContext>
{
    public static IdentityDatabase Instance { get; } = new();

    private IdentityDatabase()
    {
    }

    public override string Name => "identity";

    public override string HistorySchema => IdentityDbContext.HistorySchema;

    protected override IdentityDbContext CreateContext(DbContextOptions<IdentityDbContext> options) => new(options);
}

/// <summary>Used by <c>dotnet ef</c> only; no database is contacted unless SMARTSPARE_DESIGN_CONNECTION is set.</summary>
internal sealed class IdentityDesignTimeFactory : IDesignTimeDbContextFactory<IdentityDbContext>
{
    public IdentityDbContext CreateDbContext(string[] args) =>
        IdentityDatabase.Instance.CreateContext(
            Environment.GetEnvironmentVariable("SMARTSPARE_DESIGN_CONNECTION") ?? "Host=localhost;Database=smartspare");
}
