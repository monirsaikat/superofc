using SuperOfc.Domain.Organization;
using SuperOfc.Domain.Policies;

namespace SuperOfc.Domain.Attendance;

/// <summary>Leave that applies to one date.</summary>
public sealed record DayLeave(Guid LeaveTypeId, decimal Days, bool IsPaid)
{
    public bool IsFullDay => Days >= 1m;
}

/// <summary>Everything the calculator needs for one employee and date (doc 03, R1).</summary>
public sealed record DayInput(
    DateOnly Date,
    DateOnly JoinDate,
    DateOnly? ExitDate,
    bool OvertimeEligible,
    ShiftSpec? Shift,
    DayKind DayKind,
    DayLeave? Leave,
    IReadOnlyList<DateTime> Punches);

public sealed record DayResult(
    DateOnly Date,
    Guid? ShiftId,
    AttendanceStatus Status,
    DateTime? FirstIn,
    DateTime? LastOut,
    int PunchCount,
    int WorkedMinutes,
    int LateMinutes,
    int EarlyLeaveMinutes,
    int OvertimeMinutes,
    bool MissingPunch,
    decimal LeaveDays,
    Guid? LeaveTypeId,
    bool LeaveIsPaid);

/// <summary>Pure attendance day computation (doc 03, R1; ADR-08). Same input always gives the same result.</summary>
public static class AttendanceCalculator
{
    public static DayResult Compute(DayInput input, AttendancePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(policy);

        var punches = Dedupe(input.Punches, policy.DuplicatePunchMinutes);
        var leave = input.Leave;
        DayResult Result(AttendanceStatus status, Work? work = null, int overtime = 0) => new(
            input.Date,
            input.Shift?.Id,
            status,
            work?.FirstIn,
            work?.LastOut,
            punches.Count,
            work?.Worked ?? 0,
            work?.Late ?? 0,
            work?.Early ?? 0,
            overtime,
            work?.Missing ?? false,
            leave?.Days ?? 0m,
            leave?.LeaveTypeId,
            leave?.IsPaid ?? false);

        // Step 2: outside employment.
        if (input.Date < input.JoinDate)
        {
            return Result(AttendanceStatus.NotJoined) with { PunchCount = 0, LeaveDays = 0, LeaveTypeId = null, LeaveIsPaid = false };
        }

        if (input.ExitDate is { } exit && input.Date > exit)
        {
            return Result(AttendanceStatus.Exited) with { PunchCount = 0, LeaveDays = 0, LeaveTypeId = null, LeaveIsPaid = false };
        }

        // Step 3: holidays, weekly offs and full-day leave. Punches still count as overtime when allowed.
        if (input.DayKind is DayKind.Holiday or DayKind.WeeklyOff)
        {
            var status = input.DayKind == DayKind.Holiday ? AttendanceStatus.Holiday : AttendanceStatus.WeeklyOff;
            leave = null;
            if (punches.Count == 0)
            {
                return Result(status);
            }

            var offWork = Pair(punches, policy, input.Shift, breakApplies: false);
            var offOvertime = policy.OvertimeEnabled && policy.OvertimeOnHolidays && input.OvertimeEligible
                ? RoundDown(offWork.Worked, policy.OvertimeRoundingMinutes)
                : 0;
            return Result(status, offWork with { Late = 0, Early = 0 }, offOvertime);
        }

        // Leave on a half working day covers all the expected time.
        if (leave is { IsFullDay: true } || (input.DayKind == DayKind.HalfDay && leave is { Days: > 0m }))
        {
            return Result(AttendanceStatus.Leave);
        }

        var shift = input.Shift;
        if (shift is null)
        {
            // No shift known: presence only.
            return punches.Count == 0 ? Result(AttendanceStatus.Absent) : Result(AttendanceStatus.Present, Pair(punches, policy, null, breakApplies: false));
        }

        if (input.DayKind == DayKind.HalfDay)
        {
            shift = shift.Halved();
        }

        // Half-day leave: the employee is expected for half the shift.
        if (leave is { Days: > 0m and < 1m })
        {
            shift = shift.Halved();
        }

        // Step 7 (no punches).
        if (punches.Count == 0)
        {
            return Result(AttendanceStatus.Absent);
        }

        // Steps 4-6.
        var work = Pair(punches, policy, shift, breakApplies: true);
        work = work with
        {
            Late = Math.Max(0, Minutes(work.FirstIn!.Value - shift.StartOn(input.Date).AddMinutes(shift.GraceInMinutes))),
            Early = work.LastOut is { } lastOut ? Math.Max(0, Minutes(shift.EndOn(input.Date).AddMinutes(-shift.GraceOutMinutes) - lastOut)) : 0,
        };

        AttendanceStatus statusResult;
        if (work.Missing)
        {
            statusResult = policy.MissingPunch switch
            {
                MissingPunchRule.TreatAsAbsent => AttendanceStatus.Absent,
                MissingPunchRule.TreatAsPresent => work.Late > 0 ? AttendanceStatus.Late : AttendanceStatus.Present,
                _ => AttendanceStatus.HalfDay,
            };
            return Result(statusResult, work with { Early = 0 });
        }

        if (work.Worked < shift.MinimumMinutesForHalfDay)
        {
            statusResult = AttendanceStatus.Absent;
        }
        else if (work.Worked < shift.MinimumMinutesForFullDay)
        {
            statusResult = AttendanceStatus.HalfDay;
        }
        else
        {
            statusResult = work.Late > 0 ? AttendanceStatus.Late : AttendanceStatus.Present;
        }

        // Step 8.
        var overtime = 0;
        if (policy.OvertimeEnabled && input.OvertimeEligible)
        {
            overtime = RoundDown(Math.Max(0, work.Worked - shift.ScheduledMinutes - shift.OvertimeStartsAfterMinutes), policy.OvertimeRoundingMinutes);
        }

        return Result(statusResult, work, overtime);
    }

