using FluentValidation;
using Microsoft.EntityFrameworkCore;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Common;
using SuperOfc.Application.Payroll;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Loans;
using SuperOfc.Domain.Policies;
using SuperOfc.Domain.Security;

namespace SuperOfc.Application.Loans;

/// <summary>Creates or edits a loan request. Only requests can be edited; approval fixes the schedule.</summary>
[RequiresPermission(Permission.ManageLoans)]
public sealed record SaveLoanCommand(
    Guid? Id,
    Guid EmployeeId,
    LoanKind Kind,
    long PrincipalMinor,
    InterestMethod Method,
    decimal AnnualRate,
    int InstallmentCount,
    int StartYear,
    int StartMonth,
    DateOnly RequestedOn,
    string? Purpose) : ICommand<Guid>;

public sealed class SaveLoanValidator : AbstractValidator<SaveLoanCommand>
{
    public SaveLoanValidator()
    {
        RuleFor(x => x.EmployeeId).NotEmpty().WithMessage("Choose the employee.");
        RuleFor(x => x.PrincipalMinor).GreaterThan(0).WithMessage("Enter the amount.");
        RuleFor(x => x.InstallmentCount).InclusiveBetween(1, LoanScheduleGenerator.MaxInstallments).WithMessage($"Use 1 to {LoanScheduleGenerator.MaxInstallments} installments.");
        RuleFor(x => x.AnnualRate).GreaterThan(0).LessThanOrEqualTo(100).When(x => x.Method == InterestMethod.FlatRate).WithMessage("Enter a yearly interest rate above 0 and up to 100 %.");
        RuleFor(x => x.StartMonth).InclusiveBetween(1, 12).WithMessage("Choose the first month of repayment.");
        RuleFor(x => x.StartYear).InclusiveBetween(2000, 2100);
        RuleFor(x => x.Purpose).MaximumLength(300);
    }
}

