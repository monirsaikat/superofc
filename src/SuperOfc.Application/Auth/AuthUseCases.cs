using System.Security.Cryptography;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Common;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Security;

namespace SuperOfc.Application.Auth;

/// <param name="RememberToken">Set when the user chose to stay signed in: the token to keep on this PC.</param>
public sealed record SignedInUser(Guid Id, string Username, string DisplayName, Role Role, string? RememberToken = null);

/// <param name="Remember">Keep the user signed in on this PC (see <see cref="SignInToken"/>).</param>
[AllowAnonymous]
public sealed record LoginCommand(string Username, string Password, bool Remember = false) : ICommand<SignedInUser>;

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
    IIdGenerator ids,
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
        var token = request.Remember ? await SignInTokens.IssueAsync(db, ids, user.Id, now, cancellationToken).ConfigureAwait(false) : null;
        audit.Record("Auth.Login", $"{user.Username} signed in.", nameof(User), user.Id);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new SignedInUser(user.Id, user.Username, user.DisplayName, user.Role, token);
    }
}

/// <summary>Signs in with a token kept on this PC ("keep me signed in") and renews it.</summary>
[AllowAnonymous]
public sealed record ResumeSignInCommand(string Token) : ICommand<SignedInUser>;

public sealed class ResumeSignInHandler(IAppDbContext db, IClock clock, IUserSession session, IAuditWriter audit)
    : IRequestHandler<ResumeSignInCommand, SignedInUser>
{
    public static readonly Error Expired = new("Auth.SignInExpired", "Please sign in again.");

    public async Task<Result<SignedInUser>> HandleAsync(ResumeSignInCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Token))
        {
            return Expired;
        }

        var now = clock.UtcNow;
        var hash = SignInTokens.Hash(request.Token);
        var token = await db.SignInTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken).ConfigureAwait(false);
        if (token is null)
        {
            return Expired;
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == token.UserId, cancellationToken).ConfigureAwait(false);
        if (token.IsExpired(now) || user is null || !user.IsActive || user.IsLockedOut(now))
        {
            db.SignInTokens.Remove(token);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return Expired;
        }

        session.SetCompany(user.CompanyId);
        token.LastUsedAtUtc = now;
        token.ExpiresAtUtc = now + SignInToken.Lifetime;
        user.RegisterSuccessfulLogin(now);
        session.SignIn(user);
        audit.Record("Auth.Login", $"{user.Username} signed in (kept signed in on this PC).", nameof(User), user.Id);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new SignedInUser(user.Id, user.Username, user.DisplayName, user.Role, request.Token);
    }
}

/// <param name="RememberToken">The token this PC kept, if any; it stops working.</param>
[AllowAnonymous]
public sealed record LogoutCommand(string? RememberToken = null) : ICommand<Unit>;

public sealed class LogoutHandler(IAppDbContext db, IUserSession session, IAuditWriter audit) : IRequestHandler<LogoutCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(LogoutCommand request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.RememberToken))
        {
            var hash = SignInTokens.Hash(request.RememberToken);
            db.SignInTokens.RemoveRange(await db.SignInTokens.Where(t => t.TokenHash == hash).ToListAsync(cancellationToken).ConfigureAwait(false));
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

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
        await SignInTokens.RevokeAllAsync(db, user.Id, cancellationToken).ConfigureAwait(false);
        audit.Record("User.PasswordChanged", $"{user.Username} changed their password.", nameof(User), user.Id);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}

/// <summary>Issues and revokes "keep me signed in" tokens (<see cref="SignInToken"/>).</summary>
public static class SignInTokens
{
    /// <summary>Adds a new token for the user and returns its plain value; also drops the user's expired tokens.</summary>
    public static async Task<string> IssueAsync(IAppDbContext db, IIdGenerator ids, Guid userId, DateTime utcNow, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(ids);
        var expired = await db.SignInTokens.Where(t => t.UserId == userId && t.ExpiresAtUtc <= utcNow).ToListAsync(cancellationToken).ConfigureAwait(false);
        db.SignInTokens.RemoveRange(expired);
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        db.SignInTokens.Add(new SignInToken { Id = ids.NewId(), UserId = userId, TokenHash = Hash(token), ExpiresAtUtc = utcNow + SignInToken.Lifetime });
        return token;
    }

    /// <summary>Ends every "keep me signed in" of the user, on every PC.</summary>
    public static async Task RevokeAllAsync(IAppDbContext db, Guid userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        db.SignInTokens.RemoveRange(await db.SignInTokens.Where(t => t.UserId == userId).ToListAsync(cancellationToken).ConfigureAwait(false));
    }

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
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
