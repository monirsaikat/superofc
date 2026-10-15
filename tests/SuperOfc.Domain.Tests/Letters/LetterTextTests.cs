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

    [Fact]
    public void Formats_are_passed_to_the_resolver_and_each_field_and_format_is_found_once()
    {
        var text = "{Employee.JoinDate:%d}{Employee.JoinDate:ord} {Employee.JoinDate:MMMM yyyy}, {Employee.JoinDate:%d} {Salary.Gross:n0}";
        LetterText.Placeholders(text).Should().Equal(
            new LetterPlaceholder("Employee.JoinDate", "%d"),
            new LetterPlaceholder("Employee.JoinDate", "ord"),
            new LetterPlaceholder("Employee.JoinDate", "MMMM yyyy"),
            new LetterPlaceholder("Salary.Gross", "n0"));
        LetterText.Fields(text).Should().Equal("Employee.JoinDate", "Salary.Gross");

        var seen = new List<string>();
        var filled = LetterText.Fill(text, (key, format) =>
        {
            seen.Add(key + "|" + format);
            return format switch { "%d" => "1", "ord" => "st", "MMMM yyyy" => "June 2023", "n0" => "70,000", _ => null };
        }, []);
        filled.Should().Be("1st June 2023, 1 70,000");
        seen.Should().Contain("Employee.JoinDate|ord");
    }

    [Fact]
    public void Plan_keeps_positions_so_split_text_can_be_filled_and_escapes_become_single_braces()
    {
        const string text = "Hi {{x}} {Employee.Name}. {Employee.HeShe} left {Missing.Value}";
        var missing = new List<string>();
        var plan = LetterText.Plan(text, (key, _) => key switch { "Employee.Name" => "Rina", "Employee.HeShe" => "she", _ => null }, missing);

        plan.Should().Equal(
            new LetterReplacement(3, 2, "{"),
            new LetterReplacement(6, 2, "}"),
            new LetterReplacement(9, 15, "Rina"),
            new LetterReplacement(26, 16, "She"));
        missing.Should().Equal("Missing.Value");
        LetterText.Apply(text, plan).Should().Be("Hi {x} Rina. She left {Missing.Value}");
    }

    [Theory]
    [InlineData("{Employee.Name:}")]
    [InlineData("{Employee.Name:a{b}")]
    [InlineData("{Employee:Name}")]
    public void Malformed_fields_are_plain_text(string text)
        => LetterText.Fields(text).Should().BeEmpty();
}
