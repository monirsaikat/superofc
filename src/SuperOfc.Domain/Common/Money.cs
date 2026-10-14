using System.Globalization;

namespace SuperOfc.Domain.Common;

/// <summary>
/// An amount in minor units (for example cents or paisa). The currency is a company-level setting,
/// so Money does not carry it. Never use decimal or double for stored amounts.
/// </summary>
public readonly record struct Money(long Minor) : IComparable<Money>
{
    public const int MinorPerMajor = 100;

    public static Money Zero => new(0);

    public decimal Major => Minor / (decimal)MinorPerMajor;

    public bool IsZero => Minor == 0;

    public bool IsNegative => Minor < 0;

    public static Money FromMajor(decimal major, RoundingMode mode = RoundingMode.HalfUp)
        => new(new RoundingPolicy(mode, 1).Apply(major * MinorPerMajor));

    public static Money operator +(Money a, Money b) => new(checked(a.Minor + b.Minor));

    public static Money operator -(Money a, Money b) => new(checked(a.Minor - b.Minor));

    public static Money operator -(Money a) => new(-a.Minor);

    public static bool operator <(Money a, Money b) => a.Minor < b.Minor;

    public static bool operator >(Money a, Money b) => a.Minor > b.Minor;

    public static bool operator <=(Money a, Money b) => a.Minor <= b.Minor;

    public static bool operator >=(Money a, Money b) => a.Minor >= b.Minor;

    public static Money Max(Money a, Money b) => a >= b ? a : b;

    public static Money Min(Money a, Money b) => a <= b ? a : b;

    public static Money Sum(IEnumerable<Money> values) => new(values.Sum(v => v.Minor));

    public Money Add(Money other) => this + other;

    public Money Subtract(Money other) => this - other;

    public Money Negate() => -this;

    public Money Abs() => new(Math.Abs(Minor));

    /// <summary>Multiply by a factor and round the result to minor units with the policy.</summary>
    public Money Multiply(decimal factor, RoundingPolicy? rounding = null)
        => new((rounding ?? RoundingPolicy.None).Apply(Minor * factor));

    /// <summary>Percentage of this amount, e.g. Percent(40) = 40 %.</summary>
    public Money Percent(decimal percent, RoundingPolicy? rounding = null) => Multiply(percent / 100m, rounding);

    /// <summary>Divide by a quantity and round.</summary>
    public Money Divide(decimal divisor, RoundingPolicy? rounding = null)
    {
        if (divisor == 0)
        {
            throw new DomainException("Cannot divide money by zero.");
        }

        return new((rounding ?? RoundingPolicy.None).Apply(Minor / divisor));
    }

    public Money Round(RoundingPolicy rounding) => new(rounding.Apply(Minor));

    /// <summary>Split into equal parts that add up exactly; earlier parts get the remainder.</summary>
    public IReadOnlyList<Money> Allocate(int parts)
    {
        if (parts <= 0)
        {
            throw new DomainException("Allocation needs at least one part.");
        }

        var baseShare = Minor / parts;
        var remainder = Minor % parts;
        var result = new Money[parts];
        for (var i = 0; i < parts; i++)
        {
            var extra = i < Math.Abs(remainder) ? Math.Sign(remainder) : 0;
            result[i] = new Money(baseShare + extra);
        }

        return result;
    }

    public int CompareTo(Money other) => Minor.CompareTo(other.Minor);

    public override string ToString() => Major.ToString("0.00", CultureInfo.InvariantCulture);
}
