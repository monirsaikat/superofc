using SuperOfc.Domain.Common;

namespace SuperOfc.Domain.Security;

/// <summary>
/// "Keep me signed in on this PC": the SHA-256 hash of a random token whose plain value only the PC that signed in
/// holds (DPAPI-encrypted for the Windows user). Removed on sign-out, password change or reset, and deactivation.
/// </summary>
public sealed class SignInToken : Entity
{
    /// <summary>How long a token lives without being used; each automatic sign-in renews it.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    public Guid UserId { get; set; }

    /// <summary>Lowercase hex SHA-256 of the token.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public DateTime ExpiresAtUtc { get; set; }

    public DateTime? LastUsedAtUtc { get; set; }

    public bool IsExpired(DateTime utcNow) => ExpiresAtUtc <= utcNow;
}
