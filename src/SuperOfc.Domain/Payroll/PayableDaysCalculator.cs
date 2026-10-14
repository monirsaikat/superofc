using SuperOfc.Domain.Attendance;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Policies;

namespace SuperOfc.Domain.Payroll;

/// <summary>The part of an attendance day that payroll needs.</summary>
public sealed record PayDay(DateOnly Date, AttendanceStatus Status, decimal LeaveDays = 0m, bool LeaveIsPaid = false, int OvertimeMinutes = 0)
{
    public static PayDay From(AttendanceDay day)
    {
        ArgumentNullException.ThrowIfNull(day);
        return new PayDay(day.Date, day.Status, day.LeaveDays, day.LeaveIsPaid, day.OvertimeMinutes);
    }
}

/// <summary>Day counts for one employee over a date range (doc 03 R2).</summary>
public sealed record DaysSummary
{
    /// <summary>Days in the range on which the person was employed.</summary>
    public int CalendarDays { get; init; }

    /// <summary>Full days worked (present or late), including the worked half of a half-day leave.</summary>
    public decimal PresentDays { get; init; }

    /// <summary>Days with status half day (each pays 0.5).</summary>
    public decimal HalfDays { get; init; }

    public decimal PaidLeaveDays { get; init; }

    public decimal UnpaidLeaveDays { get; init; }

    public decimal AbsentDays { get; init; }

    public int Holidays { get; init; }

    public int WeeklyOffs { get; init; }

    public int LateDays { get; init; }

    public decimal LatePenaltyDays { get; init; }

    public int OvertimeMinutes { get; init; }

    /// <summary>Overtime worked on holidays and weekly offs (paid at the holiday multiplier).</summary>
    public int HolidayOvertimeMinutes { get; init; }

    /// <summary>Days paid before late penalty, counting offs and holidays only when the policy pays them.</summary>
    public decimal PaidDaysBeforePenalty { get; init; }

    /// <summary>The paid offs and holidays included in <see cref="PaidDaysBeforePenalty"/>.</summary>
    public decimal PaidOffDays { get; init; }

    public decimal PayableDays => Math.Max(0m, PaidDaysBeforePenalty - LatePenaltyDays);

    /// <summary>Working days in the range (not off, not holiday), used for the working-days basis.</summary>
    public int WorkingDays => CalendarDays - Holidays - WeeklyOffs;

    public static DaysSummary operator +(DaysSummary a, DaysSummary b) => new()
    {
        CalendarDays = a.CalendarDays + b.CalendarDays,
        PresentDays = a.PresentDays + b.PresentDays,
        HalfDays = a.HalfDays + b.HalfDays,
        PaidLeaveDays = a.PaidLeaveDays + b.PaidLeaveDays,
        UnpaidLeaveDays = a.UnpaidLeaveDays + b.UnpaidLeaveDays,
        AbsentDays = a.AbsentDays + b.AbsentDays,
        Holidays = a.Holidays + b.Holidays,
        WeeklyOffs = a.WeeklyOffs + b.WeeklyOffs,
        LateDays = a.LateDays + b.LateDays,
        LatePenaltyDays = a.LatePenaltyDays + b.LatePenaltyDays,
        OvertimeMinutes = a.OvertimeMinutes + b.OvertimeMinutes,
        HolidayOvertimeMinutes = a.HolidayOvertimeMinutes + b.HolidayOvertimeMinutes,
        PaidDaysBeforePenalty = a.PaidDaysBeforePenalty + b.PaidDaysBeforePenalty,
        PaidOffDays = a.PaidOffDays + b.PaidOffDays,
    };
}

