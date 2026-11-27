using NetArchTest.Rules;
using SmartSpare.Infrastructure.Modules;

namespace SmartSpare.ArchitectureTests;

/// <summary>Module boundaries from ADR 0002 and ADR 0003.</summary>
public sealed class ModuleBoundaryTests
{
    [Fact]
    public void AllModulesAreCovered() =>
        Directory.EnumerateDirectories(Path.Combine(Solution.RootDirectory, "src", "Modules"))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ShouldBe(Solution.Modules.Select(m => m.Name).Order(StringComparer.Ordinal));

    [Theory]
    [MemberData(nameof(Solution.ModuleNames), MemberType = typeof(Solution))]
    public void Module_DoesNotDependOnOtherModulesImplementation(string name)
    {
        var module = Solution.Get(name);
        var otherImplementations = Solution.Modules.Where(m => m != module).ToList();

        var result = Selection.Of(module.Implementation)
            .ShouldNot()
            .HaveDependencyOnAny([.. otherImplementations.SelectMany(ImplementationNamespaces)])
            .GetResult();
        result.IsSuccessful.ShouldBeTrue(Describe(result));

        var forbiddenAssemblies = otherImplementations.Select(m => m.Implementation.GetName().Name!).ToHashSet(StringComparer.Ordinal);
        Solution.SmartSpareReferences(module.Implementation).ShouldNotContain(r => forbiddenAssemblies.Contains(r));
        Solution.ProjectReferences(module.Implementation.GetName().Name!).ShouldNotContain(r => forbiddenAssemblies.Contains(r));
    }

    [Theory]
    [MemberData(nameof(Solution.ModuleNames), MemberType = typeof(Solution))]
    public void Module_ReferencesOnlyBuildingBlocksAndContracts(string name)
    {
        var module = Solution.Get(name);

        // SmartSpare.ApiContracts is the public HTTP contract; modules may bind and return its DTOs (STAGE_3_PLAN S14).
        Solution.ProjectReferences(module.Implementation.GetName().Name!).ShouldAllBe(r =>
            r == "SmartSpare.SharedKernel"
            || r == "SmartSpare.Infrastructure"
            || r == "SmartSpare.ApiContracts"
            || (r.StartsWith(Solution.ModulePrefix, StringComparison.Ordinal) && r.EndsWith(Solution.ContractsSuffix, StringComparison.Ordinal)));
    }

