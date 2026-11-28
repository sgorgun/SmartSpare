using System.ComponentModel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Identity;
using SmartSpare.Infrastructure.Http;
using SmartSpare.Infrastructure.Messaging;
using SmartSpare.Modules.Identity.Domain.Organisation;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Identity.Features.Organisation;

/// <summary>Shared OpenAPI helpers of the organisation endpoints.</summary>
internal static class OrganisationApi
{
    public static IReadOnlyDictionary<string, object?> Problem(Error error, int status) =>
        OpenApiConventions.Problem(status, status switch
        {
            400 => "Validation failed",
            404 => "Resource not found",
            409 => "Conflict",
            _ => "Business rule violated",
        }, error.Code, error.Description);

    /// <summary>A read of one record: the body with an <c>ETag</c> of its version.</summary>
    public static IResult WithETag<T>(Result<T> result, Func<T, string> version) =>
        result.IsSuccess ? Results.Ok(result.Value).WithETag(version(result.Value)) : result.Error.ToProblem();

    public static RouteHandlerBuilder Documented(this RouteHandlerBuilder builder, string name, string summary, string description) =>
        builder.WithName(name).WithSummary(summary).WithDescription(description)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);
}

/// <summary>Sets the <c>ETag</c> header of a successful read (optimistic concurrency, If-Match on writes).</summary>
internal static class ETagResults
{
    public static IResult WithETag(this IResult result, string version) => new ETagResult(result, $"\"{version}\"");

    private sealed class ETagResult(IResult inner, string etag) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);
            httpContext.Response.Headers.ETag = etag;
            return inner.ExecuteAsync(httpContext);
        }
    }
}

/// <summary>Positions and their role templates under <c>/api/v1/identity/positions</c> (ADR 0026). Permission Organisation.Position.View.</summary>
internal static class PositionEndpoints
{
    private static readonly PositionSummary Example = new(
        2, "BRAND_MANAGER", "Brand manager", "Responsible for the parts business of one or more brands.", [AppRoles.BrandManager], false, false, 2, "8841");

