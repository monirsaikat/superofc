using SuperOfc.Domain.Attendance;
using SuperOfc.Domain.Leave;
using SuperOfc.Domain.Organization;
using SuperOfc.Domain.Policies;

namespace SuperOfc.Domain.Tests.Attendance;

public class AttendanceCalculatorTests
{
    // General shift 09:00-18:00, 60 min break, grace 10, half >= 240, full >= 420.
    private static readonly ShiftSpec General = new(Guid.NewGuid(), "General", new TimeOnly(9, 0), 540, 10, 0, 60, 240, 420, 0);

    // Night shift 22:00-06:00, 30 min break.
    private static readonly ShiftSpec Night = new(Guid.NewGuid(), "Night", new TimeOnly(22, 0), 480, 10, 0, 30, 210, 390, 0);

    private static readonly DateOnly Day = new(2026, 3, 10);
    private static readonly AttendancePolicy Policy = new();

    private static DateTime At(int hour, int minute = 0, int addDays = 0) => Day.AddDays(addDays).ToDateTime(new TimeOnly(hour, minute));

    private static DayResult Run(
        DateTime[] punches,
        ShiftSpec? shift = null,
        DayKind kind = DayKind.Working,
        DayLeave? leave = null,
        AttendancePolicy? policy = null,
        DateOnly? join = null,
        DateOnly? exit = null,
        bool overtimeEligible = true)
        => AttendanceCalculator.Compute(
            new DayInput(Day, join ?? new DateOnly(2025, 1, 1), exit, overtimeEligible, shift ?? General, kind, leave, punches),
            policy ?? Policy);

    [Theory]
    [InlineData(9, 0, 18, 0, AttendanceStatus.Present, 0, 480)]
    [InlineData(9, 10, 18, 0, AttendanceStatus.Present, 0, 470)] // inside grace
    [InlineData(9, 25, 18, 0, AttendanceStatus.Late, 15, 455)]
    [InlineData(9, 0, 15, 0, AttendanceStatus.HalfDay, 0, 300)]   // 360 raw > 270 half span, break deducted
    [InlineData(9, 0, 12, 0, AttendanceStatus.Absent, 0, 180)]    // below half-day minimum
    public void Status_late_and_worked_minutes_follow_the_shift(int inH, int inM, int outH, int outM, AttendanceStatus status, int late, int worked)
    {
        var result = Run([At(inH, inM), At(outH, outM)]);

        result.Status.Should().Be(status);
        result.LateMinutes.Should().Be(late);
        result.WorkedMinutes.Should().Be(worked);
        result.MissingPunch.Should().BeFalse();
    }

    [Fact]
    public void No_punches_on_a_working_day_is_absent()
        => Run([]).Status.Should().Be(AttendanceStatus.Absent);

    [Fact]
    public void Early_leave_is_measured_against_shift_end()
        => Run([At(9), At(17, 30)]).EarlyLeaveMinutes.Should().Be(30);

    [Theory]
    [InlineData(MissingPunchRule.TreatAsHalfDay, AttendanceStatus.HalfDay)]
    [InlineData(MissingPunchRule.TreatAsAbsent, AttendanceStatus.Absent)]
    [InlineData(MissingPunchRule.TreatAsPresent, AttendanceStatus.Present)]
    public void Single_punch_follows_missing_punch_policy(MissingPunchRule rule, AttendanceStatus expected)
    {
        var result = Run([At(8, 55)], policy: Policy with { MissingPunch = rule });

        result.Status.Should().Be(expected);
        result.MissingPunch.Should().BeTrue();
        result.FirstIn.Should().Be(At(8, 55));
        result.LastOut.Should().BeNull();
    }

    [Fact]
    public void Double_taps_are_ignored()
    {
        var result = Run([At(9), At(9, 1), At(18)]);

        result.PunchCount.Should().Be(2);
        result.Status.Should().Be(AttendanceStatus.Present);
    }

    [Fact]
    public void Alternating_pairing_sums_pairs()
    {
        var policy = Policy with { PunchPairing = PunchPairing.Alternating };

        // 09:00-13:00 and 14:00-18:00 = 480 raw; the shift break is still deducted, giving 420.
        var result = Run([At(9), At(13), At(14), At(18)], policy: policy);

        result.WorkedMinutes.Should().Be(420);
        result.Status.Should().Be(AttendanceStatus.Present);
    }

