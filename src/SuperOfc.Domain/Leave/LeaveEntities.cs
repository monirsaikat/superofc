using SuperOfc.Domain.Common;
using SuperOfc.Domain.Organization;

namespace SuperOfc.Domain.Leave;

public enum LeaveAccrual
{
    /// <summary>The full entitlement is credited at the start of the leave year.</summary>
    Yearly = 0,

    /// <summary>One twelfth of the entitlement is credited on the 1st of each month.</summary>
    Monthly = 1,
}

public sealed class LeaveType : CompanyEntity, ISoftDeletable
{
    public string Name { get; set; } = string.Empty;

    public string Code { get; set; } = string.Empty;

    public bool IsPaid { get; set; } = true;

    /// <summary>Days per leave year.</summary>
    public decimal YearlyEntitlement { get; set; }

    public LeaveAccrual Accrual { get; set; }

    /// <summary>Maximum unused days carried into the next year; 0 means none.</summary>
    public decimal CarryForwardCap { get; set; }

    public bool AllowsHalfDay { get; set; } = true;

    public bool RequiresAttachment { get; set; }

    /// <summary>Allows taking more than the balance (e.g. unpaid leave).</summary>
    public bool AllowNegativeBalance { get; set; }

    public int SortOrder { get; set; }

    public DateTime? DeletedAtUtc { get; set; }

    /// <summary>Entitlement earned by <paramref name="asOf"/> in the leave year starting <paramref name="yearStart"/> (pure, doc 03 R6).</summary>
    public decimal AccruedBy(DateOnly yearStart, DateOnly asOf, DateOnly joinDate)
    {
        if (asOf < yearStart)
        {
            return 0m;
        }

        var yearEnd = yearStart.AddYears(1).AddDays(-1);
        if (joinDate > yearEnd || joinDate > asOf)
        {
            return 0m;
        }

        // Months of the leave year in which the employee is employed (joining month counts).
        var firstMonth = joinDate > yearStart ? new DateOnly(joinDate.Year, joinDate.Month, 1) : yearStart;
        var monthsInYear = MonthsBetween(firstMonth, yearEnd) + 1;
        decimal value;
        if (Accrual == LeaveAccrual.Yearly)
        {
            value = YearlyEntitlement * monthsInYear / 12m;
        }
        else
        {
            var lastMonth = asOf < yearEnd ? asOf : yearEnd;
            var monthsSoFar = MonthsBetween(firstMonth, lastMonth) + 1;
            value = YearlyEntitlement * monthsSoFar / 12m;
        }

        return RoundToHalf(value);
    }

    /// <summary>Rounds down to the nearest half day so balances stay simple.</summary>
    public static decimal RoundToHalf(decimal value) => Math.Floor(value * 2m) / 2m;

    private static int MonthsBetween(DateOnly from, DateOnly to) => ((to.Year - from.Year) * 12) + to.Month - from.Month;
}

/// <summary>Balance of one leave type for one employee in one leave year.</summary>
public sealed class LeaveBalance : CompanyEntity
{
    public Guid EmployeeId { get; set; }

    public Guid LeaveTypeId { get; set; }

    /// <summary>Calendar year in which the leave year starts.</summary>
    public int Year { get; set; }

    /// <summary>Carried forward from the previous year.</summary>
    public decimal Opening { get; set; }

    public decimal Accrued { get; set; }

    public decimal Used { get; set; }

    /// <summary>Manual adjustments (positive or negative).</summary>
    public decimal Adjusted { get; set; }

    public decimal Closing => Opening + Accrued - Used + Adjusted;
}

public enum LeaveRequestStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
    Cancelled = 3,
}

public sealed class LeaveRequest : CompanyEntity
{
    public Guid EmployeeId { get; set; }

    public Guid LeaveTypeId { get; set; }

    public DateOnly FromDate { get; set; }

    public DateOnly ToDate { get; set; }

    public bool IsHalfDay { get; set; }

    /// <summary>Working days counted against the balance.</summary>
    public decimal Days { get; set; }

    public LeaveRequestStatus Status { get; set; }

    public string? Reason { get; set; }

    public string? DecisionNote { get; set; }

    public string? DecidedBy { get; set; }

    public DateTime? DecidedAtUtc { get; set; }

    public string? AttachmentPath { get; set; }

    public DateRange Range => new(FromDate, ToDate);

    /// <summary>Working days in the request: weekly offs and holidays are not counted (pure).</summary>
    public static decimal CountDays(DateOnly from, DateOnly to, bool halfDay, WorkCalendar calendar, Guid? locationId)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        if (halfDay)
        {
            return calendar.GetDayKind(from, locationId) is DayKind.Working or DayKind.HalfDay ? 0.5m : 0m;
        }

        return calendar.WorkingDays(new DateRange(from, to), locationId);
    }

    /// <summary>Leave on one date: 0, 0.5 or 1.</summary>
    public decimal DaysOn(DateOnly date, WorkCalendar calendar, Guid? locationId)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        if (!Range.Contains(date))
        {
            return 0m;
        }

        var kind = calendar.GetDayKind(date, locationId);
        if (kind is DayKind.Holiday or DayKind.WeeklyOff)
        {
            return 0m;
        }

        return IsHalfDay || kind == DayKind.HalfDay ? 0.5m : 1m;
    }
}
