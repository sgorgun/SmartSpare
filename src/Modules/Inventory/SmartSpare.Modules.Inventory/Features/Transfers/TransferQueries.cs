using System.ComponentModel;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
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
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.Infrastructure.Security;
using SmartSpare.Modules.Inventory.Domain;
using SmartSpare.Modules.Inventory.Domain.Transfers;
using SmartSpare.Modules.Inventory.Infrastructure.Persistence;
using SmartSpare.Modules.Inventory.Infrastructure.Queries;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Inventory.Features.Transfers;

/// <summary>The brands of all lines of a transfer: the resource of the brand scope check (STAGE_3_PLAN 3.3).</summary>
internal sealed record TransferBrands(IReadOnlySet<short> BrandIds) : IBrandScoped;

/// <summary>
/// Who may do what with a transfer order: the policy of the action, the brand scope over the brands of all lines (read
/// from <c>catalog.part</c>, never from the request), and the state machine of the aggregate. The same checks decide the
/// <c>allowedActions</c> of the detail and the commands, so the screen never offers what the API refuses.
/// </summary>
internal sealed class TransferAccess(
    InventoryDbContext db, IAuthorizationService authorization, IHttpContextAccessor httpContextAccessor, ICurrentUser user)
{
    public static readonly Error OutOfBrandScope = Error.Forbidden(
        "Inventory.OutOfBrandScope", "Every part of the transfer must belong to one of your brands.");

    /// <summary>The order with its lines, untracked: commands decide on it, and the SQL functions write.</summary>
    public Task<TransferOrder?> FindAsync(long id, CancellationToken cancellationToken) =>
        db.Set<TransferOrder>().AsNoTracking().Include(o => o.Lines).SingleOrDefaultAsync(o => o.Id == id, cancellationToken);

    public async Task<TransferBrands> BrandsAsync(TransferOrder order, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);
        var partIds = order.PartIds.ToArray();
        var brands = await db.Set<Part>().AsNoTracking().Where(p => partIds.Contains(p.Id)).Select(p => p.BrandId).Distinct()
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return new TransferBrands(brands.ToHashSet());
    }

    /// <summary>Whether the current user passes the policy and, for approve and cancel, the brand scope.</summary>
    public async Task<bool> MayAsync(string action, TransferOrder order, TransferBrands brands)
    {
        var principal = httpContextAccessor.HttpContext?.User;
        if (principal is null)
        {
            return false;
        }

        var policy = action switch
        {
            TransferActions.Approve => AppPolicies.ApproveTransfer,
            TransferActions.Cancel => AppPolicies.CancelTransfer,
            _ => AppPolicies.MoveStock,
        };
        if (!(await authorization.AuthorizeAsync(principal, policy).ConfigureAwait(false)).Succeeded)
        {
            return false;
        }

        return !BrandScopeRequirements.ByPolicy.TryGetValue(policy, out var scope)
               || (await authorization.AuthorizeAsync(principal, brands, scope).ConfigureAwait(false)).Succeeded;
    }

    /// <summary>The actions the current user may take on the order now.</summary>
    public async Task<IReadOnlyList<string>> AllowedActionsAsync(TransferOrder order, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(order);
        var candidates = Candidates(order, HubAccess.Of(user));
        if (candidates.Count == 0)
        {
            return [];
        }

        var brands = await BrandsAsync(order, cancellationToken).ConfigureAwait(false);
        var allowed = new List<string>();
        foreach (var action in candidates)
        {
            if (await MayAsync(action, order, brands).ConfigureAwait(false))
            {
                allowed.Add(action);
            }
        }

        return allowed;
    }

    /// <summary>The steps the state machine allows now, for a user who may write at <paramref name="hubs"/>.</summary>
    internal static IReadOnlyList<string> Candidates(TransferOrder order, HubRights hubs)
    {
        var candidates = new List<string>();
        if (order.CanApprove().IsSuccess)
        {
            candidates.Add(TransferActions.Approve);
        }

        if (order.CanCancel().IsSuccess)
        {
            candidates.Add(TransferActions.Cancel);
        }

        if (order.CanDispatch(hubs).IsSuccess)
        {
            candidates.Add(TransferActions.Dispatch);
        }

        if (order.CanReceive(hubs).IsSuccess)
        {
            candidates.Add(TransferActions.Receive);
        }

        return candidates;
    }
}

/// <summary>Builds the detail of one order for the current user (used by the detail query and by every command).</summary>
internal sealed class TransferDetailsReader(InventoryDbContext db, TransferAccess access)
{
    public async Task<TransferDetails?> ReadAsync(long id, CancellationToken cancellationToken)
    {
        var order = await access.FindAsync(id, cancellationToken).ConfigureAwait(false);
        if (order is null)
        {
            return null;
        }

        var summary = (await TransferRows.PageAsync(db, new TransferRows.Filter(id), 1, 1, cancellationToken).ConfigureAwait(false)).Single();
        var lines = await TransferRows.LinesAsync(db, id, cancellationToken).ConfigureAwait(false);
        var allowed = await access.AllowedActionsAsync(order, cancellationToken).ConfigureAwait(false);
        return new TransferDetails(summary, lines, Timeline(order), order.CancelReason, allowed);
    }