public sealed class SaveLoanHandler(IAppDbContext db, ICurrentUser user, IIdGenerator ids, IAuditWriter audit, IClock clock, ICompanyContext company, IPolicyProvider policies, PayrollLocks locks)
    : IRequestHandler<SaveLoanCommand, Guid>
{
    public async Task<Result<Guid>> HandleAsync(SaveLoanCommand request, CancellationToken cancellationToken)
    {
        var employee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == request.EmployeeId && e.CompanyId == user.CompanyId && e.DeletedAtUtc == null, cancellationToken)
            .ConfigureAwait(false);
        if (employee is null)
        {
            return Error.NotFound("Employee");
        }

        var firstDay = new DateOnly(request.StartYear, request.StartMonth, 1);
        if (employee.ExitDate is { } exit && exit < firstDay)
        {
            return Error.Validation("StartMonth", "The employee leaves before the first installment.");
        }

        if (await LoanRules.StartIsLockedAsync(locks, firstDay, cancellationToken).ConfigureAwait(false) is { } locked)
        {
            return locked;
        }

        var policy = await policies.GetSnapshotAsync(clock.TodayIn(company.TimeZone), cancellationToken).ConfigureAwait(false);
        var schedule = LoanScheduleGenerator.Generate(new ScheduleRequest(new Money(request.PrincipalMinor), request.Method, request.AnnualRate, request.InstallmentCount,
            request.StartYear, request.StartMonth, policy.Payroll.LineRounding));
        if (schedule.IsFailure)
        {
            return Result<Guid>.Failure(schedule.Errors);
        }

        Loan loan;
        if (request.Id is { } id)
        {
            var found = await db.Loans.FirstOrDefaultAsync(l => l.Id == id && l.CompanyId == user.CompanyId, cancellationToken).ConfigureAwait(false);
            if (found is null)
            {
                return Error.NotFound("Loan");
            }

            if (found.Status != LoanStatus.Requested)
            {
                return Error.Validation("Only a loan request can be edited. Approved loans keep their schedule.");
            }

            loan = found;
            if (loan.Kind != request.Kind)
            {
                loan.Number = await LoanRules.NextNumberAsync(db, user, request.Kind, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            loan = new Loan
            {
                Id = ids.NewId(), CompanyId = user.CompanyId, Status = LoanStatus.Requested,
                Number = await LoanRules.NextNumberAsync(db, user, request.Kind, cancellationToken).ConfigureAwait(false),
            };
            db.Loans.Add(loan);
        }

        loan.EmployeeId = request.EmployeeId;
        loan.Kind = request.Kind;
        loan.PrincipalMinor = request.PrincipalMinor;
        loan.InterestMethod = request.Method;
        loan.AnnualRate = request.Method == InterestMethod.FlatRate ? request.AnnualRate : 0;
        loan.InstallmentCount = request.InstallmentCount;
        loan.InterestMinor = schedule.Value.Interest.Minor;
        loan.InstallmentAmountMinor = schedule.Value.Installment.Minor;
        loan.StartYear = request.StartYear;
        loan.StartMonth = request.StartMonth;
        loan.RequestedOn = request.RequestedOn;
        loan.Purpose = string.IsNullOrWhiteSpace(request.Purpose) ? null : request.Purpose.Trim();
        audit.Record(request.Id is null ? "Loan.Requested" : "Loan.Edited", $"{loan.Number} for {employee.Code} {employee.FullName}: {loan.Principal.Major:N2} in {loan.InstallmentCount} installments",
            nameof(Loan), loan.Id);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return loan.Id;
    }
}

[RequiresPermission(Permission.ManageLoans)]
public sealed record DeleteLoanCommand(Guid Id) : ICommand<Unit>;

public sealed class DeleteLoanHandler(IAppDbContext db, ICurrentUser user, IAuditWriter audit) : IRequestHandler<DeleteLoanCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(DeleteLoanCommand request, CancellationToken cancellationToken)
    {
        var loan = await db.Loans.FirstOrDefaultAsync(l => l.Id == request.Id && l.CompanyId == user.CompanyId, cancellationToken).ConfigureAwait(false);
        if (loan is null)
        {
            return Error.NotFound("Loan");
        }

        if (loan.Status is not (LoanStatus.Requested or LoanStatus.Rejected))
        {
            return Error.Validation("Only requests and rejected loans can be deleted.");
        }

        db.Loans.Remove(loan);
        audit.Record("Loan.Deleted", $"{loan.Number} deleted", nameof(Loan), loan.Id);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}

/// <summary>Approves a request and creates its schedule. Blocked when net pay would fall below the minimum (P5-T2).</summary>
[RequiresPermission(Permission.ApproveLoans)]
public sealed record ApproveLoanCommand(Guid Id) : ICommand<Unit>;

public sealed class ApproveLoanHandler(IAppDbContext db, ICurrentUser user, IIdGenerator ids, IAuditWriter audit, IClock clock, ICompanyContext company, IPolicyProvider policies, PayrollLocks locks)
    : IRequestHandler<ApproveLoanCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(ApproveLoanCommand request, CancellationToken cancellationToken)
    {
        var loan = await LoanData.LoadAsync(db, user, request.Id, tracking: true, cancellationToken).ConfigureAwait(false);
        if (loan is null)
        {
            return Error.NotFound("Loan");
        }

        if (loan.Status != LoanStatus.Requested)
        {
            return Error.Validation("Only a request can be approved.");
        }

        if (await LoanRules.StartIsLockedAsync(locks, new DateOnly(loan.StartYear, loan.StartMonth, 1), cancellationToken).ConfigureAwait(false) is { } locked)
        {
            return locked;
        }

        var policy = await policies.GetSnapshotAsync(clock.TodayIn(company.TimeZone), cancellationToken).ConfigureAwait(false);
        var schedule = LoanScheduleGenerator.Generate(new ScheduleRequest(loan.Principal, loan.InterestMethod, loan.AnnualRate, loan.InstallmentCount, loan.StartYear, loan.StartMonth,
            policy.Payroll.LineRounding));
        if (schedule.IsFailure)
        {
            return Result<Unit>.Failure(schedule.Errors);
        }

        var check = await LoanAffordability.CheckAsync(db, user, loan.EmployeeId, loan.Id, schedule.Value, policy, cancellationToken).ConfigureAwait(false);
        if (check.BelowMinimum)
        {
            return Error.Validation(check.Warnings[0]);
        }

        loan.InterestMinor = schedule.Value.Interest.Minor;
        loan.InstallmentAmountMinor = schedule.Value.Installment.Minor;
        loan.Installments = schedule.Value.Installments.Select(i => new LoanInstallment
        {
            Id = ids.NewId(), LoanId = loan.Id, Sequence = i.Sequence, Year = i.Year, Month = i.Month,
            PrincipalMinor = i.Principal.Minor, InterestMinor = i.Interest.Minor, AmountMinor = i.Amount.Minor, Status = InstallmentStatus.Due,
        }).ToList();
        foreach (var installment in loan.Installments)
        {
            db.LoanInstallments.Add(installment);
        }

        loan.Status = LoanStatus.Approved;
        loan.ApprovedBy = user.UserName;
        loan.ApprovedAtUtc = clock.UtcNow;
        audit.Record("Loan.Approved", $"{loan.Number} approved: {loan.Total.Major:N2} in {loan.InstallmentCount} installments of {loan.InstallmentAmountMinor / 100m:N2}"
            + (check.ProjectedNet is { } net ? $", projected net {net.Major:N2}" : string.Empty), nameof(Loan), loan.Id);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}

[RequiresPermission(Permission.ApproveLoans)]
public sealed record RejectLoanCommand(Guid Id, string Reason) : ICommand<Unit>;

public sealed class RejectLoanValidator : AbstractValidator<RejectLoanCommand>
{
    public RejectLoanValidator() => RuleFor(x => x.Reason).NotEmpty().WithMessage("Enter the reason.").MaximumLength(500);
}

public sealed class RejectLoanHandler(IAppDbContext db, ICurrentUser user, IAuditWriter audit) : IRequestHandler<RejectLoanCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(RejectLoanCommand request, CancellationToken cancellationToken)
    {
        var loan = await db.Loans.FirstOrDefaultAsync(l => l.Id == request.Id && l.CompanyId == user.CompanyId, cancellationToken).ConfigureAwait(false);
        if (loan is null)
        {
            return Error.NotFound("Loan");
        }

        if (loan.Status != LoanStatus.Requested)
        {
            return Error.Validation("Only a request can be rejected.");
        }

        loan.Status = LoanStatus.Rejected;
        loan.Note = request.Reason.Trim();
        audit.Record("Loan.Rejected", $"{loan.Number} rejected: {loan.Note}", nameof(Loan), loan.Id);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}

/// <summary>Records that the money was paid out; recovery starts with the first due installment.</summary>
[RequiresPermission(Permission.ManageLoans)]
public sealed record DisburseLoanCommand(Guid Id, DateOnly Date, string? Reference) : ICommand<Unit>;

public sealed class DisburseLoanHandler(IAppDbContext db, ICurrentUser user, IIdGenerator ids, IAuditWriter audit) : IRequestHandler<DisburseLoanCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(DisburseLoanCommand request, CancellationToken cancellationToken)
    {
        var loan = await LoanData.LoadAsync(db, user, request.Id, tracking: true, cancellationToken).ConfigureAwait(false);
        if (loan is null)
        {
            return Error.NotFound("Loan");
        }

        if (loan.Status != LoanStatus.Approved)
        {
            return Error.Validation("Only an approved loan can be paid out.");
        }

        var transaction = new LoanTransaction
        {
            Id = ids.NewId(), LoanId = loan.Id, Date = request.Date, Kind = LoanTransactionKind.Disbursement, AmountMinor = loan.PrincipalMinor, Reference = LoanRules.Clean(request.Reference),
        };
        db.LoanTransactions.Add(transaction);
        loan.Status = LoanStatus.Active;
        loan.DisbursedOn = request.Date;
        loan.DisbursementReference = transaction.Reference;
        audit.Record("Loan.Disbursed", $"{loan.Number} paid out {loan.Principal.Major:N2} on {request.Date:yyyy-MM-dd}", nameof(Loan), loan.Id);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}

/// <summary>Money repaid outside payroll (cash or transfer); fills the earliest open installments (P5-T3).</summary>
[RequiresPermission(Permission.ManageLoans)]
public sealed record RecordLoanRepaymentCommand(Guid Id, DateOnly Date, long AmountMinor, string? Reference, string? Note) : ICommand<Unit>;

public sealed class RecordLoanRepaymentValidator : AbstractValidator<RecordLoanRepaymentCommand>
{
    public RecordLoanRepaymentValidator()
    {
        RuleFor(x => x.AmountMinor).GreaterThan(0).WithMessage("Enter the amount repaid.");
        RuleFor(x => x.Note).MaximumLength(300);
    }
}

public sealed class RecordLoanRepaymentHandler(IAppDbContext db, ICurrentUser user, IIdGenerator ids, IAuditWriter audit) : IRequestHandler<RecordLoanRepaymentCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(RecordLoanRepaymentCommand request, CancellationToken cancellationToken)
    {
        var loan = await LoanData.LoadAsync(db, user, request.Id, tracking: true, cancellationToken).ConfigureAwait(false);
        if (loan is null)
        {
            return Error.NotFound("Loan");
        }

        if (loan.Status != LoanStatus.Active)
        {
            return Error.Validation("Repayments can only be recorded on a loan that has been paid out and is not closed.");
        }

        var outstanding = LoanBalanceCalculator.Balance(loan).Outstanding;
        if (request.AmountMinor > outstanding.Minor)
        {
            return Error.Validation("AmountMinor", $"The amount is more than the {outstanding.Major:N2} still owed. Use Settle to close the loan.");
        }

        LoanRules.Post(db, ids, loan, LoanTransactionKind.ManualRepayment, request.Date, request.AmountMinor, InstallmentStatus.Paid, null, LoanRules.Clean(request.Reference), LoanRules.Clean(request.Note));
        LoanRules.CloseIfRepaid(loan, request.Date);
        audit.Record("Loan.Repaid", $"{loan.Number} repayment {request.AmountMinor / 100m:N2} on {request.Date:yyyy-MM-dd}", nameof(Loan), loan.Id);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}

/// <summary>Closes a loan early: remaining principal plus interest per policy SettlementInterest; the rest is waived (doc 03 R4).</summary>
[RequiresPermission(Permission.ManageLoans)]
public sealed record SettleLoanCommand(Guid Id, DateOnly Date, string? Reference) : ICommand<SettlementQuote>;

public sealed class SettleLoanHandler(IAppDbContext db, ICurrentUser user, IIdGenerator ids, IAuditWriter audit, IPolicyProvider policies) : IRequestHandler<SettleLoanCommand, SettlementQuote>
{
    public async Task<Result<SettlementQuote>> HandleAsync(SettleLoanCommand request, CancellationToken cancellationToken)
    {
        var loan = await LoanData.LoadAsync(db, user, request.Id, tracking: true, cancellationToken).ConfigureAwait(false);
        if (loan is null)
        {
            return Error.NotFound("Loan");
        }

        if (loan.Status != LoanStatus.Active)
        {
            return Error.Validation("Only a loan that has been paid out and is not closed can be settled.");
        }

        var policy = await policies.GetSnapshotAsync(request.Date, cancellationToken).ConfigureAwait(false);
        var quote = LoanBalanceCalculator.Settlement(loan, policy.Loan.SettlementInterest, request.Date.Year, request.Date.Month);
        var reference = LoanRules.Clean(request.Reference);
        if (quote.Payable.Minor > 0)
        {
            LoanRules.Post(db, ids, loan, LoanTransactionKind.Settlement, request.Date, quote.Payable.Minor, InstallmentStatus.Paid, null, reference, null);
        }

        if (quote.InterestWaived.Minor > 0)
        {
            LoanRules.Post(db, ids, loan, LoanTransactionKind.InterestWaiver, request.Date, quote.InterestWaived.Minor, InstallmentStatus.Paid, null, reference, null);
        }

        LoanRules.CloseIfRepaid(loan, request.Date);
        audit.Record("Loan.Settled", $"{loan.Number} settled for {quote.Payable.Major:N2}, interest waived {quote.InterestWaived.Major:N2}", nameof(Loan), loan.Id);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return quote;
    }
}

[RequiresPermission(Permission.ApproveLoans)]
public sealed record WriteOffLoanCommand(Guid Id, DateOnly Date, string Reason) : ICommand<Unit>;

public sealed class WriteOffLoanValidator : AbstractValidator<WriteOffLoanCommand>
{
    public WriteOffLoanValidator() => RuleFor(x => x.Reason).NotEmpty().WithMessage("Enter the reason.").MaximumLength(300);
}

public sealed class WriteOffLoanHandler(IAppDbContext db, ICurrentUser user, IIdGenerator ids, IAuditWriter audit) : IRequestHandler<WriteOffLoanCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(WriteOffLoanCommand request, CancellationToken cancellationToken)
    {
        var loan = await LoanData.LoadAsync(db, user, request.Id, tracking: true, cancellationToken).ConfigureAwait(false);
        if (loan is null)
        {
            return Error.NotFound("Loan");
        }

        if (loan.Status != LoanStatus.Active)
        {
            return Error.Validation("Only a loan that has been paid out and is not closed can be written off.");
        }

        var outstanding = LoanBalanceCalculator.Balance(loan).Outstanding;
        LoanRules.Post(db, ids, loan, LoanTransactionKind.WriteOff, request.Date, outstanding.Minor, InstallmentStatus.Waived, null, null, request.Reason.Trim());
        loan.Status = LoanStatus.WrittenOff;
        loan.ClosedOn = request.Date;
        loan.Note = request.Reason.Trim();
        audit.Record("Loan.WrittenOff", $"{loan.Number} written off {outstanding.Major:N2}: {loan.Note}", nameof(Loan), loan.Id);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}

internal static class LoanRules
{
    public static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    public static async Task<Error?> StartIsLockedAsync(PayrollLocks locks, DateOnly firstDay, CancellationToken cancellationToken)
    {
        var until = await locks.LockedUntilAsync(cancellationToken).ConfigureAwait(false);
        return until is { } u && firstDay <= u
            ? Error.Validation("StartMonth", $"Payroll is finalized up to {u:dd MMM yyyy}. Start repayment in a later month.")
            : null;
    }

    public static async Task<string> NextNumberAsync(IAppDbContext db, ICurrentUser user, LoanKind kind, CancellationToken cancellationToken)
    {
        var prefix = kind == LoanKind.Advance ? "A-" : "L-";
        var numbers = await db.Loans.AsNoTracking().Where(l => l.CompanyId == user.CompanyId && l.Number.StartsWith(prefix)).Select(l => l.Number)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var max = numbers.Select(n => int.TryParse(n[prefix.Length..], out var v) ? v : 0).DefaultIfEmpty(0).Max();
        return $"{prefix}{max + 1:0000}";
    }

    /// <summary>Records a repayment and applies it to the earliest open installments; filled ones get <paramref name="filledStatus"/>.</summary>
    public static LoanTransaction Post(IAppDbContext db, IIdGenerator ids, Loan loan, LoanTransactionKind kind, DateOnly date, long amountMinor, InstallmentStatus filledStatus,
        Guid? payslipId, string? reference, string? note, IEnumerable<LoanInstallment>? targets = null)
    {
        var transaction = new LoanTransaction
        {
            Id = ids.NewId(), LoanId = loan.Id, Date = date, Kind = kind, AmountMinor = amountMinor, PayslipId = payslipId, Reference = reference, Note = note,
        };
        foreach (var (installment, part) in LoanBalanceCalculator.Allocate(targets ?? loan.Installments, amountMinor))
        {
            installment.PaidMinor += part;
            if (installment.RemainingMinor == 0)
            {
                installment.Status = filledStatus;
                installment.PayslipId = payslipId ?? installment.PayslipId;
            }

            transaction.Allocations.Add(new LoanAllocation { Id = ids.NewId(), TransactionId = transaction.Id, InstallmentId = installment.Id, AmountMinor = part });
        }

        loan.Transactions.Add(transaction);
        db.LoanTransactions.Add(transaction);
        return transaction;
    }

    public static void CloseIfRepaid(Loan loan, DateOnly date)
    {
        if (loan.Status == LoanStatus.Active && LoanBalanceCalculator.Balance(loan).Outstanding.Minor <= 0)
        {
            loan.Status = LoanStatus.Closed;
            loan.ClosedOn = date;
        }
    }
}
