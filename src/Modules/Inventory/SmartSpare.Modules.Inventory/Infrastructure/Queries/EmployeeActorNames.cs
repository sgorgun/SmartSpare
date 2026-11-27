using SmartSpare.Infrastructure.MasterData;
using SmartSpare.Modules.Identity.Contracts;

namespace SmartSpare.Modules.Inventory.Infrastructure.Queries;

/// <summary>The names in a catalogue record's history, from Identity's employee directory (in process or over HTTP).</summary>
internal sealed class EmployeeActorNames(IEmployeeDirectory directory) : IActorNames
{
    public async Task<IReadOnlyDictionary<Guid, string>> FullNamesAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken) =>
        (await directory.FindAccountOwnersAsync(userIds, cancellationToken).ConfigureAwait(false)).ToDictionary(o => o.UserId, o => o.FullName);
}
