using Microsoft.EntityFrameworkCore;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Payroll;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Loans;
using SuperOfc.Domain.Payroll;

namespace SuperOfc.Application.Loans;

/// <summary>
/// Loans in payroll (P5-T3, doc 03 R3 step 5 and R4). Every open installment due in or before the month becomes a
/// deduction; an installment that would break the minimum net is skipped whole and moved to a new installment after
/// the last one. For an employee who leaves in the month, the whole balance is recovered as far as the minimum net
/// allows (doc 10 Q26); what cannot be recovered stays owed.
/// </summary>
public sealed class LoanPayrollParticipant(IAppDbContext db, ICurrentUser user, IIdGenerator ids) : IPayrollParticipant
{
    public const string Code = "LOAN";

    public async Task<PayrollContribution> PrepareAsync(PayrollContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var contribution = new PayrollContribution();
        var plan = await PlanAsync(context.Period, context.EmployeeIds, tracking: false, cancellationToken).ConfigureAwait(false);
        foreach (var item in plan)
        {
            contribution.AddDeduction(item.EmployeeId, item.Deduction);
        }

        return contribution;
    }

    public async Task<DateTime?> LastInputChangeAsync(PayrollPeriod period, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(period);
        var loans = await db.Loans.Where(l => l.CompanyId == user.CompanyId && l.Status == LoanStatus.Active)
            .MaxAsync(l => (DateTime?)(l.ModifiedAtUtc ?? l.CreatedAtUtc), cancellationToken).ConfigureAwait(false);
        var index = Index(period);
        var installments = await db.LoanInstallments
            .Where(i => (i.Year * 12) + i.Month - 1 <= index && db.Loans.Any(l => l.Id == i.LoanId && l.CompanyId == user.CompanyId))
            .MaxAsync(i => (DateTime?)(i.ModifiedAtUtc ?? i.CreatedAtUtc), cancellationToken).ConfigureAwait(false);
        return new[] { loans, installments }.Max();
    }

    public async Task<Result> OnFinalizedAsync(PayrollRun run, PayrollPeriod period, IReadOnlyList<Payslip> payslips, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(period);
        ArgumentNullException.ThrowIfNull(payslips);
        var slipByEmployee = payslips.ToDictionary(p => p.EmployeeId);
        var plan = await PlanAsync(period, [.. slipByEmployee.Keys], tracking: true, cancellationToken).ConfigureAwait(false);
        var deducted = payslips.SelectMany(p => p.Lines.Where(l => l.Source == PayslipLineSource.Loan).Select(l => (Slip: p, Line: l)))
            .ToDictionary(x => x.Line.ReferenceId ?? Guid.Empty, x => x);
        if (deducted.Keys.Any(id => plan.All(p => p.Deduction.ReferenceId != id)))
        {
            return Error.Conflict("A loan changed after this payroll was computed. Compute again, then finalize.");
        }

        var date = period.ToDate;
        foreach (var item in plan)
        {
            var slip = slipByEmployee[item.EmployeeId];
            if (deducted.TryGetValue(item.Deduction.ReferenceId, out var hit))
            {
                if (item.Installment is { } single && hit.Line.AmountMinor != single.RemainingMinor)
                {
                    return Error.Conflict("A loan installment changed after this payroll was computed. Compute again, then finalize.");
                }

                LoanRules.Post(db, ids, item.Loan, LoanTransactionKind.PayrollRepayment, date, hit.Line.AmountMinor, InstallmentStatus.Deducted, slip.Id, null, null,
                    item.Installment is null ? null : [item.Installment]);
                LoanRules.CloseIfRepaid(item.Loan, date);
            }
            else if (item.Installment is { } skipped)
            {
                // Skipped whole to protect the minimum net: move what is left to a new installment after the last one.
                skipped.Status = InstallmentStatus.Skipped;
                skipped.PayslipId = slip.Id;
                skipped.Note = $"Skipped in {period.FromDate:MMM yyyy} to keep net pay at the minimum.";
                var last = item.Loan.Installments.OrderByDescending(i => i.PeriodIndex).ThenByDescending(i => i.Sequence).First();
                var due = new DateOnly(last.Year, last.Month, 1).AddMonths(1);
                var carry = new LoanInstallment
                {
                    Id = ids.NewId(), LoanId = item.Loan.Id, Sequence = item.Loan.Installments.Max(i => i.Sequence) + 1, Year = due.Year, Month = due.Month,
                    PrincipalMinor = skipped.RemainingPrincipalMinor, InterestMinor = skipped.RemainingInterestMinor, AmountMinor = skipped.RemainingMinor,
                    Status = InstallmentStatus.Due, CarriedFromId = skipped.Id, Note = $"Moved from {period.FromDate:MMM yyyy}.",
                };
                item.Loan.Installments.Add(carry);
                db.LoanInstallments.Add(carry);
            }
        }

        return Result.Success();
    }