    [Fact]
    public void Overtime_is_rounded_down_and_needs_eligibility()
    {
        // 09:00-20:10 = 670 raw, 610 worked, 130 beyond 480, rounded down to 120.
        Run([At(9), At(20, 10)]).OvertimeMinutes.Should().Be(120);
        Run([At(9), At(20, 10)], overtimeEligible: false).OvertimeMinutes.Should().Be(0);
        Run([At(9), At(20, 10)], policy: Policy with { OvertimeEnabled = false }).OvertimeMinutes.Should().Be(0);
    }

    [Fact]
    public void Holiday_with_punches_stays_holiday_and_counts_all_time_as_overtime()
    {
        var result = Run([At(10), At(15, 20)], kind: DayKind.Holiday);

        result.Status.Should().Be(AttendanceStatus.Holiday);
        result.OvertimeMinutes.Should().Be(300);
        result.LateMinutes.Should().Be(0);
    }

    [Fact]
    public void Weekly_off_without_punches()
        => Run([], kind: DayKind.WeeklyOff).Status.Should().Be(AttendanceStatus.WeeklyOff);

    [Fact]
    public void Full_day_leave_wins_over_absence()
    {
        var leaveType = Guid.NewGuid();

        var result = Run([], leave: new DayLeave(leaveType, 1m, true));

        result.Status.Should().Be(AttendanceStatus.Leave);
        result.LeaveDays.Should().Be(1m);
        result.LeaveTypeId.Should().Be(leaveType);
    }

    [Fact]
    public void Half_day_leave_halves_the_expected_time()
    {
        // Half of the general shift: 09:00-13:30, half-day minimum 120, full-day minimum 210.
        var result = Run([At(9), At(13, 30)], leave: new DayLeave(Guid.NewGuid(), 0.5m, true));

        result.Status.Should().Be(AttendanceStatus.Present);
        result.LeaveDays.Should().Be(0.5m);
    }

    [Fact]
    public void Half_working_day_expects_half_the_shift()
        => Run([At(9), At(13, 30)], kind: DayKind.HalfDay).Status.Should().Be(AttendanceStatus.Present);

    [Fact]
    public void Before_join_and_after_exit()
    {
        Run([At(9), At(18)], join: Day.AddDays(1)).Status.Should().Be(AttendanceStatus.NotJoined);
        Run([At(9), At(18)], exit: Day.AddDays(-1)).Status.Should().Be(AttendanceStatus.Exited);
        Run([At(9), At(18)], exit: Day).Status.Should().Be(AttendanceStatus.Present);
    }

    [Fact]
    public void Night_shift_crossing_midnight()
    {
        var result = Run([At(21, 55), At(6, 5, addDays: 1)], shift: Night);

        result.Status.Should().Be(AttendanceStatus.Present);
        result.WorkedMinutes.Should().Be(460);
        result.LateMinutes.Should().Be(0);
        result.EarlyLeaveMinutes.Should().Be(0);
    }

    [Fact]
    public void Night_shift_window_keeps_morning_punch_on_the_previous_day()
    {
        var (from, to) = AttendanceCalculator.Window(Day, Night, Night, Policy);

        from.Should().Be(At(18));
        to.Should().Be(At(14, 0, addDays: 1));
        (At(6, 5, addDays: 1) >= from && At(6, 5, addDays: 1) < to).Should().BeTrue();
    }

    [Fact]
    public void Day_window_is_clipped_by_the_next_day()
    {
        var (_, to) = AttendanceCalculator.Window(Day, General, General, Policy);

        // General ends 18:00 + 8 h = 02:00, next window starts 05:00, so no clipping.
        to.Should().Be(At(2, 0, addDays: 1));

        var early = General with { Start = new TimeOnly(4, 0) };
        var (_, clipped) = AttendanceCalculator.Window(Day, General, early, Policy);
        clipped.Should().Be(At(0, 0, addDays: 1));
    }

    [Fact]
    public void Same_input_gives_same_result()
    {
        DateTime[] punches = [At(9, 20), At(18, 40)];
        Run(punches).Should().Be(Run(punches));
    }
}

public class ShiftAssignmentTests
{
    [Fact]
    public void Rotation_of_three_shifts_changes_weekly()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
        var start = new DateOnly(2026, 3, 1);
        var assignment = new ShiftAssignment { ShiftId = a, EffectiveFrom = start, RotationShiftIds = $"{a},{b},{c}", RotationDays = 7 };

