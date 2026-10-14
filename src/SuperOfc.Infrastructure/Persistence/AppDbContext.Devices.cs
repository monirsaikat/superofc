using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SuperOfc.Domain.Devices;
using SuperOfc.Domain.Employees;
using SuperOfc.Infrastructure.Persistence.Converters;

namespace SuperOfc.Infrastructure.Persistence;

public partial class AppDbContext
{
    public DbSet<Device> Devices => Set<Device>();

    public DbSet<DeviceUserMap> DeviceUserMaps => Set<DeviceUserMap>();

    public DbSet<UnmappedPunch> UnmappedPunches => Set<UnmappedPunch>();

    public DbSet<DeviceSyncLog> DeviceSyncLogs => Set<DeviceSyncLog>();

    public DbSet<FingerprintBackup> FingerprintBackups => Set<FingerprintBackup>();
}

internal sealed class DeviceConfiguration : IEntityTypeConfiguration<Device>
{
    public void Configure(EntityTypeBuilder<Device> builder)
    {
        builder.Property(d => d.Name).HasMaxLength(100).IsRequired();
        builder.Property(d => d.Ip).HasMaxLength(100).IsRequired();
        builder.Property(d => d.CommKeyProtected).HasMaxLength(1000);
        builder.Property(d => d.SerialNumber).HasMaxLength(100);
        builder.Property(d => d.Firmware).HasMaxLength(100);
        builder.Property(d => d.Model).HasMaxLength(100);
        builder.Property(d => d.TimeZoneId).HasMaxLength(100);
        builder.Property(d => d.StatusMessage).HasMaxLength(500);
        builder.Property(d => d.LastSyncLocal).HasConversion<NullableLocalDateTimeConverter>();
        builder.Ignore(d => d.NextPullFromLocal);
        builder.HasIndex(d => new { d.CompanyId, d.Name });
    }
}

internal sealed class DeviceUserMapConfiguration : IEntityTypeConfiguration<DeviceUserMap>
{
    public void Configure(EntityTypeBuilder<DeviceUserMap> builder)
    {
        builder.Property(m => m.DeviceUserId).HasMaxLength(50).IsRequired();
        builder.Property(m => m.NameOnDevice).HasMaxLength(100);
        builder.HasOne<Device>().WithMany().HasForeignKey(m => m.DeviceId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Employee>().WithMany().HasForeignKey(m => m.EmployeeId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(m => new { m.DeviceId, m.DeviceUserId }).IsUnique();
        builder.HasIndex(m => m.EmployeeId);
    }
}

internal sealed class UnmappedPunchConfiguration : IEntityTypeConfiguration<UnmappedPunch>
{
    public void Configure(EntityTypeBuilder<UnmappedPunch> builder)
    {
        builder.Property(p => p.DeviceUserId).HasMaxLength(50).IsRequired();
        builder.Property(p => p.DedupKey).HasMaxLength(200).IsRequired();
        builder.Property(p => p.LocalTime).HasConversion<LocalDateTimeConverter>();
        builder.HasIndex(p => p.DedupKey).IsUnique();
        builder.HasIndex(p => new { p.DeviceId, p.DeviceUserId });
    }
}

internal sealed class DeviceSyncLogConfiguration : IEntityTypeConfiguration<DeviceSyncLog>
{
    public void Configure(EntityTypeBuilder<DeviceSyncLog> builder)
    {
        builder.Property(l => l.Warning).HasMaxLength(500);
        builder.Property(l => l.Error).HasMaxLength(1000);
        builder.Ignore(l => l.Succeeded);
        builder.HasIndex(l => new { l.DeviceId, l.StartedAtUtc });
    }
}

internal sealed class FingerprintBackupConfiguration : IEntityTypeConfiguration<FingerprintBackup>
{
    public void Configure(EntityTypeBuilder<FingerprintBackup> builder)
    {
        builder.Property(f => f.DataProtected).IsRequired();
        builder.Property(f => f.SourceDeviceSerial).HasMaxLength(100);
        builder.HasOne<Employee>().WithMany().HasForeignKey(f => f.EmployeeId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(f => new { f.EmployeeId, f.FingerIndex }).IsUnique();
    }
}
