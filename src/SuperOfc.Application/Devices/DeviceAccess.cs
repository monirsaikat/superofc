using Microsoft.EntityFrameworkCore;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Attendance;
using SuperOfc.Domain.Attendance;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Devices;

namespace SuperOfc.Application.Devices;

/// <summary>Shared helpers for device handlers: endpoints, time zones, errors and moving unmapped punches.</summary>
public sealed class DeviceAccess(IAppDbContext db, ICurrentUser user, ISecretProtector secrets, ICompanyContext company, IIdGenerator ids, AttendanceEngine engine)
{
    /// <summary>Drift above this is reported as a warning (docs/06 step 7).</summary>
    public const int MaxClockDriftSeconds = 120;

    public Task<Device?> FindAsync(Guid id, CancellationToken cancellationToken)
        => db.Devices.FirstOrDefaultAsync(d => d.Id == id && d.CompanyId == user.CompanyId && d.DeletedAtUtc == null, cancellationToken);

    public DeviceEndpoint Endpoint(Device device) => new(device.Ip, device.Port, CommKey(device), device.MachineNumber);

    public int CommKey(Device device)
        => device.CommKeyProtected is { Length: > 0 } p && int.TryParse(secrets.Unprotect(p), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var key) ? key : 0;

    public string? ProtectCommKey(int key) => key == 0 ? null : secrets.Protect(key.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public TimeZoneInfo ZoneOf(Device device)
    {
        if (device.TimeZoneId is { Length: > 0 } id)
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return company.TimeZone;
    }

    public static Error ToError(DeviceGatewayException ex) => new(ex.Code, ex.Message);

    public static void MarkFailed(Device device, DeviceGatewayException ex)
    {
        device.Status = ex.IsUnreachable ? DeviceStatus.Offline : DeviceStatus.Error;
        device.StatusMessage = ex.Message;
    }

    /// <summary>
    /// Turns the unmapped punches of one device user into attendance punches for <paramref name="employeeId"/>
    /// and recomputes the affected days. Returns the number moved. Saves changes.
    /// </summary>
    public async Task<int> MoveUnmappedAsync(Guid? deviceId, string deviceUserId, Guid employeeId, CancellationToken cancellationToken)
    {
        var punches = await db.UnmappedPunches
            .Where(p => p.CompanyId == user.CompanyId && p.DeviceId == deviceId && p.DeviceUserId == deviceUserId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (punches.Count == 0)
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }

        foreach (var p in punches)
        {
            db.AttendancePunches.Add(new AttendancePunch
            {
                Id = ids.NewId(), CompanyId = p.CompanyId, EmployeeId = employeeId, DeviceId = p.DeviceId, LocalTime = p.LocalTime, PunchAtUtc = p.PunchAtUtc,
                Type = PunchTypeOf(p.InOutMode), Source = p.Source, DedupKey = p.DedupKey, DeviceUserId = p.DeviceUserId,
            });
            db.UnmappedPunches.Remove(p);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var first = DateOnly.FromDateTime(punches.Min(p => p.LocalTime)).AddDays(-1);
        var last = DateOnly.FromDateTime(punches.Max(p => p.LocalTime));
        await RecomputeAsync([employeeId], first, last, cancellationToken).ConfigureAwait(false);
        return punches.Count;
    }

    /// <summary>Recomputes in chunks of at most a year (the engine's range limit).</summary>
    public async Task RecomputeAsync(IReadOnlyCollection<Guid> employees, DateOnly first, DateOnly last, CancellationToken cancellationToken)
    {
        for (var start = first; start <= last; start = start.AddDays(366))
        {
            var end = start.AddDays(365) < last ? start.AddDays(365) : last;
            await engine.RecomputeAsync(employees, new DateRange(start, end), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Adds or replaces the explicit map of one device user.</summary>
    public async Task MapAsync(Guid deviceId, string deviceUserId, Guid employeeId, string? nameOnDevice, CancellationToken cancellationToken)
    {
        var map = await db.DeviceUserMaps.FirstOrDefaultAsync(m => m.DeviceId == deviceId && m.DeviceUserId == deviceUserId, cancellationToken).ConfigureAwait(false);
        if (map is null)
        {
            db.DeviceUserMaps.Add(new DeviceUserMap { Id = ids.NewId(), CompanyId = user.CompanyId, DeviceId = deviceId, DeviceUserId = deviceUserId, EmployeeId = employeeId, NameOnDevice = nameOnDevice });
        }
        else
        {
            map.EmployeeId = employeeId;
            map.NameOnDevice = nameOnDevice ?? map.NameOnDevice;
        }
    }

    /// <summary>ZKTeco in/out status: 0 check-in, 1 check-out, 2 break-out, 3 break-in, 4 OT-in, 5 OT-out.</summary>
    public static PunchType PunchTypeOf(int inOutMode) => inOutMode switch
    {
        0 or 3 or 4 => PunchType.In,
        1 or 2 or 5 => PunchType.Out,
        _ => PunchType.Unknown,
    };
}
