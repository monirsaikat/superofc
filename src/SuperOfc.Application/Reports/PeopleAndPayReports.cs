using Microsoft.EntityFrameworkCore;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Leave;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Loans;
using SuperOfc.Domain.Payroll;
using SuperOfc.Domain.Security;

namespace SuperOfc.Application.Reports;

/// <summary>Headcount at the start and end of a period with joiners and leavers, per department.</summary>
public sealed class HeadcountReport(ReportData data) : IReport
{
    public string Id => "headcount";

    public string Name => "Headcount, joiners and leavers";

    public string Category => "People";

    public string Description => "Employees at the start and end of a period, who joined and who left, per department.";

    public Permission Permission => Permission.ViewData;

    public IReadOnlyList<ReportParameter> Parameters { get; } = [ReportData.PeriodParameter];

    public async Task<ReportTable> RunAsync(ReportArgs args, CancellationToken cancellationToken)
    {
        var period = args.Range("Period", 3660);
        var from = period.From;
        var to = period.To!.Value;
        var people = await data.PeopleAsync(new DateRange(from.AddDays(-1), to), null, cancellationToken).ConfigureAwait(false);

        // An employee counts on their exit date and leaves after it: opening + joined - left = closing.
        var builder = new ReportBuilder("Headcount, joiners and leavers", ReportText.Range(period))
            .Column("Department", width: 2).Column("At start", ReportColumnKind.Integer, true).Column("Joined", ReportColumnKind.Integer, true)
            .Column("Left", ReportColumnKind.Integer, true).Column("At end", ReportColumnKind.Integer, true).Column("Turnover %", ReportColumnKind.Number);
        foreach (var g in people.GroupBy(p => p.Department).OrderBy(g => g.Key, StringComparer.CurrentCulture))
        {
            var opening = g.Count(p => p.JoinDate < from && (p.ExitDate is null || p.ExitDate >= from));
            var joined = g.Count(p => p.JoinDate >= from && p.JoinDate <= to);
            var left = g.Count(p => p.ExitDate is { } x && x >= from && x <= to && p.JoinDate <= to);
            var closing = g.Count(p => p.JoinDate <= to && (p.ExitDate is null || p.ExitDate > to));
            builder.Row(g.Key, opening, joined, left, closing, Turnover(left, opening, closing));
        }

        builder.Note("Turnover = leavers / average of the headcount at start and end.");
        return builder.Build();
    }

    private static decimal Turnover(int left, int opening, int closing)
        => opening + closing == 0 ? 0 : Math.Round(left * 200m / (opening + closing), 1);
}

/// <summary>Salary cost per department from the finalized payroll of a month.</summary>
public sealed class SalaryCostReport(IAppDbContext db, ICurrentUser user) : IReport
{
    public string Id => "salary-cost";

    public string Name => "Salary cost by department";

    public string Category => "Payroll";

    public string Description => "Gross pay, deductions, net pay and employer contributions per department from finalized payroll.";

    public Permission Permission => Permission.ViewSalaries;

    public IReadOnlyList<ReportParameter> Parameters { get; } = [ReportData.MonthParameter];

    public async Task<ReportTable> RunAsync(ReportArgs args, CancellationToken cancellationToken)
    {
        var month = args.Month("Month");
        var runId = await (from r in db.PayrollRuns.AsNoTracking()
                           join p in db.PayrollPeriods.AsNoTracking() on r.PeriodId equals p.Id
                           where r.CompanyId == user.CompanyId && p.Year == month.From.Year && p.Month == month.From.Month && r.Status == PayrollRunStatus.Finalized
                           select (Guid?)r.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var slips = runId is { } id
            ? await db.Payslips.AsNoTracking().Where(s => s.RunId == id).ToListAsync(cancellationToken).ConfigureAwait(false)
            : [];

        var builder = new ReportBuilder("Salary cost by department", ReportText.MonthName(month))
            .Column("Department", width: 2).Column("Employees", ReportColumnKind.Integer, true).Column("Gross", ReportColumnKind.Money, true)
            .Column("Deductions", ReportColumnKind.Money, true).Column("Net pay", ReportColumnKind.Money, true).Column("Employer contributions", ReportColumnKind.Money, true)
            .Column("Total cost", ReportColumnKind.Money, true);
        foreach (var g in slips.GroupBy(s => s.Department ?? "No department").OrderBy(g => g.Key, StringComparer.CurrentCulture))
        {
            long gross = g.Sum(s => s.GrossMinor), employer = g.Sum(s => s.EmployerContributionsMinor);
            builder.Row(g.Key, g.Count(), new Money(gross).Major, new Money(g.Sum(s => s.DeductionsMinor)).Major, new Money(g.Sum(s => s.NetMinor)).Major,
                new Money(employer).Major, new Money(gross + employer).Major);
        }

        if (runId is null)
        {
            builder.Note("Payroll for this month is not finalized yet.");
        }

        builder.Note("Total cost = gross pay + employer contributions. Departments are as printed on the payslips.");
        return builder.Build();
    }
}

/// <summary>Every loan pay-out and repayment in a period.</summary>
public sealed class LoanLedgerReport(IAppDbContext db, ICurrentUser user) : IReport
{
    public string Id => "loan-ledger";

    public string Name => "Loan ledger";

    public string Category => "Payroll";

