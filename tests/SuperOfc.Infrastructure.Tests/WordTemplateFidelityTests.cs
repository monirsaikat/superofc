using SuperOfc.Application.Letters;
using SuperOfc.Infrastructure.Reporting;

namespace SuperOfc.Infrastructure.Tests;

/// <summary>
/// The converted templates in doc-files/templates, filled with the values of the original letters, must give the original letters
/// back. Opt-in (SUPEROFC_WORD_TESTS=1, needs Word): writes both PDFs of each pair to %TEMP%/superofc-fidelity/, where
/// tools/dev/letters/compare_fidelity.py compares them page by page, pixel by pixel and word by word.
/// </summary>
public class WordTemplateFidelityTests
{
    private const string Responsibilities =
        "Designing and developing websites and web applications.\n"
        + "Collaborating with teams to implement and enhance website functionalities.\n"
        + "Troubleshooting and resolving issues to ensure optimal performance.\n"
        + "Delivering exceptional user experiences for websites and web applications.";

    /// <summary>
    /// Template, original and, per "Key|format", the text the original letter has at that field. The company name and the
    /// signatory are fields too, given exactly as each original letter types them (the NOC letter's trailing space after the
    /// signatory's name stays in the template, outside the field).
    /// </summary>
    private static readonly (string Name, string Template, string Original, Dictionary<string, string> Values)[] Pairs =
    [
        ("noc", "NOC letter.docx", "NOC Letter For Job Change.docx", new()
        {
            ["Letter.Date|d MMMM yyyy"] = "11 May 2026",
            ["Employee.Name|"] = "Md Masud Alam",
            ["Employee.JoinDate|%d"] = "1",
            ["Employee.JoinDate|ord"] = "st",
            ["Employee.JoinDate|MMMM yyyy"] = "January 2020",
            ["Employee.ExitDate|%d"] = "31",
            ["Employee.ExitDate|ord"] = "st",
            ["Employee.ExitDate|MMMM yyyy"] = "December 2025",
            ["Employee.Designation|"] = "Software Engineer",
            ["Employee.HeShe|"] = "he",
            ["Employee.HisHer|"] = "his",
            ["Employee.HimHer|"] = "him",
            ["Company.Name|"] = "THESOFTKING Limited",
            ["Company.SignatoryName|"] = "MD Hasibur Rahman Hasan",
            ["Company.SignatoryTitle|"] = "Head Of Operations",
        }),
        ("employment", "Certificate of employment.docx", "TSK Certificate of Employment.docx", new()
        {
            ["Letter.Date|%d"] = "2",
            ["Letter.Date|ord"] = "nd",
            ["Letter.Date|MMMM yyyy"] = "January 2025",
            ["Employee.Title|"] = "Mr.",
            ["Employee.Name|"] = "Md Bokul Islam",
            ["Employee.JoinDate|MMMM d, yyyy"] = "June 1, 2023",
            ["Employee.HeShe|"] = "he",
            ["Employee.Designation|"] = "Software Developer",
            ["Employee.Responsibilities|"] = Responsibilities,
            ["Salary.Gross|words"] = "Thirty-Seven Thousand",
            ["Salary.Gross|n0"] = "37,000",
            ["Company.Name|"] = "THESOFTKING Limited",
            ["Company.Name|upper"] = "THESOFTKING LIMITED",
            ["Company.SignatoryName|"] = "MD Hasibur Rahman Hasan",
            ["Company.SignatoryTitle|"] = "Head Of Operations",
        }),
        ("experience", "Experience certificate.docx", "TSK EXPERIANCE CERTIFICATE.docx", new()
        {
            ["Employee.Name|"] = "Md. Jahid Hasan Badhon",
            ["Employee.SonDaughter|cap"] = "Son",
            ["Employee.FatherName|"] = "Md Alam Mia",
            ["Employee.HeShe|"] = "he",
            ["Employee.HisHer|"] = "his",
            ["Employee.HimHer|"] = "him",
            ["Employee.Designation|"] = "Support Engineer",
            ["Employee.JoinDate|%d"] = "1",
            ["Employee.JoinDate|ord"] = "st",
            ["Employee.JoinDate|MMMM yyyy"] = "June 2023",
            ["Employee.ExitDate|%d"] = "20",
            ["Employee.ExitDate|ord"] = "th",
            ["Employee.ExitDate|MMMM yyyy"] = "August 2026",
            ["Company.Name|"] = "TheSoftKing Limited",
            ["Company.SignatoryName|"] = "Md. Hasibur Rahman Hasan",
            ["Company.SignatoryTitle|"] = "Head Of Operations",
        }),
        ("salary", "Salary certificate.docx", "TSK salary certificate.docx", new()
        {
            ["Letter.Date|d MMMM yyyy"] = "11 May 2026",
            ["Employee.Name|"] = "Md Masud Alam",
            ["Employee.JoinDate|MMMM"] = "January",
            ["Employee.JoinDate|%d"] = "1",
            ["Employee.JoinDate|ord"] = "st",
            ["Employee.JoinDate|yyyy"] = "2020",
            ["Employee.HeShe|"] = "he",
            ["Employee.HisHer|"] = "his",
            ["Employee.Designation|"] = "Software Engineer",
            ["Salary.Gross|n0"] = "70,000",
            ["Salary.Gross|words"] = "Seventy Thousand",
            ["Salary.BASIC|n0"] = "42,000",
            ["Salary.HRA|n0"] = "14,000",
            ["Salary.MEDICAL|n0"] = "7,000",
            ["Salary.OTHER|n0"] = "7,000",
            ["Company.Name|"] = "THESOFTKING Limited",
            ["Company.SignatoryName|"] = "MD Hasibur Rahman Hasan",
            ["Company.SignatoryTitle|"] = "Head of Operations",
        }),
    ];

