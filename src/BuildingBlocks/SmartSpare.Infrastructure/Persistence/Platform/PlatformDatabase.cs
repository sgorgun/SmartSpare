using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SmartSpare.Infrastructure.Persistence.Platform;

/// <summary>Public entry point of the Platform persistence for the module catalogue and the migration runner.</summary>
public static class PlatformPersistence
{
    /// <summary>Migrations of the Platform schemas <c>ref</c>, <c>util</c>, <c>audit</c>; always applied first.</summary>
    public static IModuleDatabase Database => PlatformDatabase.Instance;
}

internal sealed class PlatformDatabase : ModuleDatabase<PlatformDbContext>
{
    public static PlatformDatabase Instance { get; } = new();

    private PlatformDatabase()
    {
    }

    public override string Name => "platform";

    public override string HistorySchema => PlatformDbContext.HistorySchema;

    protected override PlatformDbContext CreateContext(DbContextOptions<PlatformDbContext> options) => new(options);
}

/// <summary>
/// Used by <c>dotnet ef</c> only (migrations add / has-pending-model-changes). No database is
/// contacted; the connection string is a placeholder unless SMARTSPARE_DESIGN_CONNECTION is set.
/// </summary>
internal sealed class PlatformDesignTimeFactory : IDesignTimeDbContextFactory<PlatformDbContext>
{
    public PlatformDbContext CreateDbContext(string[] args) =>
        PlatformDatabase.Instance.CreateContext(
            Environment.GetEnvironmentVariable("SMARTSPARE_DESIGN_CONNECTION") ?? "Host=localhost;Database=smartspare");
}
