using System.Globalization;
using System.Text.Json;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Common;
using SuperOfc.Application.Payroll;
using SuperOfc.Domain.Attendance;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Organization;
using SuperOfc.Domain.Security;

namespace SuperOfc.Application.Attendance;

// ---------- Punches ----------

public sealed record PunchDto(Guid Id, Guid EmployeeId, string EmployeeCode, string EmployeeName, DateTime LocalTime, PunchType Type, PunchSource Source, string? Reason, string CreatedBy)
{
    public bool CanDelete => Source == PunchSource.Manual;
}

public sealed record GetPunchesQuery(DateOnly From, DateOnly To, Guid? EmployeeId = null, string? Search = null, PunchSource? Source = null, int PageIndex = 0, int PageSize = 200)
    : IQuery<PagedResult<PunchDto>>;

public sealed class GetPunchesHandler(IAppDbContext db, ICurrentUser user) : IRequestHandler<GetPunchesQuery, PagedResult<PunchDto>>
{
    public async Task<Result<PagedResult<PunchDto>>> HandleAsync(GetPunchesQuery request, CancellationToken cancellationToken)
    {
        var start = request.From.ToDateTime(TimeOnly.MinValue);
        var end = request.To.AddDays(1).ToDateTime(TimeOnly.MinValue);
        var query = from p in db.AttendancePunches.AsNoTracking()
                    join e in db.Employees.AsNoTracking() on p.EmployeeId equals e.Id
                    where p.CompanyId == user.CompanyId && p.LocalTime >= start && p.LocalTime < end
                    select new { p, e.Code, e.FullName };
        if (request.EmployeeId is { } employeeId)
        {
            query = query.Where(x => x.p.EmployeeId == employeeId);
        }

        if (request.Source is { } source)
        {
            query = query.Where(x => x.p.Source == source);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim();
            query = query.Where(x => x.Code.Contains(term) || x.FullName.Contains(term));
        }

        var page = new PageRequest(request.PageIndex, request.PageSize);
        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var items = await query.OrderByDescending(x => x.p.LocalTime).ThenBy(x => x.Code).Skip(page.Skip).Take(page.Take)
            .Select(x => new PunchDto(x.p.Id, x.p.EmployeeId, x.Code, x.FullName, x.p.LocalTime, x.p.Type, x.p.Source, x.p.Reason, x.p.CreatedBy))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return new PagedResult<PunchDto>(items, total, page.PageIndex, page.Take);
    }
}

[RequiresPermission(Permission.ManageAttendance)]
public sealed record AddManualPunchCommand(Guid EmployeeId, DateTime LocalTime, PunchType Type, string Reason) : ICommand<Guid>;

public sealed class AddManualPunchValidator : AbstractValidator<AddManualPunchCommand>
{
    public AddManualPunchValidator()
    {
        RuleFor(x => x.EmployeeId).NotEmpty().WithMessage("Choose an employee.");
        RuleFor(x => x.Reason).NotEmpty().WithMessage("Enter a reason for the manual punch.").MaximumLength(300);
        RuleFor(x => x.Type).IsInEnum();
    }
}

