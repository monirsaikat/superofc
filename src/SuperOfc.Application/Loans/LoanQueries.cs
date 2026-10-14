using Microsoft.EntityFrameworkCore;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Common;
using SuperOfc.Application.Payroll;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Employees;
using SuperOfc.Domain.Loans;
using SuperOfc.Domain.Payroll;
using SuperOfc.Domain.Policies;
using SuperOfc.Domain.Security;

namespace SuperOfc.Application.Loans;

public sealed record LoanListItem(
    Guid Id,
    string Number,
    Guid EmployeeId,
    string EmployeeCode,
    string EmployeeName,
    LoanKind Kind,
    LoanStatus Status,
    Money Principal,
    Money Total,
    Money Installment,
    int InstallmentCount,
    int InstallmentsDone,
    DateOnly FirstMonth,
    Money Outstanding,
    DateOnly? NextDue,
    bool EmployeeLeft,
    DateOnly RequestedOn)
{
    /// <summary>The employee has left but still owes money (doc 10 Q26 receivable).</summary>
    public bool IsReceivable => EmployeeLeft && Outstanding.Minor > 0;
}

public sealed record LoanInstallmentDto(Guid Id, int Sequence, int Year, int Month, Money Principal, Money Interest, Money Amount, Money Paid, InstallmentStatus Status, string? Note, bool IsCarried)
{
    public DateOnly DueMonth => new(Year, Month, 1);
}

/// <summary>One statement row: money lent (debit) or repaid (credit) with the running balance.</summary>
public sealed record LoanStatementLine(DateOnly Date, LoanTransactionKind? Kind, string Description, Money Debit, Money Credit, Money Balance, string? Reference);

public sealed record LoanDetail(
    LoanListItem Summary,
    InterestMethod InterestMethod,
    decimal AnnualRate,
    Money Interest,
    string? Purpose,
    string? ApprovedBy,
    DateTime? ApprovedAtUtc,
    DateOnly? DisbursedOn,
    string? DisbursementReference,
    DateOnly? ClosedOn,
    string? Note,
    IReadOnlyList<LoanInstallmentDto> Installments,
    IReadOnlyList<LoanStatementLine> Statement,
    LoanBalance Balance,
    SettlementQuote? Settlement);

[RequiresPermission(Permission.ViewSalaries)]
public sealed record GetLoansQuery(LoanStatus? Status = null, Guid? EmployeeId = null, string? Search = null, bool OpenOnly = false) : IQuery<IReadOnlyList<LoanListItem>>;

public sealed class GetLoansHandler(IAppDbContext db, ICurrentUser user) : IRequestHandler<GetLoansQuery, IReadOnlyList<LoanListItem>>
{
    public async Task<Result<IReadOnlyList<LoanListItem>>> HandleAsync(GetLoansQuery request, CancellationToken cancellationToken)
    {
        var query = db.Loans.AsNoTracking().Include(l => l.Installments).Include(l => l.Transactions).Where(l => l.CompanyId == user.CompanyId);
        if (request.Status is { } status)
        {
            query = query.Where(l => l.Status == status);
        }

        if (request.OpenOnly)
        {
            query = query.Where(l => l.Status == LoanStatus.Requested || l.Status == LoanStatus.Approved || l.Status == LoanStatus.Active);
        }

        if (request.EmployeeId is { } employeeId)
        {
            query = query.Where(l => l.EmployeeId == employeeId);
        }

        var loans = await query.ToListAsync(cancellationToken).ConfigureAwait(false);
        var employees = await LoanData.EmployeesAsync(db, loans.Select(l => l.EmployeeId), cancellationToken).ConfigureAwait(false);
        var rows = loans.Select(l => LoanData.ToListItem(l, employees[l.EmployeeId]));
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var text = request.Search.Trim();
            rows = rows.Where(r => r.Number.Contains(text, StringComparison.OrdinalIgnoreCase) || r.EmployeeCode.Contains(text, StringComparison.OrdinalIgnoreCase)
                                   || r.EmployeeName.Contains(text, StringComparison.CurrentCultureIgnoreCase));
        }

