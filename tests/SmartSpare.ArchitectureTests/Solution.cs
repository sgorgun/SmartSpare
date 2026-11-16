using System.Reflection;
using System.Xml.Linq;

namespace SmartSpare.ArchitectureTests;

/// <summary>A business module: its implementation assembly and its public Contracts assembly.</summary>
public sealed record Module(string Name, Assembly Implementation, Assembly Contracts)
{
    public string Namespace => $"SmartSpare.Modules.{Name}";

    public override string ToString() => Name;
}

/// <summary>Single list of everything the architecture rules inspect.</summary>
internal static class Solution
{
    public const string ModulePrefix = "SmartSpare.Modules.";
    public const string ContractsSuffix = ".Contracts";

    public static readonly Assembly SharedKernel = typeof(SharedKernel.Result).Assembly;

    public static readonly Assembly ApiContracts = Assembly.Load("SmartSpare.ApiContracts");

    public static readonly IReadOnlyList<Module> Modules =
    [
        Create(typeof(Modules.Identity.IdentityModuleInstaller), typeof(Modules.Identity.Contracts.IdentityModule)),
        Create(typeof(Modules.Inventory.InventoryModuleInstaller), typeof(Modules.Inventory.Contracts.InventoryModule)),
        Create(typeof(Modules.Fleet.FleetModuleInstaller), typeof(Modules.Fleet.Contracts.FleetModule)),
        Create(typeof(Modules.Procurement.ProcurementModuleInstaller), typeof(Modules.Procurement.Contracts.ProcurementModule)),
        Create(typeof(Modules.Forecasting.ForecastingModuleInstaller), typeof(Modules.Forecasting.Contracts.ForecastingModule)),
        Create(typeof(Modules.Reporting.ReportingModuleInstaller), typeof(Modules.Reporting.Contracts.ReportingModule)),
    ];

    public static TheoryData<string> ModuleNames => [.. Modules.Select(m => m.Name)];

    public static Module Get(string name) => Modules.Single(m => m.Name == name);

    public static string RootDirectory { get; } = FindRoot();

    /// <summary>Names of SmartSpare assemblies an assembly references in its compiled metadata.</summary>
    public static IEnumerable<string> SmartSpareReferences(Assembly assembly) =>
        assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => n.StartsWith("SmartSpare.", StringComparison.Ordinal));

    /// <summary>
    /// Project names from &lt;ProjectReference&gt; items. Checked in addition to assembly metadata,
    /// because the compiler drops references to assemblies whose types are not used yet.
    /// </summary>
    public static IEnumerable<string> ProjectReferences(string projectName)
    {
        var path = Directory
            .EnumerateFiles(Path.Combine(RootDirectory, "src"), projectName + ".csproj", SearchOption.AllDirectories)
            .Single();

        return XDocument.Load(path)
            .Descendants("ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension(e.Attribute("Include")!.Value.Replace('\\', '/')));
    }

    private static Module Create(Type installer, Type contractsType) =>
        new(installer.Name.Replace("ModuleInstaller", string.Empty, StringComparison.Ordinal), installer.Assembly, contractsType.Assembly);

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SmartSpare.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("SmartSpare.slnx not found above " + AppContext.BaseDirectory);
    }
}
