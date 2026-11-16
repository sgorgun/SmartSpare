using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;
using SmartSpare.Infrastructure.Persistence;

namespace SmartSpare.Modules.Reporting.Infrastructure.Persistence;

/// <summary>Migrations of the Reporting module, applied after the Platform (ModuleCatalog order).</summary>
internal sealed class ReportingDatabase : ModuleDatabase<ReportingDbContext>
{
    public static ReportingDatabase Instance { get; } = new();

    private ReportingDatabase()
    {
    }

    public override string Name => "reporting";

    public override string HistorySchema => ReportingDbContext.HistorySchema;

    protected override ReportingDbContext CreateContext(DbContextOptions<ReportingDbContext> options) => new(options);
}

/// <summary>Used by <c>dotnet ef</c> only; no database is contacted unless SMARTSPARE_DESIGN_CONNECTION is set.</summary>
internal sealed class ReportingDesignTimeFactory : IDesignTimeDbContextFactory<ReportingDbContext>
{
    public ReportingDbContext CreateDbContext(string[] args) =>
        ReportingDatabase.Instance.CreateContext(
            Environment.GetEnvironmentVariable("SMARTSPARE_DESIGN_CONNECTION") ?? "Host=localhost;Database=smartspare");
}
