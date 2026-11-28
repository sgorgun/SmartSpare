using Microsoft.EntityFrameworkCore;
using SmartSpare.Infrastructure.Jobs;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.Modules.Identity.Contracts;
using SmartSpare.Modules.Identity.Domain;
using SmartSpare.Modules.Identity.Domain.Organisation;
using SmartSpare.Modules.Identity.Infrastructure.Persistence;

namespace SmartSpare.Modules.Identity.Features.Organisation;

/// <summary>
/// How employee changes become <see cref="EmployeeDirectoryChanged"/> events (ADR 0029 amendment 2, PR 7j1c). The
/// snapshots are read after the change, inside the command's transaction, so they carry exactly what commits, also after
/// <c>iam.anonymise_employee</c>, which writes in SQL.
/// </summary>
internal static class EmployeeDirectoryPublication
{
    /// <summary>The current snapshots of the given employees, or of every employee when <paramref name="ids"/> is null.</summary>
    public static async Task<IReadOnlyList<EmployeeSnapshot>> SnapshotsAsync(IdentityDbContext db, IReadOnlyCollection<Guid>? ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        var employees = db.Set<Employee>().AsNoTracking();
        if (ids is not null)
        {
            employees = employees.Where(e => ids.Contains(e.Id));
        }

        return await (from e in employees
                      join u in db.Set<AppUser>().AsNoTracking() on e.Id equals u.EmployeeId into accounts
                      from u in accounts.DefaultIfEmpty()
                      orderby e.Id
                      select new EmployeeSnapshot(e.Id, e.Code, e.FullName, !e.IsArchived && e.AnonymisedAt == null, u == null ? null : u.Id))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Queues the current state of the employees in the unit of work, so the outbox row commits with the change.</summary>
    public static async Task QueueAsync(IdentityDbContext db, IUnitOfWork unitOfWork, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        foreach (var chunk in (await SnapshotsAsync(db, ids, cancellationToken).ConfigureAwait(false)).Chunk(EmployeeDirectoryChanged.MaxEmployees))
        {
            unitOfWork.AddIntegrationEvent(new EmployeeDirectoryChanged(chunk));
        }
    }
}

/// <summary>
/// Publishes every employee (ADR 0029 amendment 2): the initial fill of Inventory's employee copy, and a repair tool.
/// <c>--job identity.employee-publish</c> in the services topology; a one-time post-deployment step of #85.
/// </summary>
internal sealed class EmployeeDirectoryPublishJob(IdentityDbContext db, IUnitOfWork<IdentityDbContext> unitOfWork) : IJob
{
    public const string JobName = "identity.employee-publish";

    public string Name => JobName;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        foreach (var chunk in (await EmployeeDirectoryPublication.SnapshotsAsync(db, null, cancellationToken).ConfigureAwait(false)).Chunk(EmployeeDirectoryChanged.MaxEmployees))
        {
            unitOfWork.AddIntegrationEvent(new EmployeeDirectoryChanged(chunk));
        }

        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
