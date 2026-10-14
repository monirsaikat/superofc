using SuperOfc.Domain.Common;

namespace SuperOfc.Domain.Security;

public enum Role
{
    Viewer = 0,
    Accounts = 1,
    HR = 2,
    Admin = 3,
}

public sealed class User : CompanyEntity
{
    public string Username { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string PasswordHash { get; set; } = string.Empty;

    public Role Role { get; set; } = Role.Viewer;

    public bool IsActive { get; set; } = true;

    public DateTime? LastLoginUtc { get; set; }

    public int FailedAttempts { get; set; }

    public DateTime? LockoutUntilUtc { get; set; }

    public bool IsLockedOut(DateTime utcNow) => LockoutUntilUtc is not null && LockoutUntilUtc > utcNow;

    /// <summary>Record a failed login; locks the account after <paramref name="maxAttempts"/>.</summary>
    public void RegisterFailedLogin(DateTime utcNow, int maxAttempts, TimeSpan lockout)
    {
        FailedAttempts++;
        if (FailedAttempts >= maxAttempts)
        {
            LockoutUntilUtc = utcNow + lockout;
            FailedAttempts = 0;
        }
    }

    public void RegisterSuccessfulLogin(DateTime utcNow)
    {
        FailedAttempts = 0;
        LockoutUntilUtc = null;
        LastLoginUtc = utcNow;
    }
}
