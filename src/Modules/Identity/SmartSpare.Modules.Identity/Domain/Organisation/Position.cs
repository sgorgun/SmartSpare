using System.Text.RegularExpressions;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Identity.Domain.Organisation;

/// <summary>
/// A job position of the dealership (master data, STAGE_7_PLAN section 1.1). Its <see cref="Roles"/> are the role
/// template: an employee in the position has exactly these application roles (ADR 0026). Positions are archived, never
/// deleted; archiving is refused while an active employee holds the position (checked by the handler).
/// </summary>
internal sealed partial class Position : AggregateRoot<short>
{
    public const int MaxNameLength = 120;

    public const int MaxDescriptionLength = 500;

    private readonly List<PositionRole> _roles = [];

    private Position()
        : base(default)
    {
        Code = null!;
        Name = null!;
    }

    private Position(string code, string name, string? description)
        : base(default)
    {
        Code = code;
        Name = name;
        Description = description;
    }

    /// <summary>Business key, e.g. <c>BRAND_MANAGER</c> (<c>ck_position_code</c>).</summary>
    public string Code { get; private set; }

    public string Name { get; private set; }

    public string? Description { get; private set; }

    public bool IsArchived { get; private set; }

    /// <summary>A built-in position that cannot be changed or archived (the Superadministrator, ADR 0028).</summary>
    public bool IsLocked { get; private set; }

    public DateTimeOffset? ArchivedAt { get; private set; }

    public Guid? ArchivedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>The role template (role ids of <c>iam.role</c>).</summary>
    public IReadOnlyCollection<PositionRole> Roles => _roles.AsReadOnly();

    public IReadOnlySet<short> RoleIds => _roles.Select(r => r.RoleId).ToHashSet();

    public static Result<Position> Create(string code, string name, string? description, IEnumerable<short> roleIds)
    {
        ArgumentNullException.ThrowIfNull(roleIds);
        var normalised = code?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!CodePattern().IsMatch(normalised))
        {
            return OrganisationErrors.InvalidPositionCode;
        }

        var position = new Position(normalised, string.Empty, null);
        var details = position.SetDetails(name, description);
        if (details.IsFailure)
        {
            return details.Error;
        }

        position.SetRoles(roleIds);
        return position;
    }

    /// <summary>Changes name, description and template; returns whether the template changed (the holders' rights change).</summary>
    public Result<bool> Update(string name, string? description, IEnumerable<short> roleIds)
    {
        ArgumentNullException.ThrowIfNull(roleIds);
        var details = SetDetails(name, description);
        return details.IsFailure ? details.Error : SetRoles(roleIds);
    }

    public void Archive(DateTimeOffset now, Guid? by)
    {
        if (IsArchived)
        {
            return;
        }

        IsArchived = true;
        ArchivedAt = now;
        ArchivedBy = by;
    }

    public void Restore()
    {
        IsArchived = false;
        ArchivedAt = null;
        ArchivedBy = null;
    }

    private Result SetDetails(string name, string? description)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > MaxNameLength)
        {
            return OrganisationErrors.InvalidName;
        }

        var text = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (text is { Length: > MaxDescriptionLength })
        {
            return OrganisationErrors.InvalidDescription;
        }

        Name = trimmed;
        Description = text;
        return Result.Success();
    }

    private bool SetRoles(IEnumerable<short> roleIds)
    {
        var target = roleIds.ToHashSet();
        var removed = _roles.RemoveAll(r => !target.Contains(r.RoleId));
        var added = target.Except(_roles.Select(r => r.RoleId)).ToList();
        _roles.AddRange(added.Select(id => new PositionRole(id)));
        return removed > 0 || added.Count > 0;
    }

    [GeneratedRegex("^[A-Z0-9_]{2,30}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();
}

/// <summary>A role of a position's template (child entity of <see cref="Position"/>; the identifier is the role id).</summary>
internal sealed class PositionRole : Entity<short>
{
    private PositionRole()
        : base(default)
    {
    }

    internal PositionRole(short roleId)
        : base(roleId)
    {
    }

    public short RoleId => Id;
}
