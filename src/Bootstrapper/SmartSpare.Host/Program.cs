using SmartSpare.Host;
using SmartSpare.Infrastructure.Messaging;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

var modules = ModuleDiscovery.DiscoverInstallers();
foreach (var module in modules)
{
    module.AddServices(builder.Services, builder.Configuration);
}

// After all modules: wrap every handler in the logging/validation/transaction pipeline once.
builder.Services.DecorateHandlers();

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
