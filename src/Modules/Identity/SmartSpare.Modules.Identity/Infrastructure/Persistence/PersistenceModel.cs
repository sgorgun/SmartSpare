namespace SmartSpare.Modules.Identity.Infrastructure.Persistence;

// Persistence model: plain classes that mirror the tables of V016 (generated from the catalogue).
// Behaviour comes with the domain aggregates of the module stage; these types only carry data.

/// <summary>Row of <c>iam.app_user</c>.</summary>
internal sealed class AppUser
{
    public Guid Id { get; set; }

    public string UserName { get; set; } = null!;

    public string Email { get; set; } = null!;

    public string FullName { get; set; } = null!;

    public string? Phone { get; set; }

    public string? PasswordHash { get; set; }

    public Guid SecurityStamp { get; set; }

    public bool IsActive { get; set; }

    public short FailedLoginCount { get; set; }

    public DateTimeOffset? LockoutEnd { get; set; }

    public short? HomeWarehouseId { get; set; }

    public DateTimeOffset? LastLoginAt { get; set; }

    public DateTimeOffset? AnonymisedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Row of <c>iam.role</c>.</summary>
internal sealed class Role
{
    public short Id { get; set; }

    public string Code { get; set; } = null!;

    public string Name { get; set; } = null!;

    public string? Description { get; set; }
}

/// <summary>Row of <c>iam.user_brand</c>.</summary>
internal sealed class UserBrand
{
    public Guid UserId { get; set; }

    public short BrandId { get; set; }
}

/// <summary>Row of <c>iam.user_role</c>.</summary>
internal sealed class UserRole
{
    public Guid UserId { get; set; }

    public short RoleId { get; set; }

    public DateTimeOffset GrantedAt { get; set; }

    public Guid? GrantedBy { get; set; }
}

/// <summary>Platform reference table <c>ref.brand</c>, mapped read-only as the principal of this module's FKs.</summary>
internal sealed class RefBrand
{
    public short Id { get; set; }
}