    public async Task<Result> OnVoidedAsync(PayrollRun run, PayrollPeriod period, IReadOnlyList<Payslip> payslips, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payslips);
        var slipIds = payslips.Select(p => p.Id).ToList();
        var loanIds = await db.LoanInstallments.Where(i => i.PayslipId != null && slipIds.Contains(i.PayslipId.Value)).Select(i => i.LoanId)
            .Union(db.LoanTransactions.Where(t => t.PayslipId != null && slipIds.Contains(t.PayslipId.Value)).Select(t => t.LoanId))
            .Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var loanId in loanIds)
        {
            var loan = (await LoanData.LoadAsync(db, user, loanId, tracking: true, cancellationToken).ConfigureAwait(false))!;
            var byId = loan.Installments.ToDictionary(i => i.Id);

            // Undo the salary deductions of this run.
            foreach (var transaction in loan.Transactions.Where(t => t.PayslipId is { } p && slipIds.Contains(p)).ToList())
            {
                foreach (var allocation in transaction.Allocations)
                {
                    var installment = byId[allocation.InstallmentId];
                    installment.PaidMinor -= allocation.AmountMinor;
                    if (installment.Status != InstallmentStatus.Due && installment.RemainingMinor > 0)
                    {
                        installment.Status = InstallmentStatus.Due;
                        installment.PayslipId = null;
                    }
                }

                loan.Transactions.Remove(transaction);
                db.LoanTransactions.Remove(transaction);
            }

            // Undo skips: the installment is due again and its replacement at the end goes away.
            foreach (var skipped in loan.Installments.Where(i => i.Status == InstallmentStatus.Skipped && i.PayslipId is { } p && slipIds.Contains(p)).ToList())
            {
                var carry = loan.Installments.FirstOrDefault(i => i.CarriedFromId == skipped.Id);
                if (carry is { PaidMinor: > 0 } || (carry is not null && carry.Status != InstallmentStatus.Due))
                {
                    return Error.Validation($"{loan.Number}: the installment moved from {period.FromDate:MMM yyyy} has already been repaid. Reverse that repayment first.");
                }

                skipped.Status = InstallmentStatus.Due;
                skipped.PayslipId = null;
                skipped.Note = null;
                if (carry is not null)
                {
                    loan.Installments.Remove(carry);
                    db.LoanInstallments.Remove(carry);
                }
            }

            if (loan.Status == LoanStatus.Closed && LoanBalanceCalculator.Balance(loan).Outstanding.Minor > 0)
            {
                loan.Status = LoanStatus.Active;
                loan.ClosedOn = null;
            }
        }

        return Result.Success();
    }

    private sealed record Planned(Guid EmployeeId, Loan Loan, LoanInstallment? Installment, DueDeduction Deduction);

    private static int Index(PayrollPeriod period) => (period.Year * 12) + period.Month - 1;

    /// <summary>The deductions this month: one per open installment due by the month end, or one final recovery for leavers.</summary>
    private async Task<IReadOnlyList<Planned>> PlanAsync(PayrollPeriod period, IReadOnlyList<Guid> employeeIds, bool tracking, CancellationToken cancellationToken)
    {
        var index = Index(period);
        var query = db.Loans.Include(l => l.Installments).Include(l => l.Transactions).ThenInclude(t => t.Allocations)
            .Where(l => l.CompanyId == user.CompanyId && l.Status == LoanStatus.Active && employeeIds.Contains(l.EmployeeId));
        if (!tracking)
        {
            query = query.AsNoTracking();
        }

        var loans = await query.OrderBy(l => l.Number).ToListAsync(cancellationToken).ConfigureAwait(false);
        var leavers = await db.Employees.AsNoTracking().Where(e => employeeIds.Contains(e.Id) && e.ExitDate != null && e.ExitDate <= period.ToDate)
            .Select(e => e.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var plan = new List<Planned>();
        foreach (var loan in loans)
        {
            var kind = loan.Kind == LoanKind.Advance ? "Advance" : "Loan";
            if (leavers.Contains(loan.EmployeeId))
            {
                var balance = LoanBalanceCalculator.Balance(loan).Outstanding;
                if (balance.Minor > 0)
                {
                    plan.Add(new Planned(loan.EmployeeId, loan, null, new DueDeduction(loan.Id, Code, $"{kind} {loan.Number} final recovery", balance, AllowPartial: true)));
                }

                continue;
            }

            var last = loan.Installments.Count == 0 ? 0 : loan.Installments.Max(i => i.Sequence);
            foreach (var installment in loan.Installments.Where(i => i.IsOpen && i.PeriodIndex <= index).OrderBy(i => i.PeriodIndex).ThenBy(i => i.Sequence))
            {
                var name = $"{kind} {loan.Number} installment {installment.Sequence} of {last}";
                plan.Add(new Planned(loan.EmployeeId, loan, installment, new DueDeduction(installment.Id, Code, name, new Money(installment.RemainingMinor))));
            }
        }

        return plan;
    }
}
