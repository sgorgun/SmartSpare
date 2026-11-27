using Microsoft.Extensions.DependencyInjection.Extensions;
using SmartSpare.Web.Client.Features.Fleet;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.Web.Client.Features.Identity;
using SmartSpare.Web.Client.Features.Inventory;
using SmartSpare.Web.Client.Http;

namespace SmartSpare.Web.Client;

/// <summary>
/// Services of the web client, registered in the browser and in the host, which prerenders the same components
/// (STAGE_3_PLAN 8.1). Authentication state is added separately: deserialized in the browser, from the cookie on the host.
/// </summary>
public static class ClientServices
{
    public static IServiceCollection AddSmartSpareClient(this IServiceCollection services)
    {
        services.AddMudServices(options =>
        {
            options.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomCenter;
            options.SnackbarConfiguration.PreventDuplicates = true;
            options.SnackbarConfiguration.VisibleStateDuration = 8000;
        });
        services.AddLocalization();
        services.AddAuthorizationCore(AppPolicies.Register);
        services.AddCascadingAuthenticationState();
        services.AddScoped<ProblemMessages>();
        services.TryAddSingleton(TimeProvider.System);

        // One typed client per module. The handler chain is composed per DI scope instead of through IHttpClientFactory:
        // the factory creates handlers in their own scope, where AntiforgeryStateProvider (which takes the persisted token
        // once) and NavigationManager would be different, uninitialised instances.
        services.AddScoped(sp => new IdentityClient(CreateHttpClient(sp)));
        services.AddScoped(sp => new InventoryClient(CreateHttpClient(sp)));
        services.AddScoped(sp => new FleetClient(CreateHttpClient(sp)));
        return services;
    }

    /// <summary>Same-origin <see cref="HttpClient"/> with the antiforgery and session handlers (STAGE_3_PLAN 8.5).</summary>
    public static HttpClient CreateHttpClient(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var navigation = services.GetRequiredService<NavigationManager>();
#pragma warning disable CA2000 // The handlers are disposed with the HttpClient, which the typed client owns.
        var handler = new AntiforgeryHandler(services.GetRequiredService<AntiforgeryStateProvider>())
        {
            InnerHandler = new UnauthorizedHandler(navigation, services.GetRequiredService<ISnackbar>(), services.GetRequiredService<ProblemMessages>())
            {
                InnerHandler = new HttpClientHandler(),
            },
        };
#pragma warning restore CA2000
        return new HttpClient(handler) { BaseAddress = new Uri(navigation.BaseUri) };
    }
}