        assignment.ShiftOn(start).Should().Be(a);
        assignment.ShiftOn(start.AddDays(6)).Should().Be(a);
        assignment.ShiftOn(start.AddDays(7)).Should().Be(b);
        assignment.ShiftOn(start.AddDays(14)).Should().Be(c);
        assignment.ShiftOn(start.AddDays(21)).Should().Be(a);
    }

    [Fact]
    public void Resolver_uses_latest_matching_assignment_or_default()
    {
        Guid general = Guid.NewGuid(), night = Guid.NewGuid();
        var list = new[]
        {
            new ShiftAssignment { ShiftId = night, EffectiveFrom = new DateOnly(2026, 3, 1), EffectiveTo = new DateOnly(2026, 3, 31) },
        };

        ShiftResolver.Resolve(list, new DateOnly(2026, 3, 15), general).Should().Be(night);
        ShiftResolver.Resolve(list, new DateOnly(2026, 4, 1), general).Should().Be(general);
    }

    [Fact]
    public void Overlap_is_detected()
    {
        var list = new[]
        {
            new ShiftAssignment { EffectiveFrom = new DateOnly(2026, 3, 1), EffectiveTo = new DateOnly(2026, 3, 31) },
            new ShiftAssignment { EffectiveFrom = new DateOnly(2026, 3, 31) },
        };

        ShiftResolver.HasOverlap(list).Should().BeTrue();
        list[1].EffectiveFrom = new DateOnly(2026, 4, 1);
        ShiftResolver.HasOverlap(list).Should().BeFalse();
    }

    [Fact]
    public void Night_shift_span_crosses_midnight()
    {
        var shift = new Shift { StartTime = new TimeOnly(22, 0), EndTime = new TimeOnly(6, 0), BreakMinutes = 30 };

        shift.CrossesMidnight.Should().BeTrue();
        shift.SpanMinutes.Should().Be(480);
        shift.ScheduledMinutes.Should().Be(450);
    }
}

public class LeaveRuleTests
{
    private static readonly DateOnly YearStart = new(2026, 1, 1);

    [Fact]
    public void Yearly_accrual_is_prorated_for_joiners()
    {
        var annual = new LeaveType { YearlyEntitlement = 12, Accrual = LeaveAccrual.Yearly };

        annual.AccruedBy(YearStart, YearStart, new DateOnly(2020, 1, 1)).Should().Be(12);
        annual.AccruedBy(YearStart, new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 1)).Should().Be(6);
        annual.AccruedBy(YearStart, new DateOnly(2026, 6, 30), new DateOnly(2026, 7, 1)).Should().Be(0);
    }

    [Fact]
    public void Monthly_accrual_counts_months_so_far()
    {
        var earned = new LeaveType { YearlyEntitlement = 18, Accrual = LeaveAccrual.Monthly };

        earned.AccruedBy(YearStart, new DateOnly(2026, 1, 1), new DateOnly(2020, 1, 1)).Should().Be(1.5m);
        earned.AccruedBy(YearStart, new DateOnly(2026, 4, 15), new DateOnly(2020, 1, 1)).Should().Be(6m);
        earned.AccruedBy(YearStart, new DateOnly(2027, 2, 1), new DateOnly(2020, 1, 1)).Should().Be(18m);
    }

    [Fact]
    public void Request_days_skip_offs_and_holidays()
    {
        // Default week: Friday off. 2026-03-05 Thu .. 2026-03-09 Mon with a holiday on Sunday the 8th.
        var calendar = new WorkCalendar(WorkingWeek.Default, [new Holiday { Date = new DateOnly(2026, 3, 8), Name = "H" }]);

        var days = LeaveRequest.CountDays(new DateOnly(2026, 3, 5), new DateOnly(2026, 3, 9), false, calendar, null);
        var expected = calendar.WorkingDays(new SuperOfc.Domain.Common.DateRange(new DateOnly(2026, 3, 5), new DateOnly(2026, 3, 9)));

        days.Should().Be(expected);
        days.Should().BeLessThan(5);
        LeaveRequest.CountDays(new DateOnly(2026, 3, 6), new DateOnly(2026, 3, 6), true, calendar, null).Should().Be(0);
    }

    [Fact]
    public void Balance_closing_adds_up()
    {
        var balance = new LeaveBalance { Opening = 2, Accrued = 10, Used = 3.5m, Adjusted = -1 };

        balance.Closing.Should().Be(7.5m);
    }
}
