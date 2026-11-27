using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.Host.IntegrationTests.Support;
using SmartSpare.Infrastructure.Security;
using SmartSpare.Modules.Identity.Contracts;
using SmartSpare.SharedKernel;

namespace SmartSpare.Host.IntegrationTests;

/// <summary>
/// The golden access matrix of STAGE_7_PLAN section 1.7. Every T001 demo user signs in through the real Identity module,
/// and the test records what that user may do: the claims (roles, brands, hubs), every policy, every brand-scoped action
/// per brand, writes per hub (a stock adjustment: 201, or 403 with SS302) and reads per hub (stock and machines, never
/// scoped). The result must equal <c>AccessMatrix/access-matrix.json</c>, recorded on <c>main</c> before the
/// authorization rework, so the move to employee-derived rights cannot change who may do what by accident. An intended
/// change is a reviewed edit of the JSON in the PR that makes it. Regenerate with <c>SMARTSPARE_UPDATE_ACCESS_MATRIX=1</c>.
/// </summary>
[Trait("Category", "Integration")]
[Collection(DemoDatabaseGroup.Name)]
public sealed class AccessMatrixTests(DemoDatabaseFixture database) : IAsyncLifetime
{
    private const string UpdateVariable = "SMARTSPARE_UPDATE_ACCESS_MATRIX";

    /// <summary>The T001 demo users (fixed ids <c>…0001</c>–<c>…0012</c>; 012 is anonymised).</summary>
    private static readonly string[] DemoUsers =
    [
        "tomas.radzevicius", "rasa.kazlauskiene", "darius.petraitis", "mantas.petrauskas", "jonas.balciunas",
        "egle.stankeviciute", "lukas.vaitkus", "ruta.jankauskaite", "ignas.zukauskas", "vida.mockute",
        "admin.smartspare", "paulius.urbonas",
    ];

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private HostFactory _host = null!;
    private Dictionary<short, string> _brands = null!;
    private Dictionary<short, string> _hubs = null!;
    private long _part;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string GoldenPath =>
        Path.Combine(DemoDatabaseFixture.RepositoryRoot(), "tests", "SmartSpare.Host.IntegrationTests", "AccessMatrix", "access-matrix.json");

    public async ValueTask InitializeAsync()
    {
        _host = new HostFactory { ConnectionString = database.ApiConnectionString };
        _brands = await database.CodesAsync("SELECT id, code FROM ref.brand");
        _hubs = await database.CodesAsync("SELECT id, code FROM inventory.warehouse WHERE is_active");
        _part = await database.ScalarAsync<long>(
            """
            SELECT part_id FROM inventory.warehouse_part
            GROUP BY part_id HAVING count(*) = (SELECT count(*) FROM inventory.warehouse WHERE is_active)
            ORDER BY part_id LIMIT 1
            """);
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Every_demo_user_may_do_exactly_what_the_golden_matrix_records()
    {
        var actual = new SortedDictionary<string, UserAccess>(StringComparer.Ordinal);
        foreach (var user in DemoUsers)
        {
            actual[user] = await AccessOfAsync(user);
        }

        var json = JsonSerializer.Serialize(actual, Json).ReplaceLineEndings("\n") + "\n";
        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(GoldenPath)!);
            await File.WriteAllTextAsync(GoldenPath, json, Ct);
        }

