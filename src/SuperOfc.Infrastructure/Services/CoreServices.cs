using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Policies;
using SuperOfc.Domain.Organization;
using SuperOfc.Domain.Platform;
using SuperOfc.Domain.Policies;
using SuperOfc.Domain.Security;

namespace SuperOfc.Infrastructure.Services;

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}

public sealed class GuidV7Generator : IIdGenerator
{
    public Guid NewId() => Guid.CreateVersion7();
}

public sealed class UserSession : IUserSession
{
    private readonly Lock _gate = new();
    private User? _user;
    private Guid _companyId;

    public event EventHandler? Changed;

    public bool IsAuthenticated => _user is not null;

    public Guid? UserId => _user?.Id;

    public string UserName => _user?.Username ?? "system";

    public string DisplayName => _user?.DisplayName ?? string.Empty;

    public Role Role => _user?.Role ?? Role.Viewer;

    public Guid CompanyId => _companyId;

    public void SignIn(User user)
    {
        ArgumentNullException.ThrowIfNull(user);
        lock (_gate)
        {
            _user = new User { Id = user.Id, Username = user.Username, DisplayName = user.DisplayName, Role = user.Role, CompanyId = user.CompanyId };
            _companyId = user.CompanyId;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SignOut()
    {
        lock (_gate)
        {
            _user = null;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetCompany(Guid companyId)
    {
        lock (_gate)
        {
            _companyId = companyId;
        }
    }
}

public sealed class CompanyContext(IServiceScopeFactory scopeFactory, IUserSession session) : ICompanyContext
{
    private Company? _company;

    public event EventHandler? Changed;

    public Company? Company => _company;

    public Guid CompanyId => _company?.Id ?? session.CompanyId;

    public TimeZoneInfo TimeZone
    {
        get
        {
            if (_company is null)
            {
                return TimeZoneInfo.Local;
            }

            return TimeZoneInfo.TryFindSystemTimeZoneById(_company.TimeZoneId, out var tz) ? tz : TimeZoneInfo.Local;
        }
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var id = session.CompanyId;
        _company = id == Guid.Empty
            ? await db.Companies.AsNoTracking().OrderBy(c => c.CreatedAtUtc).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            : await db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken).ConfigureAwait(false);
        if (_company is not null && session.CompanyId == Guid.Empty)
        {
            session.SetCompany(_company.Id);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}

public sealed class AuditWriter(IAppDbContext db, IClock clock, ICurrentUser currentUser, IIdGenerator ids) : IAuditWriter
{
    private static readonly JsonSerializerOptions JsonOptions = PolicyJson.Options;

    public void Record(string action, string summary, string? entityType = null, Guid? entityId = null, object? before = null, object? after = null)
    {
        db.AuditEntries.Add(new AuditEntry
        {
            Id = ids.NewId(),
            CompanyId = currentUser.CompanyId,
            UserName = currentUser.UserName,
            AtUtc = clock.UtcNow,
            Action = action,
            Summary = summary,
            EntityType = entityType,
            EntityId = entityId,
            BeforeJson = ToJson(before),
            AfterJson = ToJson(after),
        });
    }

    private static string? ToJson(object? value) => value switch
    {
        null => null,
        string s => s,
        _ => JsonSerializer.Serialize(value, value.GetType(), JsonOptions),
    };
}

/// <summary>PBKDF2 hashing from ASP.NET Core Identity (V3 format, 100k+ iterations).</summary>
public sealed class IdentityPasswordHasher : IPasswordHasher
{
    private static readonly PasswordHasher<User> Inner = new();
    private static readonly User Dummy = new();

    public string Hash(string password) => Inner.HashPassword(Dummy, password);

    public bool Verify(string hash, string password)
    {
        if (string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(password))
        {
            return false;
        }

        try
        {
            return Inner.VerifyHashedPassword(Dummy, hash, password) != PasswordVerificationResult.Failed;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>Caches policy versions per company and resolves the snapshot effective on a date.</summary>
public sealed class PolicyProvider(IServiceScopeFactory scopeFactory, ICurrentUser currentUser) : IPolicyProvider
{
    private readonly Lock _gate = new();
    private Dictionary<Guid, List<PolicyVersion>> _cache = [];

    public async Task<PolicySnapshot> GetSnapshotAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        var companyId = currentUser.CompanyId;
        List<PolicyVersion>? versions;
        lock (_gate)
        {
            _cache.TryGetValue(companyId, out versions);
        }

        if (versions is null)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
            versions = await db.PolicyVersions.AsNoTracking().Where(p => p.CompanyId == companyId).ToListAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _cache[companyId] = versions;
            }
        }

        return Build(versions, date);
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _cache = [];
        }
    }

    public static PolicySnapshot Build(IEnumerable<PolicyVersion> versions, DateOnly date)
    {
        var effective = versions
            .Where(v => v.EffectiveFrom <= date)
            .GroupBy(v => v.Section)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(v => v.EffectiveFrom).First().Json);

        T Read<T>(string section)
            where T : new()
            => effective.TryGetValue(section, out var json) ? PolicyJson.Deserialize<T>(json) : new T();

        return new PolicySnapshot(
            Read<AttendancePolicy>(PolicySnapshot.AttendanceSection),
            Read<PayrollPolicy>(PolicySnapshot.PayrollSection),
            Read<LoanPolicy>(PolicySnapshot.LoanSection),
            Read<GeneralPolicy>(PolicySnapshot.GeneralSection));
    }
}

/// <summary>Encrypts secrets with DPAPI (machine scope, so every Windows user of the PC can use shared data).</summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SuperOfc.Secrets.v1");

    public string Protect(string plain)
    {
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.LocalMachine);
        return Convert.ToBase64String(bytes);
    }

    public string Unprotect(string protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue))
        {
            return string.Empty;
        }

