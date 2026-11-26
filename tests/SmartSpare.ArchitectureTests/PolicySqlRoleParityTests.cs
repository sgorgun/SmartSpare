using System.Text.RegularExpressions;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.Infrastructure.Security;

namespace SmartSpare.ArchitectureTests;

/// <summary>
/// Parity rule of STAGE_3_PLAN section 3.2: a policy that fronts a SQL business function allows exactly the roles of
/// that function's <c>util.require_role(...)</c> call. The test reads <c>db/sql/</c> like the other architecture tests read
/// the repository, so a role list changed on one side only, or a new SQL function without a policy, fails the build.
/// </summary>
public sealed partial class PolicySqlRoleParityTests
{
    /// <summary>The policy table of section 3.2: each policy and the SQL functions it fronts.</summary>
    private static readonly Dictionary<string, string[]> Fronts = new(StringComparer.Ordinal)
    {
        [AppPolicies.Administration] = ["iam.anonymise_user"],
        [AppPolicies.PlanInventory] = ["inventory.flag_exceptional_demand", "procurement.submit_purchase_order", "procurement.export_purchase_order"],
        [AppPolicies.ApproveTransfer] = ["inventory.approve_transfer"],
        [AppPolicies.CancelTransfer] = ["inventory.cancel_transfer"],
        [AppPolicies.MoveStock] = ["inventory.dispatch_transfer", "inventory.receive_transfer", "procurement.receive_purchase_order_line"],
        [AppPolicies.RecordServiceWork] = ["fleet.log_engine_hours", "fleet.complete_service_order"],
        [AppPolicies.ManageServiceOrders] = ["fleet.create_service_order_draft", "fleet.cancel_service_order"],
        [AppPolicies.ApprovePurchaseOrder] = ["procurement.approve_purchase_order"],
        [AppPolicies.CancelPurchaseOrder] = ["procurement.cancel_purchase_order"],
        [AppPolicies.DecideRecommendation] = ["forecast.decide_recommendation"],
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

    public static TheoryData<string> BrandScopedPolicies => [.. BrandScopeRequirements.ByPolicy.Keys];

    [Theory]
    [MemberData(nameof(MappedFunctions))]
    public void Policy_allows_exactly_the_roles_of_the_sql_function(string policy, string function)
    {
        SqlRoles.Value.ShouldContainKey(function, $"{function} has no util.require_role call in db/sql/");

        var sql = SqlRoles.Value[function].Order(StringComparer.Ordinal).ToList();
        var api = AppPolicies.Roles[policy].Order(StringComparer.Ordinal).ToList();

        api.ShouldBe(sql, $"policy {policy} [{string.Join(", ", api)}] differs from {function} [{string.Join(", ", sql)}]");
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
        var role = policy == AppPolicies.Administration ? "app_admin" : "app_write";

        SqlExecuteGrants.Value.ShouldContainKey(function, $"{function} (policy {policy}) has no GRANT EXECUTE in db/sql/");
        SqlExecuteGrants.Value[function].ShouldContain(role, $"{function} (policy {policy}) is not executable by {role}");
    }

    [Fact]
    public void Every_sql_role_check_is_fronted_by_a_policy()
    {
        var mapped = Fronts.Values.SelectMany(f => f).ToHashSet(StringComparer.Ordinal);

        SqlRoles.Value.Keys.Where(f => !mapped.Contains(f)).Order(StringComparer.Ordinal)
            .ShouldBeEmpty("a SQL function with a role check needs a policy row in STAGE_3_PLAN section 3.2 and here");
    }

    [Fact]
    public void The_sql_files_are_found()
    {
        SqlRoles.Value.Count.ShouldBeGreaterThanOrEqualTo(Fronts.Values.Sum(f => f.Length));
    }

    [Theory]
    [MemberData(nameof(BrandScopedPolicies))]
    public void Brand_scope_unscoped_roles_plus_the_scoped_role_equal_the_policy_roles(string policy)
    {
        var requirement = BrandScopeRequirements.ByPolicy[policy];

        requirement.UnscopedRoles.Append(requirement.ScopedRole).Order(StringComparer.Ordinal)
            .ShouldBe(AppPolicies.Roles[policy].Order(StringComparer.Ordinal));
    }

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
                var roles = RequireRole().Matches(body)
                    .SelectMany(call => QuotedRole().Matches(call.Groups["args"].Value).Select(r => r.Groups["role"].Value))
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
}