public sealed class AddManualPunchHandler(IAppDbContext db, ICurrentUser user, ICompanyContext company, IIdGenerator ids, IAuditWriter audit, AttendanceEngine engine, IClock clock, PayrollLocks locks)
    : IRequestHandler<AddManualPunchCommand, Guid>
{
    public async Task<Result<Guid>> HandleAsync(AddManualPunchCommand request, CancellationToken cancellationToken)
    {
        var employee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == request.EmployeeId && e.CompanyId == user.CompanyId && e.DeletedAtUtc == null, cancellationToken).ConfigureAwait(false);
        if (employee is null)
        {
            return Error.NotFound("Employee");
        }

        var local = new DateTime(request.LocalTime.Year, request.LocalTime.Month, request.LocalTime.Day, request.LocalTime.Hour, request.LocalTime.Minute, 0, DateTimeKind.Unspecified);
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(clock.UtcNow, company.TimeZone);
        if (local > nowLocal.AddMinutes(5))
        {
            return Error.Validation(nameof(request.LocalTime), "A punch cannot be in the future.");
        }

        if (await locks.IsLockedAsync(DateOnly.FromDateTime(local), cancellationToken).ConfigureAwait(false))
        {
            return PayrollLocks.LockedError(DateRange.Month(DateOnly.FromDateTime(local).Year, DateOnly.FromDateTime(local).Month), "LocalTime");
        }

        var key = AttendancePunch.ManualKey(employee.Id, local);
        if (await db.AttendancePunches.AnyAsync(p => p.DedupKey == key, cancellationToken).ConfigureAwait(false))
        {
            return Error.Validation(nameof(request.LocalTime), "There is already a manual punch for this employee at this minute.");
        }

        var punch = new AttendancePunch
        {
            Id = ids.NewId(),
            CompanyId = user.CompanyId,
            EmployeeId = employee.Id,
            LocalTime = local,
            PunchAtUtc = TimeZoneInfo.ConvertTimeToUtc(local, company.TimeZone),
            Type = request.Type,
            Source = PunchSource.Manual,
            DedupKey = key,
            Reason = request.Reason.Trim(),
        };
        db.AttendancePunches.Add(punch);
        audit.Record("Attendance.ManualPunch", $"Manual punch for {employee.Code} at {local:yyyy-MM-dd HH:mm}: {punch.Reason}", nameof(AttendancePunch), punch.Id, null,
            new { employee.Code, local, request.Type, punch.Reason });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var date = DateOnly.FromDateTime(local);
        await engine.RecomputeAsync([employee.Id], new DateRange(date.AddDays(-1), date), cancellationToken).ConfigureAwait(false);
        return punch.Id;
    }
}

[RequiresPermission(Permission.ManageAttendance)]
public sealed record DeleteManualPunchCommand(Guid Id, string Reason) : ICommand<Unit>;