    [Fact]
    public async Task Filled_templates_print_like_the_original_letters()
    {
        if (Environment.GetEnvironmentVariable("SUPEROFC_WORD_TESTS") != "1")
        {
            return;
        }

        var output = Path.Combine(Path.GetTempPath(), "superofc-fidelity");
        Directory.CreateDirectory(output);
        foreach (var stale in Directory.GetFiles(output, "*.pdf"))
        {
            File.Delete(stale);
        }

        var samples = Path.Combine(WordLetterServiceTests.RepositoryRoot(), "doc-files");
        var service = new WordLetterService();
        var jobs = new List<WordPdfJob>();
        foreach (var (name, template, original, values) in Pairs)
        {
            var missing = new List<string>();
            var used = new HashSet<string>();
            var filled = service.Fill(
                File.ReadAllBytes(Path.Combine(samples, "templates", template)),
                (key, format) =>
                {
                    var lookup = $"{key}|{format}";
                    used.Add(lookup);
                    return values.GetValueOrDefault(lookup);
                },
                missing);

            missing.Should().BeEmpty(name);
            used.Should().BeEquivalentTo(values.Keys, name);
            var originalBytes = File.ReadAllBytes(Path.Combine(samples, original));
            service.Text(filled).Should().Be(service.Text(originalBytes), name);

            var filledPath = Path.Combine(output, $"{name}-filled.docx");
            var originalPath = Path.Combine(output, $"{name}-original.docx");
            File.WriteAllBytes(filledPath, filled);
            File.WriteAllBytes(originalPath, originalBytes);
            jobs.Add(new WordPdfJob(originalPath, Path.Combine(output, $"{name}-original.pdf")));
            jobs.Add(new WordPdfJob(filledPath, Path.Combine(output, $"{name}-filled.pdf")));
        }

        using var converter = new WordPdfConverter();
        converter.IsAvailable.Should().BeTrue();
        await converter.ConvertAsync(jobs);

        jobs.Should().OnlyContain(j => new FileInfo(j.PdfPath).Length > 0);
    }
}
