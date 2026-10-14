namespace SuperOfc.Domain.Organization;

public enum WorkDayType
{
    Full = 0,
    Half = 1,
    Off = 2,
}

/// <summary>Which weekdays are working days. Stored as seven columns on the company.</summary>
public sealed record WorkingWeek(
    WorkDayType Monday,
    WorkDayType Tuesday,
    WorkDayType Wednesday,
    WorkDayType Thursday,
    WorkDayType Friday,
    WorkDayType Saturday,
    WorkDayType Sunday)
{
    /// <summary>Friday and Saturday off (doc 10, Q3 assumption).</summary>
    public static WorkingWeek Default { get; } = new(
        WorkDayType.Full, WorkDayType.Full, WorkDayType.Full, WorkDayType.Full,
        WorkDayType.Off, WorkDayType.Off, WorkDayType.Full);

    public WorkDayType For(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => Monday,
        DayOfWeek.Tuesday => Tuesday,
        DayOfWeek.Wednesday => Wednesday,
        DayOfWeek.Thursday => Thursday,
        DayOfWeek.Friday => Friday,
        DayOfWeek.Saturday => Saturday,
        DayOfWeek.Sunday => Sunday,
        _ => WorkDayType.Full,
    };

    public WorkDayType For(DateOnly date) => For(date.DayOfWeek);

    public WorkingWeek With(DayOfWeek day, WorkDayType type) => day switch
    {
        DayOfWeek.Monday => this with { Monday = type },
        DayOfWeek.Tuesday => this with { Tuesday = type },
        DayOfWeek.Wednesday => this with { Wednesday = type },
        DayOfWeek.Thursday => this with { Thursday = type },
        DayOfWeek.Friday => this with { Friday = type },
        DayOfWeek.Saturday => this with { Saturday = type },
        DayOfWeek.Sunday => this with { Sunday = type },
        _ => this,
    };
}
