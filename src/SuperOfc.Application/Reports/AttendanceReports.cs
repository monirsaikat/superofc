using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Attendance;
using SuperOfc.Domain.Attendance;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Payroll;
using SuperOfc.Domain.Security;

namespace SuperOfc.Application.Reports;

/// <summary>Data shared by reports: employees with department names, and attendance worked out up to today.</summary>
public sealed class ReportData(IAppDbContext db, ICurrentUser user, AttendanceEngine engine, IClock clock, ICompanyContext company)
{
    public sealed record Person(Guid Id, string Code, string Name, string Department, DateOnly JoinDate, DateOnly? ExitDate);

    public DateOnly Today => clock.TodayIn(company.TimeZone);

    public int FiscalStartMonth => company.Company?.FiscalYearStartMonth ?? 7;

    /// <summary>Employees employed at some point in the range, optionally in one department, ordered by code.</summary>
    public async Task<List<Person>> PeopleAsync(DateRange range, Guid? departmentId, CancellationToken cancellationToken)
    {
        var to = range.To ?? DateOnly.MaxValue;
        var query = db.Employees.AsNoTracking().Where(e => e.CompanyId == user.CompanyId && e.DeletedAtUtc == null && e.JoinDate <= to && (e.ExitDate == null || e.ExitDate >= range.From));
        if (departmentId is { } id)
        {
            query = query.Where(e => e.DepartmentId == id);
        }

        return await (from e in query
                      join d in db.Departments.AsNoTracking() on e.DepartmentId equals d.Id into dj
                      from d in dj.DefaultIfEmpty()
                      orderby e.Code
                      select new Person(e.Id, e.Code, e.FullName, d == null ? "No department" : d.Name, e.JoinDate, e.ExitDate))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> DepartmentNameAsync(Guid? id, CancellationToken cancellationToken)
        => id is null ? "All departments"
            : await db.Departments.AsNoTracking().Where(d => d.Id == id).Select(d => d.Name).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? "All departments";

    /// <summary>
    /// Attendance for the range, never past today (future days are not worked out). Uses the same engine as the
    /// attendance sheet and payroll, without saving. Days before joining or after leaving are left out.
    /// </summary>
    public async Task<(DateRange Range, ILookup<Guid, DayResult> Days, bool Clipped)> AttendanceAsync(IReadOnlyCollection<Guid> employeeIds, DateRange range, CancellationToken cancellationToken)
    {
        var to = range.To!.Value;
        var clipped = to > Today;
        if (clipped)
        {
            to = Today;
        }

        if (to < range.From || employeeIds.Count == 0)
        {
            return (range, Array.Empty<(Guid, DayResult)>().ToLookup(x => x.Item1, x => x.Item2), clipped);
        }

        var actual = new DateRange(range.From, to);
        var results = await engine.PreviewAsync(employeeIds, actual, cancellationToken).ConfigureAwait(false);
        return (actual, results.Where(r => r.Result.Status is not (AttendanceStatus.NotJoined or AttendanceStatus.Exited)).ToLookup(r => r.EmployeeId, r => r.Result), clipped);
    }

    public static ReportParameter MonthParameter => new("Month", "Month", ReportParameterKind.Month);

    public static ReportParameter DepartmentParameter => new("Department", "Department", ReportParameterKind.Department, Required: false);

    public static ReportParameter PeriodParameter => new("Period", "Period", ReportParameterKind.DateRange);

    public static decimal Hours(int minutes) => Math.Round(minutes / 60m, 2);
}

/// <summary>Days present, late, half, absent, on leave and overtime per employee or per department for a month.</summary>
public sealed class AttendanceSummaryReport(ReportData data) : IReport
{
    public string Id => "attendance-summary";

    public string Name => "Attendance summary";

    public string Category => "Attendance";

    public string Description => "Days present, late, half days, absences, leave and overtime for a month, per employee or per department.";

    public Permission Permission => Permission.ViewData;

    public IReadOnlyList<ReportParameter> Parameters { get; } =
    [
        ReportData.MonthParameter,
        ReportData.DepartmentParameter,
        new("GroupBy", "Show", ReportParameterKind.Choice, Required: false, Choices: [new("employee", "Each employee"), new("department", "Each department")]),
    ];

