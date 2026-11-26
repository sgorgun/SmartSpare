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
        IsLockedOut: false, IsAnonymised: false, HomeWarehouseId: null, Roles: [AppRoles.InventoryPlanner], Brands: [], LastLoginAt: null);

    public static void Map(IEndpointRouteBuilder v1)
    {
        var users = v1.MapGroup("/users").RequireAuthorization(AppPolicies.Administration);

        users.MapGet("", async (
                IQueryHandler<ListUsersQuery, PagedResponse<UserSummary>> handler,
                CancellationToken cancellationToken,
                [Description("Text contained in the user name.")] string? search = null,
                [Description("Page number, from 1.")] int page = 1,
                [Description("Users per page, 1 to 100.")] int pageSize = UserLimits.DefaultPageSize) =>
                (await handler.Handle(new ListUsersQuery(search, page, pageSize), cancellationToken).ConfigureAwait(false)).ToHttpResult())
            .WithName("ListUsers")
            .WithSummary("Lists users with masked personal data.")
            .WithDescription(
                "The user directory from iam.v_app_user_masked: user name, masked name and e-mail, status, lockout, roles, "
                + "brands and home hub, ordered by user name. Policy Administration.")
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
            .WithDescription("Every role and brand code and the active hubs (from the Inventory module). Policy Administration.")
            .Produces<UserFormOptions>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithResponseExample(StatusCodes.Status200OK, new UserFormOptions(
                [AppRoles.BrandManager, AppRoles.InventoryPlanner], ["CLAAS", "JD"], [new HubOption(3, "KLP", "Klaipėda")]))
            .WithResponseExample(StatusCodes.Status401Unauthorized, Problem(Error.Failure(ProblemCodes.Unauthenticated, "Unauthorized"), 401));

        users.MapPost("", async (CreateUserRequest request, ICommandHandler<CreateUserCommand, Guid> handler, CancellationToken cancellationToken) =>
            {
                var result = await handler.Handle(new CreateUserCommand(request), cancellationToken).ConfigureAwait(false);
                return result.IsSuccess
                    ? Results.Created($"/api/v1/identity/users/{result.Value}", new { id = result.Value })
                    : result.Error.ToProblem();
            })
            .WithName("CreateUser")
            .WithSummary("Creates a user with an initial password, roles, brands and home hub.")
            .WithDescription(
                "The user name is stored in lower case; the password follows the policy (at least 12 characters, 4 different). "
                + "Brands go only to brand managers; the home hub must be an active hub (checked with the Inventory module). "
                + "A used user name or e-mail gives 409 Identity.UserNameTaken. Policy Administration; written as app_admin.")
            .Accepts<CreateUserRequest>("application/json")
            .Produces(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithRequestExample(new CreateUserRequest("rasa.petrauskiene", "rasa.petrauskiene@dealer.example", "Rasa Petrauskienė", null,
                "a long initial password", new UserAccessRequest([AppRoles.WarehouseKeeper], [], 3)))
            .WithResponseExample(StatusCodes.Status400BadRequest, Problem(UserErrors.UnknownWarehouse, 400))
            .WithResponseExample(StatusCodes.Status409Conflict, Problem(UserErrors.UserNameTaken, 409));

        users.MapPut("/{id:guid}/access", async (
                [Description("User id.")] Guid id, UserAccessRequest request,
                ICommandHandler<UpdateUserAccessCommand, UserSummary> handler, CancellationToken cancellationToken) =>
                (await handler.Handle(new UpdateUserAccessCommand(id, request), cancellationToken).ConfigureAwait(false)).ToHttpResult())
            .WithName("UpdateUserAccess")
            .WithSummary("Sets the roles, brands and home hub of a user.")
            .WithDescription(
                "Replaces the user's roles, brands and home hub; every change rotates the security stamp, so the user's sessions "
                + "pick up the new claims at their next check. An administrator cannot remove the own Administrator role (422 "
                + "Identity.OwnAdministration). Policy Administration; written as app_admin.")
            .Accepts<UserAccessRequest>("application/json")
            .Produces<UserSummary>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithRequestExample(new UserAccessRequest([AppRoles.BrandManager], ["CLAAS"], null))
            .WithResponseExample(StatusCodes.Status200OK, Example)
            .WithResponseExample(StatusCodes.Status404NotFound, Problem(UserErrors.NotFound, 404))
            .WithResponseExample(StatusCodes.Status422UnprocessableEntity, Problem(UserErrors.OwnAdministration, 422));

        MapAction<UnlockUserCommand>(users, "unlock", id => new UnlockUserCommand(id), "UnlockUser", "Unlocks a locked-out user.",
            "Ends a lockout after failed sign-ins and resets the failure counter. Policy Administration.");
        MapAction<DeactivateUserCommand>(users, "deactivate", id => new DeactivateUserCommand(id), "DeactivateUser", "Deactivates a user.",
            "No new sign-ins; existing sessions end at their next check. An administrator cannot deactivate the own account (422 "
            + "Identity.OwnAdministration). Policy Administration.");
        MapAction<AnonymiseUserCommand>(users, "anonymise", id => new AnonymiseUserCommand(id), "AnonymiseUser", "Anonymises a user (GDPR).",
            "Erases the personal data with iam.anonymise_user; the row stays for audit references and the user can never sign in "
            + "again. An administrator cannot anonymise the own account (403, SS301). Policy Administration.");

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
