using FluentValidation;
using Microsoft.EntityFrameworkCore;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Common;
using SuperOfc.DeviceBridge.Contracts;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Devices;
using SuperOfc.Domain.Security;

namespace SuperOfc.Application.Devices;

public sealed record DeviceDto(
    Guid Id,
    string Name,
    string Ip,
    int Port,
    bool HasCommKey,
    int MachineNumber,
    string? SerialNumber,
    string? Firmware,
    string? Model,
    string? TimeZoneId,
    bool IsEnabled,
    DateTime? LastSyncAtUtc,
    DeviceStatus Status,
    string? StatusMessage,
    int? ClockOffsetSeconds,
    int MappedUsers,
    int UnmappedPunches)
{
    public bool HasClockDrift => ClockOffsetSeconds is { } s && Math.Abs(s) > DeviceAccess.MaxClockDriftSeconds;

    public int ClockOffsetMinutes => (int)Math.Round((ClockOffsetSeconds ?? 0) / 60.0);

    public string Address => Port == 4370 ? Ip : $"{Ip}:{Port}";
}

public sealed record GetDevicesQuery : IQuery<IReadOnlyList<DeviceDto>>;

public sealed class GetDevicesHandler(IAppDbContext db, ICurrentUser user) : IRequestHandler<GetDevicesQuery, IReadOnlyList<DeviceDto>>
{
    public async Task<Result<IReadOnlyList<DeviceDto>>> HandleAsync(GetDevicesQuery request, CancellationToken cancellationToken)
    {
        var devices = await db.Devices.AsNoTracking().Where(d => d.CompanyId == user.CompanyId && d.DeletedAtUtc == null).OrderBy(d => d.Name)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var maps = await db.DeviceUserMaps.AsNoTracking().Where(m => m.CompanyId == user.CompanyId).GroupBy(m => m.DeviceId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken).ConfigureAwait(false);
        var unmapped = await db.UnmappedPunches.AsNoTracking().Where(p => p.CompanyId == user.CompanyId && p.DeviceId != null).GroupBy(p => p.DeviceId!.Value)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken).ConfigureAwait(false);
        return devices.Select(d => new DeviceDto(d.Id, d.Name, d.Ip, d.Port, d.CommKeyProtected is not null, d.MachineNumber, d.SerialNumber, d.Firmware, d.Model,
            d.TimeZoneId, d.IsEnabled, d.LastSyncAtUtc, d.Status, d.StatusMessage, d.ClockOffsetSeconds, maps.GetValueOrDefault(d.Id), unmapped.GetValueOrDefault(d.Id))).ToList();
    }
}

/// <summary>Adds or edits a device. <see cref="CommKey"/> null on edit keeps the stored key.</summary>
[RequiresPermission(Permission.ManageDevices)]
public sealed record SaveDeviceCommand(Guid? Id, string Name, string Ip, int Port, int? CommKey, int MachineNumber, string? TimeZoneId, bool IsEnabled) : ICommand<Guid>;

public sealed class SaveDeviceValidator : AbstractValidator<SaveDeviceCommand>
{
    public SaveDeviceValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Enter a name.").MaximumLength(100);
        RuleFor(x => x.Ip).NotEmpty().WithMessage("Enter the device IP address.").MaximumLength(100)
            .Must(ip => ip is null || (!ip.Trim().Contains(' ', StringComparison.Ordinal) && Uri.CheckHostName(ip.Trim()) != UriHostNameType.Unknown))
            .WithMessage("Enter a valid IP address or host name, for example 192.168.1.201.");
        RuleFor(x => x.Port).InclusiveBetween(1, 65535).WithMessage("The port must be between 1 and 65535 (usually 4370).");
        RuleFor(x => x.CommKey).InclusiveBetween(0, 999999).When(x => x.CommKey is not null).WithMessage("The communication key is a number from 0 to 999999.");
        RuleFor(x => x.MachineNumber).InclusiveBetween(1, 255);
        RuleFor(x => x.TimeZoneId).Must(BeKnownZone).WithMessage("Choose a time zone from the list.");
    }

    private static bool BeKnownZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return true;
        }

        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
    }
}

