using SuperOfc.Domain.Common;
using SuperOfc.Domain.Employees;
using SuperOfc.Domain.Organization;

namespace SuperOfc.Domain.Tests.Organization;

public class WorkCalendarTests
{
    private static readonly Guid Factory = Guid.NewGuid();

    // 2026-03-06 is a Friday, 2026-03-07 Saturday, 2026-03-08 Sunday, 2026-03-26 Thursday.
    private static WorkCalendar Calendar(WorkingWeek? week = null, params Holiday[] holidays) => new(week ?? WorkingWeek.Default, holidays);

    [Fact]
    public void Weekly_off_and_working_days_follow_the_working_week()
    {
        var calendar = Calendar();

        calendar.GetDayKind(new DateOnly(2026, 3, 6)).Should().Be(DayKind.WeeklyOff);
        calendar.GetDayKind(new DateOnly(2026, 3, 8)).Should().Be(DayKind.Working);
    }

    [Fact]
    public void Half_day_weekday_counts_half_in_working_days()
    {
        var week = WorkingWeek.Default.With(DayOfWeek.Thursday, WorkDayType.Half);
        var calendar = Calendar(week);

        calendar.GetDayKind(new DateOnly(2026, 3, 26)).Should().Be(DayKind.HalfDay);
        // March 2026: 31 days, 8 off days (Fri/Sat), 4 Thursdays as half days.
        calendar.WorkingDays(DateRange.Month(2026, 3)).Should().Be(31 - 8 - 2);
    }

    [Fact]
    public void Holiday_on_weekly_off_is_still_a_holiday()
    {
        var calendar = Calendar(null, new Holiday { Date = new DateOnly(2026, 3, 6), Name = "On Friday" });

        calendar.GetDayKind(new DateOnly(2026, 3, 6)).Should().Be(DayKind.Holiday);
    }

    [Fact]
    public void Location_specific_holiday_applies_only_to_that_location()
    {
        var calendar = Calendar(null, new Holiday { Date = new DateOnly(2026, 3, 26), Name = "Factory day", LocationId = Factory });

        calendar.GetDayKind(new DateOnly(2026, 3, 26), Factory).Should().Be(DayKind.Holiday);
        calendar.GetDayKind(new DateOnly(2026, 3, 26), Guid.NewGuid()).Should().Be(DayKind.Working);
        calendar.GetDayKind(new DateOnly(2026, 3, 26)).Should().Be(DayKind.Working);
    }
}

public class EmployeeLifecycleTests
{
    private static Employee New() => new() { JoinDate = new DateOnly(2025, 1, 15), Status = EmployeeStatus.Probation };

    [Fact]
    public void Confirm_moves_probation_to_active()
    {
        var e = New();

        e.Confirm(new DateOnly(2025, 7, 15));

        e.Status.Should().Be(EmployeeStatus.Active);
        e.ConfirmationDate.Should().Be(new DateOnly(2025, 7, 15));
    }

    [Fact]
    public void Exit_before_join_is_rejected_and_exit_limits_employment()
    {
        var e = New();
        var act = () => e.Exit(new DateOnly(2024, 12, 31), "x");
        act.Should().Throw<DomainException>();

        e.Exit(new DateOnly(2026, 2, 10), "Resigned");

        e.Status.Should().Be(EmployeeStatus.Left);
        e.IsEmployedOn(new DateOnly(2026, 2, 10)).Should().BeTrue();
        e.IsEmployedOn(new DateOnly(2026, 2, 11)).Should().BeFalse();
        e.EmploymentWithin(DateRange.Month(2026, 2))!.Value.Days.Should().Be(10);
    }

    [Fact]
    public void Joiner_mid_month_is_employed_only_from_join_date()
    {
        var e = New();

        e.EmploymentWithin(DateRange.Month(2025, 1))!.Value.Days.Should().Be(17);
        e.EmploymentWithin(DateRange.Month(2024, 12)).Should().BeNull();
    }

    [Fact]
    public void Rejoin_resets_exit_and_requires_later_date()
    {
        var e = New();
        e.Exit(new DateOnly(2025, 6, 30), "Resigned");

        var act = () => e.Rejoin(new DateOnly(2025, 6, 30));
        act.Should().Throw<DomainException>();
        e.Rejoin(new DateOnly(2025, 9, 1));

        e.ExitDate.Should().BeNull();
        e.Status.Should().Be(EmployeeStatus.Probation);
        e.JoinDate.Should().Be(new DateOnly(2025, 9, 1));
    }
}
