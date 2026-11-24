using System.Text.RegularExpressions;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Identity.Domain;

/// <summary>
/// Sign-in name of a user. The rule equals the database CHECK <c>ck_app_user_user_name</c>:
/// <c>user_name ~ '^[a-z0-9._-]{3,50}$'</c> on a <c>citext</c> column. A regular expression match on
/// <c>citext</c> is case-insensitive, so upper-case ASCII letters pass the CHECK as well, and the value object
/// accepts the same strings. Its canonical form is lower-case: <see cref="Create"/> stores
/// <c>Tomas.R</c> as <c>tomas.r</c>. Equality stays case-insensitive, like <c>citext</c>. PR 3c tightens the
/// CHECK to <c>user_name = lower(user_name)</c> as well (#69).
/// </summary>
internal sealed partial class UserName : ValueObject
{
    private UserName(string value) => Value = value;

    public string Value { get; }

    /// <summary>Validates <paramref name="value"/> and returns it in canonical lower-case form.</summary>
    public static Result<UserName> Create(string? value) =>
        value is not null && Pattern().IsMatch(value)
            ? new UserName(value.ToLowerInvariant())
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

    // Explicit ASCII ranges instead of RegexOptions.IgnoreCase: .NET case folding would also match non-ASCII
    // look-alikes such as the Kelvin sign (U+212A) for 'k'.
    [GeneratedRegex("^[A-Za-z0-9._-]{3,50}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