        var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), Entropy, DataProtectionScope.LocalMachine);
        return Encoding.UTF8.GetString(bytes);
    }
}

/// <summary>
/// Keeps the "stay signed in" token in <c>&lt;data root&gt;\signin\&lt;Windows user SID&gt;.dat</c>, encrypted with DPAPI in
/// user scope: only the same Windows user on the same PC can read it, even though the data folder is shared.
/// </summary>
public sealed class DpapiSignInTokenStore(AppEnvironment environment) : ISignInTokenStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SuperOfc.SignIn.v1");

    private string FilePath
    {
        get
        {
            var user = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
            return Path.Combine(environment.Root, "signin", user + ".dat");
        }
    }

    public string? Load()
    {
        try
        {
            var path = FilePath;
            return File.Exists(path)
                ? Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser))
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return null;
        }
    }

    public void Save(string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        var path = FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.CurrentUser));
    }

    public void Clear()
    {
        try
        {
            File.Delete(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The token is also removed from the database, so a leftover file no longer signs anyone in.
        }
    }
}

/// <summary>Stores files under the data folder with path-traversal protection.</summary>
public sealed class LocalFileStorage(IAppEnvironment environment) : IFileStorage
{
    public async Task<string> SaveAsync(string category, Guid ownerId, string fileName, Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var safeCategory = Sanitize(category);
        var safeName = Sanitize(Path.GetFileName(fileName));
        var relative = Path.Combine(safeCategory, ownerId.ToString("N"), $"{Guid.CreateVersion7():N}_{safeName}");
        var full = GetFullPath(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await using var file = File.Create(full);
        await content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
        return relative;
    }

    public Stream OpenRead(string relativePath) => File.OpenRead(GetFullPath(relativePath));

    public string GetFullPath(string relativePath)
    {
        var root = Path.GetFullPath(environment.FilesDirectory);
        var full = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("The file path is outside the data folder.");
        }

        return full;
    }

    public void Delete(string relativePath)
    {
        var full = GetFullPath(relativePath);
        if (File.Exists(full))
        {
            File.Delete(full);
        }
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim('.', ' ');
        return string.IsNullOrEmpty(cleaned) ? "file" : cleaned;
    }
}
