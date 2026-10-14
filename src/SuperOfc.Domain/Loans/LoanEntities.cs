using SuperOfc.Domain.Common;
using SuperOfc.Domain.Policies;

namespace SuperOfc.Domain.Loans;

public enum LoanKind
{
    Loan = 0,

    /// <summary>A salary advance: usually one or a few installments and no interest.</summary>
    Advance = 1,
}

public enum LoanStatus
{
    Requested = 0,

    /// <summary>Approved with a schedule; not paid out yet.</summary>
    Approved = 1,

    /// <summary>Paid out; installments are being recovered.</summary>
    Active = 2,

    /// <summary>Fully repaid or settled.</summary>
    Closed = 3,

    WrittenOff = 4,

    Rejected = 5,
}

public enum InstallmentStatus
{
    Due = 0,

    /// <summary>Recovered through payroll.</summary>
    Deducted = 1,

    /// <summary>Paid outside payroll (cash or settlement).</summary>
    Paid = 2,

    /// <summary>Not recovered in its month (minimum net pay); its amount moved to a new installment at the end.</summary>
    Skipped = 3,

    /// <summary>Written off.</summary>
    Waived = 4,
}

public enum LoanTransactionKind
{
    Disbursement = 0,
    PayrollRepayment = 1,
    ManualRepayment = 2,
    Settlement = 3,

    /// <summary>Interest not charged on early settlement (policy SettlementInterest).</summary>
    InterestWaiver = 4,
    WriteOff = 5,
}

/// <summary>A loan or salary advance to an employee (doc 03 Loans and advances, R4).</summary>
public sealed class Loan : CompanyEntity
{
    public Guid EmployeeId { get; set; }

    /// <summary>L-0001 for loans, A-0001 for advances.</summary>
    public string Number { get; set; } = string.Empty;

    public LoanKind Kind { get; set; }

    public long PrincipalMinor { get; set; }

    public InterestMethod InterestMethod { get; set; }

    /// <summary>Flat interest in percent per year.</summary>
    public decimal AnnualRate { get; set; }

    public int InstallmentCount { get; set; }

    /// <summary>Total interest over the loan (zero for interest-free loans).</summary>
    public long InterestMinor { get; set; }

    /// <summary>The regular installment; the last one absorbs rounding.</summary>
    public long InstallmentAmountMinor { get; set; }

    public int StartYear { get; set; }

    public int StartMonth { get; set; }

    public LoanStatus Status { get; set; }

    public DateOnly RequestedOn { get; set; }

    public string? Purpose { get; set; }

    public string? ApprovedBy { get; set; }

    public DateTime? ApprovedAtUtc { get; set; }

    public DateOnly? DisbursedOn { get; set; }

    public string? DisbursementReference { get; set; }

    public DateOnly? ClosedOn { get; set; }

    /// <summary>Reason for rejection or write-off.</summary>
    public string? Note { get; set; }

    public List<LoanInstallment> Installments { get; set; } = [];

    public List<LoanTransaction> Transactions { get; set; } = [];

    public Money Principal => new(PrincipalMinor);

    public Money Interest => new(InterestMinor);

    public Money Total => new(PrincipalMinor + InterestMinor);

    public bool IsOpen => Status is LoanStatus.Approved or LoanStatus.Active;
}

/// <summary>One scheduled repayment. Partial payments fill <see cref="PaidMinor"/> until it equals the amount.</summary>
public sealed class LoanInstallment : Entity
{
    public Guid LoanId { get; set; }

    public int Sequence { get; set; }

    public int Year { get; set; }

    public int Month { get; set; }

    public long PrincipalMinor { get; set; }

    public long InterestMinor { get; set; }

    public long AmountMinor { get; set; }

    public long PaidMinor { get; set; }

    public InstallmentStatus Status { get; set; }

    /// <summary>The payslip that deducted or skipped it.</summary>
    public Guid? PayslipId { get; set; }

    /// <summary>For an installment added at the end: the skipped installment it replaces.</summary>
    public Guid? CarriedFromId { get; set; }

    public string? Note { get; set; }

    public long RemainingMinor => AmountMinor - PaidMinor;

    public bool IsOpen => Status == InstallmentStatus.Due && RemainingMinor > 0;

    public int PeriodIndex => (Year * 12) + Month - 1;

    /// <summary>Interest still owed in this installment: payments go to interest first (doc 03 R4).</summary>
    public long RemainingInterestMinor => Math.Max(0, InterestMinor - PaidMinor);

    public long RemainingPrincipalMinor => RemainingMinor - RemainingInterestMinor;
}

/// <summary>Money moving on a loan. The balance is derived from these, never stored (doc 03 R4).</summary>
public sealed class LoanTransaction : Entity
{
    public Guid LoanId { get; set; }

    public DateOnly Date { get; set; }

    public LoanTransactionKind Kind { get; set; }

    /// <summary>Always positive; the kind says which way it moves the balance.</summary>
    public long AmountMinor { get; set; }

    public Guid? PayslipId { get; set; }

    public string? Reference { get; set; }

    public string? Note { get; set; }

    /// <summary>Which installments this payment covered, so it can be reversed exactly.</summary>
    public List<LoanAllocation> Allocations { get; set; } = [];

    public Money Amount => new(AmountMinor);

    public bool ReducesBalance => Kind != LoanTransactionKind.Disbursement;
}

/// <summary>Part of a repayment applied to one installment.</summary>
public sealed class LoanAllocation : Entity
{
    public Guid TransactionId { get; set; }

    public Guid InstallmentId { get; set; }

    public long AmountMinor { get; set; }
}
