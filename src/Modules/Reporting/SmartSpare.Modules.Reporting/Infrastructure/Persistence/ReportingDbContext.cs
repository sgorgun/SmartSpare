using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;

namespace SmartSpare.Modules.Reporting.Infrastructure.Persistence;

/// <summary>
/// DbContext of the Reporting module: schema(s) mart (data marts, KPI views, refresh log); applied last because the marts read every schema (ADR 0011, MODULE_MAPPING section 1.7).
/// Tables, keys, intra-module FKs, indexes, CHECKs, enums and comments are modelled here with the names
/// of V016; functions, triggers, partitions, grants and seed data live in db/sql/reporting/.
/// </summary>
internal sealed class ReportingDbContext(DbContextOptions<ReportingDbContext> options) : DbContext(options)
{
    public const string HistorySchema = "mart";

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Every index is declared explicitly with its V016 name; EF must not add FK indexes of its own.
        configurationBuilder.Conventions.Remove<ForeignKeyIndexConvention>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ReportingDbContext).Assembly);
    }
}
