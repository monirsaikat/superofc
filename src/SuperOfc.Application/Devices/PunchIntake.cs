using Microsoft.EntityFrameworkCore;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Attendance;
using SuperOfc.Domain.Attendance;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Devices;

namespace SuperOfc.Application.Devices;

/// <summary>One raw punch from a device or a file, before mapping.</summary>
public sealed record RawPunch(string DeviceUserId, DateTime LocalTime, int VerifyMode, int InOutMode, string DedupKey);

public sealed record IntakeResult(int Received, int Inserted, int Duplicates, int Unmapped, IReadOnlyCollection<Guid> Employees, DateOnly? FirstDate, DateOnly? LastDate);

/// <summary>
/// The shared path for device sync and file import (docs/06 sync steps 2-5): dedup, map to employees
/// (explicit map, then code, then number), store punches or unmapped punches, then recompute affected days.
/// </summary>
public sealed class PunchIntake(IAppDbContext db, ICurrentUser user, IIdGenerator ids, AttendanceEngine engine, IClock clock, ICompanyContext company)
{
    private DeviceUserResolver? _resolver;
    private readonly HashSet<Guid> _employees = [];
    private DateOnly? _first;
    private DateOnly? _last;
    private int _received;
    private int _inserted;
    private int _duplicates;
    private int _unmapped;

    /// <summary>Prepares the resolver for one device (null for imports keyed by employee code).</summary>
    public async Task BeginAsync(Guid? deviceId, CancellationToken cancellationToken)
    {
        var maps = deviceId is { } id
            ? await db.DeviceUserMaps.AsNoTracking().Where(m => m.DeviceId == id).Select(m => new { m.DeviceUserId, m.EmployeeId }).ToListAsync(cancellationToken).ConfigureAwait(false)
            : [];
        var employees = await db.Employees.AsNoTracking().Where(e => e.CompanyId == user.CompanyId && e.DeletedAtUtc == null)
            .Select(e => new { e.Code, e.Id }).ToListAsync(cancellationToken).ConfigureAwait(false);
        _resolver = new DeviceUserResolver(maps.Select(m => (m.DeviceUserId, m.EmployeeId)), employees.Select(e => (e.Code, e.Id)));
    }

    /// <summary>Stores one batch in its own transaction (what was stored stays stored if a later batch fails).</summary>
    public async Task AddBatchAsync(Guid? deviceId, IReadOnlyList<RawPunch> batch, PunchSource source, TimeZoneInfo deviceZone, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var resolver = _resolver ?? throw new InvalidOperationException("Call BeginAsync first.");
        _received += batch.Count;
        var unique = batch.GroupBy(p => p.DedupKey, StringComparer.Ordinal).Select(g => g.First()).ToList();
        _duplicates += batch.Count - unique.Count;
        var keys = unique.Select(p => p.DedupKey).ToList();
        var known = new HashSet<string>(await db.AttendancePunches.AsNoTracking().Where(p => keys.Contains(p.DedupKey)).Select(p => p.DedupKey).ToListAsync(cancellationToken).ConfigureAwait(false), StringComparer.Ordinal);
        known.UnionWith(await db.UnmappedPunches.AsNoTracking().Where(p => keys.Contains(p.DedupKey)).Select(p => p.DedupKey).ToListAsync(cancellationToken).ConfigureAwait(false));

        foreach (var punch in unique)
        {
            if (known.Contains(punch.DedupKey))
            {
                _duplicates++;
                continue;
            }

            var local = DateTime.SpecifyKind(punch.LocalTime, DateTimeKind.Unspecified);
            var utc = TimeZoneInfo.ConvertTimeToUtc(local, deviceZone);

            // Store in company local time (the device may be in another zone).
            var companyLocal = TimeZoneInfo.ConvertTimeFromUtc(utc, company.TimeZone);
            var (employeeId, isMapped) = resolver.Resolve(punch.DeviceUserId);
            if (employeeId is null)
            {
                db.UnmappedPunches.Add(new UnmappedPunch
                {
                    Id = ids.NewId(), CompanyId = user.CompanyId, DeviceId = deviceId, DeviceUserId = punch.DeviceUserId, LocalTime = companyLocal, PunchAtUtc = utc,
                    DedupKey = punch.DedupKey, VerifyMode = punch.VerifyMode, InOutMode = punch.InOutMode, Source = source,
                });
                _unmapped++;
                continue;
            }

            if (!isMapped && deviceId is { } device)
            {
                // Remember automatic matches so the users page shows them.
                db.DeviceUserMaps.Add(new DeviceUserMap { Id = ids.NewId(), CompanyId = user.CompanyId, DeviceId = device, DeviceUserId = punch.DeviceUserId, EmployeeId = employeeId.Value });
                resolver.Remember(punch.DeviceUserId, employeeId.Value);
            }

            db.AttendancePunches.Add(new AttendancePunch
            {
                Id = ids.NewId(), CompanyId = user.CompanyId, EmployeeId = employeeId.Value, DeviceId = deviceId, LocalTime = companyLocal, PunchAtUtc = utc,
                Type = DeviceAccess.PunchTypeOf(punch.InOutMode),
                Source = source, DedupKey = punch.DedupKey, DeviceUserId = punch.DeviceUserId,
            });
            _inserted++;
            _employees.Add(employeeId.Value);
            var date = DateOnly.FromDateTime(companyLocal);
            _first = _first is null || date < _first ? date : _first;
            _last = _last is null || date > _last ? date : _last;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Recomputes only the employees and dates that received punches (one day earlier for night shifts).</summary>
    public async Task<IntakeResult> FinishAsync(CancellationToken cancellationToken)
    {
        if (_employees.Count > 0 && _first is { } first && _last is { } last)
        {
            var today = clock.TodayIn(company.TimeZone);
            var from = first.AddDays(-1);
            var to = last < today ? last : today;
            for (var start = from; start <= to; start = start.AddDays(366))
            {
                var end = start.AddDays(365) < to ? start.AddDays(365) : to;
                await engine.RecomputeAsync(_employees, new DateRange(start, end), cancellationToken).ConfigureAwait(false);
            }
        }

        return new IntakeResult(_received, _inserted, _duplicates, _unmapped, _employees, _first, _last);
    }
}
