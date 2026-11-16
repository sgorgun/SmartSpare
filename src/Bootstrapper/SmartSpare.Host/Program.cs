using SmartSpare.Infrastructure.Messaging;
using SmartSpare.Infrastructure.Modules;
using SmartSpare.Modules.Fleet;
using SmartSpare.Modules.Forecasting;
using SmartSpare.Modules.Identity;
using SmartSpare.Modules.Inventory;
using SmartSpare.Modules.Procurement;
using SmartSpare.Modules.Reporting;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

// The single list of modules hosted by this process. Extracting a module into its own service
// means removing it here and installing it in the new host (ADR 0002).
IModuleInstaller[] modules =
[
    new IdentityModuleInstaller(),
    new InventoryModuleInstaller(),
    new FleetModuleInstaller(),
    new ProcurementModuleInstaller(),
    new ForecastingModuleInstaller(),
    new ReportingModuleInstaller(),
];

var duplicate = modules.GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
if (duplicate is not null)
{
    throw new InvalidOperationException($"More than one module installer is named '{duplicate.Key}'.");
}

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
