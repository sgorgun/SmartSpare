namespace SmartSpare.Modules.Identity.Domain;

/// <summary>
/// A permission of the code catalogue as the database knows it (<c>iam.permission</c>, ADR 0028): seeded by migration,
/// never edited by users; a test fails if the table and <c>Permissions.All</c> differ.
/// </summary>
internal sealed class PermissionDefinition
{
    private PermissionDefinition()
    {
        Code = null!;
        Module = null!;
        Description = null!;
    }

    public string Code { get; private set; }

    public string Module { get; private set; }

    public string Description { get; private set; }
}

/// <summary>One permission of a role's bundle (<c>iam.role_permission</c>, ADR 0028).</summary>
internal sealed class RolePermission
{
    private RolePermission()
    {
        Permission = null!;
    }

    public RolePermission(short roleId, string permission)
    {
        RoleId = roleId;
        Permission = permission;
    }

    public short RoleId { get; private set; }

    public string Permission { get; private set; }
}