    public string Description => "Every loan and advance paid out and repaid in a period, with salary deductions and cash repayments.";

    public Permission Permission => Permission.ViewSalaries;

    public IReadOnlyList<ReportParameter> Parameters { get; } = [ReportData.PeriodParameter];

    public async Task<ReportTable> RunAsync(ReportArgs args, CancellationToken cancellationToken)
    {
        var period = args.Range("Period", 3660);
        var to = period.To!.Value;
        var rows = await (from t in db.LoanTransactions.AsNoTracking()
                          join l in db.Loans.AsNoTracking() on t.LoanId equals l.Id
                          join e in db.Employees.AsNoTracking() on l.EmployeeId equals e.Id
                          where l.CompanyId == user.CompanyId && t.Date >= period.From && t.Date <= to
                          orderby t.Date, l.Number
                          select new { t.Date, l.Number, e.Code, e.FullName, t.Kind, t.AmountMinor, t.Reference })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var builder = new ReportBuilder("Loan ledger", ReportText.Range(period))
            .Column("Date", ReportColumnKind.Date).Column("Loan").Column("Code").Column("Name", width: 2).Column("Entry", width: 1.5f)
            .Column("Paid out", ReportColumnKind.Money, true).Column("Repaid or cleared", ReportColumnKind.Money, true).Column("Reference", width: 1.2f);
        foreach (var r in rows)
        {
            var amount = new Money(r.AmountMinor).Major;
            var payOut = r.Kind == LoanTransactionKind.Disbursement;
            builder.Row(r.Date, r.Number, r.Code, r.FullName, Entry(r.Kind), payOut ? amount : null, payOut ? null : amount, r.Reference);
        }

        builder.Note("Repaid or cleared includes salary deductions, cash repayments, settlements, waived interest and write-offs.");
        return builder.Build();
    }

    private static string Entry(LoanTransactionKind kind) => kind switch
    {
        LoanTransactionKind.Disbursement => "Paid out",
        LoanTransactionKind.PayrollRepayment => "Salary deduction",
        LoanTransactionKind.ManualRepayment => "Cash repayment",
        LoanTransactionKind.Settlement => "Early settlement",
        LoanTransactionKind.InterestWaiver => "Interest waived",
        LoanTransactionKind.WriteOff => "Written off",
        _ => kind.ToString(),
    };
}

/// <summary>Leave balances for a leave year: opening, credited, taken, adjusted and closing, per employee and leave type.</summary>
public sealed class LeaveBalanceReport(ReportData data, IAppDbContext db, ICurrentUser user, LeaveLedger ledger) : IReport
{
    public string Id => "leave-balance";

    public string Name => "Leave balances";

    public string Category => "Attendance";

    public string Description => "Each employee's leave for a year: opening balance, credited, taken, adjusted and what is left.";

    public Permission Permission => Permission.ViewData;

    public IReadOnlyList<ReportParameter> Parameters { get; } =
    [
        new("Year", "Leave year", ReportParameterKind.FiscalYear),
        new("LeaveType", "Leave type", ReportParameterKind.LeaveType, Required: false),
        ReportData.DepartmentParameter,
    ];

    public async Task<ReportTable> RunAsync(ReportArgs args, CancellationToken cancellationToken)
    {
        var year = args.Year("Year");
        var typeId = args.Id("LeaveType");
        var departmentId = args.Id("Department");
        var company = await ledger.CompanyAsync(cancellationToken).ConfigureAwait(false);
        var start = LeaveLedger.YearStart(company, year);
        var range = new DateRange(start, start.AddYears(1).AddDays(-1));
        var people = await data.PeopleAsync(range, departmentId, cancellationToken).ConfigureAwait(false);
        var types = await db.LeaveTypes.AsNoTracking().Where(t => t.CompanyId == user.CompanyId && t.DeletedAtUtc == null && (typeId == null || t.Id == typeId))
            .OrderBy(t => t.SortOrder).ThenBy(t => t.Name).ToListAsync(cancellationToken).ConfigureAwait(false);
        var ids = people.Select(p => p.Id).ToList();
        var employees = await db.Employees.AsNoTracking().Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, cancellationToken).ConfigureAwait(false);

        var builder = new ReportBuilder("Leave balances",
                $"Leave year {ReportText.FiscalYear(year, company.FiscalYearStartMonth)} · {await data.DepartmentNameAsync(departmentId, cancellationToken).ConfigureAwait(false)}")
            .Column("Code").Column("Name", width: 2).Column("Department", width: 1.5f).Column("Leave type", width: 1.5f).Column("Opening", ReportColumnKind.Number, true)
            .Column("Credited", ReportColumnKind.Number, true).Column("Taken", ReportColumnKind.Number, true).Column("Adjusted", ReportColumnKind.Number, true)
            .Column("Left", ReportColumnKind.Number, true);
        foreach (var p in people)
        {
            foreach (var type in types)
            {
                var b = await ledger.ProjectAsync(company, employees[p.Id], type, year, cancellationToken).ConfigureAwait(false);
                builder.Row(p.Code, p.Name, p.Department, type.Name, b.Opening, b.Accrued, b.Used, b.Adjusted, b.Closing);
            }
        }

        builder.Note("Left = opening + credited - taken + adjusted.");
        return builder.Build();
    }
}