public sealed class DeleteManualPunchHandler(IAppDbContext db, ICurrentUser user, IAuditWriter audit, AttendanceEngine engine, PayrollLocks locks) : IRequestHandler<DeleteManualPunchCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(DeleteManualPunchCommand request, CancellationToken cancellationToken)
    {
        var punch = await db.AttendancePunches.FirstOrDefaultAsync(p => p.Id == request.Id && p.CompanyId == user.CompanyId, cancellationToken).ConfigureAwait(false);
        if (punch is null)
        {
            return Error.NotFound("Punch");
        }

        if (punch.Source != PunchSource.Manual)
        {
            return Error.Validation("Punches from devices cannot be deleted. Correct the attendance day instead.");
        }

        if (await locks.IsLockedAsync(DateOnly.FromDateTime(punch.LocalTime), cancellationToken).ConfigureAwait(false))
        {
            return PayrollLocks.LockedError(DateRange.Month(DateOnly.FromDateTime(punch.LocalTime).Year, DateOnly.FromDateTime(punch.LocalTime).Month), "LocalTime");
        }

        db.AttendancePunches.Remove(punch);
        audit.Record("Attendance.ManualPunchDeleted", $"Manual punch at {punch.LocalTime:yyyy-MM-dd HH:mm} deleted: {request.Reason}", nameof(AttendancePunch), punch.Id,
            new { punch.EmployeeId, punch.LocalTime, punch.Reason }, null);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var date = DateOnly.FromDateTime(punch.LocalTime);
        await engine.RecomputeAsync([punch.EmployeeId], new DateRange(date.AddDays(-1), date), cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}

// ---------- Recompute ----------

[RequiresPermission(Permission.ManageAttendance)]
public sealed record RecomputeAttendanceCommand(DateOnly From, DateOnly To, IReadOnlyList<Guid>? EmployeeIds = null) : ICommand<int>;

public sealed class RecomputeAttendanceValidator : AbstractValidator<RecomputeAttendanceCommand>
{
    public RecomputeAttendanceValidator()
    {
        RuleFor(x => x.To).GreaterThanOrEqualTo(x => x.From).WithMessage("The end date must be on or after the start date.");
        RuleFor(x => x).Must(x => x.To.DayNumber - x.From.DayNumber < 400).WithMessage("Recompute at most about a year at a time.");
    }
}

public sealed class RecomputeAttendanceHandler(AttendanceEngine engine) : IRequestHandler<RecomputeAttendanceCommand, int>
{
    public async Task<Result<int>> HandleAsync(RecomputeAttendanceCommand request, CancellationToken cancellationToken)
        => await engine.RecomputeAsync(request.EmployeeIds, new DateRange(request.From, request.To), cancellationToken).ConfigureAwait(false);
}

// ---------- Monthly sheet ----------

public sealed record AttendanceCell(AttendanceStatus? Status, bool IsCorrected, bool MissingPunch, int LateMinutes, int OvertimeMinutes, decimal LeaveDays);

public sealed record AttendanceRow(
    Guid EmployeeId,
    string Code,
    string Name,
    string? Department,
    IReadOnlyList<AttendanceCell> Cells,
    int Present,
    int Late,
    int HalfDays,
    int Absent,
    decimal Leave,
    int OvertimeMinutes);

public sealed record AttendanceMonth(int Year, int Month, IReadOnlyList<DateOnly> Dates, IReadOnlyList<DayKind> DayKinds, IReadOnlyList<AttendanceRow> Rows, int MissingDays);

public sealed record GetAttendanceMonthQuery(int Year, int Month, Guid? DepartmentId = null, string? Search = null, Guid? EmployeeId = null) : IQuery<AttendanceMonth>;

public sealed class GetAttendanceMonthHandler(IAppDbContext db, ICurrentUser user) : IRequestHandler<GetAttendanceMonthQuery, AttendanceMonth>
{
    public async Task<Result<AttendanceMonth>> HandleAsync(GetAttendanceMonthQuery request, CancellationToken cancellationToken)
        => await BuildAsync(db, user, request, cancellationToken).ConfigureAwait(false);

    internal static async Task<AttendanceMonth> BuildAsync(IAppDbContext db, ICurrentUser user, GetAttendanceMonthQuery request, CancellationToken cancellationToken)
    {
        var range = DateRange.Month(request.Year, request.Month);
        var from = range.From;
        var to = range.To!.Value;
        var dates = range.EachDay().ToList();

        var company = await db.Companies.AsNoTracking().FirstAsync(c => c.Id == user.CompanyId, cancellationToken).ConfigureAwait(false);
        var holidays = await db.Holidays.AsNoTracking().Where(h => h.CompanyId == user.CompanyId && h.Date >= from && h.Date <= to).ToListAsync(cancellationToken).ConfigureAwait(false);
        var calendar = new WorkCalendar(company.WorkingWeek, holidays);

        var employees = db.Employees.AsNoTracking()
            .Where(e => e.CompanyId == user.CompanyId && e.DeletedAtUtc == null && e.JoinDate <= to && (e.ExitDate == null || e.ExitDate >= from));
        if (request.DepartmentId is { } departmentId)
        {
            employees = employees.Where(e => e.DepartmentId == departmentId);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim();
            employees = employees.Where(e => e.Code.Contains(term) || e.FullName.Contains(term));
        }

        if (request.EmployeeId is { } employeeId)
        {
            employees = employees.Where(e => e.Id == employeeId);
        }

        var list = await (from e in employees
                          join d in db.Departments.AsNoTracking() on e.DepartmentId equals d.Id into dj
                          from d in dj.DefaultIfEmpty()
                          orderby e.Code
                          select new { e.Id, e.Code, e.FullName, Department = d == null ? null : d.Name })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var ids = list.Select(e => e.Id).ToList();

        var days = (await db.AttendanceDays.AsNoTracking()
                .Where(d => d.CompanyId == user.CompanyId && ids.Contains(d.EmployeeId) && d.Date >= from && d.Date <= to)
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(d => (d.EmployeeId, d.Date));

        var missing = 0;
        var rows = new List<AttendanceRow>(list.Count);
        foreach (var e in list)
        {
            var cells = new AttendanceCell[dates.Count];
            int present = 0, late = 0, half = 0, absent = 0, overtime = 0;
            decimal leave = 0;
            for (var i = 0; i < dates.Count; i++)
            {
                if (!days.TryGetValue((e.Id, dates[i]), out var d))
                {
                    cells[i] = new AttendanceCell(null, false, false, 0, 0, 0);
                    missing++;
                    continue;
                }

                cells[i] = new AttendanceCell(d.Status, d.IsManuallyCorrected, d.MissingPunch, d.LateMinutes, d.OvertimeMinutes, d.LeaveDays);
                switch (d.Status)
                {
                    case AttendanceStatus.Present: present++; break;
                    case AttendanceStatus.Late: present++; late++; break;
                    case AttendanceStatus.HalfDay: half++; break;
                    case AttendanceStatus.Absent: absent++; break;
                }

                leave += d.LeaveDays;
                overtime += d.OvertimeMinutes;
            }

            rows.Add(new AttendanceRow(e.Id, e.Code, e.FullName, e.Department, cells, present, late, half, absent, leave, overtime));
        }

        return new AttendanceMonth(request.Year, request.Month, dates, dates.Select(d => calendar.GetDayKind(d)).ToList(), rows, missing);
    }
}

public sealed record AttendanceDayDetail(
    Guid EmployeeId,
    string EmployeeCode,
    string EmployeeName,
    DateOnly Date,
    string? ShiftName,
    AttendanceStatus? Status,
    DateTime? FirstIn,
    DateTime? LastOut,
    int WorkedMinutes,
    int LateMinutes,
    int EarlyLeaveMinutes,
    int OvertimeMinutes,
    bool MissingPunch,
    decimal LeaveDays,
    bool IsManuallyCorrected,
    string? CorrectionNote,
    IReadOnlyList<PunchDto> Punches);

public sealed record GetAttendanceDayQuery(Guid EmployeeId, DateOnly Date) : IQuery<AttendanceDayDetail>;

public sealed class GetAttendanceDayHandler(IAppDbContext db, ICurrentUser user) : IRequestHandler<GetAttendanceDayQuery, AttendanceDayDetail>
{
    public async Task<Result<AttendanceDayDetail>> HandleAsync(GetAttendanceDayQuery request, CancellationToken cancellationToken)
    {
        var employee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == request.EmployeeId && e.CompanyId == user.CompanyId, cancellationToken).ConfigureAwait(false);
        if (employee is null)
        {
            return Error.NotFound("Employee");
        }

        var day = await db.AttendanceDays.AsNoTracking().FirstOrDefaultAsync(d => d.EmployeeId == request.EmployeeId && d.Date == request.Date, cancellationToken).ConfigureAwait(false);
        var shiftName = day?.ShiftId is { } shiftId
            ? await db.Shifts.AsNoTracking().Where(s => s.Id == shiftId).Select(s => s.Name).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            : null;

        // Show punches around the day so night-shift and early punches are visible.
        var from = request.Date.AddDays(-1).ToDateTime(new TimeOnly(18, 0));
        var to = request.Date.AddDays(1).ToDateTime(new TimeOnly(14, 0));
        var punches = await db.AttendancePunches.AsNoTracking()
            .Where(p => p.EmployeeId == request.EmployeeId && p.LocalTime >= from && p.LocalTime < to)
            .OrderBy(p => p.LocalTime)
            .Select(p => new PunchDto(p.Id, p.EmployeeId, employee.Code, employee.FullName, p.LocalTime, p.Type, p.Source, p.Reason, p.CreatedBy))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return new AttendanceDayDetail(employee.Id, employee.Code, employee.FullName, request.Date, shiftName, day?.Status, day?.FirstIn, day?.LastOut,
            day?.WorkedMinutes ?? 0, day?.LateMinutes ?? 0, day?.EarlyLeaveMinutes ?? 0, day?.OvertimeMinutes ?? 0, day?.MissingPunch ?? false,
            day?.LeaveDays ?? 0, day?.IsManuallyCorrected ?? false, day?.CorrectionNote, punches);
    }
}

/// <summary>Overrides the computed values of one day. Recompute never overwrites it until the correction is reset.</summary>
[RequiresPermission(Permission.ManageAttendance)]
public sealed record CorrectAttendanceDayCommand(
    Guid EmployeeId,
    DateOnly Date,
    AttendanceStatus Status,
    int LateMinutes,
    int OvertimeMinutes,
    string Reason) : ICommand<Unit>;

public sealed class CorrectAttendanceDayValidator : AbstractValidator<CorrectAttendanceDayCommand>
{
    public CorrectAttendanceDayValidator()
    {
        RuleFor(x => x.Reason).NotEmpty().WithMessage("Enter a reason for the correction.").MaximumLength(300);
        RuleFor(x => x.Status).IsInEnum().Must(s => s is not (AttendanceStatus.NotJoined or AttendanceStatus.Exited)).WithMessage("Choose a working status.");
        RuleFor(x => x.LateMinutes).InclusiveBetween(0, 24 * 60);
        RuleFor(x => x.OvertimeMinutes).InclusiveBetween(0, 24 * 60);
    }
}

public sealed class CorrectAttendanceDayHandler(IAppDbContext db, ICurrentUser user, IIdGenerator ids, IAuditWriter audit, AttendanceEngine engine, PayrollLocks locks)
    : IRequestHandler<CorrectAttendanceDayCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(CorrectAttendanceDayCommand request, CancellationToken cancellationToken)
    {
        var employee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == request.EmployeeId && e.CompanyId == user.CompanyId, cancellationToken).ConfigureAwait(false);
        if (employee is null)
        {
            return Error.NotFound("Employee");
        }

        if (!employee.IsEmployedOn(request.Date))
        {
            return Error.Validation(nameof(request.Date), "The employee was not employed on this date.");
        }

        if (await locks.IsLockedAsync(request.Date, cancellationToken).ConfigureAwait(false))
        {
            return PayrollLocks.LockedError(DateRange.Month(request.Date.Year, request.Date.Month), "Date");
        }

        var day = await db.AttendanceDays.FirstOrDefaultAsync(d => d.EmployeeId == request.EmployeeId && d.Date == request.Date, cancellationToken).ConfigureAwait(false);
        if (day is null)
        {
            await engine.RecomputeAsync([request.EmployeeId], new DateRange(request.Date, request.Date), cancellationToken).ConfigureAwait(false);
            day = await db.AttendanceDays.FirstAsync(d => d.EmployeeId == request.EmployeeId && d.Date == request.Date, cancellationToken).ConfigureAwait(false);
        }

        var before = Snapshot(day);
        day.Status = request.Status;
        day.LateMinutes = request.Status == AttendanceStatus.Late ? Math.Max(1, request.LateMinutes) : 0;
        day.OvertimeMinutes = request.OvertimeMinutes;
        day.IsManuallyCorrected = true;
        day.CorrectionNote = request.Reason.Trim();
        if (request.Status != AttendanceStatus.Leave)
        {
            day.LeaveDays = 0;
            day.LeaveTypeId = null;
        }

        var after = Snapshot(day);
        db.AttendanceCorrections.Add(new AttendanceCorrection
        {
            Id = ids.NewId(),
            CompanyId = user.CompanyId,
            EmployeeId = request.EmployeeId,
            Date = request.Date,
            BeforeJson = JsonSerializer.Serialize(before),
            AfterJson = JsonSerializer.Serialize(after),
            Reason = day.CorrectionNote,
        });
        audit.Record("Attendance.Corrected", $"{employee.Code} {request.Date:yyyy-MM-dd}: {before.Status} → {request.Status} ({day.CorrectionNote})", nameof(AttendanceDay), day.Id, before, after);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }

    internal static DaySnapshot Snapshot(AttendanceDay d) => new(d.Status, d.LateMinutes, d.OvertimeMinutes, d.WorkedMinutes, d.LeaveDays);
}