    public static void Map(IEndpointRouteBuilder v1)
    {
        var positions = v1.MapGroup("/positions").RequireAuthorization(Permissions.Organisation.Position.View).WithTags("Organisation");

        positions.MapGet("", async (
                IQueryHandler<ListPositionsQuery, PagedResponse<PositionSummary>> handler, CancellationToken cancellationToken,
                [Description("Text contained in the code or name.")] string? search = null,
                [Description("true lists archived positions instead of active ones.")] bool archived = false,
                [Description("Page number, from 1.")] int page = 1,
                [Description("Positions per page, 1 to 100.")] int pageSize = UserLimits.DefaultPageSize) =>
                (await handler.Handle(new ListPositionsQuery(search, archived, page, pageSize), cancellationToken).ConfigureAwait(false)).ToHttpResult())
            .Documented("ListPositions", "Lists positions with their role templates.",
                "Active (or archived) positions by name, with the roles of the template and the number of active employees. Permission Organisation.Position.View.")
            .Produces<PagedResponse<PositionSummary>>()
            .ProducesValidationProblem()
            .WithResponseExample(StatusCodes.Status200OK, new PagedResponse<PositionSummary>([Example], 1, UserLimits.DefaultPageSize, 1));

        positions.MapGet("/{id:int}", async ([Description("Position id.")] short id, IQueryHandler<GetPositionQuery, PositionSummary> handler, CancellationToken cancellationToken) =>
                OrganisationApi.WithETag(await handler.Handle(new GetPositionQuery(id), cancellationToken).ConfigureAwait(false), p => p.Version))
            .Documented("GetPosition", "Returns one position.", "The position with its role template; the ETag header carries its version for If-Match. Permission Organisation.Position.View.")
            .Produces<PositionSummary>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseExample(StatusCodes.Status200OK, Example)
            .WithResponseExample(StatusCodes.Status404NotFound, OrganisationApi.Problem(OrganisationErrors.PositionNotFound, 404));

        positions.MapPost("", async (CreatePositionRequest request, ICommandHandler<CreatePositionCommand, PositionSummary> handler, CancellationToken cancellationToken) =>
            {
                var result = await handler.Handle(new CreatePositionCommand(request), cancellationToken).ConfigureAwait(false);
                return result.IsSuccess
                    ? Results.Created($"/api/v1/identity/positions/{result.Value.Id}", result.Value)
                    : result.Error.ToProblem();
            })
            .RequireAuthorization(Permissions.Organisation.Position.Create)
            .Documented("CreatePosition", "Creates a position with its role template.",
                "The code is stored in capitals and never changes. A used code gives 409 Identity.CodeTaken. Permission Organisation.Position.Create; written as app_admin.")
            .Accepts<CreatePositionRequest>("application/json")
            .Produces<PositionSummary>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithRequestExample(new CreatePositionRequest("PARTS_ADVISOR", "Parts advisor", "Sells parts at the counter.", [AppRoles.InventoryPlanner]))
            .WithResponseExample(StatusCodes.Status409Conflict, OrganisationApi.Problem(OrganisationErrors.CodeTaken, 409));

        positions.MapPut("/{id:int}", async (
                [Description("Position id.")] short id, UpdatePositionRequest request, [FromHeader(Name = "If-Match")] string? ifMatch,
                ICommandHandler<UpdatePositionCommand, PositionSummary> handler, CancellationToken cancellationToken) =>
                (await handler.Handle(new UpdatePositionCommand(id, request, Versions.Parse(ifMatch)), cancellationToken).ConfigureAwait(false)).ToHttpResult())
            .RequireAuthorization(Permissions.Organisation.Position.Edit)
            .Documented("UpdatePosition", "Changes a position and its role template.",
                "Needs If-Match with the version read. A changed template changes the roles of every employee in the position: their "
                + "sessions get the new roles at the next session check (at most 30 seconds). The last position that grants ADMINISTRATOR to "
                + "an active account keeps that role (409 Identity.LastAdministrator). A stale version gives 409. Permission Organisation.Position.Edit.")
            .Accepts<UpdatePositionRequest>("application/json")
            .Produces<PositionSummary>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithRequestExample(new UpdatePositionRequest("Brand manager", null, [AppRoles.BrandManager]))
            .WithResponseExample(StatusCodes.Status200OK, Example)
            .WithResponseExample(StatusCodes.Status409Conflict, OrganisationApi.Problem(OrganisationErrors.LastAdministrator, 409));

        MapState<ArchivePositionCommand>(positions, "archive", (id, v) => new ArchivePositionCommand(id, v), "ArchivePosition", "Archives a position.",
            "Needs If-Match. Refused while active employees hold the position (409 SS501). An archived position cannot be chosen. Permission Organisation.Position.Archive.");
        MapState<RestorePositionCommand>(positions, "restore", (id, v) => new RestorePositionCommand(id, v), "RestorePosition", "Restores an archived position.",
            "Needs If-Match. Permission Organisation.Position.Archive.");
    }

    private static void MapState<TCommand>(RouteGroupBuilder group, string action, Func<short, uint?, TCommand> create, string name, string summary, string description)
        where TCommand : ICommand<PositionSummary> =>
        group.MapPost($"/{{id:int}}/{action}", async (
                [Description("Position id.")] short id, [FromHeader(Name = "If-Match")] string? ifMatch,
                ICommandHandler<TCommand, PositionSummary> handler, CancellationToken cancellationToken) =>
                (await handler.Handle(create(id, Versions.Parse(ifMatch)), cancellationToken).ConfigureAwait(false)).ToHttpResult())
            .RequireAuthorization(Permissions.Organisation.Position.Archive)
            .Documented(name, summary, description)
            .Produces<PositionSummary>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithResponseExample(StatusCodes.Status200OK, Example)
            .WithResponseExample(StatusCodes.Status409Conflict, OrganisationApi.Problem(OrganisationErrors.PositionInUse, 409));
}

/// <summary>Employees and their accounts under <c>/api/v1/identity/employees</c> (ADR 0026). Permission Organisation.Employee.View.</summary>
internal static class EmployeeEndpoints
{
    private static readonly EmployeeSummary Example = new(
        Guid.Parse("00000000-0000-4000-8000-000000000001"), "E0001", "Tomas Radzevičius", "tomas.radzevicius@dealer.example", "+370 612 10001",
        2, "BRAND_MANAGER", "Brand manager", new DateOnly(2024, 3, 1), null, false, false, "tomas.radzevicius", "9120");