    public async Task<ReportTable> RunAsync(ReportArgs args, CancellationToken cancellationToken)
    {
        var month = args.Month("Month");
        var departmentId = args.Id("Department");
        var byDepartment = args.Choice("GroupBy", "employee") == "department";
        var people = await data.PeopleAsync(month, departmentId, cancellationToken).ConfigureAwait(false);
        var (range, days, clipped) = await data.AttendanceAsync([.. people.Select(p => p.Id)], month, cancellationToken).ConfigureAwait(false);

        var builder = new ReportBuilder("Attendance summary", $"{ReportText.MonthName(month)} · {await data.DepartmentNameAsync(departmentId, cancellationToken).ConfigureAwait(false)}");
        if (byDepartment)
        {
            builder.Column("Department", width: 2).Column("Employees", ReportColumnKind.Integer, true);
        }
        else
        {
            builder.Column("Code").Column("Name", width: 2).Column("Department", width: 1.5f);
        }

        builder.Column("Present", ReportColumnKind.Integer, true).Column("Late", ReportColumnKind.Integer, true).Column("Half day", ReportColumnKind.Integer, true)
            .Column("Absent", ReportColumnKind.Integer, true).Column("Leave days", ReportColumnKind.Number, true).Column("Holidays", ReportColumnKind.Integer, true)
            .Column("Weekly off", ReportColumnKind.Integer, true).Column("Overtime hours", ReportColumnKind.Number, true);

        var counts = people.Select(p => (Person: p, Counts: Count(days[p.Id]))).ToList();
        if (byDepartment)
        {
            foreach (var g in counts.GroupBy(c => c.Person.Department).OrderBy(g => g.Key, StringComparer.CurrentCulture))
            {
                var sum = g.Select(x => x.Counts).Aggregate(Counts.Zero, (a, b) => a + b);
                builder.Row([g.Key, g.Count(), .. sum.Cells()]);
            }
        }
        else
        {
            foreach (var (p, c) in counts)
            {
                builder.Row([p.Code, p.Name, p.Department, .. c.Cells()]);
            }
        }

        builder.Note("Present counts days on time; late days are counted under Late only.");
        if (clipped)
        {
            builder.Note($"Days after {range.To!.Value.ToString("d MMM yyyy", CultureInfo.InvariantCulture)} have not happened yet and are not included.");
        }

        return builder.Build();
    }

    private static Counts Count(IEnumerable<DayResult> days)
    {
        var c = Counts.Zero;
        foreach (var d in days)
        {
            c += d.Status switch
            {
                AttendanceStatus.Present => Counts.Zero with { Present = 1 },
                AttendanceStatus.Late => Counts.Zero with { Late = 1 },
                AttendanceStatus.HalfDay => Counts.Zero with { HalfDay = 1, Leave = d.LeaveDays },
                AttendanceStatus.Absent => Counts.Zero with { Absent = 1 },
                AttendanceStatus.Leave => Counts.Zero with { Leave = d.LeaveDays },
                AttendanceStatus.Holiday => Counts.Zero with { Holidays = 1 },
                AttendanceStatus.WeeklyOff => Counts.Zero with { WeeklyOff = 1 },
                _ => Counts.Zero,
            };
            c += Counts.Zero with { OvertimeMinutes = d.OvertimeMinutes };
        }

        return c;
    }

    private sealed record Counts(int Present, int Late, int HalfDay, int Absent, decimal Leave, int Holidays, int WeeklyOff, int OvertimeMinutes)
    {
        public static Counts Zero { get; } = new(0, 0, 0, 0, 0, 0, 0, 0);

        public static Counts operator +(Counts a, Counts b) => new(a.Present + b.Present, a.Late + b.Late, a.HalfDay + b.HalfDay, a.Absent + b.Absent, a.Leave + b.Leave,
            a.Holidays + b.Holidays, a.WeeklyOff + b.WeeklyOff, a.OvertimeMinutes + b.OvertimeMinutes);

        public object?[] Cells() => [Present, Late, HalfDay, Absent, Leave, Holidays, WeeklyOff, ReportData.Hours(OvertimeMinutes)];
    }
}

/// <summary>Every late arrival and absence in a period, one row per employee and day.</summary>
public sealed class LateAbsentReport(ReportData data) : IReport
{
    public string Id => "late-absent";

    public string Name => "Late and absent list";

    public string Category => "Attendance";

    public string Description => "Each late arrival and absence in a period, with the first punch and minutes late.";

    public Permission Permission => Permission.ViewData;

    public IReadOnlyList<ReportParameter> Parameters { get; } =
    [
        ReportData.PeriodParameter,
        ReportData.DepartmentParameter,
        new("Show", "Show", ReportParameterKind.Choice, Required: false, Choices: [new("both", "Late and absent"), new("late", "Late only"), new("absent", "Absent only")]),
    ];

