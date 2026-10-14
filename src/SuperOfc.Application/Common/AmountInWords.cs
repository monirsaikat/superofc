using System.Text;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Organization;

namespace SuperOfc.Application.Common;

/// <summary>
/// English amount in words for payslips (doc 10 Q16), e.g. "Taka Forty-Nine Thousand Five Hundred Only".
/// South Asian grouping uses lakh and crore; Western grouping uses million and billion.
/// </summary>
public static class AmountInWords
{
    private static readonly string[] Ones =
    [
        "Zero", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine", "Ten", "Eleven", "Twelve", "Thirteen", "Fourteen", "Fifteen",
        "Sixteen", "Seventeen", "Eighteen", "Nineteen",
    ];

    private static readonly string[] Tens = ["", "", "Twenty", "Thirty", "Forty", "Fifty", "Sixty", "Seventy", "Eighty", "Ninety"];

    public static string Format(Money amount, string currencyCode, NumberGrouping grouping)
    {
        var (major, minor) = Names(currencyCode);
        var abs = Math.Abs(amount.Minor);
        var whole = abs / Money.MinorPerMajor;
        var fraction = abs % Money.MinorPerMajor;
        var text = new StringBuilder();
        if (amount.IsNegative)
        {
            text.Append("Minus ");
        }

        text.Append(major).Append(' ').Append(Words(whole, grouping));
        if (fraction > 0)
        {
            text.Append(" and ").Append(Words(fraction, grouping)).Append(' ').Append(minor);
        }

        return text.Append(" Only").ToString();
    }

    public static string Words(long number, NumberGrouping grouping)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(number);
        if (number == 0)
        {
            return Ones[0];
        }

        var parts = new List<string>();
        if (grouping == NumberGrouping.SouthAsian)
        {
            // 1,23,45,67,890 = 123 crore 45 lakh 67 thousand 890; crores above 99 are spelled recursively.
            var crore = number / 10_000_000;
            number %= 10_000_000;
            if (crore > 0)
            {
                parts.Add(Words(crore, grouping) + " Crore");
            }

            Add(parts, number / 100_000, "Lakh");
            number %= 100_000;
        }
        else
        {
            Add(parts, number / 1_000_000_000_000, "Trillion");
            number %= 1_000_000_000_000;
            Add(parts, number / 1_000_000_000, "Billion");
            number %= 1_000_000_000;
            Add(parts, number / 1_000_000, "Million");
            number %= 1_000_000;
        }

        Add(parts, number / 1000, "Thousand");
        number %= 1000;
        if (number > 0)
        {
            parts.Add(UpTo999(number));
        }

        return string.Join(' ', parts);
    }

    private static void Add(List<string> parts, long count, string scale)
    {
        if (count > 0)
        {
            parts.Add(UpTo999(count) + " " + scale);
        }
    }

    private static string UpTo999(long n)
    {
        var words = new List<string>();
        if (n >= 100)
        {
            words.Add(Ones[n / 100] + " Hundred");
            n %= 100;
        }

        if (n >= 20)
        {
            words.Add(n % 10 == 0 ? Tens[n / 10] : Tens[n / 10] + "-" + Ones[n % 10]);
        }
        else if (n > 0)
        {
            words.Add(Ones[n]);
        }

        return string.Join(' ', words);
    }

    private static (string Major, string Minor) Names(string currencyCode) => currencyCode.ToUpperInvariant() switch
    {
        "BDT" => ("Taka", "Paisa"),
        "INR" or "PKR" or "NPR" or "LKR" => ("Rupees", "Paisa"),
        "USD" or "CAD" or "AUD" or "SGD" => ("Dollars", "Cents"),
        "EUR" => ("Euros", "Cents"),
        "GBP" => ("Pounds", "Pence"),
        "AED" => ("Dirhams", "Fils"),
        "SAR" => ("Riyals", "Halalas"),
        _ => (currencyCode.ToUpperInvariant(), "Cents"),
    };
}
