using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Identity.Domain;

/// <summary>A role granted to a user (child entity of <see cref="AppUser"/>; the identifier is the role id).</summary>
internal sealed class UserRoleAssignment : Entity<short>
{
    private UserRoleAssignment()
        : base(default)
    {
    }

    internal UserRoleAssignment(short roleId, DateTimeOffset grantedAt, Guid? grantedBy)
        : base(roleId)
    {
        GrantedAt = grantedAt;
        GrantedBy = grantedBy;
    }

    public short RoleId => Id;

    public DateTimeOffset GrantedAt { get; private set; }

    /// <summary>The user who granted the role; <see langword="null"/> for seeded or system grants.</summary>
    public Guid? GrantedBy { get; private set; }
}