        return rows.OrderBy(r => r.Status switch { LoanStatus.Requested => 0, LoanStatus.Approved => 1, LoanStatus.Active => 2, _ => 3 })
            .ThenByDescending(r => r.RequestedOn).ThenByDescending(r => r.Number, StringComparer.Ordinal).ToList();
    }
}

[RequiresPermission(Permission.ViewSalaries)]
public sealed record GetLoanQuery(Guid Id) : IQuery<LoanDetail>;

public sealed class GetLoanHandler(IAppDbContext db, ICurrentUser user, IClock clock, ICompanyContext company, IPolicyProvider policies) : IRequestHandler<GetLoanQuery, LoanDetail>
{
    public Task<Result<LoanDetail>> HandleAsync(GetLoanQuery request, CancellationToken cancellationToken)
        => DetailAsync(db, user, clock, company, policies, request.Id, cancellationToken);

    internal static async Task<Result<LoanDetail>> DetailAsync(
        IAppDbContext db, ICurrentUser user, IClock clock, ICompanyContext company, IPolicyProvider policies, Guid id, CancellationToken cancellationToken)
    {
        var loan = await LoanData.LoadAsync(db, user, id, tracking: false, cancellationToken).ConfigureAwait(false);
        if (loan is null)
        {
            return Error.NotFound("Loan");
        }

        var employees = await LoanData.EmployeesAsync(db, [loan.EmployeeId], cancellationToken).ConfigureAwait(false);
        var today = clock.TodayIn(company.TimeZone);
        SettlementQuote? quote = null;
        if (loan.Status == LoanStatus.Active)
        {
            var policy = await policies.GetSnapshotAsync(today, cancellationToken).ConfigureAwait(false);
            quote = LoanBalanceCalculator.Settlement(loan, policy.Loan.SettlementInterest, today.Year, today.Month);
        }

        return new LoanDetail(LoanData.ToListItem(loan, employees[loan.EmployeeId]), loan.InterestMethod, loan.AnnualRate, loan.Interest, loan.Purpose, loan.ApprovedBy,
            loan.ApprovedAtUtc, loan.DisbursedOn, loan.DisbursementReference, loan.ClosedOn, loan.Note,
            loan.Installments.OrderBy(i => i.Sequence).Select(LoanData.ToDto).ToList(), LoanData.Statement(loan), LoanBalanceCalculator.Balance(loan), quote);
    }
}

public sealed record LoanPreview(LoanSchedule? Schedule, Money? CurrentNet, Money? ProjectedNet, Money? Gross, IReadOnlyList<string> Warnings);

/// <summary>Live schedule for the new-loan form, with the employee's projected net pay and warnings (P5-T2).</summary>
[RequiresPermission(Permission.ViewSalaries)]
public sealed record PreviewLoanQuery(Guid? EmployeeId, Guid? LoanId, long PrincipalMinor, InterestMethod Method, decimal AnnualRate, int InstallmentCount, int StartYear, int StartMonth)
    : IQuery<LoanPreview>;

public sealed class PreviewLoanHandler(IAppDbContext db, ICurrentUser user, IClock clock, ICompanyContext company, IPolicyProvider policies) : IRequestHandler<PreviewLoanQuery, LoanPreview>
{
    public async Task<Result<LoanPreview>> HandleAsync(PreviewLoanQuery request, CancellationToken cancellationToken)
    {
        var today = clock.TodayIn(company.TimeZone);
        var policy = await policies.GetSnapshotAsync(today, cancellationToken).ConfigureAwait(false);
        var schedule = LoanScheduleGenerator.Generate(new ScheduleRequest(new Money(request.PrincipalMinor), request.Method, request.AnnualRate, request.InstallmentCount,
            request.StartYear, request.StartMonth, policy.Payroll.LineRounding));
        if (schedule.IsFailure)
        {
            return new LoanPreview(null, null, null, null, [.. schedule.Errors.Select(e => e.Message)]);
        }

        var warnings = new List<string>();
        if (request.EmployeeId is not { } employeeId)
        {
            return new LoanPreview(schedule.Value, null, null, null, warnings);
        }

        var check = await LoanAffordability.CheckAsync(db, user, employeeId, request.LoanId, schedule.Value, policy, cancellationToken).ConfigureAwait(false);
        warnings.AddRange(check.Warnings);
        return new LoanPreview(schedule.Value, check.CurrentNet, check.ProjectedNet, check.Gross, warnings);
    }
}

