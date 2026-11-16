using SmartSpare.Infrastructure.Messaging;
using SmartSpare.Modules.Forecasting.Contracts;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Forecasting.Features.Ping;

internal sealed class PingQueryHandler : IQueryHandler<PingQuery, PingResponse>
{
    public Task<Result<PingResponse>> Handle(PingQuery query, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success(new PingResponse(ForecastingModule.Name, "ok")));
}
