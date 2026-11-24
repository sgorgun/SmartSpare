extern alias runner;

using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;
using Npgsql;
using runner::SmartSpare.MigrationRunner;
using Testcontainers.PostgreSql;

namespace SmartSpare.E2E;

/// <summary>
/// The whole application for the smoke test (STAGE_3_PLAN 9.4): PostgreSQL 17 built by the migration runner, the T001
/// demo dataset, demo passwords set by <c>set-demo-passwords</c>, the real host on Kestrel with HTTPS (the session
/// cookies are <c>Secure</c>), and headless Chromium.
/// </summary>
public sealed class E2EEnvironment : IAsyncLifetime
{
    /// <summary>Small demo dataset: the smoke test needs the users, not the volume.</summary>
    private const int DemoParts = 200;

    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder("postgres:17").Build();
    private HostOnKestrel? _host;
    private IPlaywright? _playwright;

    public string DemoPassword { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));

    public Uri BaseAddress { get; private set; } = null!;

    public IBrowser Browser { get; private set; } = null!;

    /// <summary>Where screenshots and traces go; CI uploads it when the test fails.</summary>
    public string ArtifactsDirectory { get; } = Path.Combine(RepositoryRoot(), "TestResults", "e2e");

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(ArtifactsDirectory);
        await _database.StartAsync(ct);

        var options = new MigrationRunnerOptions
        {
            AdminConnectionString = _database.GetConnectionString(),
            Passwords = new BootstrapPasswords { Migrator = NewSecret(), Api = NewSecret(), Jobs = NewSecret(), Reporting = NewSecret() },
        };
        await new DatabaseMigrator(options, ModuleCatalog.ModuleCatalog.Databases(), NullLogger<DatabaseMigrator>.Instance).RunAsync(ct);
        await LoadDemoDatasetAsync(options.Passwords.Migrator!, ct);
        await DemoPasswords.SetAsync(options.BuildMigratorConnectionString(), DemoPassword, ct);

        _host = new HostOnKestrel(new NpgsqlConnectionStringBuilder(_database.GetConnectionString())
        {
            Database = "smartspare",
            Username = "smartspare_api",
            Password = options.Passwords.Api,
        }.ConnectionString);
        BaseAddress = _host.Start();

        // Idempotent; CI installs Chromium with its system dependencies before the test (ci.yml, job E2E).
        if (Microsoft.Playwright.Program.Main(["install", "chromium"]) != 0)
        {
            throw new InvalidOperationException("Playwright could not install Chromium.");
        }

        _playwright = await Playwright.CreateAsync();
        Browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
    }

    public async ValueTask DisposeAsync()
    {
        if (Browser is not null)
        {
            await Browser.DisposeAsync();
        }

        _playwright?.Dispose();
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }

        await _database.DisposeAsync();
    }

    /// <summary>Runs <c>db/testdata/T001__demo_dataset.sql</c> with the container's psql, as the migrator (like verify_database.sh).</summary>
    private async Task LoadDemoDatasetAsync(string migratorPassword, CancellationToken ct)
    {
        var script = await File.ReadAllBytesAsync(Path.Combine(RepositoryRoot(), "db", "testdata", "T001__demo_dataset.sql"), ct);
        await _database.CopyAsync(script, "/tmp/T001__demo_dataset.sql", ct: ct);
        var result = await _database.ExecAsync(
            ["bash", "-c", $"PGPASSWORD='{migratorPassword}' psql -X -q -v ON_ERROR_STOP=1 -h localhost -U smartspare_migrator -d smartspare -v parts={DemoParts} -f /tmp/T001__demo_dataset.sql"],
            ct);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"T001 failed: {result.Stderr}");
        }
    }

    private static string NewSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    internal static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SmartSpare.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("SmartSpare.slnx not found above " + AppContext.BaseDirectory);
    }

    /// <summary>The real host (<c>Program</c>) on Kestrel with a throw-away certificate, at a free loopback port.</summary>
    private sealed class HostOnKestrel : WebApplicationFactory<Program>
    {
        private readonly string _connectionString;
        private readonly X509Certificate2 _certificate = SelfSignedCertificate();

        public HostOnKestrel(string connectionString)
        {
            _connectionString = connectionString;
            UseKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(_certificate)));
        }

        public Uri Start()
        {
            StartServer();
            var addresses = Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
                ?? throw new InvalidOperationException("Kestrel reports no address.");
            var address = addresses.Addresses.First();
            return new Uri(address.Replace("127.0.0.1", "localhost", StringComparison.Ordinal));
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.UseSetting("ConnectionStrings:smartspare", _connectionString);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _certificate.Dispose();
            }
        }

        private static X509Certificate2 SelfSignedCertificate()
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("localhost");
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
            // Re-imported from PKCS#12 so the private key is usable by the TLS stack on every platform.
            return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pkcs12), password: null);
        }
    }
}

[CollectionDefinition(Name)]
public sealed class E2EGroup : ICollectionFixture<E2EEnvironment>
{
    public const string Name = "End-to-end environment";
}