    public static void Map(IEndpointRouteBuilder v1)
    {
        var employees = v1.MapGroup("/employees").RequireAuthorization(Permissions.Organisation.Employee.View).WithTags("Organisation");

        employees.MapGet("", async (
                IQueryHandler<ListEmployeesQuery, PagedResponse<EmployeeSummary>> handler, CancellationToken cancellationToken,
                [Description("Text contained in the employee number or name.")] string? search = null,
                [Description("Only employees of this position.")] short? positionId = null,
                [Description("true lists employees who left instead of current ones.")] bool archived = false,
                [Description("Page number, from 1.")] int page = 1,
                [Description("Employees per page, 1 to 100.")] int pageSize = UserLimits.DefaultPageSize) =>
                (await handler.Handle(new ListEmployeesQuery(search, positionId, archived, page, pageSize), cancellationToken).ConfigureAwait(false)).ToHttpResult())
            .Documented("ListEmployees", "Lists employees.",
                "Current (or former) employees by name with position and account. Personal data in full: policy Administration.")
            .Produces<PagedResponse<EmployeeSummary>>()
            .ProducesValidationProblem()
            .WithResponseExample(StatusCodes.Status200OK, new PagedResponse<EmployeeSummary>([Example], 1, UserLimits.DefaultPageSize, 1));

        employees.MapGet("/{id:guid}", async ([Description("Employee id.")] Guid id, IQueryHandler<GetEmployeeQuery, EmployeeSummary> handler, CancellationToken cancellationToken) =>
                OrganisationApi.WithETag(await handler.Handle(new GetEmployeeQuery(id), cancellationToken).ConfigureAwait(false), e => e.Version))
            .Documented("GetEmployee", "Returns one employee.", "The employee with position and account; the ETag header carries the version for If-Match. Permission Organisation.Employee.View.")
            .Produces<EmployeeSummary>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithResponseExample(StatusCodes.Status200OK, Example)
            .WithResponseExample(StatusCodes.Status404NotFound, OrganisationApi.Problem(OrganisationErrors.EmployeeNotFound, 404));

        employees.MapPost("", async (CreateEmployeeRequest request, ICommandHandler<CreateEmployeeCommand, EmployeeSummary> handler, CancellationToken cancellationToken) =>
            {
                var result = await handler.Handle(new CreateEmployeeCommand(request), cancellationToken).ConfigureAwait(false);
                return result.IsSuccess
                    ? Results.Created($"/api/v1/identity/employees/{result.Value.Id}", result.Value)
                    : result.Error.ToProblem();
            })
            .RequireAuthorization(Permissions.Organisation.Employee.Create)
            .Documented("CreateEmployee", "Creates an employee.",
                "The position must be active (422 SS500 for an archived one). A used number or e-mail gives 409. An employee needs no account. Permission Organisation.Employee.Create.")
            .Accepts<CreateEmployeeRequest>("application/json")
            .Produces<EmployeeSummary>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithRequestExample(new CreateEmployeeRequest("E0042", "Asta Vaitkienė", "asta.vaitkiene@dealer.example", null, 5, new DateOnly(2026, 12, 1)))
            .WithResponseExample(StatusCodes.Status409Conflict, OrganisationApi.Problem(OrganisationErrors.CodeTaken, 409))
            .WithResponseExample(StatusCodes.Status422UnprocessableEntity, OrganisationApi.Problem(OrganisationErrors.PositionArchived, 422));

        employees.MapPut("/{id:guid}", async (
                [Description("Employee id.")] Guid id, UpdateEmployeeRequest request, [FromHeader(Name = "If-Match")] string? ifMatch,
                ICommandHandler<UpdateEmployeeCommand, EmployeeSummary> handler, CancellationToken cancellationToken) =>
                (await handler.Handle(new UpdateEmployeeCommand(id, request, Versions.Parse(ifMatch)), cancellationToken).ConfigureAwait(false)).ToHttpResult())
            .RequireAuthorization(Permissions.Organisation.Employee.Edit)
            .Documented("UpdateEmployee", "Changes an employee's data or position.",
                "Needs If-Match. A new position changes the account's roles: its sessions get them at the next session check (at most 30 "
                + "seconds). Moving the last active administrator away gives 409 Identity.LastAdministrator. Permission Organisation.Employee.Edit.")
            .Accepts<UpdateEmployeeRequest>("application/json")
            .Produces<EmployeeSummary>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithRequestExample(new UpdateEmployeeRequest("Tomas Radzevičius", "tomas.radzevicius@dealer.example", null, 3, new DateOnly(2024, 3, 1)))
            .WithResponseExample(StatusCodes.Status200OK, Example)
            .WithResponseExample(StatusCodes.Status409Conflict, OrganisationApi.Problem(OrganisationErrors.LastAdministrator, 409));

        employees.MapPost("/{id:guid}/archive", async (
                [Description("Employee id.")] Guid id, ArchiveEmployeeRequest request, [FromHeader(Name = "If-Match")] string? ifMatch,
                ICommandHandler<ArchiveEmployeeCommand, EmployeeSummary> handler, CancellationToken cancellationToken) =>
                (await handler.Handle(new ArchiveEmployeeCommand(id, request, Versions.Parse(ifMatch)), cancellationToken).ConfigureAwait(false)).ToHttpResult())
            .RequireAuthorization(Permissions.Organisation.Employee.Archive)
            .Documented("ArchiveEmployee", "Records that an employee left.",
                "Needs If-Match. The employee is archived with the leaving date; the account can no longer sign in, at once, and open "
                + "sessions end at the next session check. Not for oneself or the last active administrator. Permission Organisation.Employee.Archive.")
            .Accepts<ArchiveEmployeeRequest>("application/json")
            .Produces<EmployeeSummary>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithRequestExample(new ArchiveEmployeeRequest(new DateOnly(2026, 12, 31)))
            .WithResponseExample(StatusCodes.Status200OK, Example with { IsArchived = true, LeftOn = new DateOnly(2026, 12, 31) });

        employees.MapPost("/{id:guid}/restore", async (
                [Description("Employee id.")] Guid id, [FromHeader(Name = "If-Match")] string? ifMatch,
                ICommandHandler<RestoreEmployeeCommand, EmployeeSummary> handler, CancellationToken cancellationToken) =>
                (await handler.Handle(new RestoreEmployeeCommand(id, Versions.Parse(ifMatch)), cancellationToken).ConfigureAwait(false)).ToHttpResult())
            .RequireAuthorization(Permissions.Organisation.Employee.Archive)
            .Documented("RestoreEmployee", "Restores an employee archived by mistake.", "Needs If-Match. An anonymised employee cannot be restored. Permission Organisation.Employee.Archive.")
            .Produces<EmployeeSummary>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithResponseExample(StatusCodes.Status200OK, Example);

        employees.MapPost("/{id:guid}/account", async (
                [Description("Employee id.")] Guid id, CreateAccountRequest request,
                ICommandHandler<CreateAccountCommand, EmployeeSummary> handler, CancellationToken cancellationToken) =>
            {
                var result = await handler.Handle(new CreateAccountCommand(id, request), cancellationToken).ConfigureAwait(false);
                return result.IsSuccess ? Results.Created($"/api/v1/identity/employees/{id}", result.Value) : result.Error.ToProblem();
            })
            .RequireAuthorization(Permissions.Organisation.Account.Create)
            .Documented("CreateEmployeeAccount", "Creates the user account of an employee.",
                "The account's roles come from the employee's position; there is no other way to give roles. One account per employee "
                + "(409 Identity.AccountExists); a used user name gives 409 Identity.UserNameTaken. Permission Organisation.Account.Create.")
            .Accepts<CreateAccountRequest>("application/json")
            .Produces<EmployeeSummary>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithRequestExample(new CreateAccountRequest("asta.vaitkiene", "a long initial password"))
            .WithResponseExample(StatusCodes.Status409Conflict, OrganisationApi.Problem(OrganisationErrors.AccountExists, 409));

        employees.MapPost("/{id:guid}/anonymise", async (
                [Description("Employee id.")] Guid id, ICommandHandler<AnonymiseEmployeeCommand, EmployeeSummary> handler, CancellationToken cancellationToken) =>
                (await handler.Handle(new AnonymiseEmployeeCommand(id), cancellationToken).ConfigureAwait(false)).ToHttpResult())
            .RequireAuthorization(Permissions.Organisation.Employee.Anonymise)
            .Documented("AnonymiseEmployee", "Anonymises an employee (GDPR).",
                "Erases the personal data of the employee and of the account with iam.anonymise_employee; the rows stay for audit "
                + "references. Not for oneself (403, SS301) or the last active administrator. Permission Organisation.Employee.Anonymise.")
            .Produces<EmployeeSummary>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithResponseExample(StatusCodes.Status200OK, Example with { FullName = "Anonymised Employee", Email = null, Phone = null, IsArchived = true, IsAnonymised = true });
    }
}
