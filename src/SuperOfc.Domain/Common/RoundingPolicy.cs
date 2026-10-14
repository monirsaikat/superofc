namespace SuperOfc.Domain.Common;

public enum RoundingMode
{
    /// <summary>Round half away from zero (commercial rounding).</summary>
    HalfUp = 0,

    /// <summary>Banker's rounding.</summary>
    HalfEven = 1,

    /// <summary>Always towards positive infinity.</summary>
    Up = 2,

    /// <summary>Always towards zero.</summary>
    Down = 3,
}

/// <summary>How an amount is rounded: mode plus step in minor units (1 = cents, 100 = whole units).</summary>
public sealed record RoundingPolicy(RoundingMode Mode, long StepMinor)
{
    public static RoundingPolicy None { get; } = new(RoundingMode.HalfUp, 1);

    public static RoundingPolicy WholeUnit { get; } = new(RoundingMode.HalfUp, 100);

    public long Apply(decimal minorUnits)
    {
        if (StepMinor <= 0)
        {
            throw new DomainException("Rounding step must be positive.");
        }

        var steps = minorUnits / StepMinor;
        var rounded = Mode switch
        {
            RoundingMode.HalfUp => Math.Round(steps, MidpointRounding.AwayFromZero),
            RoundingMode.HalfEven => Math.Round(steps, MidpointRounding.ToEven),
            RoundingMode.Up => Math.Ceiling(steps),
            RoundingMode.Down => Math.Truncate(steps),
            _ => throw new DomainException($"Unknown rounding mode {Mode}."),
        };
        return (long)rounded * StepMinor;
    }
}
