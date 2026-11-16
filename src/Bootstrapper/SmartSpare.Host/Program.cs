using SmartSpare.Infrastructure.Messaging;
using SmartSpare.Infrastructure.Messaging.Outbox;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.ModuleCatalog;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();
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

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}
else
{
    app.UseHsts();
}

app.MapDefaultEndpoints();

foreach (var module in modules)
{
    module.MapEndpoints(app);
}

await app.RunAsync().ConfigureAwait(false);

/// <summary>Entry point; public so integration tests can use <c>WebApplicationFactory&lt;Program&gt;</c>.</summary>
public partial class Program;
