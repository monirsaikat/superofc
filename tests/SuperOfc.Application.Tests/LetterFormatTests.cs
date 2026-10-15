using System.Globalization;
using SuperOfc.Application.Common;
using SuperOfc.Application.Letters;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Letters;
using SuperOfc.Domain.Organization;

namespace SuperOfc.Application.Tests;

/// <summary>Field formats such as <c>{Employee.JoinDate:d MMMM yyyy}</c> (docs/specs/word-letter-templates.md).</summary>
public class LetterFormatTests
{
    private static readonly IReadOnlySet<string> Codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "BASIC" };

    private static LetterValues Values()
    {
        var v = new LetterValues("dd MMM yyyy", new MoneyFormatter("Tk", NumberGrouping.SouthAsian));
        v.Set("Employee.JoinDate", new DateOnly(2023, 6, 1));
        v.Set("Salary.Gross", Money.FromMajor(70_000m));
        v.Set("Salary.BASIC", Money.FromMajor(37_000m));
        v.Set("Employee.Name", "rina das");
        v.Set("Employee.SonDaughter", "son");
        return v;
    }

    [Theory]
    [InlineData(1, "st")]
    [InlineData(2, "nd")]
    [InlineData(3, "rd")]
    [InlineData(4, "th")]
    [InlineData(11, "th")]
    [InlineData(12, "th")]
    [InlineData(13, "th")]
    [InlineData(21, "st")]
    [InlineData(22, "nd")]
    [InlineData(23, "rd")]
    [InlineData(31, "st")]
    [InlineData(101, "st")]
    [InlineData(111, "th")]
    [InlineData(112, "th")]
    public void Ordinal_suffixes(int number, string suffix) => LetterFormats.OrdinalSuffix(number).Should().Be(suffix);

    [Theory]
    [InlineData(1, "st")]
    [InlineData(2, "nd")]
    [InlineData(3, "rd")]
    [InlineData(4, "th")]
    [InlineData(11, "th")]
    [InlineData(12, "th")]
    [InlineData(13, "th")]
    [InlineData(21, "st")]
    [InlineData(22, "nd")]
    [InlineData(23, "rd")]
    public void Ord_gives_the_suffix_of_the_day(int day, string suffix)
    {
        var v = Values();
        v.Set("Letter.Date", new DateOnly(2026, 3, day));
        v.Resolve("Letter.Date", "ord").Should().Be(suffix);
        v.Resolve("Letter.Date", "ORD").Should().Be(suffix, "format names are case-insensitive");
    }

    [Fact]
    public void Dates_use_the_company_format_without_a_format_and_english_patterns_with_one()
    {
        var v = Values();
        v.Resolve("Employee.JoinDate", null).Should().Be(new DateOnly(2023, 6, 1).ToString("dd MMM yyyy", CultureInfo.CurrentCulture));
        v.Resolve("Employee.JoinDate", "MMMM d, yyyy").Should().Be("June 1, 2023");
        v.Resolve("Employee.JoinDate", "d MMMM yyyy").Should().Be("1 June 2023");
        v.Resolve("Employee.JoinDate", "d").Should().Be("1", "a one-character format is a custom pattern, not the short date");
        v.Resolve("Employee.JoinDate", "%d").Should().Be("1");
        v.Resolve("Employee.JoinDate", "MMMM yyyy").Should().Be("June 2023");

        var missing = new List<string>();
        LetterText.Fill("joined on the {Employee.JoinDate:%d}{Employee.JoinDate:ord} of {Employee.JoinDate:MMMM yyyy}", v.Resolve, missing)
            .Should().Be("joined on the 1st of June 2023");
        missing.Should().BeEmpty();
    }

    [Fact]
    public void Money_formats()
    {
        var v = Values();
        v.Resolve("Salary.Gross", null).Should().Be("Tk 70,000.00");
        v.Resolve("Salary.Gross", "n0").Should().Be("70,000");
        v.Resolve("Salary.Gross", "N2").Should().Be("70,000.00");
        v.Resolve("Salary.Gross", "words").Should().Be("Seventy Thousand");
        v.Resolve("Salary.BASIC", "n0").Should().Be("37,000");
        v.Resolve("Salary.BASIC", "words").Should().Be("Thirty-Seven Thousand");

        v.Set("Salary.YearlyGross", Money.FromMajor(840_000m));
        v.Resolve("Salary.YearlyGross", "n0").Should().Be("8,40,000", "the company's grouping is kept");
        v.Resolve("Salary.YearlyGross", "words").Should().Be("Eight Lakh Forty Thousand");
    }

    [Fact]
    public void Text_formats_and_inputs()
    {
        var v = Values();
        v.Resolve("Employee.Name", "upper").Should().Be("RINA DAS");
        v.Resolve("Employee.Name", "LOWER").Should().Be("rina das");
        v.Resolve("Employee.Name", "cap").Should().Be("Rina das");
        v.Resolve("Employee.Name", "title").Should().Be("Rina Das");
        v.Resolve("Employee.SonDaughter", "cap").Should().Be("Son");
        v.Resolve("Employee.Name", null).Should().Be("rina das");
        v.Resolve("Employee.Name", "xyz").Should().Be("rina das", "an unsaved preview ignores a format it does not know");
        v.Resolve("Employee.Phone", "upper").Should().BeNull();

        v.AddInputs(new Dictionary<string, string?> { ["Reason"] = "  late arrival\nthree times " });
        v.Resolve("Input.Reason", null).Should().Be("late arrival\nthree times");
        v.Resolve("Input.Reason", "upper").Should().Be("LATE ARRIVAL\nTHREE TIMES");
        v["Letter.Number"] = "HR/2026/0001";
        v["Letter.Number"].Should().Be("HR/2026/0001");
    }

    [Theory]
    [InlineData("Employee.Name", "upper")]
    [InlineData("Employee.Name", "Title")]
    [InlineData("Employee.SonDaughter", "cap")]
    [InlineData("Employee.Responsibilities", "lower")]
    [InlineData("Employee.JoinDate", "ord")]
    [InlineData("Employee.JoinDate", "d")]
    [InlineData("Employee.JoinDate", "MMMM d, yyyy")]
    [InlineData("Today", "d MMMM yyyy")]
    [InlineData("Salary.Gross", "n0")]
    [InlineData("Salary.Increase", "words")]
    [InlineData("Salary.BASIC", "n2")]
    [InlineData("Input.Purpose", "upper")]
    [InlineData("Employee.Nmae", "whatever")]
    public void Valid_formats_and_unknown_fields_are_not_format_errors(string key, string format)
        => LetterFields.FormatError(key, format, Codes).Should().BeNull();

    [Theory]
    [InlineData("Employee.Name", "xyz")]
    [InlineData("Employee.Name", "n0")]
    [InlineData("Salary.Gross", "upper")]
    [InlineData("Salary.BASIC", "n3")]
    [InlineData("Input.Purpose", "words")]
    [InlineData("Employee.JoinDate", "'abc")]
    [InlineData("Employee.JoinDate", "%")]
    [InlineData("Employee.JoinDate", "HH:mm")]
    public void Unsuitable_formats_are_errors(string key, string format)
        => LetterFields.FormatError(key, format, Codes).Should().Be($"Unknown format \"{format}\" in {{{key}:{format}}}.");

    [Fact]
    public void Every_field_has_a_type()
    {
        LetterFields.TypeOf("Employee.FatherName", Codes).Should().Be(LetterFieldType.Text);
        LetterFields.TypeOf("Employee.Responsibilities", Codes).Should().Be(LetterFieldType.Lines);
        LetterFields.TypeOf("Employee.Address", Codes).Should().Be(LetterFieldType.Lines);
        LetterFields.TypeOf("Company.CurrencyCode", Codes).Should().Be(LetterFieldType.Text);
        LetterFields.TypeOf("Salary.NewEffectiveFrom", Codes).Should().Be(LetterFieldType.Date);
        LetterFields.TypeOf("salary.basic", Codes).Should().Be(LetterFieldType.Money);
        LetterFields.TypeOf("Input.Anything", Codes).Should().Be(LetterFieldType.Lines);
        LetterFields.TypeOf("Salary.HOUSE", Codes).Should().BeNull();
        LetterFields.All.Where(f => f.Key.StartsWith("Salary.", StringComparison.Ordinal) && !f.Key.EndsWith("InWords", StringComparison.Ordinal)
                && !f.Key.EndsWith("EffectiveFrom", StringComparison.Ordinal))
            .Should().OnlyContain(f => f.Type == LetterFieldType.Money);
        LetterFields.All.Where(f => f.Key.EndsWith("Date", StringComparison.Ordinal) || f.Key == "Today" || f.Key.EndsWith("DateOfBirth", StringComparison.Ordinal))
            .Should().OnlyContain(f => f.Type == LetterFieldType.Date);
    }
}
