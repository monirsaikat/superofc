using System.Text.Json;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Attendance;
using SuperOfc.Application.Common;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Organization;
using SuperOfc.Domain.Payroll;
using SuperOfc.Domain.Security;

namespace SuperOfc.Application.Payroll;

public sealed record PayrollPeriodDto(
    Guid? Id,
    int Year,
    int Month,
    PayrollPeriodStatus Status,
    Guid? RunId,
    int? RunNumber,
    PayrollRunStatus? RunStatus,
    int EmployeeCount,
    int PaidCount,
    Money Gross,
    Money Net,
    DateTime? ComputedAtUtc)
{
    public DateOnly FirstDay => new(Year, Month, 1);

    public bool CanCompute => Status is PayrollPeriodStatus.Open or PayrollPeriodStatus.Computed;
}

public sealed record PayrollPeriodList(IReadOnlyList<PayrollPeriodDto> Periods, int NextYear, int NextMonth);

/// <summary>Recent payroll months, plus the next month that can be computed.</summary>
[RequiresPermission(Permission.ViewSalaries)]
public sealed record GetPayrollPeriodsQuery(int Take = 24) : IQuery<PayrollPeriodList>;

public sealed class GetPayrollPeriodsHandler(IAppDbContext db, ICurrentUser user, IClock clock, ICompanyContext company) : IRequestHandler<GetPayrollPeriodsQuery, PayrollPeriodList>
{
    public async Task<Result<PayrollPeriodList>> HandleAsync(GetPayrollPeriodsQuery request, CancellationToken cancellationToken)
    {
        var periods = await db.PayrollPeriods.AsNoTracking().Where(p => p.CompanyId == user.CompanyId)
            .OrderByDescending(p => p.Year).ThenByDescending(p => p.Month).Take(Math.Clamp(request.Take, 1, 240)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var ids = periods.Select(p => p.Id).ToList();
        var runs = await db.PayrollRuns.AsNoTracking().Where(r => ids.Contains(r.PeriodId) && r.Status != PayrollRunStatus.Voided)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var runIds = runs.Select(r => r.Id).ToList();
        var paid = await db.Payslips.AsNoTracking().Where(p => runIds.Contains(p.RunId) && p.PaymentStatus == PaymentStatus.Paid)
            .GroupBy(p => p.RunId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken).ConfigureAwait(false);

        var rows = periods.Select(p =>
        {
            var run = runs.Where(r => r.PeriodId == p.Id).OrderByDescending(r => r.RunNumber).FirstOrDefault();
            return new PayrollPeriodDto(p.Id, p.Year, p.Month, p.Status, run?.Id, run?.RunNumber, run?.Status, run?.EmployeeCount ?? 0,
                run is null ? 0 : paid.GetValueOrDefault(run.Id), new Money(run?.GrossMinor ?? 0), new Money(run?.NetMinor ?? 0), run?.ComputedAtUtc);
        }).ToList();

        // The next month: the one after the latest period, or last month when there is none.
        var today = clock.TodayIn(company.TimeZone);
        var next = periods.Count == 0 ? new DateOnly(today.Year, today.Month, 1).AddMonths(-1) : new DateOnly(periods[0].Year, periods[0].Month, 1).AddMonths(1);
        return new PayrollPeriodList(rows, next.Year, next.Month);
    }
}

public sealed record PayrollRunSummary(Guid RunId, Guid PeriodId, int RunNumber, int Employees, Money Gross, Money Deductions, Money Net, int WithWarnings, TimeSpan Elapsed);

/// <summary>
/// Computes payroll for a month (doc 03 R3, R7): recomputes attendance, then creates a new draft run that replaces any
/// earlier draft. Finalized months must be voided first.
/// </summary>
[RequiresPermission(Permission.ManagePayroll)]
public sealed record ComputePayrollCommand(int Year, int Month) : ICommand<PayrollRunSummary>;

public sealed class ComputePayrollValidator : AbstractValidator<ComputePayrollCommand>
{
    public ComputePayrollValidator()
    {
        RuleFor(x => x.Year).InclusiveBetween(2000, 2100);
        RuleFor(x => x.Month).InclusiveBetween(1, 12);
    }
}

public sealed class ComputePayrollHandler(
    IAppDbContext db,
    ICurrentUser user,
    IIdGenerator ids,
    IClock clock,
    ICompanyContext company,
    IPolicyProvider policies,
    IAuditWriter audit,
    AttendanceEngine engine,
    IEnumerable<IPayrollParticipant> participants,
    ILogger<ComputePayrollHandler> logger) : IRequestHandler<ComputePayrollCommand, PayrollRunSummary>
{
    public async Task<Result<PayrollRunSummary>> HandleAsync(ComputePayrollCommand request, CancellationToken cancellationToken)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var range = DateRange.Month(request.Year, request.Month);
        var today = clock.TodayIn(company.TimeZone);
        if (range.To!.Value > today)
        {
            return Error.Validation("Month", $"{range.From:MMMM yyyy} has not ended yet. Payroll can be computed from {range.To.Value.AddDays(1):dd MMM yyyy}.");
        }

        var period = await db.PayrollPeriods.FirstOrDefaultAsync(p => p.CompanyId == user.CompanyId && p.Year == request.Year && p.Month == request.Month, cancellationToken).ConfigureAwait(false);
        if (period is { IsLocked: true })
        {
            return Error.Validation("Month", $"Payroll for {range.From:MMMM yyyy} is finalized. Void it to compute again.");
        }

        var employees = await db.Employees.AsNoTracking()
            .Where(e => e.CompanyId == user.CompanyId && e.DeletedAtUtc == null && e.JoinDate <= range.To && (e.ExitDate == null || e.ExitDate >= range.From))
            .OrderBy(e => e.Code).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (employees.Count == 0)
        {
            return Error.Validation("Month", "Nobody was employed in this month.");
        }

        var employeeIds = employees.Select(e => e.Id).ToList();

        // R7: attendance is recomputed first so corrections made since are included.
        await engine.RecomputeAsync(employeeIds, range, cancellationToken).ConfigureAwait(false);

        var policy = await policies.GetSnapshotAsync(range.To.Value, cancellationToken).ConfigureAwait(false);
        var days = (await db.AttendanceDays.AsNoTracking().Where(d => employeeIds.Contains(d.EmployeeId) && d.Date >= range.From && d.Date <= range.To)
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToLookup(d => d.EmployeeId, PayDay.From);
        var components = (await db.SalaryComponents.AsNoTracking().Where(c => c.CompanyId == user.CompanyId && c.DeletedAtUtc == null && c.IsActive)
            .ToListAsync(cancellationToken).ConfigureAwait(false)).Select(ComponentDef.From).ToList();
        var assignments = (await db.SalaryAssignments.AsNoTracking().Include(a => a.Lines)
                .Where(a => employeeIds.Contains(a.EmployeeId) && a.EffectiveFrom <= range.To && (a.EffectiveTo == null || a.EffectiveTo >= range.From))
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToLookup(a => a.EmployeeId);
        var adjustments = (await db.PayrollAdjustments.AsNoTracking().Where(a => a.CompanyId == user.CompanyId && a.Year == request.Year && a.Month == request.Month)
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToLookup(a => a.EmployeeId);
        var departments = await db.Departments.AsNoTracking().Where(d => d.CompanyId == user.CompanyId).ToDictionaryAsync(d => d.Id, d => d.Name, cancellationToken).ConfigureAwait(false);
        var designations = await db.Designations.AsNoTracking().Where(d => d.CompanyId == user.CompanyId).ToDictionaryAsync(d => d.Id, d => d.Name, cancellationToken).ConfigureAwait(false);
        var holidays = await db.Holidays.AsNoTracking().Where(h => h.CompanyId == user.CompanyId && h.Date >= range.From && h.Date <= range.To).ToListAsync(cancellationToken).ConfigureAwait(false);
        var companyRow = await db.Companies.AsNoTracking().FirstAsync(c => c.Id == user.CompanyId, cancellationToken).ConfigureAwait(false);
        var calendar = new WorkCalendar(companyRow.WorkingWeek, holidays);

        if (period is null)
        {
            period = PayrollPeriod.For(user.CompanyId, ids.NewId(), request.Year, request.Month);
            db.PayrollPeriods.Add(period);
        }

        var context = new PayrollContext(period, employeeIds, policy);
        var contributions = new List<PayrollContribution>();
        foreach (var participant in participants)
        {
            contributions.Add(await participant.PrepareAsync(context, cancellationToken).ConfigureAwait(false));
        }

        await using var tx = await db.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // A new compute replaces the draft; finalized and voided runs stay for history.
        var drafts = await db.PayrollRuns.Where(r => r.PeriodId == period.Id && r.Status == PayrollRunStatus.Draft).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (drafts.Count > 0)
        {
            var draftIds = drafts.Select(d => d.Id).ToList();
            await db.PayslipLines.Where(l => db.Payslips.Any(p => p.Id == l.PayslipId && draftIds.Contains(p.RunId))).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            await db.Payslips.Where(p => draftIds.Contains(p.RunId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            db.PayrollRuns.RemoveRange(drafts);
        }

        var runNumber = (await db.PayrollRuns.Where(r => r.PeriodId == period.Id && r.Status != PayrollRunStatus.Draft).Select(r => (int?)r.RunNumber).MaxAsync(cancellationToken).ConfigureAwait(false) ?? 0) + 1;
        var run = new PayrollRun
        {
            Id = ids.NewId(), CompanyId = user.CompanyId, PeriodId = period.Id, RunNumber = runNumber, Status = PayrollRunStatus.Draft,
            ComputedAtUtc = clock.UtcNow, ComputedBy = user.UserName,
            PolicyJson = JsonSerializer.Serialize(new { policy.Payroll, policy.Attendance }),
        };
        db.PayrollRuns.Add(run);

        var monthWorkingDays = range.EachDay().Count(d => calendar.GetDayKind(d) is DayKind.Working or DayKind.HalfDay);
        var withWarnings = 0;
        foreach (var e in employees)
        {
            var segments = assignments[e.Id].OrderBy(a => a.EffectiveFrom)
                .Select(a => new PaySegment(new DateRange(a.EffectiveFrom, a.EffectiveTo), a.Lines.Select(l => new PayLine(l.ComponentId, l.AmountMinor, l.Rate)).ToList())).ToList();
            var input = new Domain.Payroll.PayslipInput(range, new DateRange(e.JoinDate, e.ExitDate), e.IsOvertimeEligible, days[e.Id].ToList(), monthWorkingDays,
                components, segments, policy.Payroll, policy.Attendance)
            {
                Adjustments = adjustments[e.Id].Select(a => new AdjustmentInput(a.Id, a.Name, a.Kind, new Money(a.AmountMinor), a.IsTaxable, a.Reason)).ToList(),
                Loans = contributions.SelectMany(c => c.Deductions.GetValueOrDefault(e.Id) ?? []).ToList(),
                Tax = contributions.Select(c => c.Tax.GetValueOrDefault(e.Id)).FirstOrDefault(t => t is not null),
            };
            var result = PayslipCalculator.Compute(input);
            var warnings = result.Warnings.Concat(result.Skipped.Select(s => $"{s.Deduction.Name}: {s.Reason}")).ToList();
            withWarnings += warnings.Count > 0 ? 1 : 0;

            var payslip = new Payslip
            {
                Id = ids.NewId(), CompanyId = user.CompanyId, RunId = run.Id, EmployeeId = e.Id, EmployeeCode = e.Code, EmployeeName = e.FullName,
                Department = e.DepartmentId is { } d ? departments.GetValueOrDefault(d) : null,
                Designation = e.DesignationId is { } g ? designations.GetValueOrDefault(g) : null,
                PaymentMode = e.PaymentMode, BankName = e.BankName, BankAccount = e.BankAccount,
                CalendarDays = result.Days.CalendarDays, PayableDays = result.Days.PayableDays, PresentDays = result.Days.PresentDays, HalfDays = result.Days.HalfDays,
                PaidLeaveDays = result.Days.PaidLeaveDays, UnpaidLeaveDays = result.Days.UnpaidLeaveDays, AbsentDays = result.Days.AbsentDays,
                Holidays = result.Days.Holidays, WeeklyOffs = result.Days.WeeklyOffs, LateDays = result.Days.LateDays, LatePenaltyDays = result.Days.LatePenaltyDays,
                OvertimeHours = (result.Days.OvertimeMinutes + result.Days.HolidayOvertimeMinutes) / 60m,
                GrossMinor = result.Gross.Minor, DeductionsMinor = result.Deductions.Minor, NetMinor = result.Net.Minor, TaxMinor = result.Tax.Minor, LoanMinor = result.Loan.Minor,
                EmployerContributionsMinor = result.EmployerContributions.Minor, TaxableGrossMinor = result.TaxableGross.Minor,
                Warnings = warnings.Count == 0 ? null : string.Join(Environment.NewLine, warnings),
            };
            payslip.Lines = result.Lines.Select(l => new PayslipLine
            {
                Id = ids.NewId(), PayslipId = payslip.Id, Order = l.Order, Code = l.Code, Name = l.Name, Kind = l.Kind, Source = l.Source, Basis = l.Basis,
                FullAmountMinor = l.FullAmount?.Minor, Quantity = l.Quantity, AmountMinor = l.Amount.Minor, IsTaxable = l.IsTaxable, ReferenceId = l.ReferenceId,
            }).ToList();
            db.Payslips.Add(payslip);

            run.EmployeeCount++;
            run.GrossMinor += payslip.GrossMinor;
            run.DeductionsMinor += payslip.DeductionsMinor;
            run.NetMinor += payslip.NetMinor;
            run.EmployerContributionsMinor += payslip.EmployerContributionsMinor;
        }

        period.Status = PayrollPeriodStatus.Computed;
        audit.Record("Payroll.Computed", $"Payroll {range.From:MMMM yyyy} run {run.RunNumber}: {run.EmployeeCount} employees, net {new Money(run.NetMinor).Major:N2}", nameof(PayrollRun), run.Id);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        watch.Stop();
        logger.LogInformation("Payroll {Year}-{Month} computed: {Count} payslips in {Elapsed} ms", request.Year, request.Month, run.EmployeeCount, watch.ElapsedMilliseconds);
        return new PayrollRunSummary(run.Id, period.Id, run.RunNumber, run.EmployeeCount, new Money(run.GrossMinor), new Money(run.DeductionsMinor), new Money(run.NetMinor), withWarnings, watch.Elapsed);
    }
}

public sealed record PayslipRowDto(
    Guid Id,
    Guid EmployeeId,
    string EmployeeCode,
    string EmployeeName,
    string? Department,
    decimal PayableDays,
    int CalendarDays,
    Money Gross,
    Money Deductions,
    Money Net,
    Domain.Employees.PaymentMode PaymentMode,
    PaymentStatus PaymentStatus,
    DateOnly? PaidOn,
    string? Warnings)
{
    public bool HasWarnings => !string.IsNullOrEmpty(Warnings);
}

public sealed record PayrollRunDto(
    Guid Id,
    Guid PeriodId,
    int Year,
    int Month,
    int RunNumber,
    PayrollRunStatus Status,
    PayrollPeriodStatus PeriodStatus,
    DateTime ComputedAtUtc,
    string ComputedBy,
    DateTime? FinalizedAtUtc,
    string? FinalizedBy,
    string? VoidReason,
    Money Gross,
    Money Deductions,
    Money Net,
    Money EmployerContributions,
    IReadOnlyList<PayslipRowDto> Payslips)
{
    public bool IsDraft => Status == PayrollRunStatus.Draft;

    public bool IsFinalized => Status == PayrollRunStatus.Finalized;
}

[RequiresPermission(Permission.ViewSalaries)]
public sealed record GetPayrollRunQuery(Guid RunId) : IQuery<PayrollRunDto>;

public sealed class GetPayrollRunHandler(IAppDbContext db, ICurrentUser user) : IRequestHandler<GetPayrollRunQuery, PayrollRunDto>
{
    public async Task<Result<PayrollRunDto>> HandleAsync(GetPayrollRunQuery request, CancellationToken cancellationToken)
    {
        var run = await db.PayrollRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == request.RunId && r.CompanyId == user.CompanyId, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return Error.NotFound("PayrollRun");
        }

        var period = await db.PayrollPeriods.AsNoTracking().FirstAsync(p => p.Id == run.PeriodId, cancellationToken).ConfigureAwait(false);
        var slips = await db.Payslips.AsNoTracking().Where(p => p.RunId == run.Id).OrderBy(p => p.EmployeeCode)
            .Select(p => new PayslipRowDto(p.Id, p.EmployeeId, p.EmployeeCode, p.EmployeeName, p.Department, p.PayableDays, p.CalendarDays, new Money(p.GrossMinor),
                new Money(p.DeductionsMinor), new Money(p.NetMinor), p.PaymentMode, p.PaymentStatus, p.PaidOn, p.Warnings))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return new PayrollRunDto(run.Id, run.PeriodId, period.Year, period.Month, run.RunNumber, run.Status, period.Status, run.ComputedAtUtc, run.ComputedBy, run.FinalizedAtUtc,
            run.FinalizedBy, run.VoidReason, new Money(run.GrossMinor), new Money(run.DeductionsMinor), new Money(run.NetMinor), new Money(run.EmployerContributionsMinor), slips);
    }
}

public sealed record PayslipLineDto(string Code, string Name, ComponentKind Kind, PayslipLineSource Source, Money Amount, string? Basis, Money? FullAmount, decimal? Quantity);

public sealed record PayslipDto(
    Guid Id,
    Guid RunId,
    int Year,
    int Month,
    PayrollRunStatus RunStatus,
    Guid EmployeeId,
    string EmployeeCode,
    string EmployeeName,
    string? Department,
    string? Designation,
    Domain.Employees.PaymentMode PaymentMode,
    string? BankName,
    string? BankAccount,
    int CalendarDays,
    decimal PayableDays,
    decimal PresentDays,
    decimal HalfDays,
    decimal PaidLeaveDays,
    decimal UnpaidLeaveDays,
    decimal AbsentDays,
    int Holidays,
    int WeeklyOffs,
    int LateDays,
    decimal LatePenaltyDays,
    decimal OvertimeHours,
    Money Gross,
    Money Deductions,
    Money Net,
    Money EmployerContributions,
    PaymentStatus PaymentStatus,
    DateOnly? PaidOn,
    string? PaymentReference,
    string? Warnings,
    IReadOnlyList<PayslipLineDto> EarningLines,
    IReadOnlyList<PayslipLineDto> DeductionLines,
    IReadOnlyList<PayslipLineDto> EmployerLines);

[RequiresPermission(Permission.ViewSalaries)]
public sealed record GetPayslipQuery(Guid PayslipId) : IQuery<PayslipDto>;

public sealed class GetPayslipHandler(IAppDbContext db, ICurrentUser user) : IRequestHandler<GetPayslipQuery, PayslipDto>
{
    public async Task<Result<PayslipDto>> HandleAsync(GetPayslipQuery request, CancellationToken cancellationToken)
    {
        var slip = await db.Payslips.AsNoTracking().Include(p => p.Lines).FirstOrDefaultAsync(p => p.Id == request.PayslipId && p.CompanyId == user.CompanyId, cancellationToken).ConfigureAwait(false);
        if (slip is null)
        {
            return Error.NotFound("Payslip");
        }

        var run = await db.PayrollRuns.AsNoTracking().FirstAsync(r => r.Id == slip.RunId, cancellationToken).ConfigureAwait(false);
        var period = await db.PayrollPeriods.AsNoTracking().FirstAsync(p => p.Id == run.PeriodId, cancellationToken).ConfigureAwait(false);
        return PayslipMapper.ToDto(slip, run, period);
    }
}

public static class PayslipMapper
{
    public static PayslipDto ToDto(Payslip slip, PayrollRun run, PayrollPeriod period)
    {
        ArgumentNullException.ThrowIfNull(slip);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(period);
        IReadOnlyList<PayslipLineDto> Lines(ComponentKind kind) => slip.Lines.Where(l => l.Kind == kind).OrderBy(l => l.Order)
            .Select(l => new PayslipLineDto(l.Code, l.Name, l.Kind, l.Source, l.Amount, l.Basis, l.FullAmountMinor is { } f ? new Money(f) : null, l.Quantity)).ToList();
        return new PayslipDto(slip.Id, slip.RunId, period.Year, period.Month, run.Status, slip.EmployeeId, slip.EmployeeCode, slip.EmployeeName, slip.Department, slip.Designation,
            slip.PaymentMode, slip.BankName, slip.BankAccount, slip.CalendarDays, slip.PayableDays, slip.PresentDays, slip.HalfDays, slip.PaidLeaveDays, slip.UnpaidLeaveDays,
            slip.AbsentDays, slip.Holidays, slip.WeeklyOffs, slip.LateDays, slip.LatePenaltyDays, slip.OvertimeHours, slip.Gross, slip.Deductions, slip.Net,
            new Money(slip.EmployerContributionsMinor), slip.PaymentStatus, slip.PaidOn, slip.PaymentReference, slip.Warnings,
            Lines(ComponentKind.Earning), Lines(ComponentKind.Deduction), Lines(ComponentKind.EmployerContribution));
    }
}

/// <summary>Locks the run and its month (doc 03 R7). All or nothing; automatic device sync waits meanwhile.</summary>
[RequiresPermission(Permission.FinalizePayroll)]
public sealed record FinalizePayrollRunCommand(Guid RunId) : ICommand<Unit>;

public sealed class FinalizePayrollRunHandler(
    IAppDbContext db,
    ICurrentUser user,
    IClock clock,
    IAuditWriter audit,
    IDeviceSyncGate syncGate,
    PayrollLocks locks,
    IEnumerable<IPayrollParticipant> participants) : IRequestHandler<FinalizePayrollRunCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(FinalizePayrollRunCommand request, CancellationToken cancellationToken)
    {
        using var pause = syncGate.Pause("Finalizing payroll");
        var run = await db.PayrollRuns.FirstOrDefaultAsync(r => r.Id == request.RunId && r.CompanyId == user.CompanyId, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return Error.NotFound("PayrollRun");
        }

        if (run.Status != PayrollRunStatus.Draft)
        {
            return Error.Validation("Only a draft run can be finalized.");
        }

        var period = await db.PayrollPeriods.FirstAsync(p => p.Id == run.PeriodId, cancellationToken).ConfigureAwait(false);
        var payslips = await db.Payslips.Include(p => p.Lines).Where(p => p.RunId == run.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (payslips.Count == 0)
        {
            return Error.Validation("The run has no payslips.");
        }

        if (payslips.FirstOrDefault(p => p.NetMinor < 0) is { } negative)
        {
            return Error.Validation($"Net pay of {negative.EmployeeCode} {negative.EmployeeName} is negative. Fix the deductions and compute again.");
        }

        // Attendance or salary may have changed since this draft was computed.
        var lastChange = await LastInputChangeAsync(period, cancellationToken).ConfigureAwait(false);
        if (lastChange > run.ComputedAtUtc)
        {
            return Error.Conflict("Attendance, salary, loans or adjustments changed after this run was computed. Compute again, check the totals, then finalize.");
        }

        await using var tx = await db.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        run.Status = PayrollRunStatus.Finalized;
        run.FinalizedAtUtc = clock.UtcNow;
        run.FinalizedBy = user.UserName;
        period.Status = PayrollPeriodStatus.Locked;
        foreach (var participant in participants)
        {
            var posted = await participant.OnFinalizedAsync(run, period, payslips, cancellationToken).ConfigureAwait(false);
            if (posted.IsFailure)
            {
                return Result<Unit>.Failure(posted.Errors);
            }
        }

        audit.Record("Payroll.Finalized", $"Payroll {period.FromDate:MMMM yyyy} run {run.RunNumber} finalized: {payslips.Count} payslips, net {new Money(run.NetMinor).Major:N2}", nameof(PayrollRun), run.Id);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        locks.Invalidate();
        return Unit.Value;
    }

    private async Task<DateTime> LastInputChangeAsync(PayrollPeriod period, CancellationToken cancellationToken)
    {
        var from = period.FromDate;
        var to = period.ToDate;
        var days = await db.AttendanceDays.Where(d => d.CompanyId == user.CompanyId && d.Date >= from && d.Date <= to)
            .MaxAsync(d => (DateTime?)(d.ModifiedAtUtc ?? d.CreatedAtUtc), cancellationToken).ConfigureAwait(false);
        var salaries = await db.SalaryAssignments.Where(a => a.CompanyId == user.CompanyId && a.EffectiveFrom <= to && (a.EffectiveTo == null || a.EffectiveTo >= from))
            .MaxAsync(a => (DateTime?)(a.ModifiedAtUtc ?? a.CreatedAtUtc), cancellationToken).ConfigureAwait(false);
        var adjustments = await db.PayrollAdjustments.Where(a => a.CompanyId == user.CompanyId && a.Year == period.Year && a.Month == period.Month)
            .MaxAsync(a => (DateTime?)(a.ModifiedAtUtc ?? a.CreatedAtUtc), cancellationToken).ConfigureAwait(false);
        var latest = new[] { days, salaries, adjustments }.Max() ?? DateTime.MinValue;
        foreach (var participant in participants)
        {
            if (await participant.LastInputChangeAsync(period, cancellationToken).ConfigureAwait(false) is { } changed && changed > latest)
            {
                latest = changed;
            }
        }

        return latest;
    }
}

/// <summary>Reverses the latest finalized run of a month and reopens it (doc 03 R7). Payslips must not be marked paid.</summary>
[RequiresPermission(Permission.FinalizePayroll)]
public sealed record VoidPayrollRunCommand(Guid RunId, string Reason) : ICommand<Unit>;

public sealed class VoidPayrollRunValidator : AbstractValidator<VoidPayrollRunCommand>
{
    public VoidPayrollRunValidator() => RuleFor(x => x.Reason).NotEmpty().WithMessage("Enter the reason for voiding.").MaximumLength(500);
}

public sealed class VoidPayrollRunHandler(IAppDbContext db, ICurrentUser user, IClock clock, IAuditWriter audit, PayrollLocks locks, IEnumerable<IPayrollParticipant> participants)
    : IRequestHandler<VoidPayrollRunCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(VoidPayrollRunCommand request, CancellationToken cancellationToken)
    {
        var run = await db.PayrollRuns.FirstOrDefaultAsync(r => r.Id == request.RunId && r.CompanyId == user.CompanyId, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return Error.NotFound("PayrollRun");
        }

        if (run.Status != PayrollRunStatus.Finalized)
        {
            return Error.Validation("Only a finalized run can be voided. A draft can simply be computed again.");
        }

        var period = await db.PayrollPeriods.FirstAsync(p => p.Id == run.PeriodId, cancellationToken).ConfigureAwait(false);
        var laterLocked = await db.PayrollPeriods.AnyAsync(
            p => p.CompanyId == user.CompanyId && (p.Year > period.Year || (p.Year == period.Year && p.Month > period.Month))
                 && (p.Status == PayrollPeriodStatus.Locked || p.Status == PayrollPeriodStatus.Paid), cancellationToken).ConfigureAwait(false);
        if (laterLocked)
        {
            return Error.Validation("A later month is finalized. Void the latest month first.");
        }

        var payslips = await db.Payslips.Include(p => p.Lines).Where(p => p.RunId == run.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (payslips.Any(p => p.PaymentStatus == PaymentStatus.Paid))
        {
            return Error.Validation("Some payslips are marked paid. Mark them unpaid first.");
        }

        await using var tx = await db.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        run.Status = PayrollRunStatus.Voided;
        run.VoidedAtUtc = clock.UtcNow;
        run.VoidReason = request.Reason.Trim();
        period.Status = PayrollPeriodStatus.Open;
        foreach (var participant in participants)
        {
            var reverted = await participant.OnVoidedAsync(run, period, payslips, cancellationToken).ConfigureAwait(false);
            if (reverted.IsFailure)
            {
                return Result<Unit>.Failure(reverted.Errors);
            }
        }

        audit.Record("Payroll.Voided", $"Payroll {period.FromDate:MMMM yyyy} run {run.RunNumber} voided: {run.VoidReason}", nameof(PayrollRun), run.Id);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        locks.Invalidate();
        return Unit.Value;
    }
}

/// <summary>Marks payslips of a finalized run paid (or unpaid again). The month becomes Paid when all are paid.</summary>
[RequiresPermission(Permission.ManagePayroll)]
public sealed record MarkPayslipsPaidCommand(IReadOnlyList<Guid> PayslipIds, bool Paid, DateOnly? PaidOn, string? Reference) : ICommand<int>;

public sealed class MarkPayslipsPaidHandler(IAppDbContext db, ICurrentUser user, IAuditWriter audit) : IRequestHandler<MarkPayslipsPaidCommand, int>
{
    public async Task<Result<int>> HandleAsync(MarkPayslipsPaidCommand request, CancellationToken cancellationToken)
    {
        var ids = request.PayslipIds.ToHashSet();
        var slips = await db.Payslips.Where(p => ids.Contains(p.Id) && p.CompanyId == user.CompanyId).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (slips.Count == 0)
        {
            return Error.Validation("Choose at least one payslip.");
        }

        var runIds = slips.Select(s => s.RunId).Distinct().ToList();
        var runs = await db.PayrollRuns.Where(r => runIds.Contains(r.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (runs.Any(r => r.Status != PayrollRunStatus.Finalized))
        {
            return Error.Validation("Finalize the payroll before marking payments.");
        }

        if (request.Paid && request.PaidOn is null)
        {
            return Error.Validation("PaidOn", "Enter the payment date.");
        }

        foreach (var slip in slips)
        {
            slip.PaymentStatus = request.Paid ? PaymentStatus.Paid : PaymentStatus.Unpaid;
            slip.PaidOn = request.Paid ? request.PaidOn : null;
            slip.PaymentReference = request.Paid ? request.Reference?.Trim() : null;
        }

        foreach (var run in runs)
        {
            var period = await db.PayrollPeriods.FirstAsync(p => p.Id == run.PeriodId, cancellationToken).ConfigureAwait(false);
            var others = await db.Payslips.Where(p => p.RunId == run.Id && !ids.Contains(p.Id)).AllAsync(p => p.PaymentStatus == PaymentStatus.Paid, cancellationToken).ConfigureAwait(false);
            period.Status = request.Paid && others ? PayrollPeriodStatus.Paid : PayrollPeriodStatus.Locked;
        }

        audit.Record(request.Paid ? "Payroll.MarkedPaid" : "Payroll.MarkedUnpaid", $"{slips.Count} payslips marked {(request.Paid ? "paid" : "unpaid")}{(request.Reference is null ? string.Empty : " (" + request.Reference + ")")}");
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return slips.Count;
    }
}

public sealed record PayrollAdjustmentDto(Guid Id, Guid EmployeeId, string EmployeeCode, string EmployeeName, string Name, ComponentKind Kind, Money Amount, bool IsTaxable, string? Reason);

[RequiresPermission(Permission.ViewSalaries)]
public sealed record GetPayrollAdjustmentsQuery(int Year, int Month) : IQuery<IReadOnlyList<PayrollAdjustmentDto>>;

public sealed class GetPayrollAdjustmentsHandler(IAppDbContext db, ICurrentUser user) : IRequestHandler<GetPayrollAdjustmentsQuery, IReadOnlyList<PayrollAdjustmentDto>>
{
    public async Task<Result<IReadOnlyList<PayrollAdjustmentDto>>> HandleAsync(GetPayrollAdjustmentsQuery request, CancellationToken cancellationToken)
    {
        var rows = await (from a in db.PayrollAdjustments.AsNoTracking()
                          join e in db.Employees.AsNoTracking() on a.EmployeeId equals e.Id
                          where a.CompanyId == user.CompanyId && a.Year == request.Year && a.Month == request.Month
                          orderby e.Code
                          select new PayrollAdjustmentDto(a.Id, a.EmployeeId, e.Code, e.FullName, a.Name, a.Kind, new Money(a.AmountMinor), a.IsTaxable, a.Reason))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows;
    }
}

[RequiresPermission(Permission.ManagePayroll)]
public sealed record SavePayrollAdjustmentCommand(Guid? Id, Guid EmployeeId, int Year, int Month, string Name, ComponentKind Kind, long AmountMinor, bool IsTaxable, string? Reason) : ICommand<Guid>;

public sealed class SavePayrollAdjustmentValidator : AbstractValidator<SavePayrollAdjustmentCommand>
{
    public SavePayrollAdjustmentValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Enter what the adjustment is for.").MaximumLength(100);
        RuleFor(x => x.AmountMinor).GreaterThan(0).WithMessage("Enter an amount above zero.");
        RuleFor(x => x.Kind).NotEqual(ComponentKind.EmployerContribution).WithMessage("Choose earning or deduction.");
        RuleFor(x => x.Reason).MaximumLength(300);
        RuleFor(x => x.Month).InclusiveBetween(1, 12);
    }
}

public sealed class SavePayrollAdjustmentHandler(IAppDbContext db, ICurrentUser user, IIdGenerator ids, IAuditWriter audit, PayrollLocks locks) : IRequestHandler<SavePayrollAdjustmentCommand, Guid>
{
    public async Task<Result<Guid>> HandleAsync(SavePayrollAdjustmentCommand request, CancellationToken cancellationToken)
    {
        var range = DateRange.Month(request.Year, request.Month);
        if (await locks.FirstLockedInAsync(range, cancellationToken).ConfigureAwait(false) is { } locked)
        {
            return PayrollLocks.LockedError(locked, "Month");
        }

        var employee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == request.EmployeeId && e.CompanyId == user.CompanyId, cancellationToken).ConfigureAwait(false);
        if (employee is null)
        {
            return Error.NotFound("Employee");
        }

        PayrollAdjustment adjustment;
        if (request.Id is { } id)
        {
            var found = await db.PayrollAdjustments.FirstOrDefaultAsync(a => a.Id == id && a.CompanyId == user.CompanyId, cancellationToken).ConfigureAwait(false);
            if (found is null)
            {
                return Error.NotFound("PayrollAdjustment");
            }

            adjustment = found;
        }
        else
        {
            adjustment = new PayrollAdjustment { Id = ids.NewId(), CompanyId = user.CompanyId };
            db.PayrollAdjustments.Add(adjustment);
        }

        adjustment.EmployeeId = employee.Id;
        adjustment.Year = request.Year;
        adjustment.Month = request.Month;
        adjustment.Name = request.Name.Trim();
        adjustment.Kind = request.Kind;
        adjustment.AmountMinor = request.AmountMinor;
        adjustment.IsTaxable = request.IsTaxable && request.Kind == ComponentKind.Earning;
        adjustment.Reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
        audit.Record(request.Id is null ? "Payroll.AdjustmentAdded" : "Payroll.AdjustmentUpdated",
            $"{adjustment.Kind} {adjustment.Name} {new Money(adjustment.AmountMinor).Major:N2} for {employee.Code} in {range.From:MMMM yyyy}", "Employee", employee.Id);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return adjustment.Id;
    }
}

[RequiresPermission(Permission.ManagePayroll)]
public sealed record DeletePayrollAdjustmentCommand(Guid Id) : ICommand<Unit>;

public sealed class DeletePayrollAdjustmentHandler(IAppDbContext db, ICurrentUser user, IAuditWriter audit, PayrollLocks locks) : IRequestHandler<DeletePayrollAdjustmentCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(DeletePayrollAdjustmentCommand request, CancellationToken cancellationToken)
    {
        var adjustment = await db.PayrollAdjustments.FirstOrDefaultAsync(a => a.Id == request.Id && a.CompanyId == user.CompanyId, cancellationToken).ConfigureAwait(false);
        if (adjustment is null)
        {
            return Error.NotFound("PayrollAdjustment");
        }

        if (await locks.FirstLockedInAsync(DateRange.Month(adjustment.Year, adjustment.Month), cancellationToken).ConfigureAwait(false) is { } locked)
        {
            return PayrollLocks.LockedError(locked, "Month");
        }

        db.PayrollAdjustments.Remove(adjustment);
        audit.Record("Payroll.AdjustmentDeleted", $"{adjustment.Name} removed", "Employee", adjustment.EmployeeId);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
