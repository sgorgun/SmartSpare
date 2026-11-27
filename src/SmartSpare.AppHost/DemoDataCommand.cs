using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace SmartSpare.AppHost;

/// <summary>
/// The "Load demo data" command of the postgres resource (issue #33). It runs the same two steps as
/// <c>db/scripts/load_testdata.sh</c>:
/// <list type="number">
/// <item><c>db/testdata/T001__demo_dataset.sql</c> with the psql inside the postgres container
/// (<c>db/</c> is bind-mounted read-only), connected as <c>smartspare_migrator</c>;</item>
/// <item><c>MigrationRunner set-demo-passwords</c>, which hashes the AppHost's demo-user password.</item>
/// </list>
/// Passwords are handed to the child processes through their environment, never on a command line.
/// </summary>
internal static partial class DemoDataCommand
{
    public const string ContainerName = "smartspare-postgres";

    private const int DemoSkus = 2000;

    public static void Add(
        IResourceBuilder<PostgresServerResource> postgres,
        IResourceBuilder<ParameterResource> migratorPassword,
        IResourceBuilder<ParameterResource> demoUserPassword,
        IResourceBuilder<ParameterResource> superadminPassword,
        string migrationRunnerProject)
    {
        postgres.WithCommand(
            name: "load-demo-data",
            displayName: "Load demo data",
            executeCommand: context => ExecuteAsync(context, postgres.Resource, migratorPassword.Resource, demoUserPassword.Resource, superadminPassword.Resource, migrationRunnerProject),
            commandOptions: new CommandOptions
            {
                Description = "Loads the T001 demo dataset (2,000 SKUs) into an empty smartspare database and sets the demo users' password.",
                ConfirmationMessage = "Load the T001 demo dataset? It needs a freshly migrated database without demo data.",
                IconName = "DatabaseArrowDown",
            });
    }

    private static async Task<ExecuteCommandResult> ExecuteAsync(
        ExecuteCommandContext context,
        PostgresServerResource postgres,
        ParameterResource migratorPassword,
        ParameterResource demoUserPassword,
        ParameterResource superadminPassword,
        string migrationRunnerProject)
    {
        var cancellationToken = context.CancellationToken;
        var migrator = await migratorPassword.GetValueAsync(cancellationToken).ConfigureAwait(false);
        var demo = await demoUserPassword.GetValueAsync(cancellationToken).ConfigureAwait(false);
        var superadmin = await superadminPassword.GetValueAsync(cancellationToken).ConfigureAwait(false) ?? string.Empty;
        var admin = await postgres.ConnectionStringExpression.GetValueAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(migrator) || string.IsNullOrEmpty(demo) || string.IsNullOrEmpty(admin))
        {
            return CommandResults.Failure("The postgres connection or the generated passwords are not available yet.");
        }

        var t001 = await RunAsync(
            context.Logger,
            "docker",
            ["exec", "-e", "PGPASSWORD", ContainerName,
             "psql", "-X", "-q", "-v", "ON_ERROR_STOP=1", "-h", "localhost", "-U", "smartspare_migrator", "-d", "smartspare",
             "-v", $"parts={DemoSkus}", "-f", "/db/testdata/T001__demo_dataset.sql"],
            new Dictionary<string, string> { ["PGPASSWORD"] = migrator },
            cancellationToken).ConfigureAwait(false);
        if (t001 is not null)
        {
            return CommandResults.Failure($"T001 failed (is the demo data already loaded?): {t001}");
        }

        var passwords = await RunAsync(
            context.Logger,
            "dotnet",
            ["run", "--project", migrationRunnerProject, "--no-build", "--", "set-demo-passwords"],
            new Dictionary<string, string>
            {
                ["ConnectionStrings__postgres"] = admin,
                ["Migrations__Passwords__Migrator"] = migrator,
                ["SMARTSPARE_DEMO_USER_PASSWORD"] = demo,
                ["SMARTSPARE_SUPERADMIN_PASSWORD"] = superadmin,
            },
            cancellationToken).ConfigureAwait(false);

        return passwords is null
            ? CommandResults.Success("Demo data loaded. Sign in as e.g. tomas.radzevicius with the demo-user-password parameter.")
            : CommandResults.Failure($"set-demo-passwords failed: {passwords}");
    }

    /// <summary>Runs a process and returns <see langword="null"/> on success, otherwise the end of its error output.</summary>
    private static async Task<string?> RunAsync(
        ILogger logger, string fileName, IEnumerable<string> arguments, IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in environment)
        {
            start.Environment[name] = value;
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        var errors = new StringBuilder();
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { errors.AppendLine(e.Data); } };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { LogOutput(logger, fileName, e.Data); } };
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        if (process.ExitCode == 0)
        {
            return null;
        }

        var text = errors.ToString().Trim();
        return text.Length > 500 ? text[^500..] : text;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Tool}: {Line}")]
    private static partial void LogOutput(ILogger logger, string tool, string line);
}
