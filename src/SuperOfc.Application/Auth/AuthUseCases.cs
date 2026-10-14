using FluentValidation;
using Microsoft.EntityFrameworkCore;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Common;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Security;

namespace SuperOfc.Application.Auth;

public sealed record SignedInUser(Guid Id, string Username, string DisplayName, Role Role);

[AllowAnonymous]
public sealed record LoginCommand(string Username, string Password) : ICommand<SignedInUser>;

public sealed class LoginValidator : AbstractValidator<LoginCommand>
{
    public LoginValidator()
    {
        RuleFor(x => x.Username).NotEmpty().WithMessage("Enter your username.");
        RuleFor(x => x.Password).NotEmpty().WithMessage("Enter your password.");
    }
}

public sealed class LoginHandler(
    IAppDbContext db,
    IPasswordHasher hasher,
    IClock clock,
    IUserSession session,
    IPolicyProvider policies,
    IAuditWriter audit) : IRequestHandler<LoginCommand, SignedInUser>
{
    public static readonly Error InvalidCredentials = new("Auth.InvalidCredentials", "The username or password is incorrect.");

    public async Task<Result<SignedInUser>> HandleAsync(LoginCommand request, CancellationToken cancellationToken)
    {
        var username = request.Username.Trim();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username, cancellationToken).ConfigureAwait(false);
        var now = clock.UtcNow;

        if (user is null || !user.IsActive)
        {
            audit.Record("Auth.LoginFailed", $"Failed sign-in for unknown or inactive user '{username}'.");
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return InvalidCredentials;
        }

        session.SetCompany(user.CompanyId);

        if (user.IsLockedOut(now))
        {
            var minutes = (int)Math.Ceiling((user.LockoutUntilUtc!.Value - now).TotalMinutes);
            return new Error("Auth.LockedOut", $"Too many failed attempts. Try again in {minutes} minute(s).");
        }

        if (!hasher.Verify(user.PasswordHash, request.Password))
        {
            var general = (await policies.GetSnapshotAsync(DateOnly.FromDateTime(now), cancellationToken).ConfigureAwait(false)).General;
            user.RegisterFailedLogin(now, general.MaxFailedLogins, TimeSpan.FromMinutes(general.LockoutMinutes));
            audit.Record("Auth.LoginFailed", $"Failed sign-in for '{user.Username}'.", nameof(User), user.Id);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return user.IsLockedOut(now)
                ? new Error("Auth.LockedOut", $"Too many failed attempts. Try again in {general.LockoutMinutes} minute(s).")
                : InvalidCredentials;
        }

        user.RegisterSuccessfulLogin(now);
        session.SignIn(user);
        audit.Record("Auth.Login", $"{user.Username} signed in.", nameof(User), user.Id);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new SignedInUser(user.Id, user.Username, user.DisplayName, user.Role);
    }
}

[AllowAnonymous]
public sealed record LogoutCommand : ICommand<Unit>;

public sealed class LogoutHandler(IAppDbContext db, IUserSession session, IAuditWriter audit) : IRequestHandler<LogoutCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(LogoutCommand request, CancellationToken cancellationToken)
    {
        if (session.IsAuthenticated)
        {
            audit.Record("Auth.Logout", $"{session.UserName} signed out.", nameof(User), session.UserId);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            session.SignOut();
        }

        return Unit.Value;
    }
}

/// <summary>Re-checks the password of the signed-in user (lock screen).</summary>
[AllowAnonymous]
public sealed record UnlockCommand(string Password) : ICommand<Unit>;

public sealed class UnlockHandler(IAppDbContext db, IPasswordHasher hasher, ICurrentUser current) : IRequestHandler<UnlockCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(UnlockCommand request, CancellationToken cancellationToken)
    {
        if (current.UserId is null)
        {
            return new Error("Auth.NotSignedIn", "Please sign in first.");
        }

        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == current.UserId, cancellationToken).ConfigureAwait(false);
        return user is not null && hasher.Verify(user.PasswordHash, request.Password) ? Unit.Value : LoginHandler.InvalidCredentials;
    }
}

[RequiresPermission(Permission.ViewData)]
public sealed record ChangeOwnPasswordCommand(string CurrentPassword, string NewPassword) : ICommand<Unit>;

public sealed class ChangeOwnPasswordValidator : AbstractValidator<ChangeOwnPasswordCommand>
{
    public ChangeOwnPasswordValidator()
    {
        RuleFor(x => x.NewPassword).SetValidator(new PasswordRule());
    }
}

public sealed class ChangeOwnPasswordHandler(IAppDbContext db, IPasswordHasher hasher, ICurrentUser current, IAuditWriter audit)
    : IRequestHandler<ChangeOwnPasswordCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(ChangeOwnPasswordCommand request, CancellationToken cancellationToken)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == current.UserId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return Error.NotFound("User");
        }

        if (!hasher.Verify(user.PasswordHash, request.CurrentPassword))
        {
            return new Error("Auth.WrongPassword", "Your current password is incorrect.");
        }

        user.PasswordHash = hasher.Hash(request.NewPassword);
        audit.Record("User.PasswordChanged", $"{user.Username} changed their password.", nameof(User), user.Id);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}

/// <summary>Minimum password rule shared by all password inputs.</summary>
public sealed class PasswordRule : AbstractValidator<string>
{
    public const int MinimumLength = 8;

    public PasswordRule()
    {
        RuleFor(x => x)
            .NotEmpty().WithMessage("Enter a password.")
            .MinimumLength(MinimumLength).WithMessage($"Use at least {MinimumLength} characters.")
            .Must(p => p.Any(char.IsLetter) && p.Any(char.IsDigit)).WithMessage("Use at least one letter and one number.");
    }
}