    public async Task<ReportTable> RunAsync(ReportArgs args, CancellationToken cancellationToken)
    {
        var period = args.Range("Period", 92);
        var departmentId = args.Id("Department");
        var show = args.Choice("Show", "both");
        var people = await data.PeopleAsync(period, departmentId, cancellationToken).ConfigureAwait(false);
        var (range, days, clipped) = await data.AttendanceAsync([.. people.Select(p => p.Id)], period, cancellationToken).ConfigureAwait(false);

        var builder = new ReportBuilder("Late and absent list", $"{ReportText.Range(period)} · {await data.DepartmentNameAsync(departmentId, cancellationToken).ConfigureAwait(false)}")
            .Column("Date", ReportColumnKind.Date).Column("Code").Column("Name", width: 2).Column("Department", width: 1.5f).Column("Status")
            .Column("First in").Column("Minutes late", ReportColumnKind.Integer, true);
        var rows = people.SelectMany(p => days[p.Id].Where(d => (d.Status == AttendanceStatus.Late && show != "absent") || (d.Status == AttendanceStatus.Absent && show != "late"))
                .Select(d => (Person: p, Day: d)))
            .OrderBy(x => x.Day.Date).ThenBy(x => x.Person.Code, StringComparer.Ordinal);
        foreach (var (p, d) in rows)
        {
            builder.Row(d.Date, p.Code, p.Name, p.Department, d.Status == AttendanceStatus.Late ? "Late" : "Absent",
                d.FirstIn is { } first ? first.ToString("HH:mm", CultureInfo.InvariantCulture) : null, d.LateMinutes);
        }

        var late = rows.Count(x => x.Day.Status == AttendanceStatus.Late);
        builder.Note($"{late} late arrivals and {rows.Count() - late} absences.");
        if (clipped)
        {
            builder.Note($"Days after {range.To!.Value.ToString("d MMM yyyy", CultureInfo.InvariantCulture)} have not happened yet and are not included.");
        }

        return builder.Build();
    }
}

/// <summary>Overtime hours per employee from attendance, and the overtime paid in finalized payroll for the month.</summary>
public sealed class OvertimeReport(ReportData data, IAppDbContext db, ICurrentUser user) : IReport
{
    public string Id => "overtime";

    public string Name => "Overtime";

    public string Category => "Attendance";

    public string Description => "Overtime hours per employee for a month, and the overtime paid once payroll is finalized.";

    public Permission Permission => Permission.ViewData;

    public IReadOnlyList<ReportParameter> Parameters { get; } = [ReportData.MonthParameter, ReportData.DepartmentParameter];

    public async Task<ReportTable> RunAsync(ReportArgs args, CancellationToken cancellationToken)
    {
        var month = args.Month("Month");
        var departmentId = args.Id("Department");
        var people = await data.PeopleAsync(month, departmentId, cancellationToken).ConfigureAwait(false);
        var (range, days, clipped) = await data.AttendanceAsync([.. people.Select(p => p.Id)], month, cancellationToken).ConfigureAwait(false);
        var canSeePay = PermissionMatrix.Has(user.Role, Permission.ViewSalaries);

        // Overtime paid: lines from the finalized run of the month, if any.
        Dictionary<Guid, long> paid = [];
        var finalized = false;
        if (canSeePay)
        {
            var runId = await (from r in db.PayrollRuns.AsNoTracking()
                               join p in db.PayrollPeriods.AsNoTracking() on r.PeriodId equals p.Id
                               where r.CompanyId == user.CompanyId && p.Year == month.From.Year && p.Month == month.From.Month && r.Status == PayrollRunStatus.Finalized
                               select (Guid?)r.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (runId is { } id)
            {
                finalized = true;
                paid = await (from l in db.PayslipLines.AsNoTracking()
                              join s in db.Payslips.AsNoTracking() on l.PayslipId equals s.Id
                              where s.RunId == id && l.Source == PayslipLineSource.Overtime
                              group l by s.EmployeeId into g
                              select new { g.Key, Amount = g.Sum(x => x.AmountMinor) }).ToDictionaryAsync(x => x.Key, x => x.Amount, cancellationToken).ConfigureAwait(false);
            }
        }

        var builder = new ReportBuilder("Overtime", $"{ReportText.MonthName(month)} · {await data.DepartmentNameAsync(departmentId, cancellationToken).ConfigureAwait(false)}")
            .Column("Code").Column("Name", width: 2).Column("Department", width: 1.5f).Column("Days with overtime", ReportColumnKind.Integer, true)
            .Column("Workday hours", ReportColumnKind.Number, true).Column("Holiday and off-day hours", ReportColumnKind.Number, true).Column("Total hours", ReportColumnKind.Number, true);
        if (canSeePay)
        {
            builder.Column("Overtime paid", ReportColumnKind.Money, true);
        }

        foreach (var p in people)
        {
            var ot = days[p.Id].Where(d => d.OvertimeMinutes > 0).ToList();
            if (ot.Count == 0 && !paid.ContainsKey(p.Id))
            {
                continue;
            }

            var off = ot.Where(d => d.Status is AttendanceStatus.Holiday or AttendanceStatus.WeeklyOff).Sum(d => d.OvertimeMinutes);
            var all = ot.Sum(d => d.OvertimeMinutes);
            object?[] cells = [p.Code, p.Name, p.Department, ot.Count, ReportData.Hours(all - off), ReportData.Hours(off), ReportData.Hours(all)];
            builder.Row(canSeePay ? [.. cells, finalized ? new Money(paid.GetValueOrDefault(p.Id)).Major : null] : cells);
        }

        if (canSeePay && !finalized)
        {
            builder.Note("Payroll for this month is not finalized, so overtime paid is blank.");
        }

        if (clipped)
        {
            builder.Note($"Days after {range.To!.Value.ToString("d MMM yyyy", CultureInfo.InvariantCulture)} have not happened yet and are not included.");
        }

        return builder.Build();
    }
}
