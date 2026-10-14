using Microsoft.EntityFrameworkCore;
using SuperOfc.Application.Abstractions;
using SuperOfc.Domain.Attendance;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Leave;
using SuperOfc.Domain.Organization;
using SuperOfc.Domain.Policies;

namespace SuperOfc.Application.Attendance;

/// <summary>
/// Loads everything for a set of employees and dates in a few queries, runs the pure calculator and
/// upserts <see cref="AttendanceDay"/> rows. Manually corrected days are never overwritten.
/// </summary>
public sealed class AttendanceEngine(IAppDbContext db, ICurrentUser user, IPolicyProvider policies, IClock clock, IIdGenerator ids, IAttendanceDayStore store, Payroll.PayrollLocks locks)
{
    /// <summary>Recomputes and saves. Returns the number of employee-days written.</summary>
    public async Task<int> RecomputeAsync(IReadOnlyCollection<Guid>? employeeIds, DateRange range, CancellationToken cancellationToken)
        => (await RunAsync(employeeIds, range, persist: true, cancellationToken).ConfigureAwait(false)).Count;

    /// <summary>Computes without saving (for example today's live figures). Corrected days return their stored values.</summary>
    public async Task<IReadOnlyList<(Guid EmployeeId, DayResult Result)>> PreviewAsync(IReadOnlyCollection<Guid>? employeeIds, DateRange range, CancellationToken cancellationToken)
        => await RunAsync(employeeIds, range, persist: false, cancellationToken).ConfigureAwait(false);

