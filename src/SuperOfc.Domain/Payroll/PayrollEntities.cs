using SuperOfc.Domain.Common;

namespace SuperOfc.Domain.Payroll;

public enum PayrollPeriodStatus
{
    Open = 0,
    Computed = 1,

    /// <summary>A run is finalized: attendance and pay for these dates cannot change.</summary>
    Locked = 2,

    /// <summary>Locked and every payslip is marked paid.</summary>
    Paid = 3,
}

/// <summary>One calendar month of payroll (doc 03 R7).</summary>
public sealed class PayrollPeriod : CompanyEntity
{
    public int Year { get; set; }

    public int Month { get; set; }

    public DateOnly FromDate { get; set; }

    public DateOnly ToDate { get; set; }

    public PayrollPeriodStatus Status { get; set; }

    public DateRange Range => new(FromDate, ToDate);

    public bool IsLocked => Status is PayrollPeriodStatus.Locked or PayrollPeriodStatus.Paid;

    public static PayrollPeriod For(Guid companyId, Guid id, int year, int month) => new()
    {
        Id = id,
        CompanyId = companyId,
        Year = year,
        Month = month,
        FromDate = new DateOnly(year, month, 1),
        ToDate = new DateOnly(year, month, DateTime.DaysInMonth(year, month)),
    };
}

public enum PayrollRunStatus
{
    Draft = 0,
    Finalized = 1,
    Voided = 2,
}

public sealed class PayrollRun : CompanyEntity
{
    public Guid PeriodId { get; set; }

    public int RunNumber { get; set; }

    public PayrollRunStatus Status { get; set; }

    public DateTime ComputedAtUtc { get; set; }

    public string ComputedBy { get; set; } = string.Empty;

    public DateTime? FinalizedAtUtc { get; set; }

    public string? FinalizedBy { get; set; }

    public DateTime? VoidedAtUtc { get; set; }

    public string? VoidReason { get; set; }

    /// <summary>The payroll and attendance policy used, as JSON, so a run can be explained later.</summary>
    public string PolicyJson { get; set; } = "{}";

    public int EmployeeCount { get; set; }

    public long GrossMinor { get; set; }

    public long DeductionsMinor { get; set; }

    public long NetMinor { get; set; }

    public long EmployerContributionsMinor { get; set; }
}

public enum PaymentStatus
{
    Unpaid = 0,
    Paid = 1,
}

/// <summary>One employee's pay in one run. Employee details are copied so later edits never change it.</summary>
public sealed class Payslip : CompanyEntity
{
    public Guid RunId { get; set; }

    public Guid EmployeeId { get; set; }

    public string EmployeeCode { get; set; } = string.Empty;

    public string EmployeeName { get; set; } = string.Empty;

    public string? Department { get; set; }

    public string? Designation { get; set; }

    public Employees.PaymentMode PaymentMode { get; set; }

    public string? BankName { get; set; }

    public string? BankAccount { get; set; }

    public int CalendarDays { get; set; }

    public decimal PayableDays { get; set; }

    public decimal PresentDays { get; set; }

    public decimal HalfDays { get; set; }

    public decimal PaidLeaveDays { get; set; }

    public decimal UnpaidLeaveDays { get; set; }

    public decimal AbsentDays { get; set; }

    public int Holidays { get; set; }

    public int WeeklyOffs { get; set; }

    public int LateDays { get; set; }

    public decimal LatePenaltyDays { get; set; }

    public decimal OvertimeHours { get; set; }

    public long GrossMinor { get; set; }

    public long DeductionsMinor { get; set; }

    public long NetMinor { get; set; }

    public long TaxMinor { get; set; }

    public long LoanMinor { get; set; }

    public long EmployerContributionsMinor { get; set; }

    public long TaxableGrossMinor { get; set; }

    /// <summary>Plain-language problems found while computing (for example "no salary assigned").</summary>
    public string? Warnings { get; set; }

    public PaymentStatus PaymentStatus { get; set; }

    public DateOnly? PaidOn { get; set; }

    public string? PaymentReference { get; set; }

    public List<PayslipLine> Lines { get; set; } = [];

    public Money Gross => new(GrossMinor);

    public Money Deductions => new(DeductionsMinor);

    public Money Net => new(NetMinor);
}

public enum PayslipLineSource
{
    Component = 0,
    Overtime = 1,
    Adjustment = 2,
    Loan = 3,
    Tax = 4,
}

/// <summary>One amount on a payslip, with how it was worked out (doc 03 R3 step 7).</summary>
public sealed class PayslipLine
{
    public Guid Id { get; set; }

    public Guid PayslipId { get; set; }

    public int Order { get; set; }

    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public ComponentKind Kind { get; set; }

    public PayslipLineSource Source { get; set; }

    /// <summary>How the amount was worked out, e.g. "40,000 × 28 / 31 days".</summary>
    public string? Basis { get; set; }

    /// <summary>Full monthly amount before pro-rating, where that applies.</summary>
    public long? FullAmountMinor { get; set; }

    public decimal? Quantity { get; set; }

    public long AmountMinor { get; set; }

    public bool IsTaxable { get; set; }

    /// <summary>The loan installment or other record this line settles.</summary>
    public Guid? ReferenceId { get; set; }

    public Money Amount => new(AmountMinor);
}

/// <summary>A one-off bonus or deduction for an employee in a month (doc 03).</summary>
public sealed class PayrollAdjustment : CompanyEntity
{
    public Guid EmployeeId { get; set; }

    public int Year { get; set; }

    public int Month { get; set; }

    public string Name { get; set; } = string.Empty;

    public ComponentKind Kind { get; set; }

    public long AmountMinor { get; set; }

    public bool IsTaxable { get; set; } = true;

    public string? Reason { get; set; }
}
