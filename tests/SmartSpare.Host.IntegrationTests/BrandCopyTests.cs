using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Identity;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.Host.IntegrationTests.Support;
using SmartSpare.Infrastructure.Jobs;
using SmartSpare.Infrastructure.Messaging.Outbox;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.Modules.Inventory.Contracts;

namespace SmartSpare.Host.IntegrationTests;

/// <summary>
/// The brand move (STAGE_7_PLAN 3.2, PR 7h1): brands are Inventory's <c>catalog.brand</c>; Fleet and Identity keep
/// read-only copies with the same ids, filled by <c>inventory.brand-catalog-publish</c> and kept by
/// <c>BrandCatalogChanged</c>, the newest event winning. An archived brand gets no new assignments and is not offered in
/// the user form. Single host and PostgreSQL 17.
/// </summary>
[Trait("Category", "Integration")]
[Collection(IdentityDatabaseGroup.Name)]
public sealed class BrandCopyTests(IdentityDatabaseFixture database) : IAsyncLifetime
{
    private static readonly TestUser Admin = TestUser.Create("brand.admin", [AppRoles.Administrator]);

    private readonly string _code = "B" + Convert.ToHexString(RandomNumberGenerator.GetBytes(3));

    private HostFactory _host = null!;
    private short _brandId;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _host = new HostFactory { ConnectionString = database.ApiConnectionString };
        _brandId = await database.ScalarAsync<short>(
            "INSERT INTO catalog.brand (code, name, country_code) VALUES (@code, @name, 'DE') RETURNING id", ("code", _code), ("name", $"Brand {_code}"));
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact]
    public async Task The_publish_job_fills_both_copies_with_the_same_ids_and_an_older_event_never_overwrites_a_newer_one()
    {
        await RunPublishJobAsync();

        await EventuallyAsync(async () => await CopiesAsync() == ($"Brand {_code}", $"Brand {_code}"));

        // A late event from before the publish is ignored by both copies.
        var stale = new BrandCatalogChanged([new BrandSnapshot(_brandId, _code, "Stale name", false)]) { OccurredAt = DateTimeOffset.UtcNow.AddDays(-1) };
        await DeliverAsync(stale);
        (await CopiesAsync()).ShouldBe(($"Brand {_code}", $"Brand {_code}"));

        // A newer one wins in both.
        var renamed = new BrandCatalogChanged([new BrandSnapshot(_brandId, _code, "Renamed", true)]) { OccurredAt = DateTimeOffset.UtcNow.AddMinutes(1) };
        await DeliverAsync(renamed);
        (await CopiesAsync()).ShouldBe(("Renamed", "Renamed"));
        (await database.ScalarAsync<bool>("SELECT is_archived FROM iam.brand_snapshot WHERE brand_id = @id", ("id", _brandId))).ShouldBeTrue();
    }

    [Theory]
    [InlineData("fleet")]
    [InlineData("iam")]
    public async Task The_copies_are_read_only_for_the_application_login(string schema)
    {
        await using var connection = new NpgsqlConnection(database.ApiConnectionString);
        await connection.OpenAsync(Ct);
        await using var write = new NpgsqlCommand(
            $"INSERT INTO {schema}.brand_snapshot (brand_id, code, name, is_archived, source_occurred_at) VALUES (32000, 'X', 'X', false, now())", connection);

        var error = await Should.ThrowAsync<PostgresException>(() => write.ExecuteNonQueryAsync(Ct));

        error.SqlState.ShouldBe("42501", "only apply_brands writes the copy");
    }

    [Fact]
    public async Task An_archived_brand_gets_no_new_assignments_and_is_not_offered_in_the_user_form()
    {
        var employee = await database.CreateUserAsync($"{_code.ToLowerInvariant()}.manager", [AppRoles.BrandManager], []);
        await database.ExecuteAsync(
            """
            UPDATE catalog.brand SET is_archived = true, archived_at = now() WHERE id = @id;
            INSERT INTO iam.brand_snapshot (brand_id, code, name, is_archived, source_occurred_at) VALUES (@id, @code, 'Archived', true, now());
            """,
            ("id", _brandId), ("code", _code));

        using (var assign = await SendAsync(HttpMethod.Put, $"/api/v1/inventory/brands/{_brandId}/responsibles",
                   new ResponsiblesRequest([new ResponsibleEntry(employee, "BRAND_MANAGER")])))
        {
            assign.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await Problem.ReadAsync(assign)).Code.ShouldBe("SS500");
        }

        using (var clear = await SendAsync(HttpMethod.Put, $"/api/v1/inventory/brands/{_brandId}/responsibles", new ResponsiblesRequest([])))
        {
            clear.StatusCode.ShouldBe(HttpStatusCode.OK, "clearing the assignments of an archived brand stays possible");
        }

        using var options = await SendAsync(HttpMethod.Get, "/api/v1/identity/users/form-options");
        var form = (await options.Content.ReadFromJsonAsync<UserFormOptions>(Ct))!;
        form.Brands.ShouldNotContain(_code);
        form.Brands.ShouldContain("CLAAS");
    }

    private async Task RunPublishJobAsync()
    {
        await using var scope = _host.Services.CreateAsyncScope();
        var job = scope.ServiceProvider.GetServices<IJob>().Single(j => j.Name == "inventory.brand-catalog-publish");
        await job.RunAsync(Ct);
    }

    private async Task DeliverAsync(BrandCatalogChanged changed)
    {
        await using var scope = _host.Services.CreateAsyncScope();
        foreach (var consumer in scope.ServiceProvider.GetServices<IIntegrationEventConsumer<BrandCatalogChanged>>())
        {
            await IntegrationEventDelivery.DeliverAsync(
                scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<UnitOfWorkRegistry>(), consumer, changed, Guid.NewGuid(), Ct);
        }
    }

    private async Task<(string? Fleet, string? Identity)> CopiesAsync() => (
        await database.ScalarAsync<string?>("SELECT name::text FROM fleet.brand_snapshot WHERE brand_id = @id", ("id", _brandId)),
        await database.ScalarAsync<string?>("SELECT name::text FROM iam.brand_snapshot WHERE brand_id = @id", ("id", _brandId)));

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null)
    {
        using var browser = _host.NewBrowser();
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative)) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _host.Token(Admin.Principal()));
        return await browser.Client.SendAsync(request, Ct);
    }

    /// <summary>The single host delivers through the outbox dispatcher's polling interval; waits up to 30 s.</summary>
    private static async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(500, Ct);
        }

        (await condition()).ShouldBeTrue("the event reached the consumers within 30 s");
    }
}
