using SmartSpare.Modules.Identity;

namespace SmartSpare.Services.Identity;

/// <summary>Entry point of the Identity service (ADR 0015); everything else is the shared <see cref="ServiceHost"/>.</summary>
public sealed class IdentityService
{
    // Not static: tests start the host through WebApplicationFactory<IdentityService>.
    private IdentityService()
    {
    }

    public static Task<int> Main(string[] args) => ServiceHost.RunAsync(args, new IdentityModuleInstaller());
}
