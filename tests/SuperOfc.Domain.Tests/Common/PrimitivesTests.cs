using SuperOfc.Domain.Common;
using SuperOfc.Domain.Organization;
using SuperOfc.Domain.Security;

namespace SuperOfc.Domain.Tests.Common;

public class DateRangeTests
{
    private static DateOnly D(int month, int day) => new(2026, month, day);

    [Theory]
    [InlineData(1, 1, 1, 31, 1, 31, 2, 28, true)]
    [InlineData(1, 1, 1, 30, 1, 31, 2, 28, false)]
    [InlineData(2, 1, 2, 28, 1, 1, 1, 31, false)]
    [InlineData(1, 10, 1, 20, 1, 15, 1, 16, true)]
    public void Overlaps_is_inclusive(int fm1, int fd1, int tm1, int td1, int fm2, int fd2, int tm2, int td2, bool expected)
    {
        var a = new DateRange(D(fm1, fd1), D(tm1, td1));
        var b = new DateRange(D(fm2, fd2), D(tm2, td2));

        a.Overlaps(b).Should().Be(expected);
        b.Overlaps(a).Should().Be(expected);
    }

    [Fact]
    public void Open_ended_ranges_overlap_everything_after_start()
    {
        var open = new DateRange(D(3, 1), null);

        open.Overlaps(new DateRange(D(12, 1), D(12, 31))).Should().BeTrue();
        open.Overlaps(new DateRange(D(1, 1), D(2, 28))).Should().BeFalse();
        open.Contains(D(12, 31)).Should().BeTrue();
    }

    [Fact]
    public void Month_has_correct_days_and_intersection()
    {
        var feb = DateRange.Month(2028, 2);
        feb.Days.Should().Be(29);

        var part = new DateRange(new DateOnly(2028, 2, 10), null).Intersect(feb);
        part!.Value.Days.Should().Be(20);
        part.Value.EachDay().Count().Should().Be(20);
    }

    [Fact]
    public void End_before_start_throws()
    {
        var act = () => new DateRange(D(2, 1), D(1, 1));

        act.Should().Throw<DomainException>();
    }
}

public class ResultTests
{
    [Fact]
    public void Combine_collects_all_errors()
    {
        var combined = Result.Combine(Result.Success(), new Error("A", "a"), new Error("B", "b"));

        combined.IsFailure.Should().BeTrue();
        combined.Errors.Select(e => e.Code).Should().Equal("A", "B");
    }

    [Fact]
    public void Value_of_failure_throws_and_map_propagates_errors()
    {
        Result<int> failed = new Error("X", "x");

        var act = () => failed.Value;
        act.Should().Throw<InvalidOperationException>();
        failed.Map(v => v * 2).FirstError!.Code.Should().Be("X");

        Result<int> ok = 21;
        ok.Map(v => v * 2).Value.Should().Be(42);
    }
}

public class CompanyAndUserTests
{
    [Theory]
    [InlineData(7, 2026, 6, 30, 2025)]
    [InlineData(7, 2026, 7, 1, 2026)]
    [InlineData(1, 2026, 3, 15, 2026)]
    public void Fiscal_year_start_depends_on_month(int startMonth, int year, int month, int day, int expectedYear)
    {
        var company = new Company { FiscalYearStartMonth = startMonth };

        company.FiscalYearStart(new DateOnly(year, month, day)).Should().Be(new DateOnly(expectedYear, startMonth, 1));
        company.FiscalYear(new DateOnly(year, month, day)).Days.Should().BeInRange(365, 366);
    }

    [Fact]
    public void Working_week_default_has_friday_saturday_off()
    {
        var week = WorkingWeek.Default;

        week.For(DayOfWeek.Friday).Should().Be(WorkDayType.Off);
        week.For(DayOfWeek.Sunday).Should().Be(WorkDayType.Full);
        week.With(DayOfWeek.Sunday, WorkDayType.Half).For(DayOfWeek.Sunday).Should().Be(WorkDayType.Half);
    }

    [Fact]
    public void User_locks_after_max_failed_attempts_and_unlocks_after_time()
    {
        var now = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var user = new User();

        for (var i = 0; i < 4; i++)
        {
            user.RegisterFailedLogin(now, 5, TimeSpan.FromMinutes(10));
        }

        user.IsLockedOut(now).Should().BeFalse();
        user.RegisterFailedLogin(now, 5, TimeSpan.FromMinutes(10));
        user.IsLockedOut(now).Should().BeTrue();
        user.IsLockedOut(now.AddMinutes(11)).Should().BeFalse();
    }

    [Fact]
    public void Permission_matrix_admin_has_everything_viewer_only_views()
    {
        foreach (var permission in Enum.GetValues<Permission>())
        {
            PermissionMatrix.Has(Role.Admin, permission).Should().BeTrue();
        }

        PermissionMatrix.For(Role.Viewer).Should().BeEquivalentTo([Permission.ViewData]);
    }
}
