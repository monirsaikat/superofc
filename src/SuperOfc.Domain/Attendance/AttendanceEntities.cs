using SuperOfc.Domain.Common;

namespace SuperOfc.Domain.Attendance;

/// <summary>A working shift. Times are company local wall-clock times.</summary>
public sealed class Shift : CompanyEntity, ISoftDeletable
{
    public string Name { get; set; } = string.Empty;

    public string? Code { get; set; }

    public TimeOnly StartTime { get; set; } = new(9, 0);

    public TimeOnly EndTime { get; set; } = new(18, 0);

    public int GraceInMinutes { get; set; } = 10;

    public int GraceOutMinutes { get; set; }

    public int BreakMinutes { get; set; } = 60;

    public int MinimumMinutesForHalfDay { get; set; } = 240;

    public int MinimumMinutesForFullDay { get; set; } = 420;

    /// <summary>Overtime starts only after this many minutes beyond the scheduled minutes.</summary>
    public int OvertimeStartsAfterMinutes { get; set; }

    /// <summary>The shift used for employees without an assignment.</summary>
    public bool IsDefault { get; set; }

    public DateTime? DeletedAtUtc { get; set; }

    public bool CrossesMidnight => EndTime <= StartTime;

    /// <summary>Minutes from start to end, across midnight when needed.</summary>
    public int SpanMinutes => (int)((EndTime.ToTimeSpan() - StartTime.ToTimeSpan() + TimeSpan.FromDays(CrossesMidnight ? 1 : 0)).TotalMinutes);

    /// <summary>Expected working minutes (span minus break).</summary>
    public int ScheduledMinutes => Math.Max(0, SpanMinutes - BreakMinutes);

    public ShiftSpec ToSpec() => new(Id, Name, StartTime, SpanMinutes, GraceInMinutes, GraceOutMinutes, BreakMinutes,
        MinimumMinutesForHalfDay, MinimumMinutesForFullDay, OvertimeStartsAfterMinutes);
}

/// <summary>Immutable shift values used by the calculator (no persistence concerns).</summary>
public sealed record ShiftSpec(
    Guid Id,
    string Name,
    TimeOnly Start,
    int SpanMinutes,
    int GraceInMinutes,
    int GraceOutMinutes,
    int BreakMinutes,
    int MinimumMinutesForHalfDay,
    int MinimumMinutesForFullDay,
    int OvertimeStartsAfterMinutes)
{
    public int ScheduledMinutes => Math.Max(0, SpanMinutes - BreakMinutes);

    public DateTime StartOn(DateOnly date) => date.ToDateTime(Start);

    public DateTime EndOn(DateOnly date) => StartOn(date).AddMinutes(SpanMinutes);

    /// <summary>Same shift with half the expected time (half working day).</summary>
    public ShiftSpec Halved() => this with
    {
        SpanMinutes = SpanMinutes / 2,
        BreakMinutes = 0,
        MinimumMinutesForHalfDay = MinimumMinutesForHalfDay / 2,
        MinimumMinutesForFullDay = MinimumMinutesForFullDay / 2,
    };
}

/// <summary>
/// Gives an employee a shift for a date range. With a rotation, the shift changes every
/// <see cref="RotationDays"/> days following <see cref="RotationShiftIds"/>, counted from <see cref="EffectiveFrom"/>.
/// </summary>
public sealed class ShiftAssignment : CompanyEntity
{
    public Guid EmployeeId { get; set; }

    public Guid ShiftId { get; set; }

    public DateOnly EffectiveFrom { get; set; }

    public DateOnly? EffectiveTo { get; set; }

    /// <summary>Comma-separated shift ids; empty means no rotation.</summary>
    public string? RotationShiftIds { get; set; }

    public int RotationDays { get; set; } = 7;

    public DateRange Range => new(EffectiveFrom, EffectiveTo);

    public IReadOnlyList<Guid> Rotation => string.IsNullOrWhiteSpace(RotationShiftIds)
        ? []
        : RotationShiftIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Guid.Parse).ToList();

    /// <summary>Shift id for a date inside the range.</summary>
    public Guid ShiftOn(DateOnly date)
    {
        var rotation = Rotation;
        if (rotation.Count == 0)
        {
            return ShiftId;
        }

        var step = (date.DayNumber - EffectiveFrom.DayNumber) / Math.Max(1, RotationDays);
        return rotation[step % rotation.Count];
    }
}

