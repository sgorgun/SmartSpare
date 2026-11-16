using SmartSpare.Infrastructure.Messaging;
using SmartSpare.Modules.Inventory.Contracts;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Inventory.Features.Ping;

internal sealed class PingQueryHandler : IQueryHandler<PingQuery, PingResponse>
{
    public Task<Result<PingResponse>> Handle(PingQuery query, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success(new PingResponse(InventoryModule.Name, "ok")));
}
