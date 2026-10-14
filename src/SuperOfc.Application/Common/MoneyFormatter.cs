using System.Globalization;
using System.Text;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Organization;

namespace SuperOfc.Application.Common;

/// <summary>The single place where money is turned into text (UI, PDF, Excel).</summary>
public sealed class MoneyFormatter
{
    public MoneyFormatter(string symbol, NumberGrouping grouping)
    {
        Symbol = symbol;
        Grouping = grouping;
    }

    public static MoneyFormatter Default { get; } = new("$", NumberGrouping.Western);

    public string Symbol { get; }

    public NumberGrouping Grouping { get; }

    public static MoneyFormatter For(Company? company)
        => company is null ? Default : new MoneyFormatter(company.CurrencySymbol, company.Grouping);

    /// <summary>Formats with symbol, e.g. "Tk 12,34,567.50".</summary>
    public string Format(Money money, bool withSymbol = true, int decimals = 2)
    {
        var number = FormatNumber(money.Major, decimals);
        if (!withSymbol)
        {
            return number;
        }

        return money.IsNegative ? $"-{Symbol} {number.TrimStart('-')}" : $"{Symbol} {number}";
    }

    public string FormatNumber(decimal value, int decimals = 2)
    {
        var negative = value < 0;
        var abs = Math.Round(Math.Abs(value), decimals, MidpointRounding.AwayFromZero);
        var text = abs.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        var parts = text.Split('.');
        var integer = Grouping == NumberGrouping.SouthAsian ? GroupSouthAsian(parts[0]) : GroupWestern(parts[0]);
        var result = parts.Length > 1 ? $"{integer}.{parts[1]}" : integer;
        return negative && abs != 0 ? "-" + result : result;
    }

    /// <summary>Parses user input such as "12,500.50" or "Tk 1,00,000" to money.</summary>
    public static bool TryParse(string? text, out Money money)
    {
        money = Money.Zero;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var cleaned = new string(text.Where(c => char.IsDigit(c) || c is '.' or '-').ToArray());
        if (decimal.TryParse(cleaned, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
        {
            money = Money.FromMajor(value);
            return true;
        }

        return false;
    }

    private static string GroupWestern(string digits)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < digits.Length; i++)
        {
            if (i > 0 && (digits.Length - i) % 3 == 0)
            {
                sb.Append(',');
            }

            sb.Append(digits[i]);
        }

        return sb.ToString();
    }

    private static string GroupSouthAsian(string digits)
    {
        if (digits.Length <= 3)
        {
            return digits;
        }

        var last3 = digits[^3..];
        var rest = digits[..^3];
        var sb = new StringBuilder();
        for (var i = 0; i < rest.Length; i++)
        {
            if (i > 0 && (rest.Length - i) % 2 == 0)
            {
                sb.Append(',');
            }

            sb.Append(rest[i]);
        }

        return $"{sb},{last3}";
    }
}
