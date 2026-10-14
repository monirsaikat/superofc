using SuperOfc.Domain.Common;

namespace SuperOfc.Domain.Tests.Common;

public class MoneyTests
{
    [Fact]
    public void Add_and_subtract_work_in_minor_units()
    {
        var a = Money.FromMajor(10.25m);
        var b = Money.FromMajor(0.75m);

        (a + b).Should().Be(new Money(1100));
        (a - b).Should().Be(new Money(950));
        (-a).Minor.Should().Be(-1025);
    }

    [Theory]
    [InlineData(10000, 3, new long[] { 3334, 3333, 3333 })]
    [InlineData(100, 3, new long[] { 34, 33, 33 })]
    [InlineData(-100, 3, new long[] { -34, -33, -33 })]
    [InlineData(5, 5, new long[] { 1, 1, 1, 1, 1 })]
    public void Allocate_never_loses_minor_units(long minor, int parts, long[] expected)
    {
        var shares = new Money(minor).Allocate(parts);

        shares.Select(s => s.Minor).Should().Equal(expected);
        Money.Sum(shares).Minor.Should().Be(minor);
    }

    [Theory]
    [InlineData(1000, 33.333, RoundingMode.HalfUp, 1, 333)]
    [InlineData(1000, 25, RoundingMode.HalfUp, 100, 300)]
    [InlineData(1000, 25, RoundingMode.HalfEven, 100, 200)]
    [InlineData(1050, 1, RoundingMode.Down, 100, 1000)]
    [InlineData(1001, 1, RoundingMode.Up, 100, 1100)]
    [InlineData(-1050, 1, RoundingMode.HalfUp, 100, -1100)]
    public void Percent_and_multiply_apply_rounding_policy(long minor, double percentOrFactor, RoundingMode mode, long step, long expected)
    {
        var money = new Money(minor);
        var policy = new RoundingPolicy(mode, step);

        var result = percentOrFactor == 1 ? money.Round(policy) : money.Percent((decimal)percentOrFactor, policy);

        result.Minor.Should().Be(expected);
    }

    [Fact]
    public void Percent_of_basic_is_exact_when_possible()
    {
        Money.FromMajor(25000m).Percent(40m).Should().Be(Money.FromMajor(10000m));
    }

    [Fact]
    public void Divide_by_zero_throws()
    {
        var act = () => Money.FromMajor(1).Divide(0);

        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void Comparison_and_min_max()
    {
        var small = new Money(1);
        var large = new Money(2);

        (small < large).Should().BeTrue();
        Money.Max(small, large).Should().Be(large);
        Money.Min(small, large).Should().Be(small);
    }

    [Fact]
    public void FromMajor_rounds_fractions_of_minor_units()
    {
        Money.FromMajor(0.005m).Minor.Should().Be(1);
        Money.FromMajor(0.005m, RoundingMode.Down).Minor.Should().Be(0);
    }
}