    /// <summary>
    /// The punch window of a day: from shift start minus the "before" hours to shift end plus the "after" hours,
    /// but never past the start of the next day's window (so a punch belongs to one day only).
    /// </summary>
    public static (DateTime From, DateTime To) Window(DateOnly date, ShiftSpec? shift, ShiftSpec? nextDayShift, AttendancePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var from = shift is null ? date.ToDateTime(TimeOnly.MinValue) : shift.StartOn(date).AddHours(-policy.WindowBeforeShiftHours);
        var to = shift is null ? date.AddDays(1).ToDateTime(TimeOnly.MinValue) : shift.EndOn(date).AddHours(policy.WindowAfterShiftHours);
        var next = date.AddDays(1);
        var nextFrom = nextDayShift is null ? next.ToDateTime(TimeOnly.MinValue) : nextDayShift.StartOn(next).AddHours(-policy.WindowBeforeShiftHours);
        if (nextFrom < to)
        {
            to = nextFrom;
        }

        return (from, to);
    }

    /// <summary>Drops punches that follow the previous kept punch within <paramref name="minutes"/> (double taps).</summary>
    public static List<DateTime> Dedupe(IEnumerable<DateTime> punches, int minutes)
    {
        ArgumentNullException.ThrowIfNull(punches);
        var result = new List<DateTime>();
        foreach (var p in punches.OrderBy(p => p))
        {
            if (result.Count == 0 || (p - result[^1]).TotalMinutes >= minutes)
            {
                result.Add(p);
            }
        }

        return result;
    }

    private sealed record Work(DateTime? FirstIn, DateTime? LastOut, int Worked, bool Missing, int Late = 0, int Early = 0);

    private static Work Pair(List<DateTime> punches, AttendancePolicy policy, ShiftSpec? shift, bool breakApplies)
    {
        if (punches.Count == 1)
        {
            return new Work(punches[0], null, 0, Missing: true);
        }

        int raw;
        var missing = false;
        if (policy.PunchPairing == PunchPairing.Alternating)
        {
            raw = 0;
            for (var i = 0; i + 1 < punches.Count; i += 2)
            {
                raw += Minutes(punches[i + 1] - punches[i]);
            }

            missing = punches.Count % 2 == 1;
        }
        else
        {
            raw = Minutes(punches[^1] - punches[0]);
        }

        // The break is deducted only when the person stayed longer than half the shift span.
        var worked = raw;
        if (breakApplies && shift is not null && shift.BreakMinutes > 0 && raw > shift.SpanMinutes / 2)
        {
            worked = Math.Max(0, raw - shift.BreakMinutes);
        }

        return new Work(punches[0], punches[^1], worked, missing);
    }

    private static int Minutes(TimeSpan span) => (int)Math.Floor(span.TotalMinutes);

    private static int RoundDown(int minutes, int step) => step <= 1 ? minutes : minutes / step * step;
}