public sealed class SaveDeviceHandler(IAppDbContext db, ICurrentUser user, IIdGenerator ids, IAuditWriter audit, DeviceAccess access) : IRequestHandler<SaveDeviceCommand, Guid>
{
    public async Task<Result<Guid>> HandleAsync(SaveDeviceCommand request, CancellationToken cancellationToken)
    {
        var ip = request.Ip.Trim();
        var clash = await db.Devices.AnyAsync(d => d.CompanyId == user.CompanyId && d.DeletedAtUtc == null && d.Ip == ip && d.Port == request.Port && d.Id != request.Id, cancellationToken).ConfigureAwait(false);
        if (clash)
        {
            return Error.Validation(nameof(request.Ip), "Another device already uses this address.");
        }

        Device device;
        if (request.Id is { } id)
        {
            var found = await access.FindAsync(id, cancellationToken).ConfigureAwait(false);
            if (found is null)
            {
                return Error.NotFound("Device");
            }

            device = found;
        }
        else
        {
            device = new Device { Id = ids.NewId(), CompanyId = user.CompanyId };
            db.Devices.Add(device);
        }

        var addressChanged = request.Id is not null && (device.Ip != ip || device.Port != request.Port);
        device.Name = request.Name.Trim();
        device.Ip = ip;
        device.Port = request.Port;
        device.MachineNumber = request.MachineNumber;
        device.TimeZoneId = string.IsNullOrWhiteSpace(request.TimeZoneId) ? null : request.TimeZoneId;
        device.IsEnabled = request.IsEnabled;
        if (request.CommKey is { } key)
        {
            device.CommKeyProtected = access.ProtectCommKey(key);
        }

        if (addressChanged)
        {
            device.Status = DeviceStatus.Unknown;
            device.StatusMessage = null;
        }

        // The comm key is never written to the audit log.
        audit.Record(request.Id is null ? "Device.Created" : "Device.Updated", $"Device {device.Name} ({device.Ip}:{device.Port})", nameof(Device), device.Id,
            after: new { device.Name, device.Ip, device.Port, device.MachineNumber, device.TimeZoneId, device.IsEnabled });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return device.Id;
    }
}

[RequiresPermission(Permission.ManageDevices)]
public sealed record DeleteDeviceCommand(Guid Id) : ICommand<Unit>;

public sealed class DeleteDeviceHandler(IAppDbContext db, IClock clock, IAuditWriter audit, DeviceAccess access) : IRequestHandler<DeleteDeviceCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(DeleteDeviceCommand request, CancellationToken cancellationToken)
    {
        var device = await access.FindAsync(request.Id, cancellationToken).ConfigureAwait(false);
        if (device is null)
        {
            return Error.NotFound("Device");
        }

        // Punches already pulled stay; they belong to employees, not to the device.
        device.DeletedAtUtc = clock.UtcNow;
        device.IsEnabled = false;
        audit.Record("Device.Deleted", $"Device {device.Name}", nameof(Device), device.Id);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}

/// <summary>
/// Connects and reads device information. With <see cref="Id"/> the stored device is used and updated; the other
/// fields override it (for testing an edited address before saving). A null comm key uses the stored one.
/// </summary>
[RequiresPermission(Permission.ManageDevices)]
public sealed record TestDeviceConnectionCommand(Guid? Id, string Ip, int Port, int? CommKey, int MachineNumber = 1) : ICommand<DeviceInfo>;

