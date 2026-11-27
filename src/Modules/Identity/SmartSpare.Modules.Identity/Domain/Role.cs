using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Identity.Domain;

/// <summary>
/// A role (ADR 0028): a named bundle of permissions of the code catalogue, data that administrators edit. A position's
/// role template holds roles; an employee's permissions are the union of the roles of the position. A locked role (the
/// built-in Administrator, and the Superadministrator) cannot be changed or archived; archived roles cannot be put in a
/// template. Separation of duties and the self-granting rule need other data and are checked by the handler.
/// </summary>
internal sealed class Role : AggregateRoot<short>
{
    public const int MaxNameLength = 80;

    public const int MaxDescriptionLength = 500;

    private readonly List<RolePermission> _permissions = [];

    private Role()
        : base(default)
    {
        Code = null!;
        Name = null!;
    }

    private Role(string code, string name, string? description)
        : base(default)
    {
        Code = code;
        Name = name;
        Description = description;
    }

    public string Code { get; private set; }

    public string Name { get; private set; }

    public string? Description { get; private set; }

    public bool IsLocked { get; private set; }

    public bool IsArchived { get; private set; }

    public IReadOnlyCollection<RolePermission> Permissions => _permissions.AsReadOnly();

    public IReadOnlySet<string> PermissionNames => _permissions.Select(p => p.Permission).ToHashSet(StringComparer.Ordinal);

    /// <summary>A new role; <paramref name="isPermission"/> says whether a name is in the permission catalogue (code outside the domain).</summary>
    public static Result<Role> Create(string? code, string? name, string? description, IEnumerable<string> permissions, Func<string, bool> isPermission)
    {
        ArgumentNullException.ThrowIfNull(isPermission);
        ArgumentNullException.ThrowIfNull(permissions);
        var normalised = code?.Trim().ToUpperInvariant() ?? string.Empty;
        if (normalised.Length is < 3 or > 30 || !normalised.All(c => c is (>= 'A' and <= 'Z') or '_'))
        {
            return RoleErrors.InvalidCode;
        }

        var role = new Role(normalised, string.Empty, null);
        var changed = role.Apply(name, description, permissions, isPermission);
        return changed.IsFailure ? changed.Error : role;
    }

    /// <summary>Changes name, description and permissions; <see langword="true"/> if the permissions changed.</summary>
    public Result<bool> Update(string? name, string? description, IEnumerable<string> permissions, Func<string, bool> isPermission) =>
        IsLocked ? RoleErrors.Locked : Apply(name, description, permissions, isPermission);

    public Result Archive()
    {
        if (IsLocked)
        {
            return RoleErrors.Locked;
        }

        IsArchived = true;
        return Result.Success();
    }

    public void Restore() => IsArchived = false;

    private Result<bool> Apply(string? name, string? description, IEnumerable<string> permissions, Func<string, bool> isPermission)
    {
        var trimmedName = name?.Trim() ?? string.Empty;
        if (trimmedName.Length is 0 or > MaxNameLength)
        {
            return RoleErrors.InvalidName;
        }

        var trimmedDescription = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (trimmedDescription is { Length: > MaxDescriptionLength })
        {
            return RoleErrors.InvalidDescription;
        }

        var wanted = permissions.ToHashSet(StringComparer.Ordinal);
        if (wanted.Any(p => !isPermission(p)))
        {
            return RoleErrors.UnknownPermission;
        }

        Name = trimmedName;
        Description = trimmedDescription;
        var current = PermissionNames;
        if (current.SetEquals(wanted))
        {
            return false;
        }

        _permissions.RemoveAll(p => !wanted.Contains(p.Permission));
        foreach (var permission in wanted.Where(p => !current.Contains(p)).Order(StringComparer.Ordinal))
        {
            _permissions.Add(new RolePermission(Id, permission));
        }

        return true;
    }
}

/// <summary>The errors of roles (ADR 0028).</summary>
internal static class RoleErrors
{
    public static readonly Error InvalidCode = Error.Validation("Identity.InvalidRoleCode", "A role code has 3 to 30 capital letters or '_'.");

    public static readonly Error InvalidName = Error.Validation("Identity.InvalidName", "The name is required and must not be too long.");

    public static readonly Error InvalidDescription = Error.Validation("Identity.InvalidDescription", "The description is too long.");

    public static readonly Error UnknownPermission = Error.Validation("Identity.UnknownPermission", "A permission is not in the catalogue.");

    public static readonly Error NotFound = Error.NotFound("Identity.RoleNotFound", "The role does not exist.");

    public static readonly Error Locked = Error.BusinessRule("Identity.RoleLocked", "This built-in role is locked: it cannot be changed or archived.");

    public static readonly Error OwnRole = Error.Forbidden("SS301", "You cannot change a role you hold: someone else must grant you rights.");

    public static readonly Error OwnPosition = Error.Forbidden("SS301", "You cannot change your own position or its role template: someone else must grant you rights.");

    public static readonly Error OwnAssignments = Error.Forbidden("SS301", "You cannot change your own assignments: someone else must grant you rights.");

    public static readonly Error InUse = Error.Conflict("SS501", "The role cannot be archived while a position's role template holds it.");

    public static readonly Error Archived = Error.BusinessRule("SS500", "The role is archived and cannot be put in a role template.");

    public static readonly Error PositionLocked = Error.BusinessRule(
        "Identity.PositionLocked", "This built-in position is locked: it cannot be changed or archived.");

    public static readonly Error SuperadministratorRole = Error.BusinessRule(
        "Identity.SuperadministratorRole", "The Superadministrator role belongs only to the locked Superadministrator position.");

    public static readonly Error SuperadministratorOnly = Error.Forbidden(
        "Identity.SuperadministratorOnly", "Only a superadministrator can move an employee into or out of the Superadministrator position.");

    public static readonly Error LastSuperadministrator = Error.Conflict(
        "Identity.LastSuperadministrator", "The last active superadministrator cannot be removed, deactivated or anonymised.");

    public static Error DutyConflict((string First, string Second) pair) => Error.Conflict(
        "Identity.DutyConflict", $"Separation of duties: {pair.First} and {pair.Second} cannot be held together (ADR 0028).");
}
