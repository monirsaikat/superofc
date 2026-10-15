using System.Globalization;
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

    /// <summary>
    /// The company's own Word file when this is a Word template (ADR-24); null for a text template. The file is the layout:
    /// filling changes only the text of its fields. <see cref="Body"/> then holds the document's text, so field checks,
    /// inputs, salary permission and search work the same for both kinds.
    /// </summary>
    public byte[]? Document { get; set; }

    /// <summary>The name of the uploaded Word file, shown in settings.</summary>
    public string? DocumentFileName { get; set; }

    /// <summary>SHA-256 of <see cref="Document"/> (hex), recorded in the audit log so it is clear which file was used.</summary>
    public string? DocumentSha256 { get; set; }

    public bool IsWord => Document is not null;
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

    /// <summary>True when the letter came from a Word template; its filled file is kept in <see cref="IssuedLetterDocument"/>.</summary>
    public bool HasDocument { get; set; }
}

/// <summary>
/// The filled Word file of a letter issued from a Word template, kept so a reprint is the same file as the original.
/// A separate table so the register and template lists never load the bytes.
/// </summary>
public sealed class IssuedLetterDocument : Entity
{
    public Guid IssuedLetterId { get; set; }

    public byte[] Content { get; set; } = [];
}

/// <summary>Turns a field key and its optional format into the text to print; null when there is no value.</summary>
public delegate string? LetterResolver(string key, string? format);

/// <summary>A field in a letter: <c>{Employee.JoinDate:d MMMM yyyy}</c> has key <c>Employee.JoinDate</c> and format <c>d MMMM yyyy</c>.</summary>
public readonly record struct LetterPlaceholder(string Key, string? Format);

/// <summary>One change to make in a text: replace <paramref name="Length"/> characters at <paramref name="Index"/> with <paramref name="Value"/>.</summary>
public readonly record struct LetterReplacement(int Index, int Length, string Value);

/// <summary>
/// Finds and fills <c>{Group.Field}</c> and <c>{Group.Field:format}</c> placeholders. Pure; <c>{{</c> and <c>}}</c> print literal braces.
/// The same plan fills plain text and Word documents, where a field can be split across several runs.
/// </summary>
public static partial class LetterText
{
    /// <summary>Fields typed when the letter is issued, for example <c>{Input.Reason}</c>.</summary>
    public const string InputGroup = "Input";

    /// <summary>Field keys in order of first use, each once (formats are ignored).</summary>
    public static IReadOnlyList<string> Fields(string? text)
        => [.. Placeholders(text).Select(p => p.Key).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Every field with its format, in order, each distinct pair once.</summary>
    public static IReadOnlyList<LetterPlaceholder> Placeholders(string? text)
        => text is null
            ? []
            : [.. Placeholder().Matches(Protect(text)).Select(ToPlaceholder)
                .DistinctBy(p => (p.Key.ToUpperInvariant(), p.Format))];

    /// <summary>The names of <c>{Input.X}</c> fields, in order of first use.</summary>
    public static IReadOnlyList<string> Inputs(params string?[] texts)
        => [.. texts.SelectMany(Fields).Where(f => f.StartsWith(InputGroup + ".", StringComparison.OrdinalIgnoreCase))
            .Select(f => f[(InputGroup.Length + 1)..]).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// The replacements that fill <paramref name="text"/>, in order and not overlapping: one per field that has a value,
    /// and one per <c>{{</c> or <c>}}</c>. Fields without a value are added to <paramref name="missing"/> (once) and left as typed.
    /// A value that starts a sentence or a line is capitalized: "{Employee.HisHer} salary" gives "Her salary".
    /// </summary>
    public static IReadOnlyList<LetterReplacement> Plan(string text, LetterResolver resolve, ICollection<string> missing)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(resolve);
        ArgumentNullException.ThrowIfNull(missing);

        // Protect keeps the length, so match positions are positions in the original text.
        var source = Protect(text);
        var plan = new List<LetterReplacement>();
        var fields = Placeholder().Matches(source);
        var next = 0;
        for (var i = 0; i <= source.Length; i++)
        {
            Match? field = next < fields.Count ? fields[next] : null;
            if (field is not null && field.Index == i)
            {
                next++;
                var placeholder = ToPlaceholder(field);
                var value = resolve(placeholder.Key, placeholder.Format);
                if (string.IsNullOrWhiteSpace(value))
                {
                    if (!missing.Contains(placeholder.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        missing.Add(placeholder.Key);
                    }
                }
                else
                {
                    if (StartsSentence(source, field.Index) && char.IsLower(value[0]))
                    {
                        value = char.ToUpper(value[0], CultureInfo.CurrentCulture) + value[1..];
                    }

                    plan.Add(new LetterReplacement(field.Index, field.Length, value));
                }

                i += field.Length - 1;
                continue;
            }

            if (i < source.Length - 1 && source[i] == Escaped && source[i + 1] == Escaped)
            {
                plan.Add(new LetterReplacement(i, 2, text[i] == '{' ? "{" : "}"));
                i++;
            }
        }

        return plan;
    }

    /// <summary>Applies a plan made by <see cref="Plan"/> to the same text.</summary>
    public static string Apply(string text, IReadOnlyList<LetterReplacement> plan)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(plan);
        var result = new StringBuilder(text.Length);
        var at = 0;
        foreach (var r in plan)
        {
            result.Append(text, at, r.Index - at).Append(r.Value);
            at = r.Index + r.Length;
        }

        return result.Append(text, at, text.Length - at).ToString();
    }

    /// <summary>Replaces every field with its value; fields without a value are listed in <paramref name="missing"/> and left as they are.</summary>
    public static string Fill(string text, LetterResolver resolve, ICollection<string> missing)
        => Apply(text, Plan(text, resolve, missing));

    /// <summary>Fills from ready-made values; formats are ignored.</summary>
    public static string Fill(string text, IReadOnlyDictionary<string, string?> values, ICollection<string> missing)
    {
        ArgumentNullException.ThrowIfNull(values);
        return Fill(text, (key, _) => values.FirstOrDefault(v => string.Equals(v.Key, key, StringComparison.OrdinalIgnoreCase)).Value, missing);
    }

    private static LetterPlaceholder ToPlaceholder(Match m)
        => new(m.Groups[1].Value, m.Groups[2].Success ? m.Groups[2].Value : null);

    private static bool StartsSentence(string text, int index)
    {
        var i = index - 1;
        while (i >= 0 && text[i] is ' ' or '\t' or '\u00A0')
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

    // "{{" and "}}" become two private-use characters each, so lengths and positions do not change.
    private const char Escaped = '\uE000';

    private static string Protect(string text) => text.Replace("{{", "\uE000\uE000", StringComparison.Ordinal).Replace("}}", "\uE000\uE000", StringComparison.Ordinal);

    /// <summary><c>{Group.Field}</c>, <c>{Today}</c>, each with an optional <c>:format</c> of up to 40 characters.</summary>
    [GeneratedRegex(@"\{([A-Za-z]+\.[A-Za-z0-9_]+|Today)(?::([^{}\r\n\uE000]{1,40}))?\}")]
    private static partial Regex Placeholder();
}
