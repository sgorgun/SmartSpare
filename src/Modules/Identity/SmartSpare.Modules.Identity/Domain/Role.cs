using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Identity.Domain;

/// <summary>
/// Application (RBAC) role, e.g. <c>BRAND_MANAGER</c>. Read-only reference data seeded by the Identity
/// migration (<c>db/sql/identity/seed_roles.sql</c>); the application never creates or changes roles.
/// </summary>
internal sealed class Role : Entity<short>
{
    private Role()
        : base(default)
    {
        Code = null!;
        Name = null!;
    }

    public string Code { get; private set; }

    public string Name { get; private set; }

    public string? Description { get; private set; }
}
