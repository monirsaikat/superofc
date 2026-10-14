using System.Globalization;
using Microsoft.Data.Sqlite;
using SuperOfc.Domain.Common;
using SuperOfc.Infrastructure.Backups;
using SuperOfc.Infrastructure.Services;

namespace SuperOfc.Infrastructure.Persistence;

/// <summary>Recovery when the live database cannot be opened (startup error screen).</summary>
public static class DatabaseRecovery
{
    /// <summary>Moves the damaged files aside and puts a verified backup in place. The app must restart.</summary>
    public static Result ReplaceWithBackup(AppEnvironment environment, string backupPath)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var problem = SqliteFiles.IntegrityProblem(backupPath);
        if (problem is not null)
        {
            return Result.Failure(new Error("Backup.Invalid", $"The selected file is not a valid backup: {problem}"));
        }

        SqliteConnection.ClearAllPools();
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var quarantine = Path.Combine(environment.BackupDirectory, "damaged", stamp);
        Directory.CreateDirectory(quarantine);
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            var file = environment.DatabasePath + suffix;
            if (File.Exists(file))
            {
                File.Move(file, Path.Combine(quarantine, Path.GetFileName(file)));
            }
        }

        File.Copy(backupPath, environment.DatabasePath);
        return Result.Success();
    }
}
