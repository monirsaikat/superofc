using SuperOfc.Domain.Common;

namespace SuperOfc.Domain.Organization;

/// <summary>Digit grouping used when formatting money.</summary>
public enum NumberGrouping
{
    /// <summary>1,234,567.00</summary>
    Western = 0,

    /// <summary>12,34,567.00 (lakh / crore style)</summary>
    SouthAsian = 1,
}

public sealed class Company : Entity
{
    public string Name { get; set; } = string.Empty;

    public string? Address { get; set; }

    public string? Phone { get; set; }

    public string? Email { get; set; }

    public string? TaxIdentifier { get; set; }

    public string? LogoPath { get; set; }

    /// <summary>ISO 4217 code, e.g. BDT, USD.</summary>
    public string CurrencyCode { get; set; } = "USD";

    public string CurrencySymbol { get; set; } = "$";

    public NumberGrouping Grouping { get; set; } = NumberGrouping.Western;

    /// <summary>Windows time zone id, e.g. "Bangladesh Standard Time".</summary>
    public string TimeZoneId { get; set; } = TimeZoneInfo.Local.Id;

    /// <summary>Month (1-12) in which the fiscal year starts.</summary>
    public int FiscalYearStartMonth { get; set; } = 7;

    /// <summary>.NET date format used across the UI and documents.</summary>
    public string DateFormat { get; set; } = "dd MMM yyyy";

    public WorkingWeek WorkingWeek { get; set; } = WorkingWeek.Default;

    public bool IsSetupComplete { get; set; }

    /// <summary>First day of the fiscal year that contains <paramref name="date"/>.</summary>
    public DateOnly FiscalYearStart(DateOnly date)
    {
        var year = date.Month >= FiscalYearStartMonth ? date.Year : date.Year - 1;
        return new DateOnly(year, FiscalYearStartMonth, 1);
    }

    public DateRange FiscalYear(DateOnly date)
    {
        var start = FiscalYearStart(date);
        return new DateRange(start, start.AddYears(1).AddDays(-1));
    }
}
