using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Identity;
using SmartSpare.Infrastructure.Messaging;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.Modules.Identity.Domain;
using SmartSpare.Modules.Identity.Domain.Organisation;
using SmartSpare.Modules.Identity.Features.Users;
using SmartSpare.Modules.Identity.Infrastructure.Identity;
using SmartSpare.Modules.Identity.Infrastructure.Persistence;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Identity.Features.Organisation;

internal sealed record ListEmployeesQuery(string? Search, short? PositionId, bool Archived, int Page, int PageSize) : IQuery<PagedResponse<EmployeeSummary>>;

internal sealed class ListEmployeesQueryValidator : AbstractValidator<ListEmployeesQuery>
{
    public ListEmployeesQueryValidator()
    {
        RuleFor(q => q.Search).MaximumLength(50);
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, UserLimits.MaxPageSize);
    }
}

internal sealed record GetEmployeeQuery(Guid Id) : IQuery<EmployeeSummary>;

internal sealed record CreateEmployeeCommand(CreateEmployeeRequest Request) : ICommand<EmployeeSummary>;

internal sealed class CreateEmployeeCommandValidator : AbstractValidator<CreateEmployeeCommand>
{
    public CreateEmployeeCommandValidator() => RuleFor(c => c.Request).SetValidator(new CreateEmployeeRequestValidator());
}

internal sealed record UpdateEmployeeCommand(Guid Id, UpdateEmployeeRequest Request, uint? Version) : ICommand<EmployeeSummary>;

internal sealed class UpdateEmployeeCommandValidator : AbstractValidator<UpdateEmployeeCommand>
{
    public UpdateEmployeeCommandValidator() => RuleFor(c => c.Request).SetValidator(new UpdateEmployeeRequestValidator());
}

internal sealed record ArchiveEmployeeCommand(Guid Id, ArchiveEmployeeRequest Request, uint? Version) : ICommand<EmployeeSummary>;

internal sealed record RestoreEmployeeCommand(Guid Id, uint? Version) : ICommand<EmployeeSummary>;

internal sealed record CreateAccountCommand(Guid EmployeeId, CreateAccountRequest Request) : ICommand<EmployeeSummary>;

internal sealed class CreateAccountCommandValidator : AbstractValidator<CreateAccountCommand>
{
    public CreateAccountCommandValidator() => RuleFor(c => c.Request).SetValidator(new CreateAccountRequestValidator());
}

internal sealed record AnonymiseEmployeeCommand(Guid Id) : ICommand<EmployeeSummary>;

