using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SuperOfc.Application.Abstractions;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Platform;

namespace SuperOfc.Infrastructure.Backups;

/// <summary>Which backups to keep. Pure so it can be unit tested with any clock.</summary>
public static class BackupRetention
{
    public const int DailyToKeep = 30;
    public const int MonthlyToKeep = 12;
    public static readonly TimeSpan SafetyBackupAge = TimeSpan.FromDays(90);

    public sealed record Candidate(Guid Id, BackupType Type, DateTime CreatedAtUtc);

    /// <summary>
    /// Auto: keep the newest backup of each of the last 30 days that have backups, plus the first backup of each
    /// of the last 12 months. Pre-migration / pre-payroll / pre-restore: keep 90 days. Manual: keep forever.
    /// </summary>
    public static IReadOnlyList<Guid> SelectForDeletion(IEnumerable<Candidate> backups, DateTime utcNow)
    {
        var list = backups.ToList();
        var delete = new List<Guid>();

        var auto = list.Where(b => b.Type == BackupType.Auto).OrderByDescending(b => b.CreatedAtUtc).ToList();
        var keep = new HashSet<Guid>();
        foreach (var day in auto.GroupBy(b => b.CreatedAtUtc.Date).Take(DailyToKeep))
        {
            keep.Add(day.First().Id);
        }

        foreach (var month in auto.GroupBy(b => new { b.CreatedAtUtc.Year, b.CreatedAtUtc.Month }).Take(MonthlyToKeep))
        {
            keep.Add(month.OrderBy(b => b.CreatedAtUtc).First().Id);
        }

        delete.AddRange(auto.Where(b => !keep.Contains(b.Id)).Select(b => b.Id));
        delete.AddRange(list
            .Where(b => b.Type is BackupType.PreMigration or BackupType.PrePayroll or BackupType.PreRestore)
            .Where(b => utcNow - b.CreatedAtUtc > SafetyBackupAge)
            .Select(b => b.Id));
        return delete;
    }
}

/// <summary>Low-level SQLite file operations shared by the backup service and the startup migrator.</summary>
public static class SqliteFiles
{
    /// <summary>Copies a live database consistently using the SQLite online backup API.</summary>
    public static void OnlineCopy(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        using var source = new SqliteConnection($"Data Source={sourcePath};Mode=ReadOnly;Pooling=False");
        using var destination = new SqliteConnection($"Data Source={destinationPath};Pooling=False");
        source.Open();
        destination.Open();
        source.BackupDatabase(destination);
    }

    /// <summary>Returns null when the file passes PRAGMA integrity_check, otherwise the first problem.</summary>
    public static string? IntegrityProblem(string path, bool quick = false)
    {
        if (!File.Exists(path))
        {
            return "File not found.";
        }

        try
        {
            using var connection = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = quick ? "PRAGMA quick_check;" : "PRAGMA integrity_check;";
            var result = command.ExecuteScalar() as string;
            return string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase) ? null : result ?? "Unknown integrity error.";
        }
        catch (SqliteException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Overwrites the live database content with the backup (works while WAL files exist).</summary>
    public static void RestoreInto(string backupPath, string livePath)
    {
        SqliteConnection.ClearAllPools();
        using var source = new SqliteConnection($"Data Source={backupPath};Mode=ReadOnly;Pooling=False");
        using var destination = new SqliteConnection($"Data Source={livePath};Pooling=False");
        source.Open();
        destination.Open();
        source.BackupDatabase(destination);
        SqliteConnection.ClearAllPools();
    }
}

public sealed class BackupService(
    IAppEnvironment environment,
    IServiceScopeFactory scopeFactory,
    IClock clock,
    IIdGenerator ids,
    ILogger<BackupService> logger) : IBackupService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<Result<BackupInfo>> CreateBackupAsync(BackupType type, string? note = null, CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = clock.UtcNow;
            var folder = Path.Combine(environment.BackupDirectory, type.ToString().ToLowerInvariant());
            var path = Path.Combine(folder, $"superofc-{now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.db");
            if (File.Exists(path))
            {
                path = Path.Combine(folder, $"superofc-{now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)}.db");
            }

            await Task.Run(() => SqliteFiles.OnlineCopy(environment.DatabasePath, path), cancellationToken).ConfigureAwait(false);
            var problem = await Task.Run(() => SqliteFiles.IntegrityProblem(path), cancellationToken).ConfigureAwait(false);
            if (problem is not null)
            {
                logger.LogError("Backup {Path} failed verification: {Problem}", path, problem);
                File.Delete(path);
                return new Error("Backup.VerifyFailed", "The backup could not be verified and was discarded.");
            }

            var record = new BackupRecord
            {
                Id = ids.NewId(),
                FilePath = path,
                SizeBytes = new FileInfo(path).Length,
                Type = type,
                AppVersion = environment.AppVersion,
                Verified = true,
                Note = note,
            };
            await using (var scope = scopeFactory.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
                db.BackupRecords.Add(record);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            logger.LogInformation("Backup created: {Type} {Path} ({Bytes} bytes)", type, path, record.SizeBytes);
            return new BackupInfo(record.Id, record.FilePath, record.SizeBytes, record.Type, record.CreatedAtUtc, record.AppVersion, record.Verified);
        }
        catch (Exception ex) when (ex is IOException or SqliteException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Backup of type {Type} failed", type);
            return new Error("Backup.Failed", $"The backup failed: {ex.Message}");
        }
        finally
        {
            Gate.Release();
        }
    }

