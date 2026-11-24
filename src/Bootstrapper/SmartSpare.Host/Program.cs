using SmartSpare.Bff;
using SmartSpare.Infrastructure.Http;
using SmartSpare.Infrastructure.Messaging;
using SmartSpare.Infrastructure.Messaging.Outbox;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.ModuleCatalog;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddSmartSpareProblemDetails();
builder.Services.AddOpenApi();

// The ordered module list is shared with the migration runner (ModuleCatalog).
var modules = ModuleCatalog.CreateInstallers();

foreach (var module in modules)
{
    module.AddServices(builder.Services, builder.Configuration);
}

// After all modules: wrap every handler in the logging/validation/transaction pipeline once.
builder.Services.DecorateHandlers();

// Deliver integration events from every module's outbox (ADR 0003). Without a database (host
// tests) there is nothing to drain.
if (builder.Configuration.GetConnectionString(PersistenceRegistration.ConnectionStringName) is not null)
{
    builder.Services.AddOutboxDispatcher();
}

// The BFF (ADR 0014) and the single-host authentication (ADR 0016 section 4): the policy scheme sends
// "Authorization: Bearer" to the internal-token scheme and everything else to the cookie.
builder.Services.AddBff();
builder.Services.AddSingleHostAuthentication();

var app = builder.Build();

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

app.UseBff();

app.MapDefaultEndpoints();
app.MapBff();

// Every module endpoint validates antiforgery on unsafe requests (STAGE_3_PLAN section 1.5); the group
// has no prefix, so modules keep their own /api/{module} routes.
var moduleEndpoints = app.MapGroup(string.Empty).AddEndpointFilter<AntiforgeryValidationFilter>();
foreach (var module in modules)
{
    module.MapEndpoints(moduleEndpoints);
}

await app.RunAsync().ConfigureAwait(false);

/// <summary>Entry point; public so integration tests can use <c>WebApplicationFactory&lt;Program&gt;</c>.</summary>
public partial class Program;
