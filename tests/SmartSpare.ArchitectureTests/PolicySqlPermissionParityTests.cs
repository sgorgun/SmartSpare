using System.Text.RegularExpressions;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.Infrastructure.Security;

namespace SmartSpare.ArchitectureTests;

/// <summary>
/// Parity rule of STAGE_3_PLAN section 3.2, on permissions since ADR 0028 (PR 7x2): a SQL business function checks exactly
/// the permission its endpoint requires, with <c>util.require_permission('...')</c>, and no role code any more. The test reads <c>db/sql/</c> like the other architecture tests read
/// the repository, so a role list changed on one side only, or a new SQL function without a policy, fails the build.
/// </summary>
public sealed partial class PolicySqlPermissionParityTests
{
    /// <summary>Each permission that fronts SQL business functions, and those functions.</summary>
    private static readonly Dictionary<string, string[]> Fronts = new(StringComparer.Ordinal)
    {
        [Permissions.Organisation.Employee.Anonymise] = ["iam.anonymise_user", "iam.anonymise_employee"],
        [Permissions.Inventory.Demand.Flag] = ["inventory.flag_exceptional_demand"],
        [Permissions.Procurement.PurchaseOrder.Create] = ["procurement.submit_purchase_order", "procurement.export_purchase_order"],
        [Permissions.Inventory.Transfer.Approve] = ["inventory.approve_transfer"],
        [Permissions.Inventory.Transfer.Cancel] = ["inventory.cancel_transfer"],
        [Permissions.Inventory.Transfer.Dispatch] = ["inventory.dispatch_transfer"],
        [Permissions.Inventory.Transfer.Receive] = ["inventory.receive_transfer"],
        [Permissions.Procurement.PurchaseOrder.Receive] = ["procurement.receive_purchase_order_line"],
        [Permissions.Fleet.EngineHours.Record] = ["fleet.log_engine_hours"],
        [Permissions.Fleet.ServiceOrder.Complete] = ["fleet.complete_service_order"],
        [Permissions.Fleet.ServiceOrder.Create] = ["fleet.create_service_order_draft"],
        [Permissions.Fleet.ServiceOrder.Cancel] = ["fleet.cancel_service_order"],
        [Permissions.Procurement.PurchaseOrder.Approve] = ["procurement.approve_purchase_order"],
        [Permissions.Procurement.PurchaseOrder.Cancel] = ["procurement.cancel_purchase_order"],
        [Permissions.Forecasting.Recommendation.Decide] = ["forecast.decide_recommendation"],
    };

    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlySet<string>>> SqlRoles = new(ReadSqlRoleChecks);

    private static readonly Lazy<IReadOnlyDictionary<string, IReadOnlySet<string>>> SqlExecuteGrants = new(ReadExecuteGrants);

    public static TheoryData<string, string> MappedFunctions()
    {
        var data = new TheoryData<string, string>();
        foreach (var (policy, functions) in Fronts)
        {
            foreach (var function in functions)
            {
                data.Add(policy, function);
            }
        }

        return data;
    }

    public static TheoryData<string> BrandScopedPolicies => [.. BrandScopeRequirements.ByPermission.Keys];

    [Theory]
    [MemberData(nameof(MappedFunctions))]
    public void The_sql_function_checks_exactly_the_permission_of_its_endpoint(string policy, string function)
    {
        SqlRoles.Value.ShouldContainKey(function, $"{function} has no permission check in db/sql/");

        SqlRoles.Value[function].ToList().ShouldBe([policy], $"{function} must check {policy} with util.require_permission and no role code");
    }

    /// <summary>
    /// The other half of the agreement between a policy and its SQL function (stage 6, #118): the API reaches the function
    /// only if its database role may execute it. A fronted function is granted EXECUTE to <c>app_write</c>, the role of the
    /// API and service logins; the Administration functions to <c>app_admin</c>, which those logins switch to for one
    /// administrative transaction (STAGE_5_PLAN section 9). So a policy never fronts a function the API cannot call.
    /// </summary>
    [Theory]
    [MemberData(nameof(MappedFunctions))]
    public void The_api_role_may_execute_every_fronted_function(string policy, string function)
    {
        var role = policy.StartsWith("Organisation.", StringComparison.Ordinal) ? "app_admin" : "app_write";

        SqlExecuteGrants.Value.ShouldContainKey(function, $"{function} (policy {policy}) has no GRANT EXECUTE in db/sql/");
        SqlExecuteGrants.Value[function].ShouldContain(role, $"{function} (policy {policy}) is not executable by {role}");
    }

