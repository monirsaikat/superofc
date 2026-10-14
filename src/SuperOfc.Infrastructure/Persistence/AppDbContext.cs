using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SuperOfc.Application.Abstractions;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Organization;
using SuperOfc.Domain.Platform;
using SuperOfc.Domain.Security;
using SuperOfc.Infrastructure.Persistence.Converters;

namespace SuperOfc.Infrastructure.Persistence;

/// <summary>EF Core context. Module-specific DbSets live in partial files next to their configurations.</summary>
public partial class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IAppDbContext
{
    public DbSet<Company> Companies => Set<Company>();

    public DbSet<User> Users => Set<User>();

    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    public DbSet<Setting> Settings => Set<Setting>();

    public DbSet<PolicyVersion> PolicyVersions => Set<PolicyVersion>();

    public DbSet<BackupRecord> BackupRecords => Set<BackupRecord>();

    public DbSet<SchemaInfo> SchemaInfos => Set<SchemaInfo>();

    public Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
        => Database.BeginTransactionAsync(cancellationToken);

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);
        configurationBuilder.Properties<Money>().HaveConversion<MoneyConverter>();
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<NullableUtcDateTimeConverter>();
        configurationBuilder.Properties<string>().HaveMaxLength(4000);

        // Enums are stored as their names: readable in the file and safe against reordering.
        foreach (var enumType in typeof(Entity).Assembly.GetTypes().Where(t => t.IsEnum))
        {
            configurationBuilder.Properties(enumType).HaveConversion<string>().HaveMaxLength(40);
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes().Where(t => typeof(Entity).IsAssignableFrom(t.ClrType)))
        {
            var builder = modelBuilder.Entity(entityType.ClrType);
            builder.ToTable(entityType.ClrType.Name);
            builder.Property(nameof(Entity.Id)).ValueGeneratedNever();
            builder.Property(nameof(Entity.RowVersion)).IsConcurrencyToken();
            builder.Property(nameof(Entity.CreatedBy)).HasMaxLength(100);
            builder.Property(nameof(Entity.ModifiedBy)).HasMaxLength(100);
            if (typeof(CompanyEntity).IsAssignableFrom(entityType.ClrType))
            {
                builder.HasIndex(nameof(CompanyEntity.CompanyId));
            }
        }
    }
}