/// <summary>Counts payable days from attendance (doc 03 R2). Pure: no database, no clock.</summary>
public static class PayableDaysCalculator
{
    /// <summary>
    /// Summarises <paramref name="days"/> within <paramref name="range"/>. Dates without a record count as absent;
    /// days before joining or after exit (statuses NotJoined/Exited, or outside <paramref name="employment"/>) are not
    /// calendar days for the employee. The late penalty is not applied here; see <see cref="ApplyLatePenalty"/>.
    /// </summary>
    public static DaysSummary Summarize(IEnumerable<PayDay> days, DateRange range, DateRange employment, AttendancePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(days);
        ArgumentNullException.ThrowIfNull(policy);
        var byDate = days.Where(d => range.Contains(d.Date)).GroupBy(d => d.Date).ToDictionary(g => g.Key, g => g.Last());
        var counted = range.Intersect(employment);
        if (counted is null)
        {
            return new DaysSummary();
        }

        int calendar = 0, holidays = 0, offs = 0, late = 0, ot = 0, holidayOt = 0;
        decimal present = 0, half = 0, paidLeave = 0, unpaidLeave = 0, absent = 0, paid = 0, paidOff = 0;
        foreach (var date in counted.Value.EachDay())
        {
            var day = byDate.GetValueOrDefault(date) ?? new PayDay(date, AttendanceStatus.Absent);
            if (day.Status is AttendanceStatus.NotJoined or AttendanceStatus.Exited)
            {
                continue;
            }

            calendar++;
            switch (day.Status)
            {
                case AttendanceStatus.Holiday:
                    holidays++;
                    holidayOt += day.OvertimeMinutes;
                    paid += policy.HolidaysPaid ? 1 : 0;
                    paidOff += policy.HolidaysPaid ? 1 : 0;
                    continue;
                case AttendanceStatus.WeeklyOff:
                    offs++;
                    holidayOt += day.OvertimeMinutes;
                    paid += policy.WeeklyOffsPaid ? 1 : 0;
                    paidOff += policy.WeeklyOffsPaid ? 1 : 0;
                    continue;
                case AttendanceStatus.Leave:
                    // Leave status covers the whole expected day (doc 03 R6, ADR-17).
                    if (day.LeaveIsPaid)
                    {
                        paidLeave += 1;
                        paid += 1;
                    }
                    else
                    {
                        unpaidLeave += 1;
                    }

                    continue;
            }

            ot += day.OvertimeMinutes;
            if (day.Status == AttendanceStatus.Late)
            {
                late++;
            }

            // A half-day leave covers part of the day; the status describes the rest.
            var leave = Math.Clamp(day.LeaveDays, 0m, 1m);
            var work = 1m - leave;
            if (leave > 0)
            {
                if (day.LeaveIsPaid)
                {
                    paidLeave += leave;
                    paid += leave;
                }
                else
                {
                    unpaidLeave += leave;
                }
            }

            switch (day.Status)
            {
                case AttendanceStatus.Present or AttendanceStatus.Late:
                    present += work;
                    paid += work;
                    break;
                case AttendanceStatus.HalfDay when leave == 0:
                    half += 1;
                    paid += 0.5m;
                    absent += 0.5m;
                    break;
                case AttendanceStatus.HalfDay:
                    present += work / 2;
                    paid += work / 2;
                    absent += work / 2;
                    break;
                default:
                    absent += work;
                    break;
            }
        }

        return new DaysSummary
        {
            CalendarDays = calendar,
            PresentDays = present,
            HalfDays = half,
            PaidLeaveDays = paidLeave,
            UnpaidLeaveDays = unpaidLeave,
            AbsentDays = absent,
            Holidays = holidays,
            WeeklyOffs = offs,
            LateDays = late,
            OvertimeMinutes = ot,
            HolidayOvertimeMinutes = holidayOt,
            PaidDaysBeforePenalty = paid,
            PaidOffDays = paidOff,
        };
    }

    /// <summary>"N lates cost X days" over the whole period (doc 10 Q11). Applied once per payslip.</summary>
    public static DaysSummary ApplyLatePenalty(DaysSummary summary, LatePenalty penalty)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(penalty);
        if (!penalty.IsEnabled)
        {
            return summary with { LatePenaltyDays = 0 };
        }

        var days = summary.LateDays / penalty.Instances * penalty.DeductDays;
        return summary with { LatePenaltyDays = Math.Min(days, summary.PaidDaysBeforePenalty) };
    }

    /// <summary>
    /// The share of a monthly amount earned in one salary segment (doc 03 R2 ProrateBasis):
    /// CalendarDays = payable / days in month; WorkingDays = payable working days / working days in the month
    /// (offs and holidays left out); Fixed30 = a month is 30 days and every unpaid day costs 1/30.
    /// </summary>
    public static decimal LinkedFraction(DaysSummary segment, int daysInMonth, int monthWorkingDays, ProrateBasis basis)
    {
        ArgumentNullException.ThrowIfNull(segment);
        var fraction = basis switch
        {
            ProrateBasis.WorkingDays => (segment.PaidDaysBeforePenalty - segment.PaidOffDays - segment.LatePenaltyDays) / Math.Max(1, monthWorkingDays),
            ProrateBasis.Fixed30 => ((30m * segment.CalendarDays / daysInMonth) - (segment.CalendarDays - segment.PayableDays)) / 30m,
            _ => segment.PayableDays / daysInMonth,
        };
        return Math.Clamp(fraction, 0m, 1m);
    }

    /// <summary>Share of a monthly amount that is not attendance-linked: employed days in the segment / days in month.</summary>
    public static decimal CalendarFraction(DaysSummary segment, int daysInMonth)
    {
        ArgumentNullException.ThrowIfNull(segment);
        return Math.Clamp((decimal)segment.CalendarDays / daysInMonth, 0m, 1m);
    }
}