    [Fact]
    public void Every_sql_role_check_is_fronted_by_a_policy()
    {
        var mapped = Fronts.Values.SelectMany(f => f).ToHashSet(StringComparer.Ordinal);

        SqlRoles.Value.Keys.Where(f => !mapped.Contains(f)).Order(StringComparer.Ordinal)
            .ShouldBeEmpty("a SQL function with a permission check needs its endpoint's permission here");
    }

    [Fact]
    public void The_sql_files_are_found()
    {
        SqlRoles.Value.Count.ShouldBeGreaterThanOrEqualTo(Fronts.Values.Sum(f => f.Length));
    }

    [Theory]
    [MemberData(nameof(BrandScopedPolicies))]
    public void A_brand_scoped_action_refines_its_own_permission(string permission) =>
        BrandScopeRequirements.ByPermission[permission].Permission.ShouldBe(permission);

    /// <summary>Function name → roles of its <c>util.require_role(...)</c> calls, from every <c>CREATE FUNCTION</c> in db/sql/.</summary>
    private static Dictionary<string, IReadOnlySet<string>> ReadSqlRoleChecks()
    {
        var result = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        var files = Directory.EnumerateFiles(Path.Combine(Solution.RootDirectory, "db", "sql"), "*.sql", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal);

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var starts = CreateFunction().Matches(text).ToList();
            for (var i = 0; i < starts.Count; i++)
            {
                var end = i + 1 < starts.Count ? starts[i + 1].Index : text.Length;
                var body = text[starts[i].Index..end];
                // The checks of this definition: permission names, and role codes (marked "role:") of any util.require_role left.
                var roles = RequireRole().Matches(body)
                    .SelectMany(call => QuotedRole().Matches(call.Groups["args"].Value).Select(r => "role:" + r.Groups["role"].Value))
                    .Concat(RequirePermission().Matches(body).Select(m => m.Groups["permission"].Value))
                    .ToHashSet(StringComparer.Ordinal);

                if (roles.Count > 0)
                {
                    result[starts[i].Groups["name"].Value] = roles;     // a later file redefines the function
                }
            }
        }

        return result;
    }

    /// <summary>Function name → roles granted EXECUTE on it, from every <c>GRANT EXECUTE ON FUNCTION ... TO ...;</c> in db/sql/.</summary>
    private static Dictionary<string, IReadOnlySet<string>> ReadExecuteGrants()
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Solution.RootDirectory, "db", "sql"), "*.sql", SearchOption.AllDirectories))
        {
            foreach (Match grant in GrantExecute().Matches(File.ReadAllText(file)))
            {
                var roles = grant.Groups["roles"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                foreach (Match function in FunctionName().Matches(grant.Groups["functions"].Value))
                {
                    if (!result.TryGetValue(function.Groups["name"].Value, out var granted))
                    {
                        result[function.Groups["name"].Value] = granted = new HashSet<string>(StringComparer.Ordinal);
                    }

                    granted.UnionWith(roles);
                }
            }
        }

        return result.ToDictionary(r => r.Key, r => (IReadOnlySet<string>)r.Value, StringComparer.Ordinal);
    }

    [GeneratedRegex(@"GRANT\s+EXECUTE\s+ON\s+FUNCTION\s+(?<functions>.+?)\s+TO\s+(?<roles>[a-z_,\s]+?)\s*;", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex GrantExecute();

    [GeneratedRegex(@"(?<name>[a-z_]+\.[a-z_]+)\s*\(")]
    private static partial Regex FunctionName();

    [GeneratedRegex(@"CREATE\s+(?:OR\s+REPLACE\s+)?FUNCTION\s+(?<name>[a-z_]+\.[a-z_]+)\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex CreateFunction();

    [GeneratedRegex(@"util\.require_role\s*\((?<args>[^)]*)\)", RegexOptions.IgnoreCase)]
    private static partial Regex RequireRole();

    [GeneratedRegex(@"'(?<role>[A-Z_]+)'")]
    private static partial Regex QuotedRole();

    [GeneratedRegex(@"util\.require_permission\s*\(\s*'(?<permission>[A-Za-z.]+)'\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex RequirePermission();
}
