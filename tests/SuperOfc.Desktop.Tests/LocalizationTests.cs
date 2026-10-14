using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SuperOfc.Desktop.Resources;

namespace SuperOfc.Desktop.Tests;

[CollectionDefinition(nameof(UiCultureCollection), DisableParallelization = true)]
public sealed class UiCultureCollection;

/// <summary>Localization pass (P8-T4): no literal UI text in XAML, translations consistent with English, fallback logged.</summary>
[Collection(nameof(UiCultureCollection))]
public partial class LocalizationTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SuperOfc.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static string ResourcesFolder => Path.Combine(RepoRoot(), "src", "SuperOfc.Desktop", "Resources");

    private static Dictionary<string, string> ReadResx(string path)
        => XDocument.Load(path).Root!.Elements("data").ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? string.Empty);

    [Fact]
    public void Xaml_has_no_literal_text_for_users()
    {
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", "SuperOfc.Desktop"), "*.xaml", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                                 && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (Match m in LiteralText().Matches(lines[i]))
                {
                    if (Letters().IsMatch(m.Groups[2].Value))
                    {
                        offenders.Add($"{Path.GetFileName(file)}:{i + 1} {m.Value}");
                    }
                }
            }
        }

        offenders.Should().BeEmpty("visible text comes from Strings.resx so it can be translated");
    }

    [Fact]
    public void Translations_only_use_known_keys_and_keep_the_placeholders()
    {
        var neutral = ReadResx(Path.Combine(ResourcesFolder, "Strings.resx"));
        var translations = Directory.EnumerateFiles(ResourcesFolder, "Strings.*.resx").ToList();
        translations.Should().NotBeEmpty("a second language scaffold ships with 1.0");
        foreach (var file in translations)
        {
            foreach (var (key, value) in ReadResx(file))
            {
                neutral.Should().ContainKey(key, $"{Path.GetFileName(file)} has a key English does not");
                Placeholders(value).Should().BeEquivalentTo(Placeholders(neutral[key]), $"{key} in {Path.GetFileName(file)} must keep the same {{0}} placeholders");
            }
        }
    }

    [Fact]
    public void Switching_language_shows_no_missing_texts_and_logs_each_fallback_once()
    {
        var neutral = ReadResx(Path.Combine(ResourcesFolder, "Strings.resx"));
        var bengali = ReadResx(Path.Combine(ResourcesFolder, "Strings.bn.resx"));
        var reported = new List<string>();
        var previous = Strings.MissingTranslation;
        Strings.MissingTranslation = (key, culture) =>
        {
            lock (reported)
            {
                reported.Add($"{culture}/{key}");
            }
        };
        Strings.Culture = CultureInfo.GetCultureInfo("bn");
        try
        {
            var properties = typeof(Strings).GetProperties(BindingFlags.Public | BindingFlags.Static).Where(p => p.PropertyType == typeof(string)).ToList();
            properties.Should().HaveCount(neutral.Count);
            foreach (var property in properties)
            {
                var text = (string)property.GetValue(null)!;
                text.Should().NotBe(property.Name, "every key has at least the English text");
            }

            Strings.Nav_Dashboard.Should().Be(bengali["Nav_Dashboard"]);
            _ = Strings.App_Save;
            reported.Should().OnlyHaveUniqueItems().And.HaveCount(neutral.Count - bengali.Count, "each English fallback is reported once");
            reported.Should().NotContain("bn/Nav_Dashboard");
        }
        finally
        {
            Strings.Culture = null;
            Strings.MissingTranslation = previous;
        }
    }

    private static List<string> Placeholders(string text) => [.. Placeholder().Matches(text).Select(m => m.Value).Distinct().Order(StringComparer.Ordinal)];

    [GeneratedRegex(@"\b(Text|Content|Header|Title|PlaceholderText|ToolTip|Message|Description)=""([^""{][^""]*)""")]
    private static partial Regex LiteralText();

    [GeneratedRegex("[A-Za-z]")]
    private static partial Regex Letters();

    [GeneratedRegex(@"\{\d+(:[^}]*)?\}")]
    private static partial Regex Placeholder();
}
