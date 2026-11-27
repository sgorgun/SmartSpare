using System.ComponentModel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Identity;
using SmartSpare.Infrastructure.Http;
using SmartSpare.Infrastructure.Messaging;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Identity.Features.Users;

/// <summary>User administration (#46) under <c>/api/v1/identity</c>: policy Administration, except the own password.</summary>
internal static class UserEndpoints
{
    private static readonly UserSummary Example = new(
        Guid.Parse("00000000-0000-4000-8000-000000000009"), "ignas.zukauskas", "I. Ž.", "i***@dealer.example", IsActive: true,
        IsLockedOut: false, IsAnonymised: false, HomeWarehouseId: null, Roles: [AppRoles.InventoryPlanner], Brands: [], LastLoginAt: null,
        EmployeeId: Guid.Parse("00000000-0000-4000-8000-000000000009"), EmployeeCode: "E0009", PositionName: "Inventory planner",
        Rights: [new EffectiveRightDto("Role", AppRoles.InventoryPlanner, "Position", "Inventory planner")], WithoutEffect: [], InactiveReason: null);

    public static void Map(IEndpointRouteBuilder v1)
    {
        var users = v1.MapGroup("/users").RequireAuthorization(Permissions.Organisation.Account.View);

        users.MapGet("", async (
                IQueryHandler<ListUsersQuery, PagedResponse<UserSummary>> handler,
                CancellationToken cancellationToken,
                [Description("Text contained in the user name.")] string? search = null,
                [Description("Page number, from 1.")] int page = 1,
                [Description("Users per page, 1 to 100.")] int pageSize = UserLimits.DefaultPageSize) =>
                (await handler.Handle(new ListUsersQuery(search, page, pageSize), cancellationToken).ConfigureAwait(false)).ToHttpResult())
            .WithName("ListUsers")
            .WithSummary("Lists user accounts with their effective rights.")
            .WithDescription(
                "The accounts from iam.v_app_user_masked: user name, the employee's masked name and e-mail, status, lockout and "
                + "every effective right with its origin (role from the position, brand and hub from assignments; ADR 0026), "
                + "ordered by user name. Rights are read-only here: they change with the employee's position and assignments. "
                + "Permission Organisation.Account.View.")
            .Produces<PagedResponse<UserSummary>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithResponseExample(StatusCodes.Status200OK, new PagedResponse<UserSummary>([Example], 1, UserLimits.DefaultPageSize, 1))
            .WithResponseExample(StatusCodes.Status401Unauthorized, Problem(Error.Failure(ProblemCodes.Unauthenticated, "Unauthorized"), 401));

        users.MapGet("/form-options", async (IQueryHandler<GetUserFormOptionsQuery, UserFormOptions> handler, CancellationToken cancellationToken) =>
                (await handler.Handle(new GetUserFormOptionsQuery(), cancellationToken).ConfigureAwait(false)).ToHttpResult())
            .WithName("GetUserFormOptions")
            .WithSummary("Returns the roles, brands and hubs a user can be given.")
            .WithDescription("Every role and brand code and the active hubs (from the Inventory module). Permission Organisation.Account.View.")
            .Produces<UserFormOptions>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithResponseExample(StatusCodes.Status200OK, new UserFormOptions(
                [AppRoles.BrandManager, AppRoles.InventoryPlanner], ["CLAAS", "JD"], [new HubOption(3, "KLP", "Klaipėda")]))
            .WithResponseExample(StatusCodes.Status401Unauthorized, Problem(Error.Failure(ProblemCodes.Unauthenticated, "Unauthorized"), 401));

        MapAction<UnlockUserCommand>(users, "unlock", id => new UnlockUserCommand(id), "UnlockUser", "Unlocks a locked-out user.",
            "Ends a lockout after failed sign-ins and resets the failure counter. Permission Organisation.Account.Edit.");
        MapAction<DeactivateUserCommand>(users, "deactivate", id => new DeactivateUserCommand(id), "DeactivateUser", "Deactivates a user.",
            "No new sign-ins; existing sessions end at their next check. An administrator cannot deactivate the own account (422 "
            + "Identity.OwnAdministration). Permission Organisation.Account.Edit.");
        MapAction<AnonymiseUserCommand>(users, "anonymise", id => new AnonymiseUserCommand(id), "AnonymiseUser", "Anonymises a user's employee (GDPR).",
            "Erases the personal data of the account's employee and of the account with iam.anonymise_employee; the rows stay for "
            + "audit references and the account can never sign in again. An administrator cannot anonymise the own account (403, "
            + "SS301). Permission Organisation.Account.Edit.");

        v1.MapPost("/me/password", async (ChangePasswordRequest request, ICommandHandler<ChangeOwnPasswordCommand> handler, CancellationToken cancellationToken) =>
                (await handler.Handle(new ChangeOwnPasswordCommand(request), cancellationToken).ConfigureAwait(false)).ToHttpResult())
            .RequireAuthorization()
            .WithName("ChangeOwnPassword")
            .WithSummary("Changes the signed-in user's password.")
            .WithDescription(
                "Needs the current password; the new one follows the policy. Every session of the user, this one included, ends at "
                + "its next check, so the user signs in again. Any signed-in user.")
            .Accepts<ChangePasswordRequest>("application/json")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .WithRequestExample(new ChangePasswordRequest("the old password", "a new long passphrase"))
            .WithResponseExample(StatusCodes.Status400BadRequest, Problem(UserErrors.WrongPassword, 400));
    }

    private static void MapAction<TCommand>(
        RouteGroupBuilder users, string action, Func<Guid, TCommand> create, string name, string summary, string description)
        where TCommand : ICommand<UserSummary> =>
        users.MapPost($"/{{id:guid}}/{action}", async (
                [Description("User id.")] Guid id, ICommandHandler<TCommand, UserSummary> handler, CancellationToken cancellationToken) =>
                (await handler.Handle(create(id), cancellationToken).ConfigureAwait(false)).ToHttpResult())
            .RequireAuthorization(Permissions.Organisation.Account.Edit)
            .WithName(name)
            .WithSummary(summary)
            .WithDescription(description)
            .Produces<UserSummary>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithResponseExample(StatusCodes.Status200OK, Example)
            .WithResponseExample(StatusCodes.Status404NotFound, Problem(UserErrors.NotFound, 404));

    private static IReadOnlyDictionary<string, object?> Problem(Error error, int status) =>
        OpenApiConventions.Problem(status, status switch
        {
            400 => "Validation failed",
            401 => "Unauthorized",
            404 => "Resource not found",
            409 => "Conflict",
            _ => "Business rule violated",
        }, error.Code, error.Description);
}