    internal static IReadOnlyList<TransferStep> Timeline(TransferOrder order)
    {
        var steps = new List<TransferStep> { new("requested", order.RequestedBy, order.CreatedAt) };
        if (order.ApprovedAt is not null)
        {
            steps.Add(new("approved", order.ApprovedBy, order.ApprovedAt));
        }

        if (order.DispatchedAt is not null)
        {
            steps.Add(new("dispatched", order.DispatchedBy, order.DispatchedAt));
        }

        if (order.ReceivedAt is not null)
        {
            steps.Add(new("received", order.ReceivedBy, order.ReceivedAt));
        }

        if (order.Status == TransferStatus.Cancelled)
        {
            steps.Add(new("cancelled", null, order.UpdatedAt));
        }

        return steps;
    }
}

/// <summary>Transfer orders, newest first, filtered by status and hub.</summary>
internal sealed record ListTransfersQuery(string? Status, short? WarehouseId, int Page, int PageSize) : IQuery<PagedResponse<TransferSummary>>;

internal sealed class ListTransfersQueryValidator : AbstractValidator<ListTransfersQuery>
{
    public ListTransfersQueryValidator()
    {
        RuleFor(q => q.Status).Must(s => TransferStatuses.All.Contains(s!)).When(q => q.Status is not null)
            .WithMessage($"'Status' must be one of: {string.Join(", ", TransferStatuses.All)}.");
        RuleFor(q => q.WarehouseId).GreaterThan((short)0).When(q => q.WarehouseId is not null);
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, TransferLimits.MaxPageSize);
    }
}

/// <summary>One transfer order with lines, timeline and the actions the current user may take.</summary>
internal sealed record GetTransferQuery(long Id) : IQuery<TransferDetails>;

internal static class TransferQueryEndpoints
{
    public static void Map(IEndpointRouteBuilder group)
    {
        group.MapGet("/transfers", async (
                IQueryHandler<ListTransfersQuery, PagedResponse<TransferSummary>> handler,
                CancellationToken cancellationToken,
                [Description("Status: proposed, approved, dispatched, received or cancelled; empty lists every status.")] string? status = null,
                [Description("Hub id, as donor or recipient; empty lists every hub.")] short? warehouseId = null,
                [Description("Page number, from 1.")] int page = 1,
                [Description("Orders per page, 1 to 100.")] int pageSize = TransferLimits.DefaultPageSize) =>
                (await handler.Handle(new ListTransfersQuery(status, warehouseId, page, pageSize), cancellationToken).ConfigureAwait(false))
                    .ToHttpResult())
            .RequireAuthorization(AppPolicies.ReadInventory)
            .WithName("ListTransfers")
            .WithSummary("Lists transfer orders, newest first.")
            .WithDescription(
                "Transfer orders between hubs with their route, status, urgency, number of lines and total quantity, "
                + "filtered by status and by a hub that is donor or recipient. Policy ReadInventory; reads are not brand-scoped.")
            .Produces<PagedResponse<TransferSummary>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithResponseExample(StatusCodes.Status200OK, new PagedResponse<TransferSummary>(
                [InventoryExamples.Transfer], Page: 1, PageSize: TransferLimits.DefaultPageSize, TotalCount: 1))
            .WithResponseExample(StatusCodes.Status400BadRequest, InventoryExamples.ValidationProblem(
                "Status", "'Status' must be one of: proposed, approved, dispatched, received, cancelled."))
            .WithResponseExample(StatusCodes.Status401Unauthorized, InventoryExamples.Unauthorized);

        group.MapGet("/transfers/{id:long}", async (
                [Description("Transfer order id.")] long id,
                IQueryHandler<GetTransferQuery, TransferDetails> handler,
                CancellationToken cancellationToken) =>
                (await handler.Handle(new GetTransferQuery(id), cancellationToken).ConfigureAwait(false)).ToHttpResult())
            .RequireAuthorization(AppPolicies.ReadInventory)
            .WithName("GetTransfer")
            .WithSummary("Returns one transfer order with its lines, timeline and allowed actions.")
            .WithDescription(
                "The order, its lines with the part, the quantity, the system proposal and the donor's available stock, the "
                + "timeline of its steps and allowedActions: the actions the current user may take now, decided by the "
                + "action's policy, the brand scope (approve and cancel) and the state machine. An unknown id gives 404 "
                + "with code Inventory.TransferNotFound. Policy ReadInventory.")
            .Produces<TransferDetails>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseExample(StatusCodes.Status200OK, InventoryExamples.TransferDetails)
            .WithResponseExample(StatusCodes.Status401Unauthorized, InventoryExamples.Unauthorized)
            .WithResponseExample(StatusCodes.Status404NotFound, InventoryExamples.Problem(InventoryErrors.TransferNotFound));
    }
}
