using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.Host.IntegrationTests.Support;
using SmartSpare.Modules.Identity.Contracts;

namespace SmartSpare.Host.IntegrationTests;

/// <summary>
/// A Contracts query over HTTP against the real host (ADR 0015 §4): <see cref="HttpEmployeeDirectory"/>, the read Inventory
/// makes to Identity, calls the internal endpoint with the caller's internal token, and the endpoint refuses a request
/// without a user. Moved here from the warehouse client, removed with the warehouse copies (ADR 0029). Single host and
/// PostgreSQL 17.
/// </summary>
[Trait("Category", "Integration")]
[Collection(IdentityDatabaseGroup.Name)]
public sealed class EmployeeDirectoryTests(IdentityDatabaseFixture database) : IAsyncLifetime
{
    private static readonly TestUser Planner = TestUser.Create("dir.planner", [AppRoles.InventoryPlanner]);

    private HostFactory _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _host = new HostFactory { ConnectionString = database.ApiConnectionString };
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task Other_services_find_employees_through_the_internal_endpoint_with_the_callers_token()
    {
        var employee = await database.CreateUserAsync($"dir.{Guid.NewGuid():N}"[..20], [AppRoles.WarehouseKeeper], []);
        var directory = new HttpEmployeeDirectory(new HttpClient(new BearerHandler(_host.Token(Planner.Principal()), _host.Server.CreateHandler()))
        {
            BaseAddress = _host.Server.BaseAddress,
        });

        var found = await directory.FindAsync([employee, Guid.NewGuid()], Ct);

        found.ShouldHaveSingleItem().Id.ShouldBe(employee);
        found[0].IsActive.ShouldBeTrue();

        // Refused without a user: 401 from a service (bearer only), a redirect to sign-in from the single host's cookie scheme.
        using var anonymous = new HttpClient(_host.Server.CreateHandler()) { BaseAddress = _host.Server.BaseAddress };
        using var refused = await anonymous.PostAsJsonAsync(IdentityInternalApi.EmployeeLookup, new EmployeeLookupRequest([employee], null, 1), Ct);
        refused.StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Found);
    }

    private sealed class BearerHandler(string token, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