public sealed record DaySnapshot(AttendanceStatus Status, int LateMinutes, int OvertimeMinutes, int WorkedMinutes, decimal LeaveDays);

[RequiresPermission(Permission.ManageAttendance)]
public sealed record ResetAttendanceCorrectionCommand(Guid EmployeeId, DateOnly Date) : ICommand<Unit>;

public sealed class ResetAttendanceCorrectionHandler(IAppDbContext db, ICurrentUser user, IAuditWriter audit, AttendanceEngine engine, PayrollLocks locks)
    : IRequestHandler<ResetAttendanceCorrectionCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(ResetAttendanceCorrectionCommand request, CancellationToken cancellationToken)
    {
        var day = await db.AttendanceDays.FirstOrDefaultAsync(d => d.CompanyId == user.CompanyId && d.EmployeeId == request.EmployeeId && d.Date == request.Date, cancellationToken).ConfigureAwait(false);
        if (day is null || !day.IsManuallyCorrected)
        {
            return Unit.Value;
        }

        if (await locks.IsLockedAsync(request.Date, cancellationToken).ConfigureAwait(false))
        {
            return PayrollLocks.LockedError(DateRange.Month(request.Date.Year, request.Date.Month), "Date");
        }

        var before = CorrectAttendanceDayHandler.Snapshot(day);
        day.IsManuallyCorrected = false;
        day.CorrectionNote = null;
        audit.Record("Attendance.CorrectionReset", $"Correction on {request.Date:yyyy-MM-dd} reset", nameof(AttendanceDay), day.Id, before, null);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await engine.RecomputeAsync([request.EmployeeId], new DateRange(request.Date, request.Date), cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}

public sealed record ExportAttendanceMonthQuery(string FilePath, int Year, int Month, Guid? DepartmentId = null) : IQuery<int>;

public sealed class ExportAttendanceMonthHandler(IAppDbContext db, ICurrentUser user, ISpreadsheetService sheets) : IRequestHandler<ExportAttendanceMonthQuery, int>
{
    public async Task<Result<int>> HandleAsync(ExportAttendanceMonthQuery request, CancellationToken cancellationToken)
    {
        var m = await GetAttendanceMonthHandler.BuildAsync(db, user, new GetAttendanceMonthQuery(request.Year, request.Month, request.DepartmentId), cancellationToken).ConfigureAwait(false);
        var headers = new List<string> { "Code", "Name", "Department" };
        headers.AddRange(m.Dates.Select(d => d.Day.ToString(CultureInfo.InvariantCulture)));
        headers.AddRange(["Present", "Late", "Half days", "Absent", "Leave", "Overtime hours"]);
        var rows = m.Rows.Select(r =>
        {
            var row = new List<object?> { r.Code, r.Name, r.Department };
            row.AddRange(r.Cells.Select(c => (object?)AttendanceCodes.Short(c.Status)));
            row.AddRange([r.Present, r.Late, r.HalfDays, r.Absent, r.Leave, Math.Round(r.OvertimeMinutes / 60m, 2)]);
            return (IReadOnlyList<object?>)row;
        }).ToList();
        sheets.Write(request.FilePath, [new SheetData($"{request.Year}-{request.Month:00}", headers, rows)]);
        return rows.Count;
    }
}

/// <summary>One-letter codes used on the sheet and in exports.</summary>
public static class AttendanceCodes
{
    public static string Short(AttendanceStatus? status) => status switch
    {
        AttendanceStatus.Present => "P",
        AttendanceStatus.Late => "L",
        AttendanceStatus.HalfDay => "H",
        AttendanceStatus.Absent => "A",
        AttendanceStatus.Leave => "LV",
        AttendanceStatus.Holiday => "HD",
        AttendanceStatus.WeeklyOff => "W",
        AttendanceStatus.NotJoined or AttendanceStatus.Exited => "-",
        _ => string.Empty,
    };
}
