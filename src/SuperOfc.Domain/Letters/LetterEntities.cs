using System.Text;
using System.Text.RegularExpressions;
using SuperOfc.Domain.Common;

namespace SuperOfc.Domain.Letters;

public enum LetterKind
{
    Appointment = 0,
    Confirmation = 1,
    Increment = 2,
    Promotion = 3,
    Transfer = 4,
    SalaryCertificate = 5,
    ExperienceCertificate = 6,
    Relieving = 7,
    Warning = 8,
    Other = 9,
}

/// <summary>
/// An HR letter written once with fields such as <c>{Employee.Name}</c> (doc 03 R8). Issuing it fills the fields from
/// the employee's record, so letters are never retyped.
/// </summary>
public sealed class LetterTemplate : CompanyEntity, ISoftDeletable
{
    public string Name { get; set; } = string.Empty;

    public LetterKind Kind { get; set; }

    /// <summary>The subject line; may contain fields.</summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>The letter text; may contain fields. A blank line starts a new paragraph; **text** is bold.</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>Reference numbers look like HR/2026/0001.</summary>
    public string NumberPrefix { get; set; } = "HR";

    public bool IsActive { get; set; } = true;

    public int SortOrder { get; set; }

    public DateTime? DeletedAtUtc { get; set; }
}

/// <summary>A letter as it was issued. The text is kept, so a reprint is identical even after the employee record changes.</summary>
public sealed class IssuedLetter : CompanyEntity
{
    public Guid EmployeeId { get; set; }

    public Guid? TemplateId { get; set; }

    public string TemplateName { get; set; } = string.Empty;

    public LetterKind Kind { get; set; }

    public string Number { get; set; } = string.Empty;

    public DateOnly IssuedOn { get; set; }

    public string Subject { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    /// <summary>True when the text shows pay; only roles that see salaries may reprint it.</summary>
    public bool IncludesSalary { get; set; }

    public string IssuedBy { get; set; } = string.Empty;
}

/// <summary>Finds and fills <c>{Group.Field}</c> placeholders. Pure; <c>{{</c> and <c>}}</c> print literal braces.</summary>
public static partial class LetterText
{
    /// <summary>Fields typed when the letter is issued, for example <c>{Input.Reason}</c>.</summary>
    public const string InputGroup = "Input";

    public static IReadOnlyList<string> Fields(string? text)
        => text is null ? [] : [.. Placeholder().Matches(Protect(text)).Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>The names of <c>{Input.X}</c> fields, in order of first use.</summary>
    public static IReadOnlyList<string> Inputs(params string?[] texts)
        => [.. texts.SelectMany(Fields).Where(f => f.StartsWith(InputGroup + ".", StringComparison.OrdinalIgnoreCase))
            .Select(f => f[(InputGroup.Length + 1)..]).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Replaces every field with its value. Fields without a value are listed in <paramref name="missing"/> and left as they are.</summary>
    public static string Fill(string text, IReadOnlyDictionary<string, string?> values, ICollection<string> missing)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(missing);
        var source = Protect(text);
        var filled = Placeholder().Replace(source, m =>
        {
            var key = m.Groups[1].Value;
            var value = values.FirstOrDefault(v => string.Equals(v.Key, key, StringComparison.OrdinalIgnoreCase)).Value;
            if (string.IsNullOrWhiteSpace(value))
            {
                if (!missing.Contains(key))
                {
                    missing.Add(key);
                }

                return m.Value;
            }

            // A value that starts a sentence or a line is capitalized: "{Employee.HisHer} salary" gives "Her salary".
            return StartsSentence(source, m.Index) && char.IsLower(value[0]) ? char.ToUpper(value[0], System.Globalization.CultureInfo.CurrentCulture) + value[1..] : value;
        });
        return Restore(filled);
    }

    private static bool StartsSentence(string text, int index)
    {
        var i = index - 1;
        while (i >= 0 && text[i] is ' ' or '\t')
        {
            i--;
        }

        return i < 0 || text[i] is '.' or '!' or '?' or '\n' or '\r';
    }

    /// <summary>Splits text into paragraphs (blank line between) and each paragraph into plain and bold runs (<c>**bold**</c>).</summary>
    public static IReadOnlyList<IReadOnlyList<(string Text, bool Bold)>> Paragraphs(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var paragraphs = new List<IReadOnlyList<(string, bool)>>();
        foreach (var block in Regex.Split(text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim('\n'), @"\n[ \t]*\n"))
        {
            var runs = new List<(string, bool)>();
            // "a **b** c" splits into an odd number of parts with bold at odd indexes. An unclosed "**" prints as typed.
            var parts = block.Split("**");
            var balanced = parts.Length % 2 == 1;
            for (var i = 0; i < parts.Length; i++)
            {
                var unclosed = !balanced && i == parts.Length - 1 && i % 2 == 1;
                var part = unclosed ? "**" + parts[i] : parts[i];
                if (part.Length > 0)
                {
                    runs.Add((part, i % 2 == 1 && !unclosed));
                }
            }

            paragraphs.Add(runs);
        }

        return paragraphs;
    }

    /// <summary>Whole years and months between two dates, as "3 years 2 months".</summary>
    public static string Duration(DateOnly from, DateOnly to)
    {
        var months = ((to.Year - from.Year) * 12) + to.Month - from.Month - (to.Day < from.Day ? 1 : 0);
        months = Math.Max(0, months);
        var (years, rest) = (months / 12, months % 12);
        var text = new StringBuilder();
        if (years > 0)
        {
            text.Append(years).Append(years == 1 ? " year" : " years");
        }

        if (rest > 0 || years == 0)
        {
            text.Append(text.Length > 0 ? " " : string.Empty).Append(rest).Append(rest == 1 ? " month" : " months");
        }

        return text.ToString();
    }

    private const char Open = '';
    private const char Close = '';

    private static string Protect(string text) => text.Replace("{{", Open.ToString(), StringComparison.Ordinal).Replace("}}", Close.ToString(), StringComparison.Ordinal);

    private static string Restore(string text) => text.Replace(Open, '{').Replace(Close, '}');

    [GeneratedRegex(@"\{([A-Za-z]+\.[A-Za-z0-9_]+|Today)\}")]
    private static partial Regex Placeholder();
}
