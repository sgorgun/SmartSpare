using Microsoft.AspNetCore.HttpOverrides;
using SmartSpare.Bff;
using SmartSpare.Gateway;
using SmartSpare.Gateway.Components;
using SmartSpare.Infrastructure.Http;
using SmartSpare.Modules.Identity.Contracts;
using SmartSpare.Web.Client;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddSmartSpareProblemDetails();
builder.Services.Configure<ForwardedHeadersOptions>(options => GatewayForwardedHeaders.Configure(options, builder.Configuration));
builder.Services.AddGatewayDataProtection(builder.Configuration);

// The BFF of stage 3 (ADR 0014), with the cookie as the only scheme (ADR 0016 section 4). Credentials and sessions are
// checked by the Identity service over HTTP, as the gateway itself (section 4 of the stage 4 plan).
builder.Services.AddBff();
builder.Services.AddGatewayAuthentication();
builder.Services.AddTransient<ServiceTokenHandler>();
builder.Services.AddHttpClient<ICredentialVerifier, HttpCredentialVerifier>(GatewayClients.Identity, client => client.BaseAddress = new Uri("http://identity"))
    .AddHttpMessageHandler<ServiceTokenHandler>();

builder.Services.AddGatewayProxy();
builder.Services.AddWarmup();

builder.Services.AddRazorComponents()
    .AddInteractiveWebAssemblyComponents()
    .AddAuthenticationStateSerialization(ClientAuthenticationState.Configure);
builder.Services.AddSmartSpareClient();
builder.Services.AddAuthorization(AuthenticatedOrResourceCollectionRequirement.UseAsFallback);

var app = builder.Build();

app.UseForwardedHeaders();
app.UseSmartSpareProblemDetails();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseSecurityHeaders();
app.UseRequestLocalization(options => options.SetDefaultCulture("en-US").AddSupportedCultures("en-US").AddSupportedUICultures("en-US"));
app.MapStaticAssets().AllowAnonymous();

app.UseBff();
app.UseAntiforgery();

app.MapDefaultEndpoints();
app.MapBff();
app.MapWarmup();
app.MapApiReference(GatewayRoutes.Modules);
app.MapGatewayProxy();

app.MapRazorComponents<App>()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(SmartSpare.Web.Client.Routes).Assembly);

await app.RunAsync().ConfigureAwait(false);
