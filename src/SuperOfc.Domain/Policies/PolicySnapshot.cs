using SuperOfc.Domain.Common;

namespace SuperOfc.Domain.Policies;

/// <summary>
/// All business-rule configuration effective on one date. Calculation engines receive this object and
/// never read the database themselves (ADR-08).
/// </summary>
public sealed record PolicySnapshot(
    AttendancePolicy Attendance,
    PayrollPolicy Payroll,
    LoanPolicy Loan,
    GeneralPolicy General)
{
    public static PolicySnapshot Default { get; } = new(new(), new(), new(), new());

    public const string AttendanceSection = "Attendance";
    public const string PayrollSection = "Payroll";
    public const string LoanSection = "Loan";
    public const string GeneralSection = "General";
}

public enum PunchPairing
{
    /// <summary>First punch of the day is in, last punch is out.</summary>
    FirstLast = 0,

    /// <summary>Punches alternate in/out; worked time is the sum of pairs.</summary>
    Alternating = 1,
}

public enum MissingPunchRule
{
    TreatAsHalfDay = 0,
    TreatAsAbsent = 1,
    TreatAsPresent = 2,
}

public sealed record LatePenalty(int Instances = 0, decimal DeductDays = 1m)
{
    public bool IsEnabled => Instances > 0 && DeductDays > 0;
}

public sealed record AttendancePolicy
{
    public PunchPairing PunchPairing { get; init; } = PunchPairing.FirstLast;

    public MissingPunchRule MissingPunch { get; init; } = MissingPunchRule.TreatAsHalfDay;

    public bool OvertimeEnabled { get; init; } = true;

    /// <summary>Overtime minutes are rounded down to this step (e.g. 30).</summary>
    public int OvertimeRoundingMinutes { get; init; } = 30;

    /// <summary>Punches on holidays / weekly offs count fully as overtime.</summary>
    public bool OvertimeOnHolidays { get; init; } = true;

    /// <summary>N late days cost X days of pay. Off by default (doc 10, Q11).</summary>
    public LatePenalty LatePenalty { get; init; } = new();

    public bool WeeklyOffsPaid { get; init; } = true;

    public bool HolidaysPaid { get; init; } = true;

    /// <summary>Punches this many hours before shift start belong to the shift day.</summary>
    public int WindowBeforeShiftHours { get; init; } = 4;

    /// <summary>Punches up to this many hours after shift end belong to the shift day.</summary>
    public int WindowAfterShiftHours { get; init; } = 8;

    /// <summary>Two punches closer than this are treated as one (double tap on the device).</summary>
    public int DuplicatePunchMinutes { get; init; } = 2;
}

public enum ProrateBasis
{
    CalendarDays = 0,
    WorkingDays = 1,
    Fixed30 = 2,
}

public enum MidMonthChange
{
    ProRateBySegments = 0,
    UseLatest = 1,
}

public enum OvertimeRateBase
{
    Basic = 0,
    Gross = 1,
}

public sealed record PayrollPolicy
{
    public ProrateBasis ProrateBasis { get; init; } = ProrateBasis.CalendarDays;

    public MidMonthChange MidMonthChange { get; init; } = MidMonthChange.ProRateBySegments;

    public OvertimeRateBase OvertimeRateBase { get; init; } = OvertimeRateBase.Basic;

    public decimal OvertimeMultiplier { get; init; } = 1.5m;

    public decimal HolidayOvertimeMultiplier { get; init; } = 2m;

    /// <summary>Hours in a standard working day, used for hourly rates when a shift is unknown.</summary>
    public decimal StandardDayHours { get; init; } = 8m;

    /// <summary>Rounding applied to each payslip line.</summary>
    public RoundingMode LineRoundingMode { get; init; } = RoundingMode.HalfUp;

    /// <summary>Step in minor units; 100 rounds to whole currency units.</summary>
    public long LineRoundingStepMinor { get; init; } = 100;

    /// <summary>Net pay never falls below this (loan deductions are reduced first).</summary>
    public long MinimumNetPayMinor { get; init; }

    public RoundingPolicy LineRounding => new(LineRoundingMode, LineRoundingStepMinor);

    public Money MinimumNetPay => new(MinimumNetPayMinor);
}

public enum InterestMethod
{
    None = 0,
    FlatRate = 1,
}

public enum SettlementInterest
{
    /// <summary>Remaining interest is charged in full.</summary>
    Full = 0,

    /// <summary>Interest only for months elapsed.</summary>
    ProRata = 1,

    /// <summary>No interest on the remaining balance.</summary>
    None = 2,
}

public sealed record LoanPolicy
{
    public InterestMethod DefaultInterestMethod { get; init; } = InterestMethod.None;

    public SettlementInterest SettlementInterest { get; init; } = SettlementInterest.None;

    /// <summary>Warn when the principal exceeds this multiple of monthly gross.</summary>
    public decimal SoftLimitMultipleOfGross { get; init; } = 3m;
}

public sealed record GeneralPolicy
{
    public int IdleLockMinutes { get; init; } = 15;

    public int MaxFailedLogins { get; init; } = 5;

    public int LockoutMinutes { get; init; } = 10;

    public int AutoSyncMinutes { get; init; } = 15;
}
