using System.ComponentModel;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SmartSpare.ApiContracts;
using SmartSpare.ApiContracts.Authorization;
using SmartSpare.ApiContracts.Identity;
using SmartSpare.Infrastructure.Http;
using SmartSpare.Infrastructure.Messaging;
using SmartSpare.Infrastructure.Persistence;
using SmartSpare.Modules.Identity.Domain;
using SmartSpare.Modules.Identity.Infrastructure.Persistence;
using SmartSpare.Modules.Inventory.Contracts;
using SmartSpare.SharedKernel;

namespace SmartSpare.Modules.Identity.Features.Users;

internal static class UserErrors
{
    public static readonly Error NotFound = Error.NotFound("Identity.UserNotFound", "The user does not exist.");

    public static readonly Error OwnAdministration = Error.BusinessRule(
        "Identity.OwnAdministration", "You cannot deactivate yourself or remove your own Administrator role.");

    public static readonly Error UserNameTaken = Error.Conflict("Identity.UserNameTaken", "This user name or e-mail address is already used.");

    public static readonly Error WrongPassword = Error.Validation("Identity.WrongPassword", "The current password is not correct.");

    public static readonly Error Anonymised = Error.BusinessRule("Identity.Anonymised", "The user was anonymised and cannot be changed.");

    public static Error PasswordRejected(IEnumerable<IdentityError> errors) =>
        Error.Validation("Identity.PasswordRejected", string.Join(" ", errors.Select(e => e.Description)));
}

// ---- Queries -----------------------------------------------------------------------------------------------------

internal sealed record ListUsersQuery(string? Search, int Page, int PageSize) : IQuery<PagedResponse<UserSummary>>;

internal sealed class ListUsersQueryValidator : AbstractValidator<ListUsersQuery>
{
    public ListUsersQueryValidator()
    {
        RuleFor(q => q.Search).MaximumLength(50);
        RuleFor(q => q.Page).GreaterThanOrEqualTo(1);
        RuleFor(q => q.PageSize).InclusiveBetween(1, UserLimits.MaxPageSize);
    }
}

internal sealed record GetUserFormOptionsQuery : IQuery<UserFormOptions>;

// ---- Commands ----------------------------------------------------------------------------------------------------

/// <summary>The password policy of the Identity options (STAGE_3_PLAN 2.7), checked before a hash is stored.</summary>
internal static class PasswordChecks
{
    public static async Task<Error?> ValidateAsync(UserManager<AppUser> users, AppUser user, string password)
    {
        var errors = new List<IdentityError>();
        foreach (var validator in users.PasswordValidators)
        {
            var result = await validator.ValidateAsync(users, user, password).ConfigureAwait(false);
            errors.AddRange(result.Errors);
        }

        return errors.Count == 0 ? null : UserErrors.PasswordRejected(errors);
    }
}

/// <summary>Reads one user of the directory (the list query's row) after a change.</summary>
internal sealed class UserReader(IQueryHandler<ListUsersQuery, PagedResponse<UserSummary>> list, IdentityDbContext db)
{
    public async Task<UserSummary> ReadAsync(Guid id, CancellationToken cancellationToken)
    {
        var name = await db.Set<AppUser>().AsNoTracking().Where(u => u.Id == id).Select(u => u.UserName).SingleAsync(cancellationToken).ConfigureAwait(false);
        var page = await list.Handle(new ListUsersQuery(name.Value, 1, UserLimits.MaxPageSize), cancellationToken).ConfigureAwait(false);
        return page.Value.Items.Single(u => u.Id == id);
    }
}

