using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SuperOfc.Application.Abstractions;
using SuperOfc.Domain.Platform;
using SuperOfc.Infrastructure.Backups;
using SuperOfc.Infrastructure.Services;

namespace SuperOfc.Infrastructure.Persistence;

public enum DatabaseState
{
    Ready,
    Corrupt,
    TooNew,
    MigrationFailed,
}

public sealed record DatabaseInitResult(DatabaseState State, string? Message = null, string? PreMigrationBackup = null);

/// <summary>Seeds required reference data. Must be idempotent.</summary>
public interface IDataSeeder
{
    int Order { get; }

    Task SeedAsync(AppDbContext db, CancellationToken cancellationToken);
}

/// <summary>Implements the safe startup sequence from docs/07: check, back up, migrate, restore on failure, seed.</summary>
public sealed class DatabaseInitializer(
    AppEnvironment environment,
    IServiceScopeFactory scopeFactory,
    IClock clock,
    IIdGenerator ids,
    ILogger<DatabaseInitializer> logger)
{
    public async Task<DatabaseInitResult> InitializeAsync(CancellationToken cancellationToken = default)
    {
        environment.EnsureDirectories();
        var dbPath = environment.DatabasePath;
        var existed = File.Exists(dbPath) && new FileInfo(dbPath).Length > 0;

        if (existed)
        {
            var problem = SqliteFiles.IntegrityProblem(dbPath, quick: true);
            if (problem is not null)
            {
                logger.LogError("Database integrity check failed: {Problem}", problem);
                return new DatabaseInitResult(DatabaseState.Corrupt, problem);
            }
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var known = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
        var applied = (await db.Database.GetAppliedMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToList();
        var unknown = applied.Where(m => !known.Contains(m)).ToList();
        if (unknown.Count > 0)
        {
            logger.LogError("Database has migrations unknown to this version: {Migrations}", string.Join(", ", unknown));
            return new DatabaseInitResult(DatabaseState.TooNew, "This data was created by a newer version of SuperOfc. Please update the app.");
        }

        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToList();
        string? backupPath = null;
        if (pending.Count > 0)
        {
            if (existed)
            {
                backupPath = Path.Combine(
                    environment.BackupDirectory,
                    "premigration",
                    $"superofc-{clock.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.db");
                SqliteFiles.OnlineCopy(dbPath, backupPath);
                var backupProblem = SqliteFiles.IntegrityProblem(backupPath);
                if (backupProblem is not null)
                {
                    return new DatabaseInitResult(DatabaseState.MigrationFailed, $"Could not create a safe backup before updating your data: {backupProblem}");
                }

                logger.LogInformation("Pre-migration backup written to {Path}", backupPath);
            }

            try
            {
                await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
                logger.LogInformation("Applied {Count} migrations: {Migrations}", pending.Count, string.Join(", ", pending));
            }
            catch (Exception ex) when (ex is SqliteException or DbUpdateException or InvalidOperationException)
            {
                logger.LogError(ex, "Migration failed");
                if (backupPath is not null)
                {
                    await db.DisposeAsync().ConfigureAwait(false);
                    SqliteFiles.RestoreInto(backupPath, dbPath);
                    logger.LogWarning("Database restored from pre-migration backup after failed migration");
                }

                return new DatabaseInitResult(DatabaseState.MigrationFailed, "The update could not migrate your data. Your data is unchanged.", backupPath);
            }

            db.SchemaInfos.Add(new SchemaInfo
            {
                Id = ids.NewId(),
                AppVersion = environment.AppVersion,
                LastMigration = pending[^1],
                MigratedAtUtc = clock.UtcNow,
            });
            if (backupPath is not null)
            {
                db.BackupRecords.Add(new BackupRecord
                {
                    Id = ids.NewId(),
                    FilePath = backupPath,
                    SizeBytes = new FileInfo(backupPath).Length,
                    Type = BackupType.PreMigration,
                    AppVersion = environment.AppVersion,
                    Verified = true,
                    Note = $"Before migrating to {pending[^1]}",
                });
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var connection = new SqliteConnection(environment.ConnectionString))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL;";
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var seeder in scope.ServiceProvider.GetServices<IDataSeeder>().OrderBy(s => s.Order))
        {
            await seeder.SeedAsync(db, cancellationToken).ConfigureAwait(false);
        }

        return new DatabaseInitResult(DatabaseState.Ready, PreMigrationBackup: backupPath);
    }
}
