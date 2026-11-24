using System.Text.RegularExpressions;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Identity.Domain;

/// <summary>
/// Sign-in name of a user. The rule equals the database CHECK <c>ck_app_user_user_name</c>:
/// <c>user_name ~ '^[a-z0-9._-]{3,50}$'</c> on a <c>citext</c> column. A regular expression match on
/// <c>citext</c> is case-insensitive, so upper-case letters pass the CHECK as well; the value object
/// accepts exactly the same strings.
/// </summary>
internal sealed partial class UserName : ValueObject
{
    private UserName(string value) => Value = value;

    public string Value { get; }

    public static Result<UserName> Create(string? value) =>
        value is not null && Pattern().IsMatch(value)
            ? new UserName(value)
            : IdentityErrors.InvalidUserName;

    /// <summary>Restores a value read from the database, where the CHECK has already validated it.</summary>
    public static UserName FromDatabase(string value) =>
        Create(value) is { IsSuccess: true } result
            ? result.Value
            : throw new InvalidOperationException("iam.app_user.user_name violates ck_app_user_user_name.");

    public override string ToString() => Value;

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        // citext compares case-insensitively; so does the value object.
        yield return Value.ToLowerInvariant();
    }

    [GeneratedRegex("^[a-z0-9._-]{3,50}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
