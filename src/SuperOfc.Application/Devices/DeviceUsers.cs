using Microsoft.EntityFrameworkCore;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Common;
using SuperOfc.DeviceBridge.Contracts;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Devices;
using SuperOfc.Domain.Security;

namespace SuperOfc.Application.Devices;

public enum DeviceUserMatch
{
    None = 0,
    Mapped = 1,
    ByCode = 2,
}

public sealed record DeviceUserRow(
    string DeviceUserId,
    string NameOnDevice,
    int FingerCount,
    int Privilege,
    bool Enabled,
    Guid? EmployeeId,
    string? EmployeeCode,
    string? EmployeeName,
    DeviceUserMatch Match,
    bool HasBackup);

/// <summary>Reads users live from the device and shows which employee each one maps to.</summary>
[RequiresPermission(Permission.ManageDevices)]
public sealed record GetDeviceUsersQuery(Guid DeviceId) : IQuery<IReadOnlyList<DeviceUserRow>>;

public sealed class GetDeviceUsersHandler(IAppDbContext db, ICurrentUser user, IDeviceGateway gateway, DeviceAccess access) : IRequestHandler<GetDeviceUsersQuery, IReadOnlyList<DeviceUserRow>>
{
    public async Task<Result<IReadOnlyList<DeviceUserRow>>> HandleAsync(GetDeviceUsersQuery request, CancellationToken cancellationToken)
    {
        var device = await access.FindAsync(request.DeviceId, cancellationToken).ConfigureAwait(false);
        if (device is null)
        {
            return Error.NotFound("Device");
        }

        IReadOnlyList<DeviceUser> users;
        try
        {
            users = await gateway.GetUsersAsync(access.Endpoint(device), cancellationToken).ConfigureAwait(false);
        }
        catch (DeviceGatewayException ex)
        {
            DeviceAccess.MarkFailed(device, ex);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return DeviceAccess.ToError(ex);
        }

        var maps = await db.DeviceUserMaps.Where(m => m.DeviceId == device.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var employees = await db.Employees.AsNoTracking().Where(e => e.CompanyId == user.CompanyId && e.DeletedAtUtc == null)
            .Select(e => new { e.Id, e.Code, e.FullName }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var byId = employees.ToDictionary(e => e.Id);
        var backups = (await db.FingerprintBackups.AsNoTracking().Where(f => f.CompanyId == user.CompanyId).Select(f => f.EmployeeId).Distinct()
            .ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
        var resolver = new DeviceUserResolver(maps.Select(m => (m.DeviceUserId, m.EmployeeId)), employees.Select(e => (e.Code, e.Id)));

        var rows = new List<DeviceUserRow>(users.Count);
        foreach (var u in users.OrderBy(u => DeviceUserResolver.NumberIn(u.DeviceUserId) ?? long.MaxValue).ThenBy(u => u.DeviceUserId, StringComparer.Ordinal))
        {
            var (employeeId, isMapped) = resolver.Resolve(u.DeviceUserId);
            var employee = employeeId is { } id && byId.TryGetValue(id, out var e) ? e : null;

            // Keep the name on the device current in the map so offline screens can show it.
            if (isMapped && maps.FirstOrDefault(m => m.DeviceUserId == u.DeviceUserId) is { } map && map.NameOnDevice != u.Name)
            {
                map.NameOnDevice = u.Name;
            }

            rows.Add(new DeviceUserRow(u.DeviceUserId, u.Name, u.FingerCount, u.Privilege, u.Enabled, employee?.Id, employee?.Code, employee?.FullName,
                employee is null ? DeviceUserMatch.None : isMapped ? DeviceUserMatch.Mapped : DeviceUserMatch.ByCode, employee is not null && backups.Contains(employee.Id)));
        }

        device.Status = DeviceStatus.Online;
        device.StatusMessage = null;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return rows;
    }
}

/// <summary>Links (or with a null employee, unlinks) one device user. Linking moves that user's unmapped punches.</summary>
[RequiresPermission(Permission.ManageDevices)]
public sealed record MapDeviceUserCommand(Guid DeviceId, string DeviceUserId, Guid? EmployeeId, string? NameOnDevice = null) : ICommand<int>;

public sealed class MapDeviceUserHandler(IAppDbContext db, ICurrentUser user, IAuditWriter audit, DeviceAccess access) : IRequestHandler<MapDeviceUserCommand, int>
{
    public async Task<Result<int>> HandleAsync(MapDeviceUserCommand request, CancellationToken cancellationToken)
    {
        var device = await access.FindAsync(request.DeviceId, cancellationToken).ConfigureAwait(false);
        if (device is null)
        {
            return Error.NotFound("Device");
        }

        var deviceUserId = request.DeviceUserId.Trim();
        if (request.EmployeeId is not { } employeeId)
        {
            var existing = await db.DeviceUserMaps.FirstOrDefaultAsync(m => m.DeviceId == device.Id && m.DeviceUserId == deviceUserId, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                // Punches already stored stay with the employee; only future punches are affected.
                db.DeviceUserMaps.Remove(existing);
                audit.Record("Device.UserUnmapped", $"Device {device.Name}: user {deviceUserId} unlinked", nameof(Device), device.Id);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            return 0;
        }

        var employee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == employeeId && e.CompanyId == user.CompanyId && e.DeletedAtUtc == null, cancellationToken).ConfigureAwait(false);
        if (employee is null)
        {
            return Error.NotFound("Employee");
        }

        await access.MapAsync(device.Id, deviceUserId, employee.Id, request.NameOnDevice, cancellationToken).ConfigureAwait(false);
        audit.Record("Device.UserMapped", $"Device {device.Name}: user {deviceUserId} -> {employee.Code} {employee.FullName}", nameof(Device), device.Id);
        return await access.MoveUnmappedAsync(device.Id, deviceUserId, employee.Id, cancellationToken).ConfigureAwait(false);
    }
}

public sealed record AutoMapResult(int Mapped, int PunchesMoved);

/// <summary>Maps every device user whose id matches an employee code (or its number), then moves their unmapped punches.</summary>
[RequiresPermission(Permission.ManageDevices)]
public sealed record AutoMapDeviceUsersCommand(Guid DeviceId) : ICommand<AutoMapResult>;

public sealed class AutoMapDeviceUsersHandler(IAppDbContext db, ICurrentUser user, IDeviceGateway gateway, IAuditWriter audit, DeviceAccess access) : IRequestHandler<AutoMapDeviceUsersCommand, AutoMapResult>
{
    public async Task<Result<AutoMapResult>> HandleAsync(AutoMapDeviceUsersCommand request, CancellationToken cancellationToken)
    {
        var device = await access.FindAsync(request.DeviceId, cancellationToken).ConfigureAwait(false);
        if (device is null)
        {
            return Error.NotFound("Device");
        }

        // Users on the device plus users only seen in unmapped punches (deleted from the device since).
        var candidates = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var u in await gateway.GetUsersAsync(access.Endpoint(device), cancellationToken).ConfigureAwait(false))
            {
                candidates[u.DeviceUserId] = u.Name;
            }
        }
        catch (DeviceGatewayException ex) when (ex.IsUnreachable)
        {
            // Offline: still map what the unmapped punches tell us.
        }
        catch (DeviceGatewayException ex)
        {
            return DeviceAccess.ToError(ex);
        }

        foreach (var id in await db.UnmappedPunches.Where(p => p.DeviceId == device.Id).Select(p => p.DeviceUserId).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            candidates.TryAdd(id, null);
        }

        var maps = await db.DeviceUserMaps.AsNoTracking().Where(m => m.DeviceId == device.Id).Select(m => m.DeviceUserId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var employees = await db.Employees.AsNoTracking().Where(e => e.CompanyId == user.CompanyId && e.DeletedAtUtc == null)
            .Select(e => new { e.Code, e.Id }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var resolver = new DeviceUserResolver([], employees.Select(e => (e.Code, e.Id)));
        var mapped = new HashSet<string>(maps, StringComparer.OrdinalIgnoreCase);

        var count = 0;
        var moved = 0;
        foreach (var (deviceUserId, name) in candidates)
        {
            if (mapped.Contains(deviceUserId) || resolver.Resolve(deviceUserId).EmployeeId is not { } employeeId)
            {
                continue;
            }

            await access.MapAsync(device.Id, deviceUserId, employeeId, name, cancellationToken).ConfigureAwait(false);
            moved += await access.MoveUnmappedAsync(device.Id, deviceUserId, employeeId, cancellationToken).ConfigureAwait(false);
            count++;
        }

        if (count > 0)
        {
            audit.Record("Device.AutoMapped", $"Device {device.Name}: {count} users linked by employee code", nameof(Device), device.Id);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new AutoMapResult(count, moved);
    }
}

public sealed record PushResult(int Pushed, int FingersRestored, IReadOnlyList<string> Failed);

/// <summary>
/// Creates or updates employees as device users (name and id), restoring backed-up fingerprints when asked.
/// The device user id is the existing map, else the number in the employee code ("E0012" -> "12").
/// </summary>
[RequiresPermission(Permission.ManageDevices)]
public sealed record PushEmployeesToDeviceCommand(Guid DeviceId, IReadOnlyList<Guid> EmployeeIds, bool RestoreFingerprints) : ICommand<PushResult>;

public sealed class PushEmployeesToDeviceHandler(IAppDbContext db, ICurrentUser user, IDeviceGateway gateway, ISecretProtector secrets, IAuditWriter audit, DeviceAccess access)
    : IRequestHandler<PushEmployeesToDeviceCommand, PushResult>
{
    /// <summary>Most ZKTeco firmware shows at most 24 characters of a name.</summary>
    public const int MaxNameLength = 24;

    public async Task<Result<PushResult>> HandleAsync(PushEmployeesToDeviceCommand request, CancellationToken cancellationToken)
    {
        var device = await access.FindAsync(request.DeviceId, cancellationToken).ConfigureAwait(false);
        if (device is null)
        {
            return Error.NotFound("Device");
        }

        var ids = request.EmployeeIds.ToHashSet();
        var employees = await db.Employees.AsNoTracking().Where(e => ids.Contains(e.Id) && e.CompanyId == user.CompanyId && e.DeletedAtUtc == null)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var maps = await db.DeviceUserMaps.Where(m => m.DeviceId == device.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var backups = request.RestoreFingerprints
            ? await db.FingerprintBackups.AsNoTracking().Where(f => ids.Contains(f.EmployeeId)).ToListAsync(cancellationToken).ConfigureAwait(false)
            : [];
        var endpoint = access.Endpoint(device);
        var pushed = 0;
        var fingers = 0;
        var failed = new List<string>();
        foreach (var employee in employees.OrderBy(e => e.Code, StringComparer.OrdinalIgnoreCase))
        {
            var deviceUserId = maps.FirstOrDefault(m => m.EmployeeId == employee.Id)?.DeviceUserId
                ?? DeviceUserResolver.NumberIn(employee.Code)?.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (deviceUserId is null || maps.Any(m => m.DeviceUserId == deviceUserId && m.EmployeeId != employee.Id))
            {
                failed.Add($"{employee.Code}: no free device user id (the code needs a number).");
                continue;
            }

            var name = employee.FullName.Length > MaxNameLength ? employee.FullName[..MaxNameLength] : employee.FullName;
            try
            {
                await gateway.SetUserAsync(endpoint, new DeviceUser { DeviceUserId = deviceUserId, Name = name, Enabled = true }, cancellationToken).ConfigureAwait(false);
                var templates = backups.Where(b => b.EmployeeId == employee.Id)
                    .Select(b => new FingerTemplate { FingerIndex = b.FingerIndex, Flag = b.Flag, Data = secrets.Unprotect(b.DataProtected) }).ToList();
                if (templates.Count > 0)
                {
                    await gateway.SetTemplatesAsync(endpoint, deviceUserId, templates, cancellationToken).ConfigureAwait(false);
                    fingers += templates.Count;
                }
            }
            catch (DeviceGatewayException ex) when (!ex.IsUnreachable)
            {
                failed.Add($"{employee.Code}: {ex.Message}");
                continue;
            }
            catch (DeviceGatewayException ex)
            {
                DeviceAccess.MarkFailed(device, ex);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                return DeviceAccess.ToError(ex);
            }

            await access.MapAsync(device.Id, deviceUserId, employee.Id, name, cancellationToken).ConfigureAwait(false);
            pushed++;
        }

        audit.Record("Device.UsersPushed", $"Device {device.Name}: {pushed} employees sent, {fingers} fingerprints restored", nameof(Device), device.Id);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new PushResult(pushed, fingers, failed);
    }
}

/// <summary>Deletes a user (and their fingerprints) from the device and removes the map. Stored punches stay.</summary>
[RequiresPermission(Permission.ManageDevices)]
public sealed record DeleteDeviceUserCommand(Guid DeviceId, string DeviceUserId) : ICommand<Unit>;

public sealed class DeleteDeviceUserHandler(IAppDbContext db, IDeviceGateway gateway, IAuditWriter audit, DeviceAccess access) : IRequestHandler<DeleteDeviceUserCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(DeleteDeviceUserCommand request, CancellationToken cancellationToken)
    {
        var device = await access.FindAsync(request.DeviceId, cancellationToken).ConfigureAwait(false);
        if (device is null)
        {
            return Error.NotFound("Device");
        }

        try
        {
            await gateway.DeleteUserAsync(access.Endpoint(device), request.DeviceUserId, cancellationToken).ConfigureAwait(false);
        }
        catch (DeviceGatewayException ex)
        {
            return DeviceAccess.ToError(ex);
        }

        await db.DeviceUserMaps.Where(m => m.DeviceId == device.Id && m.DeviceUserId == request.DeviceUserId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        audit.Record("Device.UserDeleted", $"Device {device.Name}: user {request.DeviceUserId} deleted from the device", nameof(Device), device.Id);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}

public sealed record BackupResult(int Employees, int Fingers, IReadOnlyList<string> Skipped);

/// <summary>Copies fingerprint templates of mapped employees from a device into the database (encrypted with DPAPI).</summary>
[RequiresPermission(Permission.ManageDevices)]
public sealed record BackupFingerprintsCommand(Guid DeviceId, IReadOnlyList<Guid>? EmployeeIds = null) : ICommand<BackupResult>;

public sealed class BackupFingerprintsHandler(IAppDbContext db, ICurrentUser user, IIdGenerator ids, IClock clock, IDeviceGateway gateway, ISecretProtector secrets, IAuditWriter audit, DeviceAccess access)
    : IRequestHandler<BackupFingerprintsCommand, BackupResult>
{
    public async Task<Result<BackupResult>> HandleAsync(BackupFingerprintsCommand request, CancellationToken cancellationToken)
    {
        var device = await access.FindAsync(request.DeviceId, cancellationToken).ConfigureAwait(false);
        if (device is null)
        {
            return Error.NotFound("Device");
        }

        var maps = await db.DeviceUserMaps.AsNoTracking().Where(m => m.DeviceId == device.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (request.EmployeeIds is { } only)
        {
            var wanted = only.ToHashSet();
            maps = maps.Where(m => wanted.Contains(m.EmployeeId)).ToList();
        }

        var endpoint = access.Endpoint(device);
        var employees = 0;
        var fingers = 0;
        var skipped = new List<string>();
        foreach (var map in maps)
        {
            IReadOnlyList<FingerTemplate> templates;
            try
            {
                templates = await gateway.GetTemplatesAsync(endpoint, map.DeviceUserId, cancellationToken).ConfigureAwait(false);
            }
            catch (DeviceGatewayException ex) when (!ex.IsUnreachable)
            {
                skipped.Add($"{map.DeviceUserId}: {ex.Message}");
                continue;
            }
            catch (DeviceGatewayException ex)
            {
                return DeviceAccess.ToError(ex);
            }

            if (templates.Count == 0)
            {
                continue;
            }

            var existing = await db.FingerprintBackups.Where(f => f.EmployeeId == map.EmployeeId).ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var t in templates)
            {
                var row = existing.FirstOrDefault(f => f.FingerIndex == t.FingerIndex);
                if (row is null)
                {
                    row = new FingerprintBackup { Id = ids.NewId(), CompanyId = user.CompanyId, EmployeeId = map.EmployeeId, FingerIndex = t.FingerIndex };
                    db.FingerprintBackups.Add(row);
                }

                row.Flag = t.Flag;
                row.DataProtected = secrets.Protect(t.Data);
                row.SourceDeviceSerial = device.SerialNumber;
                row.CapturedAtUtc = clock.UtcNow;
                fingers++;
            }

            employees++;
        }

        audit.Record("Device.FingerprintsBackedUp", $"Device {device.Name}: {fingers} fingerprints of {employees} employees backed up", nameof(Device), device.Id);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new BackupResult(employees, fingers, skipped);
    }
}

public sealed record UnmappedGroupDto(Guid? DeviceId, string? DeviceName, string DeviceUserId, int Count, DateTime First, DateTime Last, Guid? SuggestedEmployeeId);

public sealed record GetUnmappedPunchesQuery : IQuery<IReadOnlyList<UnmappedGroupDto>>;

public sealed class GetUnmappedPunchesHandler(IAppDbContext db, ICurrentUser user) : IRequestHandler<GetUnmappedPunchesQuery, IReadOnlyList<UnmappedGroupDto>>
{
    public async Task<Result<IReadOnlyList<UnmappedGroupDto>>> HandleAsync(GetUnmappedPunchesQuery request, CancellationToken cancellationToken)
    {
        var groups = await db.UnmappedPunches.AsNoTracking().Where(p => p.CompanyId == user.CompanyId)
            .GroupBy(p => new { p.DeviceId, p.DeviceUserId })
            .Select(g => new { g.Key.DeviceId, g.Key.DeviceUserId, Count = g.Count(), First = g.Min(p => p.LocalTime), Last = g.Max(p => p.LocalTime) })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var names = await db.Devices.AsNoTracking().Where(d => d.CompanyId == user.CompanyId).ToDictionaryAsync(d => d.Id, d => d.Name, cancellationToken).ConfigureAwait(false);
        var employees = await db.Employees.AsNoTracking().Where(e => e.CompanyId == user.CompanyId && e.DeletedAtUtc == null)
            .Select(e => new { e.Code, e.Id }).ToListAsync(cancellationToken).ConfigureAwait(false);

        // Suggest only by number here; an exact code match would have been mapped automatically during sync.
        var resolver = new DeviceUserResolver([], employees.Select(e => (e.Code, e.Id)));
        return groups.OrderByDescending(g => g.Count)
            .Select(g => new UnmappedGroupDto(g.DeviceId, g.DeviceId is { } d ? names.GetValueOrDefault(d) : null, g.DeviceUserId, g.Count, g.First, g.Last, resolver.Resolve(g.DeviceUserId).EmployeeId))
            .ToList();
    }
}

/// <summary>Assigns the unmapped punches of one device user to an employee. <see cref="Remember"/> also maps future punches.</summary>
[RequiresPermission(Permission.ManageAttendance)]
public sealed record AssignUnmappedPunchesCommand(Guid? DeviceId, string DeviceUserId, Guid EmployeeId, bool Remember = true) : ICommand<int>;

public sealed class AssignUnmappedPunchesHandler(IAppDbContext db, ICurrentUser user, IAuditWriter audit, DeviceAccess access) : IRequestHandler<AssignUnmappedPunchesCommand, int>
{
    public async Task<Result<int>> HandleAsync(AssignUnmappedPunchesCommand request, CancellationToken cancellationToken)
    {
        var employee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == request.EmployeeId && e.CompanyId == user.CompanyId && e.DeletedAtUtc == null, cancellationToken).ConfigureAwait(false);
        if (employee is null)
        {
            return Error.NotFound("Employee");
        }

        if (request.Remember && request.DeviceId is { } deviceId && await access.FindAsync(deviceId, cancellationToken).ConfigureAwait(false) is not null)
        {
            await access.MapAsync(deviceId, request.DeviceUserId, employee.Id, null, cancellationToken).ConfigureAwait(false);
        }

        audit.Record("Attendance.UnmappedAssigned", $"Punches of device user {request.DeviceUserId} assigned to {employee.Code} {employee.FullName}", "Employee", employee.Id);
        return await access.MoveUnmappedAsync(request.DeviceId, request.DeviceUserId, employee.Id, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Discards the unmapped punches of one device user (for example a test user or a visitor card).</summary>
[RequiresPermission(Permission.ManageAttendance)]
public sealed record DiscardUnmappedPunchesCommand(Guid? DeviceId, string DeviceUserId) : ICommand<int>;

public sealed class DiscardUnmappedPunchesHandler(IAppDbContext db, ICurrentUser user, IAuditWriter audit) : IRequestHandler<DiscardUnmappedPunchesCommand, int>
{
    public async Task<Result<int>> HandleAsync(DiscardUnmappedPunchesCommand request, CancellationToken cancellationToken)
    {
        var count = await db.UnmappedPunches.Where(p => p.CompanyId == user.CompanyId && p.DeviceId == request.DeviceId && p.DeviceUserId == request.DeviceUserId)
            .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        audit.Record("Attendance.UnmappedDiscarded", $"{count} punches of device user {request.DeviceUserId} discarded");
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return count;
    }
}
