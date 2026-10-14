using SuperOfc.Domain.Common;

namespace SuperOfc.Domain.Organization;

public enum MasterDataKind
{
    Department = 0,
    Designation = 1,
    Location = 2,
    Grade = 3,
}

/// <summary>Simple named lookup owned by a company. Soft-deleted so old records keep their references.</summary>
public abstract class MasterDataEntity : CompanyEntity, ISoftDeletable
{
    public string Name { get; set; } = string.Empty;

    public string? Code { get; set; }

    public string? Description { get; set; }

    public int SortOrder { get; set; }

    public DateTime? DeletedAtUtc { get; set; }

    public abstract MasterDataKind Kind { get; }
}

public sealed class Department : MasterDataEntity
{
    public override MasterDataKind Kind => MasterDataKind.Department;
}

public sealed class Designation : MasterDataEntity
{
    public override MasterDataKind Kind => MasterDataKind.Designation;
}

public sealed class Location : MasterDataEntity
{
    public override MasterDataKind Kind => MasterDataKind.Location;
}

/// <summary>Optional salary band.</summary>
public sealed class Grade : MasterDataEntity
{
    public override MasterDataKind Kind => MasterDataKind.Grade;
}

public sealed class Holiday : CompanyEntity
{
    public DateOnly Date { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool IsPaid { get; set; } = true;

    /// <summary>Null means the holiday applies to every location.</summary>
    public Guid? LocationId { get; set; }
}

public enum DayKind
{
    Working = 0,
    HalfDay = 1,
    WeeklyOff = 2,
    Holiday = 3,
}

/// <summary>Answers "what kind of day is this" from the working week and the holiday list (pure).</summary>
public sealed class WorkCalendar
{
    private readonly WorkingWeek _week;
    private readonly ILookup<DateOnly, Holiday> _holidays;

    public WorkCalendar(WorkingWeek week, IEnumerable<Holiday> holidays)
    {
        _week = week;
        _holidays = holidays.ToLookup(h => h.Date);
    }

    public Holiday? HolidayOn(DateOnly date, Guid? locationId)
        => _holidays[date].FirstOrDefault(h => h.LocationId is null || h.LocationId == locationId);

    /// <summary>Holiday wins over weekly off: a holiday that falls on an off day is still a holiday.</summary>
    public DayKind GetDayKind(DateOnly date, Guid? locationId = null)
    {
        if (HolidayOn(date, locationId) is not null)
        {
            return DayKind.Holiday;
        }

        return _week.For(date) switch
        {
            WorkDayType.Off => DayKind.WeeklyOff,
            WorkDayType.Half => DayKind.HalfDay,
            _ => DayKind.Working,
        };
    }

    /// <summary>Working days in a range (half days count 0.5).</summary>
    public decimal WorkingDays(DateRange range, Guid? locationId = null)
        => range.EachDay().Sum(d => GetDayKind(d, locationId) switch
        {
            DayKind.Working => 1m,
            DayKind.HalfDay => 0.5m,
            _ => 0m,
        });
}
