using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.MigrationRunner;
using SmartSpare.ModuleCatalog;

// Usage (locally, in CI, and as the Container Apps migration job in Azure, ADR 0020):
//   dotnet run --project src/Bootstrapper/SmartSpare.MigrationRunner                 apply all migrations
//   dotnet run --project src/Bootstrapper/SmartSpare.MigrationRunner -- --module inventory
//                                apply the Platform's and one module's migrations; fails if an earlier module is behind
//   dotnet run --project src/Bootstrapper/SmartSpare.MigrationRunner -- check-model  exit 1 if a context's model
//                                                                                    has changes without a migration
//   dotnet run --project src/Bootstrapper/SmartSpare.MigrationRunner -- list         print the migration order
//   dotnet run --project src/Bootstrapper/SmartSpare.MigrationRunner -- set-demo-passwords
//                                hash SMARTSPARE_DEMO_USER_PASSWORD for the T001 demo users (after loading T001)
// The command is the first argument that is neither an option nor an option's value (--module inventory).
var command = args.Where((a, i) => !a.StartsWith('-') && (i == 0 || !args[i - 1].StartsWith("--", StringComparison.Ordinal) || args[i - 1].Contains('=', StringComparison.Ordinal)))
    .FirstOrDefault() ?? "migrate";

var builder = Host.CreateApplicationBuilder(args);
builder.ConfigureOpenTelemetry();

var options = builder.Configuration.GetSection(MigrationRunnerOptions.SectionName).Get<MigrationRunnerOptions>()
    ?? new MigrationRunnerOptions();
options.AdminConnectionString ??= builder.Configuration.GetConnectionString("postgres");
options.Module ??= builder.Configuration["module"];

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

    case "set-demo-passwords":
        var password = builder.Configuration[DemoPasswords.PasswordVariable];
        if (string.IsNullOrWhiteSpace(password))
        {
            await Console.Error.WriteLineAsync(
                $"{DemoPasswords.PasswordVariable} is not set: choose a demo password in .env (see .env.example).").ConfigureAwait(false);
            return 1;
        }

        var updated = await DemoPasswords.SetAsync(
            options.BuildMigratorConnectionString(), password, CancellationToken.None,
            builder.Configuration[DemoPasswords.SuperadministratorPasswordVariable]).ConfigureAwait(false);
        Console.WriteLine($"Set the password of {updated} demo users (anonymised users are skipped).");
        return 0;

    default:
        await Console.Error.WriteLineAsync($"Unknown command '{command}'. Use migrate, check-model, list or set-demo-passwords.").ConfigureAwait(false);
        return 2;
}
