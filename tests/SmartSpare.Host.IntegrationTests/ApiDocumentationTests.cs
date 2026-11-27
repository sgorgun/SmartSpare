using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SmartSpare.ApiContracts;
using SmartSpare.Bff;
using SmartSpare.Host.IntegrationTests.Support;
using SmartSpare.Infrastructure.Http;

namespace SmartSpare.Host.IntegrationTests;

/// <summary>
/// API documentation in the single host (ADR 0019, STAGE_4_PLAN section 7): one public document per module with only its
/// versioned business API, and the Scalar reference under its own nonce policy.
/// </summary>
public sealed partial class ApiDocumentationTests : IAsyncLifetime
{
    private readonly FakeIdentityHostFactory _host = new();

    public static TheoryData<string> Modules => ["identity", "inventory", "fleet", "procurement", "forecasting", "reporting"];

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Theory]
    [MemberData(nameof(Modules))]
    public async Task Each_module_has_a_public_document_with_only_its_versioned_api(string module)
    {
        using var browser = _host.NewBrowser();

        using var response = await browser.GetAsync(ApiDocumentation.PathOf(module));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, "documents are anonymous in every environment");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var root = document.RootElement;
        root.GetProperty("openapi").GetString().ShouldStartWith("3.1");
        root.GetProperty("info").GetProperty("title").GetString().ShouldBe($"SmartSpare {char.ToUpperInvariant(module[0])}{module[1..]} API");
        if (root.TryGetProperty("servers", out var servers))
        {
            servers.GetArrayLength().ShouldBe(0, "no server addresses, internal or other (ADR 0019 §3)");
        }
        foreach (var path in root.GetProperty("paths").EnumerateObject().Select(p => p.Name))
        {
            path.ShouldStartWith($"/api/v1/{module}/", Case.Sensitive, "no /internal, operational or other module's path (ADR 0019 §3)");
        }
    }

    [Theory]
    [InlineData("api/v1/inventory/parts", "inventory", true)]
    [InlineData("/api/v1/inventory/parts/{id}", "inventory", true)]
    [InlineData("api/inventory/ping", "inventory", false)]
    [InlineData("internal/v1/inventory/stock", "inventory", false)]
    [InlineData("api/v1/fleet/machines", "inventory", false)]
    [InlineData("health/ready", "inventory", false)]
    public void Only_the_modules_versioned_business_paths_are_documented(string path, string module, bool documented) =>
        ApiDocumentation.IsDocumented(path, module).ShouldBe(documented);

    [Fact]
    public async Task The_inventory_document_describes_every_operation_with_parameters_responses_and_examples()
    {
        var root = await DocumentAsync("inventory");
        var paths = root.GetProperty("paths");
        paths.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ShouldBe(
        [
            "/api/v1/inventory/imports", "/api/v1/inventory/imports/{id}", "/api/v1/inventory/movements", "/api/v1/inventory/parts", "/api/v1/inventory/parts/{id}", "/api/v1/inventory/stock",
            "/api/v1/inventory/stock-adjustments", "/api/v1/inventory/stock/{warehouseId}/{partId}",
            "/api/v1/inventory/stock/{warehouseId}/{partId}/levels", "/api/v1/inventory/transfers", "/api/v1/inventory/transfers/{id}",
            "/api/v1/inventory/transfers/{id}/approve", "/api/v1/inventory/transfers/{id}/cancel",
            "/api/v1/inventory/transfers/{id}/dispatch", "/api/v1/inventory/transfers/{id}/receive", "/api/v1/inventory/warehouses",
        ]);

        foreach (var (path, method, statuses) in new (string, string, string[])[]
        {
            ("/api/v1/inventory/parts", "get", ["200", "400", "401", "403"]),
            ("/api/v1/inventory/parts/{id}", "get", ["200", "401", "403", "404"]),
            ("/api/v1/inventory/warehouses", "get", ["200", "401", "403"]),
            ("/api/v1/inventory/stock", "get", ["200", "400", "401", "403"]),
            ("/api/v1/inventory/stock/{warehouseId}/{partId}", "get", ["200", "401", "403", "404"]),
            ("/api/v1/inventory/stock/{warehouseId}/{partId}/levels", "put", ["200", "400", "401", "403", "404", "409"]),
            ("/api/v1/inventory/movements", "get", ["200", "400", "401", "403"]),
            ("/api/v1/inventory/stock-adjustments", "post", ["201", "400", "401", "403", "404", "409"]),
            ("/api/v1/inventory/transfers", "get", ["200", "400", "401", "403"]),
            ("/api/v1/inventory/transfers", "post", ["201", "400", "401", "403", "422"]),
            ("/api/v1/inventory/transfers/{id}", "get", ["200", "401", "403", "404"]),
            ("/api/v1/inventory/transfers/{id}/approve", "post", ["200", "401", "403", "404", "409"]),
            ("/api/v1/inventory/transfers/{id}/cancel", "post", ["200", "400", "401", "403", "404", "409"]),
            ("/api/v1/inventory/transfers/{id}/dispatch", "post", ["200", "401", "403", "404", "409"]),
            ("/api/v1/inventory/transfers/{id}/receive", "post", ["200", "401", "403", "404", "409"]),
            ("/api/v1/inventory/imports", "post", ["201", "400", "401", "403", "409", "429"]),
            ("/api/v1/inventory/imports", "get", ["200", "400", "401", "403"]),
            ("/api/v1/inventory/imports/{id}", "get", ["200", "401", "403", "404"]),
        })
        {
            var operation = paths.GetProperty(path).GetProperty(method);
            operation.GetProperty("summary").GetString().ShouldNotBeNullOrWhiteSpace(path);
            operation.GetProperty("description").GetString().ShouldNotBeNullOrWhiteSpace(path);
            var responses = operation.GetProperty("responses");
            responses.EnumerateObject().Select(r => r.Name).ShouldBe(statuses, ignoreOrder: true, path);
            ExampleOf(responses.GetProperty(statuses[0])).ValueKind.ShouldNotBe(JsonValueKind.Undefined, $"{path} has a {statuses[0]} example");
            if (operation.TryGetProperty("parameters", out var parameters))
            {
                parameters.EnumerateArray().ShouldAllBe(
                    p => !string.IsNullOrWhiteSpace(p.GetProperty("description").GetString()), $"{path}: every parameter is described");
            }

            // A JSON body has an example; a multipart upload (the CSV import) describes its form fields instead.
            if (operation.TryGetProperty("requestBody", out var body) && body.GetProperty("content").TryGetProperty("application/json", out _))
            {
                ExampleOf(body).ValueKind.ShouldNotBe(JsonValueKind.Undefined, $"{path} has a request example");
            }
        }

        var schemas = root.GetProperty("components").GetProperty("schemas");
        foreach (var name in new[] { "StockRow", "StockPositionDetails", "ReservationSummary", "MovementRow", "ChangeStockLevelsRequest", "StockAdjustmentRequest", "StockAdjustmentResponse",
                     "TransferSummary", "TransferDetails", "TransferLineResponse", "TransferStep", "CreateTransferRequest", "CreateTransferLine", "CancelTransferRequest",
                     "ImportBatchResponse", "ImportBatchDetails", "ImportRowError" })
        {
            schemas.GetProperty(name).GetProperty("properties").EnumerateObject()
                .Where(p => !p.Value.TryGetProperty("description", out _)).Select(p => p.Name).ShouldBeEmpty($"every field of {name} is described");
        }

        var search = paths.GetProperty("/api/v1/inventory/parts").GetProperty("get");
        search.GetProperty("parameters").EnumerateArray().Select(p => p.GetProperty("name").GetString()).ShouldBe(["search", "page", "pageSize"]);
        search.GetProperty("parameters").EnumerateArray().ShouldAllBe(p => !string.IsNullOrWhiteSpace(p.GetProperty("description").GetString()));

        var notFound = ExampleOf(paths.GetProperty("/api/v1/inventory/parts/{id}").GetProperty("get").GetProperty("responses").GetProperty("404"));
        notFound.GetProperty("code").GetString().ShouldBe("Inventory.PartNotFound");

        var part = root.GetProperty("components").GetProperty("schemas").GetProperty("PartSummary");
        part.GetProperty("properties").EnumerateObject().Where(p => !p.Value.TryGetProperty("description", out _)).Select(p => p.Name).ShouldBeEmpty("every field is described");
        part.GetProperty("properties").GetProperty("unitCost").GetProperty("type").GetString().ShouldBe("number", "numbers are documented as JSON numbers");
    }

    [Fact]
    public async Task The_fleet_document_describes_every_operation_with_parameters_responses_and_examples()
    {
        var root = await DocumentAsync("fleet");
        var paths = root.GetProperty("paths");

        foreach (var (path, method, statuses) in new (string, string, string[])[]
        {
            ("/api/v1/fleet/machines", "get", ["200", "400", "401", "403"]),
            ("/api/v1/fleet/machines/{id}", "get", ["200", "401", "403", "404"]),
            ("/api/v1/fleet/machines/{id}/readings", "get", ["200", "400", "401", "403", "404"]),
            ("/api/v1/fleet/maintenance/due", "get", ["200", "400", "401", "403"]),
            ("/api/v1/fleet/maintenance-plans", "get", ["200", "401", "403"]),
            ("/api/v1/fleet/machines/{id}/readings", "post", ["201", "200", "400", "401", "403", "404", "409", "422"]),
            ("/api/v1/fleet/service-orders", "get", ["200", "400", "401", "403"]),
            ("/api/v1/fleet/service-orders/{id}", "get", ["200", "401", "403", "404"]),
            ("/api/v1/fleet/service-orders", "post", ["201", "400", "401", "403", "404", "409", "422"]),
            ("/api/v1/fleet/service-orders/{id}/start", "post", ["200", "401", "403", "404", "409"]),
            ("/api/v1/fleet/service-orders/{id}/complete", "post", ["200", "400", "401", "403", "404", "409"]),
        })
        {
            var operation = paths.GetProperty(path).GetProperty(method);
            operation.GetProperty("summary").GetString().ShouldNotBeNullOrWhiteSpace(path);
            operation.GetProperty("description").GetString().ShouldNotBeNullOrWhiteSpace(path);
            var responses = operation.GetProperty("responses");
            responses.EnumerateObject().Select(r => r.Name).ShouldBe(statuses, ignoreOrder: true, path);
            ExampleOf(responses.GetProperty(statuses[0])).ValueKind.ShouldNotBe(JsonValueKind.Undefined, $"{path} has a {statuses[0]} example");
            if (operation.TryGetProperty("parameters", out var parameters))
            {
                parameters.EnumerateArray().ShouldAllBe(
                    p => !string.IsNullOrWhiteSpace(p.GetProperty("description").GetString()), $"{path}: every parameter is described");
            }

            if (operation.TryGetProperty("requestBody", out var body))
            {
                ExampleOf(body).ValueKind.ShouldNotBe(JsonValueKind.Undefined, $"{path} has a request example");
            }
        }

        ExampleOf(paths.GetProperty("/api/v1/fleet/service-orders/{id}/start").GetProperty("post").GetProperty("responses").GetProperty("409"))
            .GetProperty("code").GetString().ShouldBe("SS300");
        var record = paths.GetProperty("/api/v1/fleet/machines/{id}/readings").GetProperty("post").GetProperty("responses");
        foreach (var (status, code) in new[] { ("403", "SS-AUTH-004"), ("404", "Fleet.MachineNotFound"), ("409", "Fleet.ReadingConflict"), ("422", "SS100") })
        {
            ExampleOf(record.GetProperty(status)).GetProperty("code").GetString().ShouldBe(code, $"the {status} example shows its code");
        }

        var schemas = root.GetProperty("components").GetProperty("schemas");
        foreach (var name in new[] { "CompleteServiceOrderRequest", "ServiceOrderSummary", "ServiceOrderDetails", "ServiceOrderLineResponse", "CreateServiceOrderRequest", "CreateServiceOrderLine",
                     "RecordReadingRequest", "RecordReadingResponse", "DraftedServiceOrder", "MachineSummary", "MachineDetails", "MaintenancePlanDue", "ReadingRow", "DueMaintenanceRow", "MaintenancePlanResponse", "KitPart" })
        {
            schemas.GetProperty(name).GetProperty("properties").EnumerateObject()
                .Where(p => !p.Value.TryGetProperty("description", out _)).Select(p => p.Name).ShouldBeEmpty($"every field of {name} is described");
        }

        var notFound = ExampleOf(paths.GetProperty("/api/v1/fleet/machines/{id}").GetProperty("get").GetProperty("responses").GetProperty("404"));
        notFound.GetProperty("code").GetString().ShouldBe("Fleet.MachineNotFound");
    }

    [Fact]
    public async Task The_session_endpoints_have_their_own_public_document_with_errors_and_examples()
    {
        var root = await DocumentAsync(BffEndpoints.DocumentName);

        root.GetProperty("info").GetProperty("title").GetString().ShouldBe("SmartSpare Session API (BFF)");
        var paths = root.GetProperty("paths");
        paths.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)
            .ShouldBe(["/bff/antiforgery", "/bff/login", "/bff/logout", "/bff/me"]);

        var login = paths.GetProperty("/bff/login").GetProperty("post");
        login.GetProperty("summary").GetString().ShouldNotBeNullOrWhiteSpace();
        ExampleOf(login.GetProperty("requestBody")).GetProperty("userName").GetString().ShouldBe("tomas.radzevicius");
        var responses = login.GetProperty("responses");
        responses.EnumerateObject().Select(r => r.Name).ShouldBe(["204", "400", "429"], ignoreOrder: true);
        ExampleOf(responses.GetProperty("400")).GetProperty("code").GetString().ShouldBe(ProblemCodes.InvalidCredentials);
        var tooMany = responses.GetProperty("429");
        tooMany.GetProperty("headers").GetProperty("Retry-After").GetProperty("schema").GetProperty("type").GetString().ShouldBe("integer");
        ExampleOf(tooMany).GetProperty("code").GetString().ShouldBe(ProblemCodes.TooManyRequests);

        var logout = paths.GetProperty("/bff/logout").GetProperty("post").GetProperty("responses");
        ExampleOf(logout.GetProperty("401")).GetProperty("code").GetString().ShouldBe(ProblemCodes.Unauthenticated);
        ExampleOf(logout.GetProperty("400")).GetProperty("code").GetString().ShouldBe(ProblemCodes.AntiforgeryFailed);
    }

    [Theory]
    [InlineData("bff")]
    [InlineData("inventory")]
    public async Task The_problem_details_schema_documents_code_and_trace_id(string document)
    {
        var root = await DocumentAsync(document);

        var properties = root.GetProperty("components").GetProperty("schemas").GetProperty("ProblemDetails").GetProperty("properties");
        properties.TryGetProperty("code", out _).ShouldBeTrue();
        properties.TryGetProperty("traceId", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Only_bff_paths_are_in_the_session_document_and_no_bff_path_is_in_a_module_document()
    {
        var bff = await DocumentAsync(BffEndpoints.DocumentName);
        bff.GetProperty("paths").EnumerateObject().ShouldAllBe(p => p.Name.StartsWith("/bff/", StringComparison.Ordinal));

        foreach (var module in Modules)
        {
            (await DocumentAsync(module)).GetProperty("paths").EnumerateObject().ShouldAllBe(p => !p.Name.StartsWith("/bff", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Every_versioned_business_endpoint_requires_authorization()
    {
        var anonymous = _host.Services.GetServices<EndpointDataSource>().SelectMany(s => s.Endpoints).OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/v1/", StringComparison.Ordinal) == true)
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(e => e.RoutePattern.RawText);

        anonymous.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_api_reference_is_public_and_runs_only_its_nonce_script()
    {
        using var browser = _host.NewBrowser();

        using var response = await browser.GetAsync(ApiReference.Path + "/");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var policy = response.Headers.GetValues("Content-Security-Policy").Single();
        var scriptSource = policy.Split(';').Select(d => d.Trim()).Single(d => d.StartsWith("script-src ", StringComparison.Ordinal));
        var nonce = NonceSource().Match(scriptSource).Groups["nonce"].Value;
        nonce.Length.ShouldBeGreaterThanOrEqualTo(22, "at least 128 bits");
        scriptSource.ShouldBe($"script-src 'self' 'nonce-{nonce}'");

        var html = await response.Content.ReadAsStringAsync(Ct);
        html.ShouldContain($"nonce=\"{nonce}\"");
        html.ShouldNotContain("fonts.scalar.com");
        html.ShouldNotContain("cdn.jsdelivr.net");
        html.IndexOf("\"bff\"", StringComparison.Ordinal).ShouldBeInRange(0, html.IndexOf("\"identity\"", StringComparison.Ordinal),
            "the session document is listed, before the module documents");

        using var again = await browser.GetAsync(ApiReference.Path + "/");
        again.Headers.GetValues("Content-Security-Policy").Single().ShouldNotContain(nonce, Case.Sensitive, "a fresh nonce per response");
    }

    [Fact]
    public async Task Other_pages_keep_the_stage_3_policy_without_a_nonce()
    {
        using var browser = _host.NewBrowser();

        using var response = await browser.GetAsync("/account/login");

        response.Headers.GetValues("Content-Security-Policy").Single().ShouldContain("script-src 'self' 'wasm-unsafe-eval'");
        response.Headers.GetValues("Content-Security-Policy").Single().ShouldNotContain("nonce-");
    }

    [GeneratedRegex("'nonce-(?<nonce>[A-Za-z0-9_-]+)'")]
    private static partial Regex NonceSource();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<JsonElement> DocumentAsync(string name)
    {
        using var browser = _host.NewBrowser();
        using var response = await browser.GetAsync(ApiDocumentation.PathOf(name));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, $"{name} is anonymous");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return document.RootElement.Clone();
    }

    /// <summary>The example of the only media type of a request body or response.</summary>
    private static JsonElement ExampleOf(JsonElement bodyOrResponse) =>
        bodyOrResponse.GetProperty("content").EnumerateObject().Single().Value.TryGetProperty("example", out var example) ? example : default;
}