/// <summary>The shared steps of the administrative commands: load the user, refuse anonymised users and changes to oneself.</summary>
internal sealed class UserAdministration(
    IdentityDbContext db, IUnitOfWork<IdentityDbContext> unitOfWork, AdminSession admin, ICurrentUser currentUser, TimeProvider clock,
    UserReader reader, IdentityFunctions functions, Infrastructure.Identity.RightsGuard guard)
    : ICommandHandler<UnlockUserCommand, UserSummary>,
      ICommandHandler<DeactivateUserCommand, UserSummary>,
      ICommandHandler<AnonymiseUserCommand, UserSummary>
{
    public Task<Result<UserSummary>> Handle(UnlockUserCommand command, CancellationToken cancellationToken) =>
        ChangeAsync(command.Id, user => user.Unlock(), cancellationToken);

    public async Task<Result<UserSummary>> Handle(DeactivateUserCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Id == currentUser.UserId)
        {
            return UserErrors.OwnAdministration;
        }

        if (await LosesLastSuperadministratorAsync(command.Id, cancellationToken).ConfigureAwait(false))
        {
            return RoleErrors.LastSuperadministrator;
        }

        return await ChangeAsync(command.Id, user => user.Deactivate(clock.GetUtcNow()), cancellationToken).ConfigureAwait(false);
    }


    /// <summary>
    /// GDPR erasure of the account's employee through <c>iam.anonymise_employee</c> (ADR 0026), which also erases the
    /// account, refuses the acting administrator (SS301) and repeats the role check.
    /// </summary>
    public async Task<Result<UserSummary>> Handle(AnonymiseUserCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var employee = await db.Set<AppUser>().AsNoTracking().Where(u => u.Id == command.Id).Select(u => (Guid?)u.EmployeeId)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (employee is null)
        {
            return UserErrors.NotFound;
        }

        if (await guard.LosesLastSuperadministratorAsync(employee.Value, cancellationToken).ConfigureAwait(false))
        {
            return RoleErrors.LastSuperadministrator;
        }

        await admin.EnterAsync(cancellationToken).ConfigureAwait(false);
        await functions.AnonymiseEmployeeAsync(employee.Value, cancellationToken).ConfigureAwait(false);
        await Organisation.EmployeeDirectoryPublication.QueueAsync(db, unitOfWork, [employee.Value], cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(command.Id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Whether the account belongs to the last active superadministrator (ADR 0028).</summary>
    private async Task<bool> LosesLastSuperadministratorAsync(Guid userId, CancellationToken cancellationToken)
    {
        var employeeId = await db.Set<AppUser>().AsNoTracking().Where(u => u.Id == userId).Select(u => (Guid?)u.EmployeeId)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return employeeId is { } id && await guard.LosesLastSuperadministratorAsync(id, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<UserSummary>> ChangeAsync(Guid id, Action<AppUser> change, CancellationToken cancellationToken)
    {
        var user = await db.Set<AppUser>().SingleOrDefaultAsync(u => u.Id == id, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return UserErrors.NotFound;
        }

        if (user.AnonymisedAt is not null)
        {
            return UserErrors.Anonymised;
        }

        change(user);
        await admin.EnterAsync(cancellationToken).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(id, cancellationToken).ConfigureAwait(false);
    }
}

internal sealed record UnlockUserCommand(Guid Id) : ICommand<UserSummary>;

internal sealed record DeactivateUserCommand(Guid Id) : ICommand<UserSummary>;

internal sealed record AnonymiseUserCommand(Guid Id) : ICommand<UserSummary>;

internal sealed record ChangeOwnPasswordCommand(ChangePasswordRequest Request) : ICommand;

internal sealed class ChangeOwnPasswordCommandValidator : AbstractValidator<ChangeOwnPasswordCommand>
{
    public ChangeOwnPasswordCommandValidator() => RuleFor(c => c.Request).SetValidator(new ChangePasswordRequestValidator());
}

/// <summary>
/// A user changes the own password (the <c>password_hash</c> grant of <c>app_write</c>, no administration). The stamp
/// rotates, so every session of the user, this one included, ends at its next check: the user signs in again.
/// </summary>
internal sealed class ChangeOwnPasswordCommandHandler(
    IdentityDbContext db, IUnitOfWork<IdentityDbContext> unitOfWork, UserManager<AppUser> users, IPasswordHasher<AppUser> hasher, ICurrentUser currentUser)
    : ICommandHandler<ChangeOwnPasswordCommand>
{
    public async Task<Result> Handle(ChangeOwnPasswordCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var id = currentUser.UserId ?? throw new InvalidOperationException("A password change needs a signed-in user.");
        var user = await db.Set<AppUser>().SingleOrDefaultAsync(u => u.Id == id, cancellationToken).ConfigureAwait(false);
        if (user?.PasswordHash is not { } hash)
        {
            return UserErrors.NotFound;
        }

        if (hasher.VerifyHashedPassword(user, hash, command.Request.CurrentPassword) == PasswordVerificationResult.Failed)
        {
            return UserErrors.WrongPassword;
        }

        if (await PasswordChecks.ValidateAsync(users, user, command.Request.NewPassword).ConfigureAwait(false) is { } rejected)
        {
            return rejected;
        }

        user.ChangePasswordHash(hasher.HashPassword(user, command.Request.NewPassword));
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }
}
