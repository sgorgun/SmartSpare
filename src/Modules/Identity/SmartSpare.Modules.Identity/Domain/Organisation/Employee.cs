using System.Text.RegularExpressions;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Identity.Domain.Organisation;

/// <summary>
/// A person working for the dealership (master data, STAGE_7_PLAN section 1.1): the only place where a person's name,
/// contacts and position are edited. A user account is created from an employee and never exists without one; an
/// employee needs no account. Archived = left the company: the account can no longer sign in (ADR 0026).
/// </summary>
internal sealed partial class Employee : AggregateRoot<Guid>
{
    public const int MaxNameLength = 150;

    private Employee()
        : base(Guid.Empty)
    {
        Code = null!;
        FullName = null!;
    }

    private Employee(Guid id, string code)
        : base(id)
    {
        Code = code;
        FullName = string.Empty;
    }

    /// <summary>Employee number, e.g. <c>E0001</c> (<c>ck_employee_code</c>).</summary>
    public string Code { get; private set; }

    /// <summary>Personal data (GDPR).</summary>
    public string FullName { get; private set; }

    /// <summary>Personal data (GDPR).</summary>
    public EmailAddress? Email { get; private set; }

    /// <summary>Personal data (GDPR).</summary>
    public string? Phone { get; private set; }

    public short PositionId { get; private set; }

    public DateOnly? HiredOn { get; private set; }

    public DateOnly? LeftOn { get; private set; }

    public bool IsArchived { get; private set; }

    public DateTimeOffset? ArchivedAt { get; private set; }

    public Guid? ArchivedBy { get; private set; }

    /// <summary>Set by <c>iam.anonymise_employee()</c> (GDPR Art. 17).</summary>
    public DateTimeOffset? AnonymisedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Whether the employee's rights apply: not left and not anonymised.</summary>
    public bool IsActive => !IsArchived && AnonymisedAt is null;

    public static Result<Employee> Create(Guid id, string code, EmployeeDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        if (id == Guid.Empty)
        {
            throw new ArgumentException("An employee needs an identifier.", nameof(id));
        }

        var normalised = code?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!CodePattern().IsMatch(normalised))
        {
            return OrganisationErrors.InvalidEmployeeCode;
        }

        var employee = new Employee(id, normalised);
        var set = employee.Update(details);
        return set.IsFailure ? set.Error : employee;
    }

    /// <summary>Changes the person's data and position; returns whether the position changed (the account's rights change).</summary>
    public Result<bool> Update(EmployeeDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);
        if (AnonymisedAt is not null)
        {
            return OrganisationErrors.EmployeeAnonymised;
        }

        var name = details.FullName?.Trim() ?? string.Empty;
        if (name.Length is 0 or > MaxNameLength)
        {
            return OrganisationErrors.InvalidName;
        }

        EmailAddress? email = null;
        if (!string.IsNullOrWhiteSpace(details.Email))
        {
            var parsed = EmailAddress.Create(details.Email.Trim());
            if (parsed.IsFailure)
            {
                return parsed.Error;
            }

            email = parsed.Value;
        }

        if (details.HiredOn is { } hired && LeftOn is { } left && left < hired)
        {
            return OrganisationErrors.LeftBeforeHired;
        }

        var positionChanged = PositionId != details.PositionId;
        FullName = name;
        Email = email;
        Phone = string.IsNullOrWhiteSpace(details.Phone) ? null : details.Phone.Trim();
        PositionId = details.PositionId;
        HiredOn = details.HiredOn;
        return positionChanged;
    }

    /// <summary>The employee left on <paramref name="leftOn"/>: archived, rights end, the account cannot sign in.</summary>
    public Result Archive(DateOnly leftOn, DateTimeOffset now, Guid? by)
    {
        if (IsArchived)
        {
            return Result.Success();
        }

        if (HiredOn is { } hired && leftOn < hired)
        {
            return OrganisationErrors.LeftBeforeHired;
        }

        IsArchived = true;
        LeftOn = leftOn;
        ArchivedAt = now;
        ArchivedBy = by;
        return Result.Success();
    }

    /// <summary>Re-employment of an employee who left by mistake; an anonymised employee stays archived.</summary>
    public Result Restore()
    {
        if (AnonymisedAt is not null)
        {
            return OrganisationErrors.EmployeeAnonymised;
        }

        IsArchived = false;
        LeftOn = null;
        ArchivedAt = null;
        ArchivedBy = null;
        return Result.Success();
    }

    [GeneratedRegex("^E[0-9]{4,6}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();
}

/// <summary>The editable data of an employee.</summary>
internal sealed record EmployeeDetails(string FullName, string? Email, string? Phone, short PositionId, DateOnly? HiredOn);