/// <summary>Projected net pay after a new installment (P5-T2: approval is blocked below the minimum net).</summary>
public static class LoanAffordability
{
    public sealed record Check(Money? Gross, Money? CurrentNet, Money? ProjectedNet, bool BelowMinimum, IReadOnlyList<string> Warnings);

    public static async Task<Check> CheckAsync(IAppDbContext db, ICurrentUser user, Guid employeeId, Guid? loanId, LoanSchedule schedule, PolicySnapshot policy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(policy);
        var warnings = new List<string>();
        var first = schedule.Installments[0];
        var firstDay = new DateOnly(first.Year, first.Month, 1);
        var assignment = await db.SalaryAssignments.AsNoTracking().Include(a => a.Lines)
            .Where(a => a.EmployeeId == employeeId && a.CompanyId == user.CompanyId && a.EffectiveFrom <= firstDay.AddMonths(1).AddDays(-1))
            .OrderByDescending(a => a.EffectiveFrom).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (assignment is null)
        {
            warnings.Add("The employee has no salary yet, so net pay after the installment cannot be checked.");
            return new Check(null, null, null, false, warnings);
        }

        var components = await db.SalaryComponents.AsNoTracking().Where(c => c.CompanyId == user.CompanyId && c.DeletedAtUtc == null && c.IsActive)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var preview = SalaryLines.Preview(assignment.Lines.Select(l => new SalaryLineInput(l.ComponentId, l.AmountMinor, l.Rate)).ToList(),
            components.Select(ComponentDef.From).ToList(), policy.Payroll.LineRounding);

        // Other loans of the same employee due in the first month of this one.
        var index = (first.Year * 12) + first.Month - 1;
        var others = await db.LoanInstallments.AsNoTracking()
            .Where(i => i.Status == InstallmentStatus.Due && (i.Year * 12) + i.Month - 1 <= index
                        && db.Loans.Any(l => l.Id == i.LoanId && l.EmployeeId == employeeId && l.Id != loanId && (l.Status == LoanStatus.Active || l.Status == LoanStatus.Approved)))
            .SumAsync(i => i.AmountMinor - i.PaidMinor, cancellationToken).ConfigureAwait(false);
        var projected = preview.Net - new Money(others) - schedule.Installment;
        var below = projected < policy.Payroll.MinimumNetPay;
        if (below)
        {
            warnings.Add($"Net pay would fall to {projected.Major:N2}, below the minimum of {policy.Payroll.MinimumNetPay.Major:N2}. Lower the installment or lengthen the loan.");
        }

        if (others > 0)
        {
            warnings.Add($"Other loans already take {others / 100m:N2} that month.");
        }

        var limit = policy.Loan.SoftLimitMultipleOfGross;
        if (limit > 0 && schedule.Principal.Minor > preview.Gross.Minor * limit)
        {
            warnings.Add($"The amount is more than {limit:0.#} months of gross pay ({preview.Gross.Major:N2}).");
        }

        return new Check(preview.Gross, preview.Net, projected, below, warnings);
    }
}

internal sealed record LoanEmployee(string Code, string Name, DateOnly? ExitDate, EmployeeStatus Status);

