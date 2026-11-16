using System.Reflection;
using SmartSpare.Infrastructure.Modules;

namespace SmartSpare.Host;

/// <summary>
/// Finds the module installers deployed next to the host. Assemblies are located on disk rather
/// than through compile-time references, because the compiler drops references to assemblies whose
/// types are never used, and the host deliberately never uses module types directly.
/// </summary>
internal static class ModuleDiscovery
{
    private const string ModuleAssemblyPattern = "SmartSpare.Modules.*.dll";
    private const string ContractsSuffix = ".Contracts";

    public static IReadOnlyList<IModuleInstaller> DiscoverInstallers()
    {
        var installers = Directory
            .EnumerateFiles(AppContext.BaseDirectory, ModuleAssemblyPattern)
            .Where(path => !Path.GetFileNameWithoutExtension(path).EndsWith(ContractsSuffix, StringComparison.Ordinal))
            .Select(path => Assembly.Load(AssemblyName.GetAssemblyName(path)))
            .SelectMany(assembly => assembly.GetExportedTypes())
            .Where(type => type is { IsClass: true, IsAbstract: false } && type.IsAssignableTo(typeof(IModuleInstaller)))
            .Select(type => (IModuleInstaller)Activator.CreateInstance(type)!)
            .OrderBy(installer => installer.Name, StringComparer.Ordinal)
            .ToList();

        var duplicate = installers.GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"More than one module installer is named '{duplicate.Key}'.");
        }

        return installers;
    }
}
