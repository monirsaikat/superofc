using SuperOfc.Domain.Letters;

namespace SuperOfc.Domain.Tests.Letters;

public class LetterTextTests
{
    [Fact]
    public void Fields_and_inputs_are_found_once_each()
    {
        var text = "Dear {Employee.Name}, {Employee.name} {Today} {Input.Reason} {{not a field}} {Input.Reason} {Input.Days}";
        LetterText.Fields(text).Should().BeEquivalentTo(["Employee.Name", "Today", "Input.Reason", "Input.Days"]);
        LetterText.Inputs("{Input.Purpose}", text).Should().Equal("Purpose", "Reason", "Days");
    }

    [Fact]
    public void Fill_replaces_known_fields_ignoring_case_and_reports_blanks()
    {
        var values = new Dictionary<string, string?> { ["Employee.Name"] = "Rina Das", ["Employee.ExitDate"] = "", ["Today"] = "14 Oct 2026" };
        var missing = new List<string>();
        var text = LetterText.Fill("{employee.name} left on {Employee.ExitDate}. {{Code}} {Today}", values, missing);
        text.Should().Be("Rina Das left on {Employee.ExitDate}. {Code} 14 Oct 2026");
        missing.Should().Equal("Employee.ExitDate");
    }

    [Fact]
    public void Paragraphs_split_on_blank_lines_and_bold_needs_a_closing_marker()
    {
        var paragraphs = LetterText.Paragraphs("Dear **Rina**,\r\n\r\nYou joined on **1 Jan**.\nSecond line.\n  \nUnclosed **bold");
        paragraphs.Should().HaveCount(3);
        paragraphs[0].Should().Equal(("Dear ", false), ("Rina", true), (",", false));
        paragraphs[1].Should().Equal(("You joined on ", false), ("1 Jan", true), (".\nSecond line.", false));
        paragraphs[2].Should().Equal(("Unclosed ", false), ("**bold", false));
    }

    [Theory]
    [InlineData("2020-01-15", "2023-03-15", "3 years 2 months")]
    [InlineData("2020-01-15", "2023-03-14", "3 years 1 month")]
    [InlineData("2025-01-01", "2025-01-20", "0 months")]
    [InlineData("2024-01-01", "2025-01-01", "1 year")]
    public void Duration_is_whole_years_and_months(string from, string to, string expected)
        => LetterText.Duration(DateOnly.Parse(from), DateOnly.Parse(to)).Should().Be(expected);

    [Fact]
    public void A_value_that_starts_a_sentence_is_capitalized()
    {
        var values = new Dictionary<string, string?> { ["Employee.HisHer"] = "her", ["Employee.HeShe"] = "she" };
        LetterText.Fill("{Employee.HisHer} pay. {Employee.HeShe} left; then {Employee.HeShe} joined.\n{Employee.HisHer} code", values, [])
            .Should().Be("Her pay. She left; then she joined.\nHer code");
    }
}
