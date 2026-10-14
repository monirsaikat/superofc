using Microsoft.EntityFrameworkCore;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Common;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Devices;
using SuperOfc.Domain.Loans;
using SuperOfc.Domain.Payroll;
using SuperOfc.Domain.Security;

namespace SuperOfc.Application.Reports;

public sealed record PayrollFigure(int Year, int Month, PayrollPeriodStatus? Status, Money Net, int Employees);

public sealed record LoanFigure(int Open, Money Outstanding, int Requested);

public sealed record DeviceFigure(int Devices, int NeedAttention, int UnlinkedPunches);

/// <summary>
/// Module figures for the dashboard in one request (P7-T3). Sections the user may not see are null.
/// Each section is one or two aggregate queries, so the whole call stays fast on large data.
/// </summary>
public sealed record DashboardFigures(PayrollFigure? Payroll, LoanFigure? Loans, DeviceFigure? Devices, int DocumentsExpiring, bool TaxRulesMissing);

[RequiresPermission(Permission.ViewData)]
public sealed record GetDashboardQuery : IQuery<DashboardFigures>;

public sealed class GetDashboardHandler(IAppDbContext db, ICurrentUser user, IClock clock, ICompanyContext company) : IRequestHandler<GetDashboardQuery, DashboardFigures>
{
    public async Task<Result<DashboardFigures>> HandleAsync(GetDashboardQuery request, CancellationToken cancellationToken)
    {
        var companyId = user.CompanyId;
        var today = clock.TodayIn(company.TimeZone);
        var salaries = PermissionMatrix.Has(user.Role, Permission.ViewSalaries);

        PayrollFigure? payroll = null;
        LoanFigure? loans = null;
        var taxMissing = false;
        if (salaries)
        {
            // Last month's payroll: the month most offices are working on.
            var last = new DateOnly(today.Year, today.Month, 1).AddMonths(-1);
            var period = await db.PayrollPeriods.AsNoTracking().FirstOrDefaultAsync(p => p.CompanyId == companyId && p.Year == last.Year && p.Month == last.Month, cancellationToken)
                .ConfigureAwait(false);
            var run = period is null
                ? null
                : await db.PayrollRuns.AsNoTracking().Where(r => r.PeriodId == period.Id && r.Status != PayrollRunStatus.Voided).OrderByDescending(r => r.RunNumber)
                    .Select(r => new { r.Id, r.EmployeeCount }).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            var net = run is null ? 0 : await db.Payslips.AsNoTracking().Where(s => s.RunId == run.Id).SumAsync(s => s.NetMinor, cancellationToken).ConfigureAwait(false);
            payroll = new PayrollFigure(last.Year, last.Month, period?.Status, new Money(net), run?.EmployeeCount ?? 0);

            var open = await db.Loans.AsNoTracking().Where(l => l.CompanyId == companyId && l.Status == LoanStatus.Active).Select(l => l.Id).ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            var outstanding = open.Count == 0
                ? 0
                : await db.LoanInstallments.AsNoTracking().Where(i => open.Contains(i.LoanId) && (i.Status == InstallmentStatus.Due || i.Status == InstallmentStatus.Skipped))
                    .SumAsync(i => i.AmountMinor - i.PaidMinor, cancellationToken).ConfigureAwait(false);
            var requested = await db.Loans.AsNoTracking().CountAsync(l => l.CompanyId == companyId && l.Status == LoanStatus.Requested, cancellationToken).ConfigureAwait(false);
            loans = new LoanFigure(open.Count, new Money(outstanding), requested);

            var startMonth = company.Company?.FiscalYearStartMonth ?? 7;
            var fiscalYear = today.Month >= startMonth ? today.Year : today.Year - 1;
            taxMissing = !await db.TaxRuleSets.AsNoTracking().AnyAsync(r => r.CompanyId == companyId && r.FiscalYear == fiscalYear, cancellationToken).ConfigureAwait(false);
        }

        DeviceFigure? devices = null;
        if (PermissionMatrix.Has(user.Role, Permission.ManageDevices))
        {
            var stale = clock.UtcNow.AddDays(-1);
            var list = await db.Devices.AsNoTracking().Where(d => d.CompanyId == companyId && d.DeletedAtUtc == null && d.IsEnabled)
                .Select(d => new { d.Status, d.LastSyncAtUtc }).ToListAsync(cancellationToken).ConfigureAwait(false);
            var unlinked = await db.UnmappedPunches.AsNoTracking().CountAsync(p => p.CompanyId == companyId, cancellationToken).ConfigureAwait(false);
            devices = new DeviceFigure(list.Count, list.Count(d => d.Status is DeviceStatus.Offline or DeviceStatus.Error || d.LastSyncAtUtc is null || d.LastSyncAtUtc < stale),
                unlinked);
        }

        var soon = today.AddDays(30);
        var expiring = await (from d in db.EmployeeDocuments.AsNoTracking()
                              join e in db.Employees.AsNoTracking() on d.EmployeeId equals e.Id
                              where e.CompanyId == companyId && e.DeletedAtUtc == null && e.ExitDate == null && d.ExpiryDate != null && d.ExpiryDate <= soon
                              select d.Id).CountAsync(cancellationToken).ConfigureAwait(false);

        return new DashboardFigures(payroll, loans, devices, expiring, taxMissing);
    }
}
