using SmartSpare.Bff;
using SmartSpare.Host;
using SmartSpare.Host.Components;
using SmartSpare.Infrastructure.Http;
using SmartSpare.Infrastructure.Modules;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.ModuleCatalog;
using SmartSpare.Web.Client;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddSmartSpareProblemDetails();
builder.Services.AddOpenApi();

// The ordered module list is shared with the migration runner (ModuleCatalog). The single host runs all of them and
// delivers integration events in process (ADR 0015 §1); the shared composition decorates handlers, checks consumer
// names, drains the outboxes and resolves the current user from the request's claims.
var modules = ModuleCatalog.CreateInstallers();
builder.Services.AddModules(builder.Configuration, modules, Topology.SingleHost);

// One key ring for every instance of the host (#22): cookies and antiforgery tokens work across instances.
builder.AddSmartSpareDataProtection();

// The BFF (ADR 0014) and the single-host authentication (ADR 0016 section 4): the policy scheme sends
// "Authorization: Bearer" to the internal-token scheme and everything else to the cookie.
builder.Services.AddBff();
builder.Services.AddSingleHostAuthentication();

// The Blazor Web App (ADR 0006, STAGE_3_PLAN 8.1): global InteractiveWebAssembly with prerendering. The host prerenders the
// client's components, so it registers the client's services too, and hands the browser an allow-listed principal (1.4).
builder.Services.AddRazorComponents()
    .AddInteractiveWebAssemblyComponents()
    .AddAuthenticationStateSerialization(ClientAuthenticationState.Configure);
builder.Services.AddSmartSpareClient();

// After AppPolicies: the fallback also admits the WebAssembly resource collection, which can't be marked AllowAnonymous.
builder.Services.AddAuthorization(AuthenticatedOrResourceCollectionRequirement.UseAsFallback);

var app = builder.Build();

// Pipeline order of STAGE_3_PLAN 8.1.
app.UseSmartSpareProblemDetails();

if (app.Environment.IsDevelopment())
{
    // API documentation is public (ADR 0019); the endpoints themselves still require authorization.
    app.MapOpenApi().AllowAnonymous();
}
else
{
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseSecurityHeaders();
app.UseRequestLocalization(options => options.SetDefaultCulture("en-US").AddSupportedCultures("en-US").AddSupportedUICultures("en-US"));

// Fingerprinted static assets, including the WebAssembly runtime; public, unlike every page and API (fallback policy).
app.MapStaticAssets().AllowAnonymous();

app.UseBff();
app.UseAntiforgery();

app.MapDefaultEndpoints();
app.MapBff();

// Every module endpoint validates antiforgery on unsafe requests (STAGE_3_PLAN section 1.5): the host accepts the cookie.
app.MapModules(modules, group => group.AddEndpointFilter<AntiforgeryValidationFilter>());

app.MapRazorComponents<App>()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(SmartSpare.Web.Client.Routes).Assembly);

await app.RunAsync().ConfigureAwait(false);

/// <summary>Entry point; public so integration tests can use <c>WebApplicationFactory&lt;Program&gt;</c>.</summary>
public partial class Program;
