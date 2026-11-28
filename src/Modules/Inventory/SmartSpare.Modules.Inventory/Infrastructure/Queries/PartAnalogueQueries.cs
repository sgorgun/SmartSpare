using Microsoft.EntityFrameworkCore;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.Infrastructure.Http;
using SmartSpare.Infrastructure.Messaging;
using SmartSpare.Modules.Inventory.Domain.Catalog;
using SmartSpare.Modules.Inventory.Domain.Stock;
using SmartSpare.Modules.Inventory.Features.PartAnalogues;
using SmartSpare.Modules.Inventory.Infrastructure.Persistence;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Inventory.Infrastructure.Queries;

/// <summary>
/// A part's analogues (ADR 0027): its links seen from it, its interchangeability group (the connected component, as
/// <c>catalog.v_part_analogue_group</c> computes it) and its supersession chain to the current part (as
/// <c>catalog.v_part_successor</c> resolves it). The link table is small, so the walk reads it once.
/// </summary>
internal sealed class PartAnalogueQueries(InventoryDbContext db) : IQueryHandler<GetPartAnaloguesQuery, PartAnaloguesView>
{
    private const int MaxChain = 100;

    public async Task<Result<PartAnaloguesView>> Handle(GetPartAnaloguesQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var links = await db.Set<PartAnalogue>().AsNoTracking()
            .Select(l => new { l.Id, l.PartId, l.AnaloguePartId, l.LinkType, l.ScalingFactor, l.ValidFrom, l.Note, Version = EF.Property<uint>(l, "xmin") })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var group = new HashSet<long> { query.PartId };
        var queue = new Queue<long>([query.PartId]);
        while (queue.Count > 0)
        {
            var part = queue.Dequeue();
            foreach (var link in links.Where(l => l.LinkType == AnalogueLinkType.Interchangeable && (l.PartId == part || l.AnaloguePartId == part)))
            {
                var other = link.PartId == part ? link.AnaloguePartId : link.PartId;
                if (group.Add(other))
                {
                    queue.Enqueue(other);
                }
            }
        }

        var chain = new List<long> { query.PartId };
        while (chain.Count < MaxChain
               && links.FirstOrDefault(l => l.LinkType == AnalogueLinkType.Supersedes && l.AnaloguePartId == chain[^1]) is { } successor)
        {
            chain.Add(successor.PartId);
        }

        var own = links.Where(l => l.PartId == query.PartId || l.AnaloguePartId == query.PartId).ToList();
        var ids = group.Concat(chain).Concat(own.Select(l => l.PartId)).Concat(own.Select(l => l.AnaloguePartId)).Distinct().ToList();
        var parts = await db.Set<Part>().AsNoTracking().Where(p => ids.Contains(p.Id))
            .Select(p => new PartRef(p.Id, p.Brand!.Code, p.Code, p.Name, p.IsArchived))
            .ToDictionaryAsync(p => p.Id, cancellationToken).ConfigureAwait(false);
        if (!parts.TryGetValue(query.PartId, out var self))
        {
            return PartAnalogueErrors.PartNotFound;
        }

        var seen = own.Select(l =>
        {
            var other = l.PartId == query.PartId ? l.AnaloguePartId : l.PartId;
            var direction = l.PartId == query.PartId ? AnalogueRelations.Replaces : AnalogueRelations.ReplacedBy;
            var relation = l.LinkType == AnalogueLinkType.Interchangeable ? AnalogueRelations.Interchangeable : direction;
            return new PartAnalogueLink(l.Id, relation, parts[other], l.ScalingFactor, l.ValidFrom, l.Note, Versions.Format(l.Version));
        }).OrderBy(l => l.Relation, StringComparer.Ordinal).ThenBy(l => l.Other.OemPartNumber, StringComparer.Ordinal).ToList();

        return new PartAnaloguesView(
            self, seen, [.. group.Select(id => parts[id]).OrderBy(p => p.Id)], [.. chain.Select(id => parts[id])]);
    }
}

/// <summary>
/// What may stand in for short parts at a hub (PR 7i3), read-only: each part's direct interchangeable links and its
/// current successor, never indirect members of its interchangeability group, because transitivity is a forecasting
/// assumption (ADR 0027 decision 2), not confirmed compatibility (#220). Availability is on hand minus reserved at the
/// hub. Archived parts are left out.
/// </summary>
internal sealed class DirectAnalogueQueries(InventoryDbContext db) : IQueryHandler<GetDirectAnaloguesQuery, IReadOnlyList<DirectAnalogues>>
{
    private const int MaxChain = 100;

    public async Task<Result<IReadOnlyList<DirectAnalogues>>> Handle(GetDirectAnaloguesQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var asked = query.PartIds.Distinct().ToList();
        var links = await db.Set<PartAnalogue>().AsNoTracking()
            .Select(l => new { l.PartId, l.AnaloguePartId, l.LinkType, l.ScalingFactor })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var found = new Dictionary<long, List<(long Part, string Kind, decimal Factor)>>();
        foreach (var part in asked)
        {
            var list = links.Where(l => l.LinkType == AnalogueLinkType.Interchangeable && (l.PartId == part || l.AnaloguePartId == part))
                .Select(l => (Part: l.PartId == part ? l.AnaloguePartId : l.PartId, Kind: DirectAnalogueKinds.Interchangeable, Factor: l.ScalingFactor))
                .ToList();

            var (current, factor, steps) = (part, 1m, 0);
            while (steps++ < MaxChain && links.FirstOrDefault(l => l.LinkType == AnalogueLinkType.Supersedes && l.AnaloguePartId == current) is { } next)
            {
                (current, factor) = (next.PartId, factor * next.ScalingFactor);
            }

            if (current != part)
            {
                list.Add((current, DirectAnalogueKinds.Successor, factor));
            }

            found[part] = list;
        }

        var ids = found.Values.SelectMany(l => l.Select(a => a.Part)).Distinct().ToList();
        var parts = await db.Set<Part>().AsNoTracking().Where(p => ids.Contains(p.Id) && !p.IsArchived)
            .Select(p => new PartRef(p.Id, p.Brand!.Code, p.Code, p.Name, p.IsArchived))
            .ToDictionaryAsync(p => p.Id, cancellationToken).ConfigureAwait(false);
        var available = await db.Set<StockBalance>().AsNoTracking().Where(b => b.WarehouseId == query.WarehouseId && ids.Contains(b.PartId))
            .Select(b => new { b.PartId, Available = b.OnHandQty - b.ReservedQty })
            .ToDictionaryAsync(b => b.PartId, b => b.Available, cancellationToken).ConfigureAwait(false);

        return asked.Select(part => new DirectAnalogues(part, query.WarehouseId, [
                .. found[part].Where(a => parts.ContainsKey(a.Part))
                    .Select(a => new DirectAnalogue(parts[a.Part], a.Kind, a.Factor, Math.Max(0, available.GetValueOrDefault(a.Part))))
                    .OrderByDescending(a => a.Available).ThenBy(a => a.Part.OemPartNumber, StringComparer.Ordinal),
            ]))
            .ToList();
    }
}