/// <summary>Picks the shift for an employee and date (pure).</summary>
public static class ShiftResolver
{
    /// <summary>Returns the assigned shift id, or <paramref name="defaultShiftId"/> when no assignment covers the date.</summary>
    public static Guid? Resolve(IEnumerable<ShiftAssignment> assignmentsForEmployee, DateOnly date, Guid? defaultShiftId)
    {
        ArgumentNullException.ThrowIfNull(assignmentsForEmployee);
        var match = assignmentsForEmployee.Where(a => a.Range.Contains(date)).OrderByDescending(a => a.EffectiveFrom).FirstOrDefault();
        return match?.ShiftOn(date) ?? defaultShiftId;
    }

    /// <summary>True when any two assignments overlap in date.</summary>
    public static bool HasOverlap(IEnumerable<ShiftAssignment> assignmentsForEmployee)
    {
        var list = assignmentsForEmployee.OrderBy(a => a.EffectiveFrom).ToList();
        for (var i = 1; i < list.Count; i++)
        {
            if (list[i - 1].Range.Overlaps(list[i].Range))
            {
                return true;
            }
        }

        return false;
    }
}

public enum PunchType
{
    Unknown = 0,
    In = 1,
    Out = 2,
}

public enum PunchSource
{
    Device = 0,
    Manual = 1,
    Import = 2,
}

/// <summary>Raw clock-in/out event. Never edited; manual punches may be deleted, device punches never.</summary>
public sealed class AttendancePunch : CompanyEntity
{
    public Guid EmployeeId { get; set; }

    public Guid? DeviceId { get; set; }

    public DateTime PunchAtUtc { get; set; }

    /// <summary>Company local wall-clock time of the punch (used for day assignment).</summary>
    public DateTime LocalTime { get; set; }

    public PunchType Type { get; set; }

    public PunchSource Source { get; set; }

    /// <summary>Unique key that prevents the same punch being stored twice.</summary>
    public string DedupKey { get; set; } = string.Empty;

    public string? DeviceUserId { get; set; }

    /// <summary>Why a manual punch was added.</summary>
    public string? Reason { get; set; }

    public static string ManualKey(Guid employeeId, DateTime localTime) => $"M:{employeeId:N}:{localTime:yyyyMMddHHmm}";

    public static string DeviceKey(string deviceSerial, string deviceUserId, DateTime deviceTime) => $"D:{deviceSerial}:{deviceUserId}:{deviceTime:yyyyMMddHHmmss}";
}

public enum AttendanceStatus
{
    Present = 0,
    Late = 1,
    HalfDay = 2,
    Absent = 3,
    Leave = 4,
    Holiday = 5,
    WeeklyOff = 6,
    NotJoined = 7,
    Exited = 8,
}

/// <summary>Computed result for one employee and date. Recomputable unless manually corrected.</summary>
public sealed class AttendanceDay : CompanyEntity
{
    public Guid EmployeeId { get; set; }

    public DateOnly Date { get; set; }

    public Guid? ShiftId { get; set; }

    public DateTime? FirstIn { get; set; }

    public DateTime? LastOut { get; set; }

    public int PunchCount { get; set; }

    public int WorkedMinutes { get; set; }

    public int LateMinutes { get; set; }

    public int EarlyLeaveMinutes { get; set; }

    public int OvertimeMinutes { get; set; }

    public AttendanceStatus Status { get; set; }

    public bool MissingPunch { get; set; }

    /// <summary>0, 0.5 or 1 day of leave on this date.</summary>
    public decimal LeaveDays { get; set; }

    public Guid? LeaveTypeId { get; set; }

    /// <summary>Whether the leave on this date is paid.</summary>
    public bool LeaveIsPaid { get; set; }

    public bool IsManuallyCorrected { get; set; }

    public string? CorrectionNote { get; set; }

    public DateTime ComputedAtUtc { get; set; }

    public void Apply(DayResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        ShiftId = result.ShiftId;
        FirstIn = result.FirstIn;
        LastOut = result.LastOut;
        PunchCount = result.PunchCount;
        WorkedMinutes = result.WorkedMinutes;
        LateMinutes = result.LateMinutes;
        EarlyLeaveMinutes = result.EarlyLeaveMinutes;
        OvertimeMinutes = result.OvertimeMinutes;
        Status = result.Status;
        MissingPunch = result.MissingPunch;
        LeaveDays = result.LeaveDays;
        LeaveTypeId = result.LeaveTypeId;
        LeaveIsPaid = result.LeaveIsPaid;
    }
}

/// <summary>Audit of a manual change to an attendance day.</summary>
public sealed class AttendanceCorrection : CompanyEntity
{
    public Guid EmployeeId { get; set; }

    public DateOnly Date { get; set; }

    public string BeforeJson { get; set; } = string.Empty;

    public string AfterJson { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;
}
