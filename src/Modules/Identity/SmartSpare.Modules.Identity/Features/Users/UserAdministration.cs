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

    public static readonly Error UnknownWarehouse = Error.Validation("Identity.UnknownWarehouse", "The home hub does not exist or is inactive.");

    public static readonly Error UnknownBrand = Error.Validation("Identity.UnknownBrand", "A brand code is unknown.");

    public static readonly Error UserNameTaken = Error.Conflict("Identity.UserNameTaken", "This user name or e-mail address is already used.");

    public static readonly Error WrongPassword = Error.Validation("Identity.WrongPassword", "The current password is not correct.");

    public static readonly Error Anonymised = Error.BusinessRule("Identity.Anonymised", "The user was anonymised and cannot be changed.");

    public static Error PasswordRejected(IEnumerable<IdentityError> errors) =>
        Error.Validation("Identity.PasswordRejected", string.Join(" ", errors.Select(e => e.Description)));
}

/// <summary>Resolves role and brand codes to their ids and checks the home hub.</summary>
internal sealed class AccessResolver(IdentityDbContext db, IdentityFunctions functions, IWarehouseQueries warehouses)
{
    public sealed record Resolved(IReadOnlyList<short> RoleIds, IReadOnlyList<short> BrandIds, short? HomeWarehouseId);

    public async Task<Result<Resolved>> ResolveAsync(UserAccessRequest access, CancellationToken cancellationToken)
    {
        var codes = access.Roles.Distinct(StringComparer.Ordinal).ToArray();
        var roles = await db.Set<Role>().AsNoTracking().Where(r => codes.Contains(r.Code)).Select(r => r.Id).ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var brandCodes = access.Brands.Select(b => b.Trim().ToUpperInvariant()).Distinct(StringComparer.Ordinal).ToArray();
        var brands = await functions.BrandIdsAsync(brandCodes, cancellationToken).ConfigureAwait(false);
        if (brands.Count != brandCodes.Length)
        {
            return UserErrors.UnknownBrand;
        }

        if (access.HomeWarehouseId is { } hub && !await warehouses.ExistsAsync(hub, cancellationToken).ConfigureAwait(false))
        {
            return UserErrors.UnknownWarehouse;
        }

        return new Resolved(roles, brands, access.HomeWarehouseId);
    }

