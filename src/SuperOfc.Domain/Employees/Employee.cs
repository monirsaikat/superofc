using SuperOfc.Domain.Common;

namespace SuperOfc.Domain.Employees;

public enum EmployeeStatus
{
    Active = 0,
    Probation = 1,
    OnLeave = 2,
    Left = 3,
}

public enum Gender
{
    Unspecified = 0,
    Male = 1,
    Female = 2,
    Other = 3,
}

public enum PaymentMode
{
    Bank = 0,
    Cash = 1,
    Cheque = 2,
    MobileBanking = 3,
}

public sealed class Employee : CompanyEntity, ISoftDeletable
{
    /// <summary>Human-readable unique code, also used as the default device user id.</summary>
    public string Code { get; set; } = string.Empty;

    public string FullName { get; set; } = string.Empty;

    public Gender Gender { get; set; }

    public DateOnly? DateOfBirth { get; set; }

    public string? Phone { get; set; }

    public string? Email { get; set; }

    public string? Address { get; set; }

    public string? NationalId { get; set; }

    public string? EmergencyContact { get; set; }

    public DateOnly JoinDate { get; set; }

    public DateOnly? ConfirmationDate { get; set; }

    public DateOnly? ExitDate { get; set; }

    public string? ExitReason { get; set; }

    public EmployeeStatus Status { get; set; } = EmployeeStatus.Probation;

    public Guid? DepartmentId { get; set; }

    public Guid? DesignationId { get; set; }

    public Guid? LocationId { get; set; }

    public Guid? GradeId { get; set; }

    public string? PhotoPath { get; set; }

    public PaymentMode PaymentMode { get; set; } = PaymentMode.Bank;

    public string? BankName { get; set; }

    public string? BankBranch { get; set; }

    public string? BankAccount { get; set; }

    public string? TaxIdentifier { get; set; }

    /// <summary>Overtime is paid only for eligible employees (doc 10, Q10).</summary>
    public bool IsOvertimeEligible { get; set; } = true;

    /// <summary>Values of company-defined custom fields as a JSON object.</summary>
    public string? CustomFieldsJson { get; set; }

    public DateTime? DeletedAtUtc { get; set; }

    /// <summary>Whether the employee is on the payroll on a given date.</summary>
    public bool IsEmployedOn(DateOnly date) => date >= JoinDate && (ExitDate is null || date <= ExitDate.Value);

    /// <summary>Dates within <paramref name="range"/> on which the employee is employed.</summary>
    public DateRange? EmploymentWithin(DateRange range) => new DateRange(JoinDate, ExitDate).Intersect(range);

    public void Exit(DateOnly date, string reason)
    {
        if (date < JoinDate)
        {
            throw new DomainException("Exit date cannot be before the join date.");
        }

        ExitDate = date;
        ExitReason = reason;
        Status = EmployeeStatus.Left;
    }

    public void Rejoin(DateOnly date)
    {
        if (ExitDate is not null && date <= ExitDate.Value)
        {
            throw new DomainException("Rejoin date must be after the exit date.");
        }

        JoinDate = date;
        ExitDate = null;
        ExitReason = null;
        ConfirmationDate = null;
        Status = EmployeeStatus.Probation;
    }

    public void Confirm(DateOnly date)
    {
        if (date < JoinDate)
        {
            throw new DomainException("Confirmation date cannot be before the join date.");
        }

        ConfirmationDate = date;
        if (Status == EmployeeStatus.Probation)
        {
            Status = EmployeeStatus.Active;
        }
    }
}

public enum EmploymentEventType
{
    Joined = 0,
    Confirmed = 1,
    Promoted = 2,
    Transferred = 3,
    Exited = 4,
    Rejoined = 5,
    Note = 6,
}

/// <summary>Employment history entry shown on the employee timeline.</summary>
public sealed class EmploymentEvent : CompanyEntity
{
    public Guid EmployeeId { get; set; }

    public EmploymentEventType Type { get; set; }

    public DateOnly Date { get; set; }

    public string Description { get; set; } = string.Empty;
}

public sealed class EmployeeDocument : CompanyEntity
{
    public Guid EmployeeId { get; set; }

    public string Title { get; set; } = string.Empty;

    public string? DocumentType { get; set; }

    /// <summary>Path relative to the files folder.</summary>
    public string FilePath { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public DateOnly? ExpiryDate { get; set; }

    public string? Notes { get; set; }
}

public enum CustomFieldType
{
    Text = 0,
    Number = 1,
    Date = 2,
    YesNo = 3,
}

/// <summary>Company-defined extra field on employees.</summary>
public sealed record CustomFieldDefinition(string Key, string Label, CustomFieldType Type);
