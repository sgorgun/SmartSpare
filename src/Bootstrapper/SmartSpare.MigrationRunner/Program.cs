using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.MigrationRunner;
using SmartSpare.ModuleCatalog;

// Usage (development and CI only; Azure uses EF migration bundles):
//   dotnet run --project src/Bootstrapper/SmartSpare.MigrationRunner                 apply all migrations
//   dotnet run --project src/Bootstrapper/SmartSpare.MigrationRunner -- check-model  exit 1 if a context's model
//                                                                                    has changes without a migration
//   dotnet run --project src/Bootstrapper/SmartSpare.MigrationRunner -- list         print the migration order
var command = args.FirstOrDefault(a => !a.StartsWith('-')) ?? "migrate";

var builder = Host.CreateApplicationBuilder(args);
builder.ConfigureOpenTelemetry();

var options = builder.Configuration.GetSection(MigrationRunnerOptions.SectionName).Get<MigrationRunnerOptions>()
    ?? new MigrationRunnerOptions();
options.AdminConnectionString ??= builder.Configuration.GetConnectionString("postgres");

using var host = builder.Build();
var databases = ModuleCatalog.Databases();

switch (command)
{
    case "list":
        foreach (var database in databases)
        {
            Console.WriteLine($"{database.Name}: {database.HistorySchema}.{PersistenceRegistration.MigrationsHistoryTable}");
        }

        return 0;

    case "check-model":
        var changed = databases.Where(d => d.HasPendingModelChanges()).Select(d => d.Name).ToList();
        foreach (var name in changed)
        {
            await Console.Error.WriteLineAsync(
                $"{name}: the model has changes that are not in a migration; add one with 'dotnet ef migrations add'.").ConfigureAwait(false);
        }

        if (changed.Count == 0)
        {
            Console.WriteLine($"No pending model changes in {databases.Count} context(s).");
        }

        return changed.Count == 0 ? 0 : 1;

    case "migrate":
        var migrator = new DatabaseMigrator(options, databases, host.Services.GetRequiredService<ILogger<DatabaseMigrator>>());
        try
        {
            await migrator.RunAsync(CancellationToken.None).ConfigureAwait(false);
            return 0;
        }
#pragma warning disable CA1031 // Top level: report any failure and exit non-zero so Aspire and CI stop.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            migrator.LogFailed(ex);
            return 1;
        }

    default:
        await Console.Error.WriteLineAsync($"Unknown command '{command}'. Use migrate, check-model or list.").ConfigureAwait(false);
        return 2;
}
