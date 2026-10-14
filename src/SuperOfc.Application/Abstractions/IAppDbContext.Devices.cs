using Microsoft.EntityFrameworkCore;
using SuperOfc.Domain.Devices;

namespace SuperOfc.Application.Abstractions;

public partial interface IAppDbContext
{
    DbSet<Device> Devices { get; }

    DbSet<DeviceUserMap> DeviceUserMaps { get; }

    DbSet<UnmappedPunch> UnmappedPunches { get; }

    DbSet<DeviceSyncLog> DeviceSyncLogs { get; }

    DbSet<FingerprintBackup> FingerprintBackups { get; }
}
