namespace SuperOfc.Domain.Common;

/// <summary>Inclusive date range. <see cref="To"/> null means open-ended.</summary>
public readonly record struct DateRange
{
    public DateRange(DateOnly from, DateOnly? to)
    {
        if (to is not null && to.Value < from)
        {
            throw new DomainException("Range end must not be before its start.");
        }

        From = from;
        To = to;
    }

    public DateOnly From { get; }

    public DateOnly? To { get; }

    public bool IsOpenEnded => To is null;

    /// <summary>Number of days in the range; throws for open-ended ranges.</summary>
    public int Days => To is null
        ? throw new DomainException("Open-ended range has no day count.")
        : To.Value.DayNumber - From.DayNumber + 1;

    public static DateRange Month(int year, int month)
        => new(new DateOnly(year, month, 1), new DateOnly(year, month, DateTime.DaysInMonth(year, month)));

    public bool Contains(DateOnly date) => date >= From && (To is null || date <= To.Value);

    public bool Overlaps(DateRange other)
        => (To is null || other.From <= To.Value) && (other.To is null || From <= other.To.Value);

    /// <summary>Intersection with another range, or null when they do not overlap.</summary>
    public DateRange? Intersect(DateRange other)
    {
        if (!Overlaps(other))
        {
            return null;
        }

        var from = From > other.From ? From : other.From;
        DateOnly? to;
        if (To is null)
        {
            to = other.To;
        }
        else if (other.To is null)
        {
            to = To;
        }
        else
        {
            to = To.Value < other.To.Value ? To : other.To;
        }

        return new DateRange(from, to);
    }

    public IEnumerable<DateOnly> EachDay()
    {
        if (To is null)
        {
            throw new DomainException("Cannot enumerate an open-ended range.");
        }

        for (var d = From; d <= To.Value; d = d.AddDays(1))
        {
            yield return d;
        }
    }

    public override string ToString()
        => $"{From:yyyy-MM-dd}..{(To is null ? "open" : To.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture))}";
}