    public Task<Result> VerifyAsync(string filePath, CancellationToken cancellationToken = default)
        => Task.Run(
            () =>
            {
                var problem = SqliteFiles.IntegrityProblem(filePath);
                if (problem is not null)
                {
                    return Result.Failure(new Error("Backup.Invalid", $"The file is not a valid SuperOfc backup: {problem}"));
                }

                using var connection = new SqliteConnection($"Data Source={filePath};Mode=ReadOnly;Pooling=False");
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='Company';";
                var hasCompany = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
                return hasCompany
                    ? Result.Success()
                    : Result.Failure(new Error("Backup.Invalid", "The file is a database but not a SuperOfc backup."));
            },
            cancellationToken);

    public async Task<Result> RestoreAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var safety = await CreateBackupAsync(BackupType.PreRestore, $"Before restoring {Path.GetFileName(filePath)}", cancellationToken).ConfigureAwait(false);
        if (safety.IsFailure)
        {
            return Result.Failure(new Error("Backup.RestoreAborted", "Could not back up the current data first, so nothing was restored."));
        }

        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Task.Run(() => SqliteFiles.RestoreInto(filePath, environment.DatabasePath), cancellationToken).ConfigureAwait(false);
            logger.LogWarning("Database restored from {Path}", filePath);
        }
        catch (Exception ex) when (ex is IOException or SqliteException)
        {
            logger.LogError(ex, "Restore from {Path} failed", filePath);
            return Result.Failure(new Error("Backup.RestoreFailed", $"Restore failed: {ex.Message}"));
        }
        finally
        {
            Gate.Release();
        }

        // The restored file does not know about backups made after it; register them again.
        await ReconcileAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    public async Task<int> ReconcileAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(environment.BackupDirectory))
        {
            return 0;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var known = (await db.BackupRecords.Select(b => b.FilePath).ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var type in Enum.GetValues<BackupType>())
        {
            var folder = Path.Combine(environment.BackupDirectory, type.ToString().ToLowerInvariant());
            if (!Directory.Exists(folder))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(folder, "superofc-*.db").Where(f => !known.Contains(f)))
            {
                var info = new FileInfo(file);
                db.BackupRecords.Add(new BackupRecord
                {
                    Id = ids.NewId(),
                    FilePath = file,
                    SizeBytes = info.Length,
                    Type = type,
                    AppVersion = "unknown",
                    Verified = false,
                    Note = "Found on disk",
                });
                added++;
            }
        }

        if (added > 0)
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Registered {Count} backup files found on disk", added);
        }

        return added;
    }

    public async Task<int> ApplyRetentionAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var candidates = await db.BackupRecords
            .Select(b => new BackupRetention.Candidate(b.Id, b.Type, b.CreatedAtUtc))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var toDelete = BackupRetention.SelectForDeletion(candidates, clock.UtcNow).ToHashSet();
        if (toDelete.Count == 0)
        {
            return 0;
        }

        var records = await db.BackupRecords.Where(b => toDelete.Contains(b.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var record in records)
        {
            try
            {
                if (File.Exists(record.FilePath))
                {
                    File.Delete(record.FilePath);
                }

                db.BackupRecords.Remove(record);
            }
            catch (IOException ex)
            {
                logger.LogWarning(ex, "Could not delete old backup {Path}", record.FilePath);
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Backup retention removed {Count} backups", records.Count);
        return records.Count;
    }

    public async Task<bool> HasBackupTodayAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var since = clock.UtcNow.Date;
        return await db.BackupRecords.AnyAsync(b => b.Type == BackupType.Auto && b.CreatedAtUtc >= since, cancellationToken).ConfigureAwait(false);
    }
}
