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

    public static readonly Assembly Bff = typeof(Bff.BffDefaults).Assembly;

    public static readonly Assembly WebClient = typeof(Web.Client.Routes).Assembly;

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

    /// <summary>Lower-case name of the module whose implementation assembly defines <paramref name="type"/>.</summary>
    public static string ModuleOf(Type type) =>
        Modules.Single(m => m.Implementation == type.Assembly).Name.ToLowerInvariant();

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

    /// <summary>
    /// C# source files of <paramref name="module"/> under its <c>{area}/</c> folder (for example <c>Domain</c>),
    /// without build output.
    /// </summary>
    public static IReadOnlyList<string> SourceFiles(Module module, string area)
    {
        ArgumentNullException.ThrowIfNull(module);
        var folder = Path.Combine(RootDirectory, "src", "Modules", module.Name, $"SmartSpare.Modules.{module.Name}", area);
        if (!Directory.Exists(folder))
        {
            return [];
        }

        var separator = Path.DirectorySeparatorChar;
        return
        [
            .. Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories)
                .Where(p => !p.Contains($"{separator}obj{separator}", StringComparison.Ordinal)
                            && !p.Contains($"{separator}bin{separator}", StringComparison.Ordinal)),
        ];
    }

    /// <summary>Package names from &lt;PackageReference&gt; items of a project under <c>src/</c>.</summary>
    public static IEnumerable<string> PackageReferences(string projectName) =>
        ProjectItems(projectName, "PackageReference");

    /// <summary>Framework names from &lt;FrameworkReference&gt; items of a project under <c>src/</c>.</summary>
    public static IEnumerable<string> FrameworkReferences(string projectName) =>
        ProjectItems(projectName, "FrameworkReference");

    private static IEnumerable<string> ProjectItems(string projectName, string item)
    {
        var path = Directory
            .EnumerateFiles(Path.Combine(RootDirectory, "src"), projectName + ".csproj", SearchOption.AllDirectories)
            .Single();

        return XDocument.Load(path).Descendants(item).Select(e => e.Attribute("Include")!.Value);
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