public sealed class TestDeviceConnectionHandler(IAppDbContext db, IDeviceGateway gateway, IClock clock, DeviceAccess access) : IRequestHandler<TestDeviceConnectionCommand, DeviceInfo>
{
    public async Task<Result<DeviceInfo>> HandleAsync(TestDeviceConnectionCommand request, CancellationToken cancellationToken)
    {
        var device = request.Id is { } id ? await access.FindAsync(id, cancellationToken).ConfigureAwait(false) : null;
        if (request.Id is not null && device is null)
        {
            return Error.NotFound("Device");
        }

        var key = request.CommKey ?? (device is null ? 0 : access.CommKey(device));
        var endpoint = new DeviceEndpoint(request.Ip.Trim(), request.Port, key, request.MachineNumber);
        try
        {
            var info = await gateway.TestConnectionAsync(endpoint, cancellationToken).ConfigureAwait(false);
            if (device is not null && device.Ip == endpoint.Ip && device.Port == endpoint.Port)
            {
                device.SerialNumber = info.SerialNumber;
                device.Firmware = info.Firmware;
                device.Model = info.DeviceName;
                device.Status = DeviceStatus.Online;
                device.StatusMessage = null;
                device.ClockOffsetSeconds = ClockOffset(info.DeviceTime, access.ZoneOf(device), clock);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            return info;
        }
        catch (DeviceGatewayException ex)
        {
            if (device is not null && device.Ip == endpoint.Ip && device.Port == endpoint.Port)
            {
                DeviceAccess.MarkFailed(device, ex);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            return DeviceAccess.ToError(ex);
        }
    }

    internal static int ClockOffset(DateTime deviceLocal, TimeZoneInfo zone, IClock clock)
        => (int)Math.Round((deviceLocal - TimeZoneInfo.ConvertTimeFromUtc(clock.UtcNow, zone)).TotalSeconds);
}

/// <summary>Sets the device clock to this PC's time (in the device's time zone).</summary>
[RequiresPermission(Permission.ManageDevices)]
public sealed record SetDeviceTimeCommand(Guid Id) : ICommand<int>;

public sealed class SetDeviceTimeHandler(IAppDbContext db, IDeviceGateway gateway, IClock clock, IAuditWriter audit, DeviceAccess access) : IRequestHandler<SetDeviceTimeCommand, int>
{
    public async Task<Result<int>> HandleAsync(SetDeviceTimeCommand request, CancellationToken cancellationToken)
    {
        var device = await access.FindAsync(request.Id, cancellationToken).ConfigureAwait(false);
        if (device is null)
        {
            return Error.NotFound("Device");
        }

        var zone = access.ZoneOf(device);
        try
        {
            var now = TimeZoneInfo.ConvertTimeFromUtc(clock.UtcNow, zone);
            var after = await gateway.SetTimeAsync(access.Endpoint(device), now, cancellationToken).ConfigureAwait(false);
            var before = device.ClockOffsetSeconds;
            device.ClockOffsetSeconds = TestDeviceConnectionHandler.ClockOffset(after, zone, clock);
            device.Status = DeviceStatus.Online;
            device.StatusMessage = null;
            audit.Record("Device.TimeSet", $"Device {device.Name} clock set (was off by {before ?? 0} s)", nameof(Device), device.Id);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return device.ClockOffsetSeconds.Value;
        }
        catch (DeviceGatewayException ex)
        {
            DeviceAccess.MarkFailed(device, ex);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return DeviceAccess.ToError(ex);
        }
    }
}

public sealed record DeviceSyncLogDto(Guid Id, Guid? DeviceId, string? DeviceName, SyncTrigger Trigger, DateTime StartedAtUtc, DateTime? FinishedAtUtc, int Pulled, int Inserted, int Duplicates, int Unmapped, string? Warning, string? Error)
{
    public bool Succeeded => Error is null;

    public TimeSpan? Duration => FinishedAtUtc - StartedAtUtc;
}

public sealed record GetDeviceSyncLogQuery(Guid? DeviceId, int Take = 100) : IQuery<IReadOnlyList<DeviceSyncLogDto>>;

public sealed class GetDeviceSyncLogHandler(IAppDbContext db, ICurrentUser user) : IRequestHandler<GetDeviceSyncLogQuery, IReadOnlyList<DeviceSyncLogDto>>
{
    public async Task<Result<IReadOnlyList<DeviceSyncLogDto>>> HandleAsync(GetDeviceSyncLogQuery request, CancellationToken cancellationToken)
    {
        var names = await db.Devices.AsNoTracking().IgnoreQueryFilters().Where(d => d.CompanyId == user.CompanyId)
            .ToDictionaryAsync(d => d.Id, d => d.Name, cancellationToken).ConfigureAwait(false);
        var query = db.DeviceSyncLogs.AsNoTracking().Where(l => l.CompanyId == user.CompanyId);
        if (request.DeviceId is { } id)
        {
            query = query.Where(l => l.DeviceId == id);
        }

        var logs = await query.OrderByDescending(l => l.StartedAtUtc).Take(Math.Clamp(request.Take, 1, 1000)).ToListAsync(cancellationToken).ConfigureAwait(false);
        return logs.Select(l => new DeviceSyncLogDto(l.Id, l.DeviceId, l.DeviceId is { } d ? names.GetValueOrDefault(d) : null, l.Trigger, l.StartedAtUtc, l.FinishedAtUtc,
            l.Pulled, l.Inserted, l.Duplicates, l.Unmapped, l.Warning, l.Error)).ToList();
    }
}
