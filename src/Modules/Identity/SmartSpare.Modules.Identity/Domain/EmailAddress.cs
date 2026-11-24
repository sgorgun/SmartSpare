using System.Text.RegularExpressions;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Identity.Domain;

/// <summary>
/// Corporate e-mail address of a user (personal data). The rule equals the database CHECK
/// <c>ck_app_user_email</c>: <c>email ~ '^[^@\s]+@[^@\s]+\.[^@\s]+$'</c> on a <c>citext</c> column.
/// </summary>
internal sealed partial class EmailAddress : ValueObject
{
    private EmailAddress(string value) => Value = value;

    public string Value { get; }

    public static Result<EmailAddress> Create(string? value) =>
        value is not null && Pattern().IsMatch(value)
            ? new EmailAddress(value)
            : IdentityErrors.InvalidEmailAddress;

    /// <summary>Restores a value read from the database, where the CHECK has already validated it.</summary>
    public static EmailAddress FromDatabase(string value) =>
        Create(value) is { IsSuccess: true } result
            ? result.Value
            : throw new InvalidOperationException("iam.app_user.email violates ck_app_user_email.");

    public override string ToString() => Value;

    protected override IEnumerable<object?> GetEqualityComponents()
    {
        // citext compares case-insensitively; so does the value object.
        yield return Value.ToLowerInvariant();
    }

    // The CHECK's pattern, verbatim. Parity with PostgreSQL is checked by the Identity integration tests.
    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
