using Microsoft.AspNetCore.Authorization;

namespace SmartSpare.ApiContracts.Authorization;

/// <summary>
/// Authorization policies of SmartSpare (STAGE_3_PLAN section 3.2), registered by the host and the client with
/// <see cref="Register"/>. Policies are claims-only (ADR 0012) and never name an authentication scheme.
/// <para>
/// Parity rule: a policy that fronts a SQL business function allows exactly the roles of that function's
/// <c>util.require_role(...)</c> call (<c>PolicySqlRoleParityTests</c>). Brand scope and warehouse match are resource
/// checks on top of the role gate and never widen it.
/// </para>
/// </summary>
public static class AppPolicies
{
    public const string Administration = nameof(Administration);
    public const string PlanInventory = nameof(PlanInventory);
    public const string ApproveTransfer = nameof(ApproveTransfer);
    public const string CancelTransfer = nameof(CancelTransfer);
    public const string MoveStock = nameof(MoveStock);
    public const string RecordServiceWork = nameof(RecordServiceWork);
    public const string ManageServiceOrders = nameof(ManageServiceOrders);
    public const string ApprovePurchaseOrder = nameof(ApprovePurchaseOrder);
    public const string CancelPurchaseOrder = nameof(CancelPurchaseOrder);
    public const string DecideRecommendation = nameof(DecideRecommendation);
    public const string ReadAudit = nameof(ReadAudit);

    /// <summary>The roles each policy allows: the table of STAGE_3_PLAN section 3.2.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Roles { get; } = new Dictionary<string, IReadOnlyList<string>>
    {
        [Administration] = [AppRoles.Administrator],
        [PlanInventory] = [AppRoles.BrandManager, AppRoles.InventoryPlanner],
        [ApproveTransfer] = [AppRoles.BrandManager, AppRoles.InventoryPlanner],
        [CancelTransfer] = [AppRoles.BrandManager, AppRoles.InventoryPlanner, AppRoles.Administrator],
        [MoveStock] = [AppRoles.WarehouseKeeper, AppRoles.Administrator],
        [RecordServiceWork] = [AppRoles.ServiceEngineer, AppRoles.Administrator],
        [ManageServiceOrders] = [AppRoles.ServiceEngineer, AppRoles.BrandManager, AppRoles.Administrator],
        [ApprovePurchaseOrder] = [AppRoles.BrandManager, AppRoles.Sponsor],
        [CancelPurchaseOrder] = [AppRoles.BrandManager, AppRoles.Sponsor, AppRoles.Administrator],
        [DecideRecommendation] = [AppRoles.BrandManager, AppRoles.InventoryPlanner],
        [ReadAudit] = [AppRoles.Sponsor, AppRoles.Administrator],
    };

    /// <summary>
    /// Registers every policy, and a fallback policy that requires an authenticated user, so an endpoint without
    /// metadata is never public by accident (anonymous endpoints opt out with <c>AllowAnonymous</c>).
    /// </summary>
    public static void Register(AuthorizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();

        foreach (var (name, roles) in Roles)
        {
            options.AddPolicy(name, policy => policy.RequireAuthenticatedUser().RequireRole(roles));
        }
    }
}
