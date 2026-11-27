namespace SmartSpare.Modules.Identity.Contracts;

/// <summary>
/// The employees of the organisation for other modules (ADR 0026): validate a responsible employee and show the name.
/// In the single host the Identity module answers in process; another service uses <see cref="HttpEmployeeDirectory"/>,
/// which calls <see cref="IdentityInternalApi.EmployeeLookup"/> with the caller's internal token. Read-only.
/// </summary>
public interface IEmployeeDirectory
{
    /// <summary>The employees with these ids that exist; unknown ids are left out.</summary>
    Task<IReadOnlyList<EmployeeInfo>> FindAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>Active employees whose number or name contains <paramref name="search"/>, for a picker.</summary>
    Task<IReadOnlyList<EmployeeInfo>> SearchAsync(string? search, int limit, CancellationToken cancellationToken);
}

/// <summary>An employee as other modules see it: id, number, name, and whether the employee still works here.</summary>
public sealed record EmployeeInfo(Guid Id, string Code, string FullName, bool IsActive);

/// <summary>Body of <see cref="IdentityInternalApi.EmployeeLookup"/>: ids to find, or a search text.</summary>
public sealed record EmployeeLookupRequest(IReadOnlyList<Guid> Ids, string? Search, int Limit);
