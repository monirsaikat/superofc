using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SuperOfc.Domain.Organization;
using SuperOfc.Domain.Platform;
using SuperOfc.Domain.Security;

namespace SuperOfc.Application.Abstractions;

/// <summary>The only persistence port. Implemented by the EF Core context in Infrastructure.</summary>
public partial interface IAppDbContext
{
    DbSet<Company> Companies { get; }

    DbSet<User> Users { get; }

    DbSet<AuditEntry> AuditEntries { get; }

    DbSet<Setting> Settings { get; }

    DbSet<PolicyVersion> PolicyVersions { get; }

    DbSet<BackupRecord> BackupRecords { get; }

    DbSet<SchemaInfo> SchemaInfos { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default);
}