/// <summary>
/// Employees: the single source of who is who (ADR 0026). A new position or leaving the company rotates the stamp of
/// the employee's account, so the new roles, or the end of every session, apply at the next session check; sign-in is
/// refused at once for an employee who left. Written as <c>app_admin</c> for the transaction only.
/// </summary>
internal sealed class EmployeeAdministration(
    IdentityDbContext db, IUnitOfWork<IdentityDbContext> unitOfWork, AdminSession admin, AdministratorGuard administrators, IdentityFunctions functions,
    UserManager<AppUser> users, IPasswordHasher<AppUser> hasher, ICurrentUser currentUser, TimeProvider clock,
    RightsGuard guard, IQueryHandler<GetEmployeeQuery, EmployeeSummary> reader)
    : ICommandHandler<CreateEmployeeCommand, EmployeeSummary>,
      ICommandHandler<UpdateEmployeeCommand, EmployeeSummary>,
      ICommandHandler<ArchiveEmployeeCommand, EmployeeSummary>,
      ICommandHandler<RestoreEmployeeCommand, EmployeeSummary>,
      ICommandHandler<CreateAccountCommand, EmployeeSummary>,
      ICommandHandler<AnonymiseEmployeeCommand, EmployeeSummary>
{
    public async Task<Result<EmployeeSummary>> Handle(CreateEmployeeCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var request = command.Request;
        if (await PositionErrorAsync(request.PositionId, cancellationToken).ConfigureAwait(false) is { } positionError)
        {
            return positionError;
        }

        if (await guard.IsSuperadministratorPositionAsync(request.PositionId, cancellationToken).ConfigureAwait(false)
            && !guard.HoldsRole(AppRoles.Superadministrator))
        {
            return RoleErrors.SuperadministratorOnly;
        }

        var created = Employee.Create(Guid.NewGuid(), request.Code, new EmployeeDetails(request.FullName, request.Email, request.Phone, request.PositionId, request.HiredOn));
        if (created.IsFailure)
        {
            return created.Error;
        }

        var employee = created.Value;
        if (await db.Set<Employee>().AsNoTracking().AnyAsync(e => e.Code == employee.Code, cancellationToken).ConfigureAwait(false))
        {
            return OrganisationErrors.CodeTaken;
        }

        if (await EmailTakenAsync(employee.Email, null, cancellationToken).ConfigureAwait(false))
        {
            return OrganisationErrors.EmailTaken;
        }

        await admin.EnterAsync(cancellationToken).ConfigureAwait(false);
        db.Add(employee);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await reader.Handle(new GetEmployeeQuery(employee.Id), cancellationToken).ConfigureAwait(false);
    }

    public Task<Result<EmployeeSummary>> Handle(UpdateEmployeeCommand command, CancellationToken cancellationToken) =>
        ChangeAsync(command.Id, command.Version, async (employee, account) =>
        {
            var request = command.Request;
            if (request.PositionId != employee.PositionId)
            {
                // ADR 0028: nobody moves their own employee to another position (someone else must grant rights).
                if (account is not null && account.Id == currentUser.UserId)
                {
                    return RoleErrors.OwnPosition;
                }

                if (await PositionErrorAsync(request.PositionId, cancellationToken).ConfigureAwait(false) is { } positionError)
                {
                    return positionError;
                }

                // ADR 0028: only a superadministrator moves someone into or out of the Superadministrator position, and
                // the last active one stays.
                var leavesSuper = await guard.IsSuperadministratorPositionAsync(employee.PositionId, cancellationToken).ConfigureAwait(false);
                if ((leavesSuper || await guard.IsSuperadministratorPositionAsync(request.PositionId, cancellationToken).ConfigureAwait(false))
                    && !guard.HoldsRole(AppRoles.Superadministrator))
                {
                    return RoleErrors.SuperadministratorOnly;
                }

                if (leavesSuper && await guard.LosesLastSuperadministratorAsync(employee.Id, cancellationToken).ConfigureAwait(false))
                {
                    return RoleErrors.LastSuperadministrator;
                }

                if (await LosesLastAdministratorAsync(employee, cancellationToken).ConfigureAwait(false))
                {
                    return OrganisationErrors.LastAdministrator;
                }
            }

            var email = string.IsNullOrWhiteSpace(request.Email) ? null : EmailAddress.Create(request.Email.Trim());
            if (email is { IsSuccess: true } && await EmailTakenAsync(email.Value, employee.Id, cancellationToken).ConfigureAwait(false))
            {
                return OrganisationErrors.EmailTaken;
            }

            var changed = employee.Update(new EmployeeDetails(request.FullName, request.Email, request.Phone, request.PositionId, request.HiredOn));
            if (changed.IsFailure)
            {
                return changed.Error;
            }

            if (changed.Value)
            {
                account?.RotateSecurityStamp();
            }

            return Result.Success();
        }, cancellationToken);

    public Task<Result<EmployeeSummary>> Handle(ArchiveEmployeeCommand command, CancellationToken cancellationToken) =>
        ChangeAsync(command.Id, command.Version, async (employee, account) =>
        {
            if (account?.Id == currentUser.UserId)
            {
                return UserErrors.OwnAdministration;
            }

            if (await LosesLastAdministratorAsync(employee, cancellationToken).ConfigureAwait(false))
            {
                return OrganisationErrors.LastAdministrator;
            }

            if (await guard.LosesLastSuperadministratorAsync(employee.Id, cancellationToken).ConfigureAwait(false))
            {
                return RoleErrors.LastSuperadministrator;
            }

            var archived = employee.Archive(command.Request.LeftOn, clock.GetUtcNow(), currentUser.UserId);
            if (archived.IsSuccess)
            {
                account?.RotateSecurityStamp();
            }

            return archived;
        }, cancellationToken);

    public Task<Result<EmployeeSummary>> Handle(RestoreEmployeeCommand command, CancellationToken cancellationToken) =>
        ChangeAsync(command.Id, command.Version, async (employee, account) =>
        {
            if (await PositionErrorAsync(employee.PositionId, cancellationToken).ConfigureAwait(false) is { } positionError)
            {
                return positionError;
            }

            var restored = employee.Restore();
            if (restored.IsSuccess)
            {
                account?.RotateSecurityStamp();
            }

            return restored;
        }, cancellationToken);

    /// <summary>Creates the employee's account; its roles come from the position, the person's data stays on the employee.</summary>
    public async Task<Result<EmployeeSummary>> Handle(CreateAccountCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var employee = await db.Set<Employee>().AsNoTracking().SingleOrDefaultAsync(e => e.Id == command.EmployeeId, cancellationToken).ConfigureAwait(false);
        if (employee is null)
        {
            return OrganisationErrors.EmployeeNotFound;
        }

        if (!employee.IsActive)
        {
            return OrganisationErrors.EmployeeInactive;
        }

        if (await db.Set<AppUser>().AsNoTracking().AnyAsync(u => u.EmployeeId == employee.Id, cancellationToken).ConfigureAwait(false))
        {
            return OrganisationErrors.AccountExists;
        }

        var userName = UserName.Create(command.Request.UserName);
        if (userName.IsFailure)
        {
            return userName.Error;
        }

        if (await db.Set<AppUser>().AsNoTracking().AnyAsync(u => u.UserName == userName.Value, cancellationToken).ConfigureAwait(false))
        {
            return UserErrors.UserNameTaken;
        }

        // A placeholder hash satisfies the format CHECK while the password validators run on the new user.
        var user = AppUser.Create(Guid.NewGuid(), employee.Id, userName.Value, hasher.HashPassword(null!, command.Request.Password));
        if (await PasswordChecks.ValidateAsync(users, user, command.Request.Password).ConfigureAwait(false) is { } rejected)
        {
            return rejected;
        }

        await admin.EnterAsync(cancellationToken).ConfigureAwait(false);
        db.Add(user);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await reader.Handle(new GetEmployeeQuery(employee.Id), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>GDPR erasure through <c>iam.anonymise_employee</c>, which refuses the acting administrator (SS301) and repeats the role check.</summary>
    public async Task<Result<EmployeeSummary>> Handle(AnonymiseEmployeeCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var employee = await db.Set<Employee>().AsNoTracking().SingleOrDefaultAsync(e => e.Id == command.Id, cancellationToken).ConfigureAwait(false);
        if (employee is null)
        {
            return OrganisationErrors.EmployeeNotFound;
        }

        if (employee.AnonymisedAt is not null)
        {
            return OrganisationErrors.EmployeeAnonymised;
        }

        if (await LosesLastAdministratorAsync(employee, cancellationToken).ConfigureAwait(false))
        {
            return OrganisationErrors.LastAdministrator;
        }

        if (await guard.LosesLastSuperadministratorAsync(employee.Id, cancellationToken).ConfigureAwait(false))
        {
            return RoleErrors.LastSuperadministrator;
        }

        await admin.EnterAsync(cancellationToken).ConfigureAwait(false);
        await functions.AnonymiseEmployeeAsync(command.Id, cancellationToken).ConfigureAwait(false);
        return await reader.Handle(new GetEmployeeQuery(command.Id), cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<EmployeeSummary>> ChangeAsync(
        Guid id, uint? version, Func<Employee, AppUser?, Task<Result>> change, CancellationToken cancellationToken)
    {
        if (version is null)
        {
            return VersionErrors.Required;
        }

        var employee = await db.Set<Employee>().SingleOrDefaultAsync(e => e.Id == id, cancellationToken).ConfigureAwait(false);
        if (employee is null)
        {
            return OrganisationErrors.EmployeeNotFound;
        }

        if (employee.AnonymisedAt is not null)
        {
            return OrganisationErrors.EmployeeAnonymised;
        }

        db.Entry(employee).Property<uint>("xmin").OriginalValue = version.Value;
        var account = await db.Set<AppUser>().SingleOrDefaultAsync(u => u.EmployeeId == id, cancellationToken).ConfigureAwait(false);
        var result = await change(employee, account).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return result.Error;
        }

        await admin.EnterAsync(cancellationToken).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await reader.Handle(new GetEmployeeQuery(id), cancellationToken).ConfigureAwait(false);
    }

    private async Task<Error?> PositionErrorAsync(short positionId, CancellationToken cancellationToken)
    {
        var archived = await db.Set<Position>().AsNoTracking().Where(p => p.Id == positionId).Select(p => (bool?)p.IsArchived)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return archived switch
        {
            null => OrganisationErrors.PositionNotFound,
            true => OrganisationErrors.PositionArchived,
            _ => null,
        };
    }

    /// <summary>Whether this employee is an active administrator and no other one would remain.</summary>
    private async Task<bool> LosesLastAdministratorAsync(Employee employee, CancellationToken cancellationToken)
    {
        var template = await db.Set<Position>().AsNoTracking().Where(p => p.Id == employee.PositionId)
            .SelectMany(p => p.Roles.Select(r => r.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
        return employee.IsActive
            && await administrators.GrantsAdministratorAsync(template.ToHashSet(), cancellationToken).ConfigureAwait(false)
            && !await administrators.AnotherAdministratorExistsAsync(employee.Id, null, cancellationToken).ConfigureAwait(false);
    }

    private Task<bool> EmailTakenAsync(EmailAddress? email, Guid? except, CancellationToken cancellationToken) =>
        email is null
            ? Task.FromResult(false)
            : db.Set<Employee>().AsNoTracking().AnyAsync(e => e.Email == email && e.Id != except, cancellationToken);
}