        File.Exists(GoldenPath).ShouldBeTrue($"{GoldenPath} is missing; record it with {UpdateVariable}=1");
        var golden = (await File.ReadAllTextAsync(GoldenPath, Ct)).ReplaceLineEndings("\n");
        json.ShouldBe(golden, $"the access matrix changed; review the difference and, if it is intended, record it with {UpdateVariable}=1");
    }

    [Fact]
    public async Task Reads_are_never_scoped_by_hub_or_brand()
    {
        foreach (var user in DemoUsers.Where(u => u != "paulius.urbonas"))
        {
            var access = await AccessOfAsync(user);
            access.Reads.Values.ShouldAllBe(status => status == 200, $"{user} must read every hub");
        }
    }

    private async Task<UserAccess> AccessOfAsync(string userName)
    {
        Result<VerifiedSession> session;
        await using (var scope = _host.Services.CreateAsyncScope())
        {
            session = await scope.ServiceProvider.GetRequiredService<ICredentialVerifier>()
                .VerifyAsync(userName, DemoDatabaseFixture.Password, IPAddress.Loopback, Ct);
        }

        if (session.IsFailure)
        {
            return new UserAccess(false, [], [], [], new(StringComparer.Ordinal), new(StringComparer.Ordinal), new(StringComparer.Ordinal), new(StringComparer.Ordinal));
        }

        // The BFF adds the permissions from the roles on every request (ADR 0028); the matrix does what the BFF does.
        ClaimsPrincipal principal;
        await using (var transformScope = _host.Services.CreateAsyncScope())
        {
            principal = await transformScope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Authentication.IClaimsTransformation>()
                .TransformAsync(new ClaimsPrincipal(new ClaimsIdentity(session.Value.Claims, "matrix", AppClaimTypes.Name, AppClaimTypes.Role)));
        }

        var authorization = _host.Services.GetRequiredService<IAuthorizationService>();

        // One row per permission of the catalogue (before PR 7x1: one per role policy; the mapping is in STAGE_7_PLAN 1.8).
        var policies = new SortedDictionary<string, bool>(StringComparer.Ordinal);
        foreach (var permission in Permissions.All.Select(p => p.Name))
        {
            policies[permission] = (await authorization.AuthorizeAsync(principal, permission)).Succeeded;
        }

        var scoped = new SortedDictionary<string, SortedDictionary<string, bool>>(StringComparer.Ordinal);
        foreach (var (policy, requirement) in BrandScopeRequirements.ByPermission)
        {
            var perBrand = new SortedDictionary<string, bool>(StringComparer.Ordinal);
            foreach (var (id, code) in _brands)
            {
                perBrand[code] = policies[policy] && await AllowsAsync(authorization, principal, requirement, id);
            }

            perBrand["CLAAS+JD"] = policies[policy] && await AllowsAsync(authorization, principal, requirement, Brand("CLAAS"), Brand("JD"));
            scoped[policy] = perBrand;
        }

        var token = _host.Token(principal);
        using var browser = _host.NewBrowser();
        var writes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var reads = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var (id, code) in _hubs)
        {
            using (var write = await browser.PostAsync("/api/v1/inventory/stock-adjustments", new StockAdjustmentRequest(id, _part, 1, 5, "Access matrix probe"), bearer: token))
            {
                writes[code] = write.StatusCode == HttpStatusCode.Created
                    ? "201"
                    : $"{(int)write.StatusCode} {(await Problem.ReadAsync(write)).Code}".TrimEnd();
            }

            reads[$"stock:{code}"] = await StatusAsync(browser, $"/api/v1/inventory/stock?warehouseId={id}&pageSize=1", token);
            reads[$"machines:{code}"] = await StatusAsync(browser, $"/api/v1/fleet/machines?warehouseId={id}&pageSize=1", token);
        }

        return new UserAccess(
            true,
            [.. principal.FindAll(AppClaimTypes.Role).Select(c => c.Value).Order(StringComparer.Ordinal)],
            [.. Codes(principal, AppClaimTypes.Brand, _brands)],
            [.. Codes(principal, AppClaimTypes.Warehouse, _hubs)],
            policies,
            scoped,
            writes,
            reads);
    }

    private short Brand(string code) => _brands.Single(b => b.Value == code).Key;

    private static async Task<bool> AllowsAsync(IAuthorizationService authorization, ClaimsPrincipal user, BrandScopedActionRequirement requirement, params short[] brands) =>
        (await authorization.AuthorizeAsync(user, new Document(brands.ToHashSet()), requirement)).Succeeded;

    private static async Task<int> StatusAsync(Browser browser, string path, string token)
    {
        using var response = await browser.GetAsync(path, bearer: token);
        return (int)response.StatusCode;
    }

    private static IEnumerable<string> Codes(ClaimsPrincipal user, string claimType, Dictionary<short, string> codes) =>
        user.FindAll(claimType)
            .Select(c => codes[short.Parse(c.Value, CultureInfo.InvariantCulture)])
            .Order(StringComparer.Ordinal);

    private sealed record Document(IReadOnlySet<short> BrandIds) : IBrandScoped;

    /// <summary>One row of the matrix; written to JSON in a stable order.</summary>
    private sealed record UserAccess(
        bool SignsIn,
        string[] Roles,
        string[] Brands,
        string[] Hubs,
        SortedDictionary<string, bool> Policies,
        SortedDictionary<string, SortedDictionary<string, bool>> BrandScopedActions,
        SortedDictionary<string, string> HubWrites,
        SortedDictionary<string, int> Reads);
}
