using System.ComponentModel;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Inventory;
using SmartSpare.Infrastructure.Http;
using SmartSpare.Infrastructure.Messaging;
using SmartSpare.Modules.Inventory.Infrastructure.Persistence;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Inventory.Features.Stock;

/// <summary>Ledger movements filtered by hub, part, type and time, newest first.</summary>
internal sealed record ListMovementsQuery(
    short? WarehouseId, long? PartId, string? MovementType, DateTimeOffset? From, DateTimeOffset? To, int Page, int PageSize)
    : IQuery<PagedResponse<MovementRow>>;

internal sealed class ListMovementsQueryValidator : AbstractValidator<ListMovementsQuery>
{
    public ListMovementsQueryValidator()
    {
        RuleFor(q => q.MovementType).MaximumLength(20);
        RuleFor(q => q.To).GreaterThan(q => q.From).When(q => q.From is not null && q.To is not null)
            .WithMessage("'To' must be after 'From'.");
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, MovementSearch.MaxPageSize);
    }
}

internal static class ListMovementsEndpoint
{
    /// <summary>GET /api/v1/inventory/movements — the stock ledger.</summary>
    public static void Map(IEndpointRouteBuilder group) =>
        group.MapGet("/movements", async (
                IQueryHandler<ListMovementsQuery, PagedResponse<MovementRow>> handler,
                CancellationToken cancellationToken,
                [Description("Hub id; empty for every hub.")] short? warehouseId = null,
                [Description("Part id; empty for every part.")] long? partId = null,
                [Description("Movement type code, for example TRANSFER_OUT or ADJUSTMENT_IN.")] string? type = null,
                [Description("Earliest time, inclusive (ISO 8601). Default: 90 days ago.")] DateTimeOffset? from = null,
                [Description("Latest time, exclusive (ISO 8601).")] DateTimeOffset? to = null,
                [Description("Page number, from 1.")] int page = 1,
                [Description("Movements per page, 1 to 200.")] int pageSize = MovementSearch.DefaultPageSize) =>
            {
                var result = await handler.Handle(new ListMovementsQuery(warehouseId, partId, type, from, to, page, pageSize), cancellationToken)
                    .ConfigureAwait(false);
                return result.ToHttpResult();
            })
            .RequireAuthorization(Permissions.Inventory.Stock.View)
            .WithName("ListMovements")
            .WithSummary("Lists stock ledger movements, newest first.")
            .WithDescription(
                "The append-only stock ledger with the signed effect of each movement on the on-hand stock (imported "
                + "historical demand has no effect) and its source document: the transfer order number, an external "
                + "reference, an import batch, a service or purchase order line. Without from, the last 90 days are listed. "
                + "Permission Inventory.Stock.View.")
            .Produces<PagedResponse<MovementRow>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithResponseExample(StatusCodes.Status200OK, new PagedResponse<MovementRow>(
                [InventoryExamples.Movement], Page: 1, PageSize: MovementSearch.DefaultPageSize, TotalCount: 1))
            .WithResponseExample(StatusCodes.Status400BadRequest, InventoryExamples.ValidationProblem("To", "'To' must be after 'From'."))
            .WithResponseExample(StatusCodes.Status401Unauthorized, InventoryExamples.Unauthorized);
}