internal static class LoanData
{
    public static async Task<Loan?> LoadAsync(IAppDbContext db, ICurrentUser user, Guid id, bool tracking, CancellationToken cancellationToken)
    {
        IQueryable<Loan> query = db.Loans.Include(l => l.Installments).Include(l => l.Transactions).ThenInclude(t => t.Allocations);
        if (!tracking)
        {
            query = query.AsNoTracking();
        }

        return await query.FirstOrDefaultAsync(l => l.Id == id && l.CompanyId == user.CompanyId, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<Dictionary<Guid, LoanEmployee>> EmployeesAsync(IAppDbContext db, IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var set = ids.ToHashSet();
        return await db.Employees.AsNoTracking().Where(e => set.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => new LoanEmployee(e.Code, e.FullName, e.ExitDate, e.Status), cancellationToken).ConfigureAwait(false);
    }

    public static LoanListItem ToListItem(Loan l, LoanEmployee e)
    {
        var balance = LoanBalanceCalculator.Balance(l);
        var next = l.Installments.Where(i => i.IsOpen).OrderBy(i => i.PeriodIndex).ThenBy(i => i.Sequence).FirstOrDefault();
        var done = l.Installments.Count(i => i.Status is InstallmentStatus.Deducted or InstallmentStatus.Paid);
        return new LoanListItem(l.Id, l.Number, l.EmployeeId, e.Code, e.Name, l.Kind, l.Status, l.Principal, l.Total, new Money(l.InstallmentAmountMinor), l.InstallmentCount,
            done, new DateOnly(l.StartYear, l.StartMonth, 1), balance.Outstanding, next is null ? null : new DateOnly(next.Year, next.Month, 1),
            e.Status == EmployeeStatus.Left || e.ExitDate is not null, l.RequestedOn);
    }

    public static LoanInstallmentDto ToDto(LoanInstallment i)
        => new(i.Id, i.Sequence, i.Year, i.Month, new Money(i.PrincipalMinor), new Money(i.InterestMinor), new Money(i.AmountMinor), new Money(i.PaidMinor), i.Status, i.Note,
            i.CarriedFromId is not null);

    /// <summary>Disbursement and interest as debits, every repayment as a credit, with the running balance.</summary>
    public static IReadOnlyList<LoanStatementLine> Statement(Loan loan)
    {
        var lines = new List<LoanStatementLine>();
        long balance = 0;
        foreach (var t in loan.Transactions.OrderBy(t => t.Date).ThenBy(t => t.Kind == LoanTransactionKind.Disbursement ? 0 : 1).ThenBy(t => t.CreatedAtUtc))
        {
            if (t.Kind == LoanTransactionKind.Disbursement)
            {
                balance += t.AmountMinor;
                lines.Add(new LoanStatementLine(t.Date, t.Kind, "Paid out", t.Amount, Money.Zero, new Money(balance), t.Reference));
                if (loan.InterestMinor > 0)
                {
                    balance += loan.InterestMinor;
                    lines.Add(new LoanStatementLine(t.Date, null, $"Interest {loan.AnnualRate:0.##} % a year for {loan.InstallmentCount} months", loan.Interest, Money.Zero, new Money(balance), null));
                }

                continue;
            }

            balance -= t.AmountMinor;
            lines.Add(new LoanStatementLine(t.Date, t.Kind, Describe(t), Money.Zero, t.Amount, new Money(balance), t.Reference));
        }

        return lines;
    }

    private static string Describe(LoanTransaction t) => t.Kind switch
    {
        LoanTransactionKind.PayrollRepayment => $"Salary deduction {t.Date:MMM yyyy}",
        LoanTransactionKind.ManualRepayment => "Repayment" + (t.Note is null ? string.Empty : ": " + t.Note),
        LoanTransactionKind.Settlement => "Early settlement",
        LoanTransactionKind.InterestWaiver => "Interest waived on settlement",
        LoanTransactionKind.WriteOff => "Written off" + (t.Note is null ? string.Empty : ": " + t.Note),
        _ => t.Kind.ToString(),
    };
}
