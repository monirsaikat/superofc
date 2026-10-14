using FluentValidation;
using Microsoft.EntityFrameworkCore;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Auth;
using SuperOfc.Application.Common;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Security;

namespace SuperOfc.Application.Users;

public sealed record UserDto(Guid Id, string Username, string DisplayName, Role Role, bool IsActive, DateTime? LastLoginUtc, bool IsLockedOut);

[RequiresPermission(Permission.ManageUsers)]
public sealed record GetUsersQuery : IQuery<IReadOnlyList<UserDto>>;

public sealed class GetUsersHandler(IAppDbContext db, ICurrentUser current, IClock clock) : IRequestHandler<GetUsersQuery, IReadOnlyList<UserDto>>
{
    public async Task<Result<IReadOnlyList<UserDto>>> HandleAsync(GetUsersQuery request, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var users = await db.Users.AsNoTracking()
            .Where(u => u.CompanyId == current.CompanyId)
            .OrderBy(u => u.Username)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return users.Select(u => new UserDto(u.Id, u.Username, u.DisplayName, u.Role, u.IsActive, u.LastLoginUtc, u.IsLockedOut(now))).ToList();
    }
}

[RequiresPermission(Permission.ManageUsers)]
public sealed record CreateUserCommand(string Username, string DisplayName, string Password, Role Role) : ICommand<Guid>;

public sealed class CreateUserValidator : AbstractValidator<CreateUserCommand>
{
    public CreateUserValidator()
    {
        RuleFor(x => x.Username).NotEmpty().MaximumLength(50).Matches("^[A-Za-z0-9._-]+$")
            .WithMessage("Use letters, numbers, dot, dash or underscore.");
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Password).SetValidator(new PasswordRule());
        RuleFor(x => x.Role).IsInEnum();
    }
}

public sealed class CreateUserHandler(IAppDbContext db, IPasswordHasher hasher, IIdGenerator ids, ICurrentUser current, IAuditWriter audit)
    : IRequestHandler<CreateUserCommand, Guid>
{
    public async Task<Result<Guid>> HandleAsync(CreateUserCommand request, CancellationToken cancellationToken)
    {
        var username = request.Username.Trim();
        if (await db.Users.AnyAsync(u => u.Username == username, cancellationToken).ConfigureAwait(false))
        {
            return new Error("User.DuplicateUsername", $"The username '{username}' is already taken.");
        }

        var user = new User
        {
            Id = ids.NewId(),
            CompanyId = current.CompanyId,
            Username = username,
            DisplayName = request.DisplayName.Trim(),
            PasswordHash = hasher.Hash(request.Password),
            Role = request.Role,
            IsActive = true,
        };
        db.Users.Add(user);
        audit.Record("User.Created", $"Created user {user.Username} ({user.Role}).", nameof(User), user.Id, after: new { user.Username, user.DisplayName, user.Role });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return user.Id;
    }
}

[RequiresPermission(Permission.ManageUsers)]
public sealed record UpdateUserCommand(Guid Id, string DisplayName, Role Role, bool IsActive) : ICommand<Unit>;

public sealed class UpdateUserValidator : AbstractValidator<UpdateUserCommand>
{
    public UpdateUserValidator()
    {
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Role).IsInEnum();
    }
}

public sealed class UpdateUserHandler(IAppDbContext db, ICurrentUser current, IAuditWriter audit) : IRequestHandler<UpdateUserCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(UpdateUserCommand request, CancellationToken cancellationToken)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == request.Id, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return Error.NotFound("User");
        }

        var removesLastAdmin = user.Role == Role.Admin && user.IsActive && (request.Role != Role.Admin || !request.IsActive);
        if (removesLastAdmin)
        {
            var otherAdmins = await db.Users.CountAsync(u => u.Id != user.Id && u.Role == Role.Admin && u.IsActive, cancellationToken).ConfigureAwait(false);
            if (otherAdmins == 0)
            {
                return new Error("User.LastAdmin", "At least one active administrator must remain.");
            }
        }

        if (user.Id == current.UserId && !request.IsActive)
        {
            return new Error("User.SelfDeactivate", "You cannot deactivate your own account.");
        }

        var before = new { user.DisplayName, user.Role, user.IsActive };
        user.DisplayName = request.DisplayName.Trim();
        user.Role = request.Role;
        user.IsActive = request.IsActive;
        audit.Record("User.Updated", $"Updated user {user.Username}.", nameof(User), user.Id, before, new { user.DisplayName, user.Role, user.IsActive });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}

[RequiresPermission(Permission.ManageUsers)]
public sealed record ResetPasswordCommand(Guid Id, string NewPassword) : ICommand<Unit>;

public sealed class ResetPasswordValidator : AbstractValidator<ResetPasswordCommand>
{
    public ResetPasswordValidator()
    {
        RuleFor(x => x.NewPassword).SetValidator(new PasswordRule());
    }
}

public sealed class ResetPasswordHandler(IAppDbContext db, IPasswordHasher hasher, IAuditWriter audit) : IRequestHandler<ResetPasswordCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(ResetPasswordCommand request, CancellationToken cancellationToken)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == request.Id, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return Error.NotFound("User");
        }

        user.PasswordHash = hasher.Hash(request.NewPassword);
        user.FailedAttempts = 0;
        user.LockoutUntilUtc = null;
        audit.Record("User.PasswordReset", $"Password reset for {user.Username}.", nameof(User), user.Id);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