    [Theory]
    [MemberData(nameof(Solution.ModuleNames), MemberType = typeof(Solution))]
    public void Domain_DoesNotDependOnEfCoreOrAspNetCore(string name)
    {
        var module = Solution.Get(name);

        var result = Selection.InArea(module, "Domain")
            .ShouldNot().HaveDependencyOnAny("Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "Npgsql")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Describe(result));
    }

    [Theory]
    [MemberData(nameof(Solution.ModuleNames), MemberType = typeof(Solution))]
    public void Contracts_ReferenceOnlySharedKernel(string name)
    {
        var module = Solution.Get(name);
        const string allowed = "SmartSpare.SharedKernel";

        Solution.SmartSpareReferences(module.Contracts).ShouldAllBe(r => r == allowed);
        Solution.ProjectReferences(module.Contracts.GetName().Name!).ShouldAllBe(r => r == allowed);

        var result = Selection.Of(module.Contracts)
            .ShouldNot().HaveDependencyOnAny("Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "SmartSpare.Infrastructure")
            .GetResult();
        result.IsSuccessful.ShouldBeTrue(Describe(result));
    }

    [Theory]
    [MemberData(nameof(Solution.ModuleNames), MemberType = typeof(Solution))]
    public void Module_ExposesOnlyItsInstaller(string name)
    {
        var module = Solution.Get(name);

        var exported = module.Implementation.GetExportedTypes();

        exported.ShouldHaveSingleItem($"{name} must expose only its module installer, found: {string.Join(", ", exported.Select(t => t.FullName))}");
        var installer = exported[0];
        installer.IsAssignableTo(typeof(IModuleInstaller)).ShouldBeTrue();
        installer.IsSealed.ShouldBeTrue();
    }

    /// <summary>
    /// Public static methods a host may call on a module installer, besides the <c>IModuleInstaller</c> members. Each one
    /// hands the host a module capability without exposing a module type (STAGE_3_PLAN sections 2.3 and 5).
    /// </summary>
    private static readonly Dictionary<string, string[]> AllowedInstallerMethods = new(StringComparer.Ordinal)
    {
        ["Identity"] = ["ConfigurePasswordHasher", "PersistDataProtectionKeys"],
    };

    [Theory]
    [MemberData(nameof(Solution.ModuleNames), MemberType = typeof(Solution))]
    public void Installer_PublicStaticMethods_AreExplicitlyAllowed(string name)
    {
        var installer = Solution.Get(name).Implementation.GetExportedTypes().Single();

        var methods = installer
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Select(m => m.Name)
            .Order(StringComparer.Ordinal);

        methods.ShouldBe(AllowedInstallerMethods.GetValueOrDefault(name, []).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ApiContracts_DoesNotReferenceModules()
    {
        Solution.SmartSpareReferences(Solution.ApiContracts)
            .ShouldNotContain(r => r.StartsWith(Solution.ModulePrefix, StringComparison.Ordinal));
        Solution.ProjectReferences("SmartSpare.ApiContracts")
            .ShouldNotContain(r => r.StartsWith(Solution.ModulePrefix, StringComparison.Ordinal));
    }

    [Fact]
    public void WebClient_ReferencesOnlyApiContracts()
    {
        // The browser gets the public HTTP contract only (ADR 0006, STAGE_3_PLAN 8.2): no module, no building block.
        Solution.ProjectReferences("SmartSpare.Web.Client").ShouldBe(["SmartSpare.ApiContracts"]);
        Solution.SmartSpareReferences(Solution.WebClient).ShouldAllBe(r => r == "SmartSpare.ApiContracts");
        Solution.PackageReferences("SmartSpare.Web.Client")
            .ShouldNotContain(p => p.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) || p.StartsWith("Npgsql", StringComparison.Ordinal));

        var result = Selection.Of(Solution.WebClient)
            .ShouldNot().HaveDependencyOnAny("SmartSpare.Modules", "SmartSpare.Infrastructure", "SmartSpare.Bff", "SmartSpare.SharedKernel")
            .GetResult();
        result.IsSuccessful.ShouldBeTrue(Describe(result));
    }

    [Fact]
    public void ApiContracts_DependsOnlyOnFluentValidationAndAuthorization()
    {
        // It is shared with the WebAssembly client: no ASP.NET Core framework, no EF Core, nothing of the server.
        string[] allowedPackages = ["FluentValidation", "Microsoft.AspNetCore.Authorization"];

        Solution.PackageReferences("SmartSpare.ApiContracts").ShouldAllBe(p => allowedPackages.Contains(p));
        Solution.FrameworkReferences("SmartSpare.ApiContracts").ShouldBeEmpty();
        Solution.ProjectReferences("SmartSpare.ApiContracts").ShouldBeEmpty();
        // The permission policy provider (ADR 0028) registers itself and reads AuthorizationOptions: the two abstractions
        // the Authorization package already brings, both available in the browser.
        string[] allowedAssemblies = [.. allowedPackages, "Microsoft.Extensions.DependencyInjection.Abstractions", "Microsoft.Extensions.Options"];
        Solution.ApiContracts.GetReferencedAssemblies().Select(a => a.Name!)
            .ShouldAllBe(n => n == "System.Runtime" || n.StartsWith("System.", StringComparison.Ordinal) || n == "netstandard"
                              || allowedAssemblies.Contains(n));
    }

    [Fact]
    public void Bff_ReferencesNoModuleImplementation_OnlyIdentityContracts()
    {
        // ADR 0016: the BFF runs in the gateway, which has no module code; it reaches Identity only through its Contracts.
        var moduleReferences = Solution.ProjectReferences("SmartSpare.Bff")
            .Concat(Solution.SmartSpareReferences(Solution.Bff))
            .Where(r => r.StartsWith(Solution.ModulePrefix, StringComparison.Ordinal))
            .Distinct()
            .ToList();

        moduleReferences.ShouldBe(["SmartSpare.Modules.Identity.Contracts"]);

        var result = Selection.Of(Solution.Bff)
            .ShouldNot().HaveDependencyOnAny([.. Solution.Modules.SelectMany(ImplementationNamespaces)])
            .GetResult();
        result.IsSuccessful.ShouldBeTrue(Describe(result));
    }

    [Fact]
    public void SharedKernel_DependsOnNothingInTheSolutionOrFrameworks()
    {
        Solution.SmartSpareReferences(Solution.SharedKernel).ShouldBeEmpty();
        Solution.ProjectReferences("SmartSpare.SharedKernel").ShouldBeEmpty();

        var result = Selection.Of(Solution.SharedKernel)
            .ShouldNot().HaveDependencyOnAny("Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "Npgsql", "FluentValidation")
            .GetResult();
        result.IsSuccessful.ShouldBeTrue(Describe(result));
    }

    /// <summary>
    /// Namespaces that belong to a module's implementation. The Contracts namespace
    /// (<c>SmartSpare.Modules.X.Contracts</c>) is deliberately not listed: it may be used by others.
    /// </summary>
    private static IEnumerable<string> ImplementationNamespaces(Module module) =>
    [
        $"{module.Namespace}.Domain",
        $"{module.Namespace}.Features",
        $"{module.Namespace}.Infrastructure",
        $"{module.Namespace}.{module.Name}ModuleInstaller",
    ];

    private static string Describe(NetArchTest.Rules.TestResult result) =>
        result.IsSuccessful ? string.Empty : "Violations: " + string.Join(", ", result.FailingTypeNames ?? []);
}
