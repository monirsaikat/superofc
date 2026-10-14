using Microsoft.EntityFrameworkCore;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Common;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Platform;
using SuperOfc.Domain.Security;

namespace SuperOfc.Application.Backups;

[RequiresPermission(Permission.ManageBackups)]
public sealed record GetBackupsQuery : IQuery<IReadOnlyList<BackupInfo>>;

public sealed class GetBackupsHandler(IAppDbContext db) : IRequestHandler<GetBackupsQuery, IReadOnlyList<BackupInfo>>
{
    public async Task<Result<IReadOnlyList<BackupInfo>>> HandleAsync(GetBackupsQuery request, CancellationToken cancellationToken)
    {
        var rows = await db.BackupRecords.AsNoTracking()
            .OrderByDescending(b => b.CreatedAtUtc)
            .Take(500)
            .Select(b => new BackupInfo(b.Id, b.FilePath, b.SizeBytes, b.Type, b.CreatedAtUtc, b.AppVersion, b.Verified))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows;
    }
}

[RequiresPermission(Permission.ManageBackups)]
public sealed record CreateBackupCommand(string? Note) : ICommand<BackupInfo>;

public sealed class CreateBackupHandler(IBackupService backups, IAppDbContext db, IAuditWriter audit) : IRequestHandler<CreateBackupCommand, BackupInfo>
{
    public async Task<Result<BackupInfo>> HandleAsync(CreateBackupCommand request, CancellationToken cancellationToken)
    {
        var result = await backups.CreateBackupAsync(BackupType.Manual, request.Note, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            audit.Record("Backup.Created", $"Manual backup {Path.GetFileName(result.Value.FilePath)}.", nameof(BackupRecord), result.Value.Id);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return result;
    }
}

/// <summary>Restores a backup file. Admin only; the app must restart afterwards.</summary>
[RequiresPermission(Permission.ManageSettings)]
public sealed record RestoreBackupCommand(string FilePath) : ICommand<Unit>;

public sealed class RestoreBackupHandler(IBackupService backups) : IRequestHandler<RestoreBackupCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(RestoreBackupCommand request, CancellationToken cancellationToken)
    {
        if (!File.Exists(request.FilePath))
        {
            return new Error("Backup.FileMissing", "The backup file could not be found.");
        }

        var verify = await backups.VerifyAsync(request.FilePath, cancellationToken).ConfigureAwait(false);
        if (verify.IsFailure)
        {
            return Result<Unit>.Failure(verify.Errors);
        }

        var restore = await backups.RestoreAsync(request.FilePath, cancellationToken).ConfigureAwait(false);
        return restore.IsSuccess ? Unit.Value : Result<Unit>.Failure(restore.Errors);
    }
}
