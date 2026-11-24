using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.Bff;
using SmartSpare.Infrastructure.Http;
using SmartSpare.Infrastructure.Security;
using SmartSpare.Modules.Identity.Contracts;
using SmartSpare.SharedKernel;

namespace SmartSpare.Host.IntegrationTests.Support;

/// <summary>
/// A host composed with exactly the registrations and pipeline of the single host (<c>AddServiceDefaults</c>, <c>AddSmartSpareProblemDetails</c>,
/// <c>AddBff</c>, <c>AddSingleHostAuthentication</c>, <c>UseSmartSpareProblemDetails</c>, <c>UseBff</c>, <c>MapBff</c>,
/// the antiforgery filter on <c>/api</c>) plus test endpoints: brand-scoped documents (STAGE_3_PLAN section 3.3),
/// a role-protected read, and one endpoint per kind of error response (#60). No database.
/// </summary>
public sealed class SecurityTestHost : IAsyncLifetime
{
    public const short Claas = 1;
    public const short JohnDeere = 2;
    public const short Lemken = 5;

    private WebApplication? _app;

    internal FakeCredentialVerifier Verifier { get; } = new();

    public FakeTimeProvider Time { get; } = new(DateTimeOffset.UtcNow);

    /// <summary>Test documents: id → brands of its lines and state.</summary>
    internal Dictionary<int, TestDocument> Documents { get; } = [];

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        builder.Configuration[$"{InternalTokenOptions.SectionName}:{nameof(InternalTokenOptions.SigningKey)}"] = TestKeys.SigningKeyPem;

        builder.AddServiceDefaults();
        builder.Services.AddSmartSpareProblemDetails();
        builder.Services.AddBff();
        builder.Services.AddSingleHostAuthentication();
        builder.Services.AddSingleton<ICredentialVerifier>(Verifier);
        builder.Services.AddSingleton<TimeProvider>(Time);
        builder.Services.AddSingleton<IStartupFilter, TestClientIp>();

        _app = builder.Build();
        _app.UseSmartSpareProblemDetails();
        _app.UseBff();
        _app.MapBff();
        MapTestEndpoints(_app.MapGroup("/api/test").AddEndpointFilter<AntiforgeryValidationFilter>());

        await _app.StartAsync(TestContext.Current.CancellationToken);
    }

    internal Browser NewBrowser() => Browser.Over(_app!.GetTestServer().CreateHandler(), TestClientIp.Unique());

    internal string Token(ClaimsPrincipal user) =>
        _app!.Services.GetRequiredService<InternalTokenIssuer>().CreateToken(user, Guid.NewGuid().ToString("N"));

    internal string Token(TestUser user) => Token(user.Principal());

    /// <summary>Adds a document whose lines carry <paramref name="brands"/>; returns its id.</summary>
    internal int AddDocument(params short[] brands)
    {
        lock (Documents)
        {
            var id = Documents.Count + 1;
            Documents[id] = new TestDocument(brands.ToHashSet());
            return id;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    private void MapTestEndpoints(RouteGroupBuilder api)
    {
        api.MapGet("/documents/{id:int}", (int id) => Documents.TryGetValue(id, out var document)
            ? Results.Ok(new { document.State })
            : Results.NotFound());

        MapAction(api, "/transfers/{id:int}/approve", AppPolicies.ApproveTransfer, BrandScopeRequirements.ApproveTransferScope, "approved");
        MapAction(api, "/transfers/{id:int}/cancel", AppPolicies.CancelTransfer, BrandScopeRequirements.CancelTransferScope, "cancelled");
        MapAction(api, "/purchase-orders/{id:int}/approve", AppPolicies.ApprovePurchaseOrder, BrandScopeRequirements.ApprovePurchaseOrderScope, "approved");
        MapAction(api, "/purchase-orders/{id:int}/cancel", AppPolicies.CancelPurchaseOrder, BrandScopeRequirements.CancelPurchaseOrderScope, "cancelled");

        api.MapGet("/audit", () => Results.Ok()).RequireAuthorization(AppPolicies.ReadAudit);
        api.MapPost("/echo", () => Results.Ok(new { ok = true }));
        api.MapGet("/business-error", () => Error.BusinessRule("SS201", "Donor warehouse would fall below its safety stock.").ToProblem());
        api.MapGet("/validation-error", () =>
            new ValidationError(new Dictionary<string, string[]> { ["Quantity"] = ["Must be positive."] }).ToProblem());
        api.MapGet("/boom", IResult () => throw new InvalidOperationException("unexpected"));
    }

    /// <summary>
    /// An approval-style endpoint as stage 5–8 endpoints will be written: the policy gates the role, then the brand-scope
    /// requirement is checked against the document loaded on the server (brands never come from the request), before
    /// any state change.
    /// </summary>
    private void MapAction(RouteGroupBuilder api, string pattern, string policy, BrandScopedActionRequirement scope, string newState) =>
        api.MapPost(pattern, async (int id, ClaimsPrincipal user, IAuthorizationService authorization) =>
            {
                if (!Documents.TryGetValue(id, out var document))
                {
                    return Results.NotFound();
                }

                var result = await authorization.AuthorizeAsync(user, document, scope);
                if (!result.Succeeded)
                {
                    return Results.Forbid();
                }

                document.State = newState;
                return Results.Ok(new { document.State });
            })
            .RequireAuthorization(policy);

    internal sealed class TestDocument(IReadOnlySet<short> brandIds) : IBrandScoped
    {
        public IReadOnlySet<short> BrandIds { get; } = brandIds;

        public string State { get; set; } = "draft";
    }
}