    /// <summary>Makes the user's roles, brands and hub equal to <paramref name="target"/>; every change rotates the stamp.</summary>
    public static void Apply(AppUser user, Resolved target, DateTimeOffset now, Guid? grantedBy)
    {
        foreach (var role in user.Roles.Select(r => r.RoleId).Except(target.RoleIds).ToList())
        {
            user.RevokeRole(role);
        }

        foreach (var role in target.RoleIds)
        {
            user.AssignRole(role, now, grantedBy);
        }

        foreach (var brand in user.Brands.Select(b => b.BrandId).Except(target.BrandIds).ToList())
        {
            user.RemoveBrand(brand);
        }

        foreach (var brand in target.BrandIds)
        {
            user.AssignBrand(brand);
        }

        user.SetHomeWarehouse(target.HomeWarehouseId);
    }
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

internal sealed record CreateUserCommand(CreateUserRequest Request) : ICommand<Guid>;

internal sealed class CreateUserCommandValidator : AbstractValidator<CreateUserCommand>
{
    public CreateUserCommandValidator() => RuleFor(c => c.Request).SetValidator(new CreateUserRequestValidator());
}

internal sealed class CreateUserCommandHandler(
    IdentityDbContext db, IUnitOfWork<IdentityDbContext> unitOfWork, UserManager<AppUser> users, IPasswordHasher<AppUser> hasher,
    AccessResolver resolver, AdminSession admin, ICurrentUser currentUser, TimeProvider clock)
    : ICommandHandler<CreateUserCommand, Guid>
{
    public async Task<Result<Guid>> Handle(CreateUserCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var request = command.Request;
        var userName = UserName.Create(request.UserName);
        var email = EmailAddress.Create(request.Email);
        if (userName.IsFailure)
        {
            return userName.Error;
        }

        if (email.IsFailure)
        {
            return email.Error;
        }

        var access = await resolver.ResolveAsync(request.Access, cancellationToken).ConfigureAwait(false);
        if (access.IsFailure)
        {
            return access.Error;
        }

        if (await db.Set<AppUser>().AsNoTracking().AnyAsync(u => u.UserName == userName.Value || u.Email == email.Value, cancellationToken).ConfigureAwait(false))
        {
            return UserErrors.UserNameTaken;
        }

        // A placeholder hash satisfies the format CHECK while the password validators run on the new user.
        var id = Guid.NewGuid();
        var user = AppUser.Create(id, userName.Value, email.Value, request.FullName.Trim(), request.Phone?.Trim(), hasher.HashPassword(null!, request.Password));
        var checks = await ValidatePasswordAsync(users, user, request.Password).ConfigureAwait(false);
        if (checks is { } rejected)
        {
            return rejected;
        }

        AccessResolver.Apply(user, access.Value, clock.GetUtcNow(), currentUser.UserId);
        await admin.EnterAsync(cancellationToken).ConfigureAwait(false);
        db.Add(user);
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return id;
    }

    internal static async Task<Error?> ValidatePasswordAsync(UserManager<AppUser> users, AppUser user, string password)
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

internal sealed record UpdateUserAccessCommand(Guid Id, UserAccessRequest Access) : ICommand<UserSummary>;

internal sealed class UpdateUserAccessCommandValidator : AbstractValidator<UpdateUserAccessCommand>
{
    public UpdateUserAccessCommandValidator() => RuleFor(c => c.Access).SetValidator(new UserAccessRequestValidator());
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
    AccessResolver resolver, UserReader reader, IdentityFunctions functions)
    : ICommandHandler<UpdateUserAccessCommand, UserSummary>,
      ICommandHandler<UnlockUserCommand, UserSummary>,
      ICommandHandler<DeactivateUserCommand, UserSummary>,
      ICommandHandler<AnonymiseUserCommand, UserSummary>
{
    private const string Administrator = AppRoles.Administrator;

    public async Task<Result<UserSummary>> Handle(UpdateUserAccessCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Id == currentUser.UserId && !command.Access.Roles.Contains(Administrator))
        {
            return UserErrors.OwnAdministration;
        }

        var access = await resolver.ResolveAsync(command.Access, cancellationToken).ConfigureAwait(false);
        if (access.IsFailure)
        {
            return access.Error;
        }

        return await ChangeAsync(command.Id, user => AccessResolver.Apply(user, access.Value, clock.GetUtcNow(), currentUser.UserId), cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<Result<UserSummary>> Handle(UnlockUserCommand command, CancellationToken cancellationToken) =>
        ChangeAsync(command.Id, user => user.Unlock(), cancellationToken);

    public Task<Result<UserSummary>> Handle(DeactivateUserCommand command, CancellationToken cancellationToken) =>
        command.Id == currentUser.UserId
            ? Task.FromResult<Result<UserSummary>>(UserErrors.OwnAdministration)
            : ChangeAsync(command.Id, user => user.Deactivate(clock.GetUtcNow()), cancellationToken);

    /// <summary>GDPR erasure through <c>iam.anonymise_user</c>, which refuses the acting administrator (SS301) and repeats the role check.</summary>
    public async Task<Result<UserSummary>> Handle(AnonymiseUserCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!await db.Set<AppUser>().AsNoTracking().AnyAsync(u => u.Id == command.Id, cancellationToken).ConfigureAwait(false))
        {
            return UserErrors.NotFound;
        }

        await admin.EnterAsync(cancellationToken).ConfigureAwait(false);
        await functions.AnonymiseAsync(command.Id, cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(command.Id, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<UserSummary>> ChangeAsync(Guid id, Action<AppUser> change, CancellationToken cancellationToken)
    {
        var user = await db.Set<AppUser>().Include(u => u.Roles).Include(u => u.Brands)
            .SingleOrDefaultAsync(u => u.Id == id, cancellationToken).ConfigureAwait(false);
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

        if (await CreateUserCommandHandler.ValidatePasswordAsync(users, user, command.Request.NewPassword).ConfigureAwait(false) is { } rejected)
        {
            return rejected;
        }

        user.ChangePasswordHash(hasher.HashPassword(user, command.Request.NewPassword));
        await unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }
}