    private async Task<List<(Guid EmployeeId, DayResult Result)>> RunAsync(IReadOnlyCollection<Guid>? employeeIds, DateRange range, bool persist, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(range.Days, 400, nameof(range));
        var companyId = user.CompanyId;
        var from = range.From;
        var to = range.To!.Value;

        var company = await db.Companies.AsNoTracking().FirstAsync(c => c.Id == companyId, cancellationToken).ConfigureAwait(false);

        var employeeQuery = db.Employees.AsNoTracking()
            .Where(e => e.CompanyId == companyId && e.DeletedAtUtc == null && e.JoinDate <= to && (e.ExitDate == null || e.ExitDate >= from));
        if (employeeIds is { Count: > 0 })
        {
            employeeQuery = employeeQuery.Where(e => employeeIds.Contains(e.Id));
        }

        var employees = await employeeQuery
            .Select(e => new { e.Id, e.JoinDate, e.ExitDate, e.LocationId, e.IsOvertimeEligible })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<(Guid EmployeeId, DayResult Result)>();
        if (employees.Count == 0)
        {
            return results;
        }

        var ids_ = employees.Select(e => e.Id).ToList();
        var shifts = await db.Shifts.AsNoTracking().Where(s => s.CompanyId == companyId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var specs = shifts.ToDictionary(s => s.Id, s => s.ToSpec());
        var defaultShiftId = shifts.Where(s => s.IsDefault && s.DeletedAtUtc == null).Select(s => (Guid?)s.Id).FirstOrDefault();

        var assignments = (await db.ShiftAssignments.AsNoTracking()
                .Where(a => a.CompanyId == companyId && ids_.Contains(a.EmployeeId) && a.EffectiveFrom <= to.AddDays(1) && (a.EffectiveTo == null || a.EffectiveTo >= from))
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToLookup(a => a.EmployeeId);

        var holidays = await db.Holidays.AsNoTracking()
            .Where(h => h.CompanyId == companyId && h.Date >= from && h.Date <= to.AddDays(1))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var calendar = new WorkCalendar(company.WorkingWeek, holidays);

        var leaves = (await db.LeaveRequests.AsNoTracking()
                .Where(r => r.CompanyId == companyId && ids_.Contains(r.EmployeeId) && r.Status == LeaveRequestStatus.Approved && r.FromDate <= to && r.ToDate >= from)
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToLookup(r => r.EmployeeId);
        var leavePaid = await db.LeaveTypes.AsNoTracking().Where(t => t.CompanyId == companyId).ToDictionaryAsync(t => t.Id, t => t.IsPaid, cancellationToken).ConfigureAwait(false);

        // Punches can belong to the previous day (night shifts) or come up to a day early (early starts).
        var punchFrom = from.AddDays(-1).ToDateTime(TimeOnly.MinValue);
        var punchTo = to.AddDays(2).ToDateTime(TimeOnly.MinValue);
        var punches = (await db.AttendancePunches.AsNoTracking()
                .Where(p => p.CompanyId == companyId && ids_.Contains(p.EmployeeId) && p.LocalTime >= punchFrom && p.LocalTime < punchTo)
                .Select(p => new { p.EmployeeId, p.LocalTime })
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToLookup(p => p.EmployeeId, p => p.LocalTime);

        var existing = (await db.AttendanceDays.AsNoTracking()
                .Where(d => d.CompanyId == companyId && ids_.Contains(d.EmployeeId) && d.Date >= from && d.Date <= to)
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(d => (d.EmployeeId, d.Date));

        var policyByDate = new Dictionary<DateOnly, AttendancePolicy>();
        foreach (var date in range.EachDay())
        {
            policyByDate[date] = (await policies.GetSnapshotAsync(date, cancellationToken).ConfigureAwait(false)).Attendance;
        }

        // Days inside finalized payroll never change (doc 03 R7); previews still show them.
        var locked = persist ? await locks.LockedRangesAsync(cancellationToken).ConfigureAwait(false) : [];

        var now = clock.UtcNow;
        var changed = new List<AttendanceDay>();
        foreach (var e in employees)
        {
            var employeeAssignments = assignments[e.Id].ToList();
            var employeePunches = punches[e.Id].OrderBy(p => p).ToList();
            var employeeLeaves = leaves[e.Id].ToList();
            ShiftSpec? SpecOn(DateOnly date)
            {
                var id = ShiftResolver.Resolve(employeeAssignments, date, defaultShiftId);
                return id is { } shiftId && specs.TryGetValue(shiftId, out var spec) ? spec : null;
            }

            foreach (var date in range.EachDay())
            {
                if (locked.Count > 0 && locked.Any(r => r.Contains(date)))
                {
                    continue;
                }

                if (existing.TryGetValue((e.Id, date), out var day) && day.IsManuallyCorrected)
                {
                    if (!persist)
                    {
                        results.Add((e.Id, new DayResult(date, day.ShiftId, day.Status, day.FirstIn, day.LastOut, day.PunchCount, day.WorkedMinutes, day.LateMinutes,
                            day.EarlyLeaveMinutes, day.OvertimeMinutes, day.MissingPunch, day.LeaveDays, day.LeaveTypeId, day.LeaveIsPaid)));
                    }

                    continue;
                }

                var policy = policyByDate[date];
                var spec = SpecOn(date);
                var (windowFrom, windowTo) = AttendanceCalculator.Window(date, spec, SpecOn(date.AddDays(1)), policy);
                var dayPunches = employeePunches.Where(p => p >= windowFrom && p < windowTo).ToList();

                DayLeave? leave = null;
                foreach (var request in employeeLeaves)
                {
                    var days = request.DaysOn(date, calendar, e.LocationId);
                    if (days > 0)
                    {
                        leave = new DayLeave(request.LeaveTypeId, Math.Min(1m, (leave?.Days ?? 0m) + days), leavePaid.GetValueOrDefault(request.LeaveTypeId, true));
                    }
                }

                var result = AttendanceCalculator.Compute(
                    new DayInput(date, e.JoinDate, e.ExitDate, e.IsOvertimeEligible, spec, calendar.GetDayKind(date, e.LocationId), leave, dayPunches),
                    policy);

                results.Add((e.Id, result));
                if (!persist || (day is not null && Same(day, result)))
                {
                    continue;
                }

                var row = new AttendanceDay { Id = day?.Id ?? ids.NewId(), CompanyId = companyId, EmployeeId = e.Id, Date = date, ComputedAtUtc = now };
                row.Apply(result);
                changed.Add(row);
            }
        }

        if (persist)
        {
            await store.UpsertAsync(changed, cancellationToken).ConfigureAwait(false);
        }

        return results;
    }

    /// <summary>Unchanged days are not rewritten, so recomputing is cheap and row versions stay stable.</summary>
    private static bool Same(AttendanceDay d, DayResult r)
        => d.ShiftId == r.ShiftId && d.Status == r.Status && d.FirstIn == r.FirstIn && d.LastOut == r.LastOut && d.PunchCount == r.PunchCount
           && d.WorkedMinutes == r.WorkedMinutes && d.LateMinutes == r.LateMinutes && d.EarlyLeaveMinutes == r.EarlyLeaveMinutes
           && d.OvertimeMinutes == r.OvertimeMinutes && d.MissingPunch == r.MissingPunch && d.LeaveDays == r.LeaveDays
           && d.LeaveTypeId == r.LeaveTypeId && d.LeaveIsPaid == r.LeaveIsPaid;
}
