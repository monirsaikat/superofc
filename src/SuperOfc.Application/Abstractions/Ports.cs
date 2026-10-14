using SuperOfc.Domain.Common;
using SuperOfc.Domain.Organization;
using SuperOfc.Domain.Platform;
using SuperOfc.Domain.Policies;
using SuperOfc.Domain.Security;

namespace SuperOfc.Application.Abstractions;

/// <summary>Time source. Never use DateTime.Now in application or domain code.</summary>
public interface IClock
{
    DateTime UtcNow { get; }

    DateOnly TodayIn(TimeZoneInfo timeZone) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(UtcNow, timeZone));
}

public interface IIdGenerator
{
    Guid NewId();
}

/// <summary>The signed-in user for this desktop session.</summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }

    Guid? UserId { get; }

    string UserName { get; }

    string DisplayName { get; }

    Role Role { get; }

    Guid CompanyId { get; }
}

/// <summary>Mutable session used by the login flow.</summary>
public interface IUserSession : ICurrentUser
{
    event EventHandler? Changed;

    void SignIn(User user);

    void SignOut();

    /// <summary>Used by first-run and background jobs that act as the system.</summary>
    void SetCompany(Guid companyId);
}

/// <summary>Cached company profile for the running app.</summary>
public interface ICompanyContext
{
    event EventHandler? Changed;

    Company? Company { get; }

    Guid CompanyId { get; }

    TimeZoneInfo TimeZone { get; }

    Task RefreshAsync(CancellationToken cancellationToken = default);
}

public interface IAuditWriter
{
    /// <summary>Adds an audit entry to the current unit of work; saved with the handler's SaveChanges.</summary>
    void Record(string action, string summary, string? entityType = null, Guid? entityId = null, object? before = null, object? after = null);
}

public interface IPasswordHasher
{
    string Hash(string password);

    bool Verify(string hash, string password);
}

public interface IPolicyProvider
{
    Task<PolicySnapshot> GetSnapshotAsync(DateOnly date, CancellationToken cancellationToken = default);

    void Invalidate();
}

public interface IAppEnvironment
{
    string AppVersion { get; }

    string DataDirectory { get; }

    string FilesDirectory { get; }

    string BackupDirectory { get; }

    string LogDirectory { get; }

    string DatabasePath { get; }
}

public sealed record BackupInfo(Guid Id, string FilePath, long SizeBytes, BackupType Type, DateTime CreatedAtUtc, string AppVersion, bool Verified);

public interface IBackupService
{
    Task<Result<BackupInfo>> CreateBackupAsync(BackupType type, string? note = null, CancellationToken cancellationToken = default);

    Task<Result> VerifyAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>Replaces the live database with the backup. The app must restart afterwards.</summary>
    Task<Result> RestoreAsync(string filePath, CancellationToken cancellationToken = default);

    Task<int> ApplyRetentionAsync(CancellationToken cancellationToken = default);

    Task<bool> HasBackupTodayAsync(CancellationToken cancellationToken = default);

    /// <summary>Registers backup files found on disk that have no record (after a restore).</summary>
    Task<int> ReconcileAsync(CancellationToken cancellationToken = default);
}

/// <summary>Seeds demo data for evaluation; idempotent.</summary>
public interface ISampleDataSeeder
{
    Task SeedAsync(Guid companyId, CancellationToken cancellationToken = default);
}

/// <summary>Stores files (photos, documents) under the data folder.</summary>
public interface IFileStorage
{
    Task<string> SaveAsync(string category, Guid ownerId, string fileName, Stream content, CancellationToken cancellationToken = default);

    Stream OpenRead(string relativePath);

    string GetFullPath(string relativePath);

    void Delete(string relativePath);
}

/// <summary>Protects secrets at rest (DPAPI on Windows).</summary>
public interface ISecretProtector
{
    string Protect(string plain);

    string Unprotect(string protectedValue);
}
