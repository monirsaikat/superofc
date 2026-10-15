using System.IO.Compression;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using SuperOfc.Application.Letters;
using SuperOfc.Domain.Letters;
using SuperOfc.Infrastructure.Reporting;
using WText = DocumentFormat.OpenXml.Wordprocessing.Text;

namespace SuperOfc.Infrastructure.Tests;

/// <summary>Word templates are filled in place: only the field text changes, every run keeps its formatting.</summary>
public class WordLetterServiceTests
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private const string Namespaces =
        "xmlns:w=\"" + W + "\" " +
        "xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\" " +
        "xmlns:wps=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\" " +
        "xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" " +
        "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" " +
        "xmlns:v=\"urn:schemas-microsoft-com:vml\" " +
        "xmlns:w14=\"http://schemas.microsoft.com/office/word/2010/wordml\" " +
        "mc:Ignorable=\"w14\"";

    private readonly WordLetterService _service = new();

    private static readonly Dictionary<string, string?> Values = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Employee.Name"] = "Rafa Islam",
        ["Employee.HisHer"] = "his",
        ["Letter.Number"] = "HR/2026/0007",
        ["Company.Name"] = "THESOFTKING Limited",
        ["Employee.Responsibilities"] = "lead the support team\nwrite weekly reports\n\nTrain new staff",
        ["Employee.Address"] = "House 12, Road 5\nDhaka 1207",
    };

    private static string? Resolve(string key, string? format) => (key, format) switch
    {
        ("Employee.JoinDate", "%d") => "1",
        ("Employee.JoinDate", "ord") => "st",
        ("Employee.JoinDate", "MMMM yyyy") => "June 2023",
        _ => Values.TryGetValue(key, out var value) ? value : null,
    };

    [Fact]
    public void Field_split_over_runs_takes_the_first_runs_formatting_and_drops_the_rest()
    {
        var docx = Document(Body(
            "<w:p><w:r><w:t xml:space=\"preserve\">Dear </w:t></w:r>"
            + "<w:r><w:rPr><w:b/></w:rPr><w:t>{Employee.</w:t></w:r>"
            + "<w:proofErr w:type=\"spellStart\"/><w:bookmarkStart w:id=\"0\" w:name=\"Who\"/>"
            + "<w:r><w:rPr><w:i/></w:rPr><w:t>Na</w:t></w:r><w:proofErr w:type=\"spellEnd\"/>"
            + "<w:r><w:rPr><w:u w:val=\"single\"/></w:rPr><w:t>me}, welcome</w:t></w:r><w:bookmarkEnd w:id=\"0\"/></w:p>"));

        var missing = new List<string>();
        var filled = _service.Fill(docx, Resolve, missing);

        missing.Should().BeEmpty();
        using var document = Open(filled);
        var paragraph = document.MainDocumentPart!.Document.Body!.Elements<Paragraph>().Single();
        paragraph.InnerText.Should().Be("Dear Rafa Islam, welcome");
        var runs = paragraph.Elements<Run>().ToList();
        runs.Should().HaveCount(3, "the run holding only covered characters is dropped");
        runs[1].RunProperties!.Bold.Should().NotBeNull();
        runs[1].InnerText.Should().Be("Rafa Islam");
        runs[2].RunProperties!.Underline.Should().NotBeNull();
        runs[2].InnerText.Should().Be(", welcome");
        runs.Should().NotContain(r => r.RunProperties != null && r.RunProperties.Italic != null);
        paragraph.Descendants<ProofError>().Should().BeEmpty();
        paragraph.Elements<BookmarkStart>().Should().ContainSingle();
        runs.Skip(1).SelectMany(r => r.Elements<WText>()).Should().OnlyContain(t => t.Space! == SpaceProcessingModeValues.Preserve);
    }

    [Fact]
    public void Input_bytes_are_not_changed()
    {
        var docx = Document(Body("<w:p><w:r><w:t>{Employee.Name}</w:t></w:r></w:p>"));
        var copy = docx.ToArray();

        _service.Fill(docx, Resolve, new List<string>());

        docx.Should().Equal(copy);
    }

    [Fact]
    public void Superscript_ordinal_stays_superscript()
    {
        var docx = Document(Body(
            "<w:p><w:r><w:t xml:space=\"preserve\">since </w:t></w:r><w:r><w:t>{Employee.JoinDate:%d}</w:t></w:r>"
            + "<w:r><w:rPr><w:vertAlign w:val=\"superscript\"/></w:rPr><w:t>{Employee.JoinDate:ord}</w:t></w:r>"
            + "<w:r><w:t xml:space=\"preserve\"> {Employee.JoinDate:MMMM yyyy}</w:t></w:r></w:p>"));

        using var document = Open(_service.Fill(docx, Resolve, new List<string>()));

        var paragraph = document.MainDocumentPart!.Document.Body!.Elements<Paragraph>().Single();
        paragraph.InnerText.Should().Be("since 1st June 2023");
        var superscript = paragraph.Elements<Run>().Single(r => r.RunProperties?.VerticalTextAlignment != null);
        superscript.InnerText.Should().Be("st");
        superscript.RunProperties!.VerticalTextAlignment!.Val!.Value.Should().Be(VerticalPositionValues.Superscript);
    }

    [Fact]
    public void Fields_in_header_footer_and_table_cells_are_filled_and_read_in_order()
    {
        var docx = Document(
            Body("<w:tbl><w:tr><w:tc><w:p><w:r><w:t>Name: {Employee.Name}</w:t></w:r></w:p></w:tc></w:tr></w:tbl><w:p><w:r><w:t>Body text</w:t></w:r></w:p>"),
            header: "<w:hdr " + Namespaces + "><w:p><w:r><w:t>Ref {Letter.Number}</w:t></w:r></w:p></w:hdr>",
            footer: "<w:ftr " + Namespaces + "><w:p><w:r><w:t>{Company.Name}</w:t></w:r></w:p></w:ftr>");

        _service.Text(docx).Should().Be("Ref {Letter.Number}\n\nName: {Employee.Name}\n\nBody text\n\n{Company.Name}");

        var filled = _service.Fill(docx, Resolve, new List<string>());

        _service.Text(filled).Should().Be("Ref HR/2026/0007\n\nName: Rafa Islam\n\nBody text\n\nTHESOFTKING Limited");
        using var document = Open(filled);
        document.MainDocumentPart!.Document.Body!.Descendants<TableCell>().Single().InnerText.Should().Be("Name: Rafa Islam");
    }

    [Fact]
    public void Text_box_choice_and_fallback_are_both_filled_and_read_once()
    {
        const string box = "<w:txbxContent><w:p><w:r><w:t>Note: {Employee.Name}</w:t></w:r></w:p></w:txbxContent>";
        var docx = Document(Body(
            "<w:p><w:r><w:t xml:space=\"preserve\">Before </w:t></w:r><w:r><mc:AlternateContent>"
            + "<mc:Choice Requires=\"wps\"><w:drawing><wp:inline><wp:extent cx=\"100\" cy=\"100\"/><wp:docPr w:id=\"1\" id=\"1\" name=\"Box\"/>"
            + "<a:graphic><a:graphicData uri=\"http://schemas.microsoft.com/office/word/2010/wordprocessingShape\"><wps:wsp><wps:txbx>"
            + box + "</wps:txbx><wps:bodyPr/></wps:wsp></a:graphicData></a:graphic></wp:inline></w:drawing></mc:Choice>"
            + "<mc:Fallback><w:pict><v:shape><v:textbox>" + box + "</v:textbox></v:shape></w:pict></mc:Fallback>"
            + "</mc:AlternateContent></w:r><w:r><w:t>after</w:t></w:r></w:p>"));

        _service.Text(docx).Should().Be("Before after\n\nNote: {Employee.Name}");

        var filled = _service.Fill(docx, Resolve, new List<string>());

        using var document = Open(filled);
        var xml = document.MainDocumentPart!.Document.OuterXml;
        CountOf(xml, "Note: Rafa Islam").Should().Be(2);
        xml.Should().NotContain("{Employee.Name}");
        CountOf(_service.Text(filled), "Note: Rafa Islam").Should().Be(1);
    }

    [Fact]
    public void Value_at_sentence_start_is_capitalized_across_runs()
    {
        var docx = Document(Body(
            "<w:p><w:r><w:t xml:space=\"preserve\">He joined in 2020. </w:t></w:r><w:r><w:t>{Employee.</w:t></w:r>"
            + "<w:r><w:rPr><w:b/></w:rPr><w:t xml:space=\"preserve\">HisHer} work was good; {Employee.HisHer} team agrees.</w:t></w:r></w:p>"));

        using var document = Open(_service.Fill(docx, Resolve, new List<string>()));

        document.MainDocumentPart!.Document.Body!.InnerText.Should().Be("He joined in 2020. His work was good; his team agrees.");
    }

    [Fact]
    public void Double_braces_print_literal_braces()
    {
        var docx = Document(Body("<w:p><w:r><w:t xml:space=\"preserve\">Type {{lit</w:t></w:r><w:r><w:t xml:space=\"preserve\">eral}} for {Employee.Name}</w:t></w:r></w:p>"));

        using var document = Open(_service.Fill(docx, Resolve, new List<string>()));

        document.MainDocumentPart!.Document.Body!.InnerText.Should().Be("Type {literal} for Rafa Islam");
    }

    [Fact]
    public void Field_without_value_is_reported_and_left_as_typed()
    {
        var docx = Document(Body("<w:p><w:r><w:t xml:space=\"preserve\">Father: {Employee.FatherName}, </w:t></w:r><w:r><w:t>{Employee.Name}</w:t></w:r></w:p>"));
        var missing = new List<string>();

        using var document = Open(_service.Fill(docx, Resolve, missing));

        missing.Should().Equal("Employee.FatherName");
        document.MainDocumentPart!.Document.Body!.InnerText.Should().Be("Father: {Employee.FatherName}, Rafa Islam");
    }

    [Fact]
    public void Lines_value_that_is_the_whole_paragraph_repeats_the_paragraph_with_its_bullet()
    {
        var docx = Document(Body(
            "<w:p><w:r><w:t>Key Job Responsibilities:</w:t></w:r></w:p>"
            + "<w:p w14:paraId=\"1A2B3C4D\"><w:pPr><w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"3\"/></w:numPr></w:pPr>"
            + "<w:bookmarkStart w:id=\"1\" w:name=\"Duties\"/><w:r><w:rPr><w:rFonts w:ascii=\"Bookman Old Style\"/></w:rPr><w:t>{Employee.Responsibilities}</w:t></w:r><w:bookmarkEnd w:id=\"1\"/></w:p>"
            + "<w:p><w:r><w:t>Remuneration Details:</w:t></w:r></w:p>"));

        using var document = Open(_service.Fill(docx, Resolve, new List<string>()));

        var paragraphs = document.MainDocumentPart!.Document.Body!.Elements<Paragraph>().ToList();
        paragraphs.Select(p => p.InnerText).Should().Equal(
            "Key Job Responsibilities:", "Lead the support team", "Write weekly reports", "Train new staff", "Remuneration Details:");
        foreach (var bullet in paragraphs.Skip(1).Take(3))
        {
            bullet.ParagraphProperties!.NumberingProperties!.NumberingId!.Val!.Value.Should().Be(3);
            bullet.Elements<Run>().Single().RunProperties!.RunFonts!.Ascii!.Value.Should().Be("Bookman Old Style");
        }

        document.MainDocumentPart.Document.Body.Descendants<BookmarkStart>().Should().ContainSingle("bookmark names stay unique");
        paragraphs.Count(p => p.ParagraphId?.Value == "1A2B3C4D").Should().Be(1);
    }

    [Fact]
    public void Lines_value_inside_a_paragraph_gets_line_breaks()
    {
        var docx = Document(Body("<w:p><w:r><w:t xml:space=\"preserve\">Address: {Employee.Address}.</w:t></w:r></w:p>"));

        using var document = Open(_service.Fill(docx, Resolve, new List<string>()));

        var paragraph = document.MainDocumentPart!.Document.Body!.Elements<Paragraph>().Single();
        var run = paragraph.Elements<Run>().Single();
        run.Elements<Break>().Should().ContainSingle();
        run.Elements<WText>().Select(t => t.Text).Should().Equal("Address: House 12, Road 5", "Dhaka 1207.");
    }

    [Fact]
    public void Field_never_spans_a_tab()
    {
        var docx = Document(Body("<w:p><w:r><w:t>{Employee.</w:t><w:tab/><w:t>Name}</w:t></w:r></w:p>"));

        using var document = Open(_service.Fill(docx, (_, _) => "X", new List<string>()));

        document.MainDocumentPart!.Document.Body!.InnerText.Should().Be("{Employee.Name}");
    }

    [Fact]
    public void Inspect_accepts_a_clean_file_and_returns_its_text()
    {
        var docx = Document(Body("<w:p><w:r><w:t>To Whom It May Concern</w:t></w:r></w:p><w:p><w:r><w:t>Dear {Employee.Name}</w:t></w:r></w:p>"),
            configure: main => main.AddHyperlinkRelationship(new Uri("https://example.com/"), true));

        var check = _service.Inspect(docx);

        check.IsValid.Should().BeTrue(check.Error);
        check.Text.Should().Be("To Whom It May Concern\n\nDear {Employee.Name}");
    }

    [Fact]
    public void Inspect_rejects_a_macro_enabled_document()
    {
        var docx = Document(Body("<w:p/>"), type: WordprocessingDocumentType.MacroEnabledDocument);

        _service.Inspect(docx).Error.Should().Be(WordLetterService.MacroMessage);
    }

    [Fact]
    public void Inspect_rejects_an_external_relationship()
    {
        var docx = Document(Body("<w:p/>"),
            configure: main => main.AddExternalRelationship("http://schemas.openxmlformats.org/officeDocument/2006/relationships/image", new Uri("file:///C:/Pictures/logo.png")));

        var check = _service.Inspect(docx);

        check.IsValid.Should().BeFalse();
        check.Error.Should().Contain("file:///C:/Pictures/logo.png");
    }

    [Fact]
    public void Inspect_rejects_a_link_to_a_local_file()
    {
        var docx = Document(Body("<w:p/>"), configure: main => main.AddHyperlinkRelationship(new Uri("file:///C:/secret.docx"), true));

        _service.Inspect(docx).Error.Should().Contain("file:///C:/secret.docx");
    }

    [Fact]
    public void Inspect_rejects_an_INCLUDETEXT_field_split_over_runs()
    {
        var docx = Document(Body(
            "<w:p><w:r><w:fldChar w:fldCharType=\"begin\"/></w:r><w:r><w:instrText xml:space=\"preserve\"> INCLUDE</w:instrText></w:r>"
            + "<w:r><w:instrText xml:space=\"preserve\">TEXT \"C:\\\\data.docx\" </w:instrText></w:r><w:r><w:fldChar w:fldCharType=\"separate\"/></w:r>"
            + "<w:r><w:t>data</w:t></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r></w:p>"));

        var check = _service.Inspect(docx);

        check.IsValid.Should().BeFalse();
        check.Error.Should().Contain("INCLUDETEXT");
    }

    [Fact]
    public void Inspect_rejects_a_simple_DDE_field_and_update_fields_on_open()
    {
        _service.Inspect(Document(Body("<w:p><w:fldSimple w:instr=\" DDEAUTO Excel x \"><w:r><w:t>1</w:t></w:r></w:fldSimple></w:p>")))
            .Error.Should().Contain("DDEAUTO");

        var update = Document(Body("<w:p/>"), configure: main =>
        {
            var settings = main.AddNewPart<DocumentSettingsPart>();
            settings.Settings = new Settings(new UpdateFieldsOnOpen { Val = true });
        });
        _service.Inspect(update).Error.Should().Contain("updateFields");
    }

    [Fact]
    public void Inspect_rejects_encrypted_and_non_Word_files()
    {
        byte[] compound = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0];
        _service.Inspect(compound).Error.Should().Be(WordLetterService.NotWordMessage);
        _service.Inspect("hello world"u8.ToArray()).Error.Should().Be(WordLetterService.NotWordMessage);
        _service.Inspect([]).Error.Should().Be(WordLetterService.NotWordMessage);

        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(archive.CreateEntry("readme.txt").Open());
            writer.Write("not a document");
        }

        _service.Inspect(zip.ToArray()).Error.Should().Be(WordLetterService.NotWordMessage);
    }

    [Theory]
    [InlineData("NOC Letter For Job Change.docx", "To Whom It May Concern")]
    [InlineData("TSK Certificate of Employment.docx", "To Whom It May Concern")]
    [InlineData("TSK EXPERIANCE CERTIFICATE.docx", "To Whom It May Concern")]
    [InlineData("TSK salary certificate.docx", "Salary Certificate")]
    [InlineData("TSK Employee_Payslip_Template.docx", "Net Pay")]
    public void Real_samples_pass_inspection_and_fill_without_changes_to_their_text(string file, string phrase)
    {
        var docx = File.ReadAllBytes(Path.Combine(RepositoryRoot(), "doc-files", file));

        var check = _service.Inspect(docx.ToArray());
        var filled = _service.Fill(docx.ToArray(), (_, _) => null, new List<string>());

        check.IsValid.Should().BeTrue(check.Error);
        check.Text.Should().Contain(phrase);
        _service.Text(filled).Should().Be(check.Text);
        _service.Inspect(filled).IsValid.Should().BeTrue();
        using var document = Open(filled);
        document.MainDocumentPart!.Document.Descendants<ProofError>().Should().BeEmpty();
        document.MainDocumentPart.DocumentSettingsPart?.Settings.Descendants<ProofState>().Should().BeEmpty();
    }

    [Fact]
    public void Salary_certificate_text_box_note_is_read_once()
    {
        var docx = File.ReadAllBytes(Path.Combine(RepositoryRoot(), "doc-files", "TSK salary certificate.docx"));

        CountOf(_service.Text(docx), "Note: THESOFTKING Limited shall not be held responsible").Should().Be(1);
    }

    internal static string RepositoryRoot()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "SuperOfc.sln")))
        {
            folder = folder.Parent;
        }

        return folder?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    internal static string Body(string content) => "<w:document " + Namespaces + "><w:body>" + content + "</w:body></w:document>";

    internal static byte[] Document(
        string documentXml,
        string? header = null,
        string? footer = null,
        WordprocessingDocumentType type = WordprocessingDocumentType.Document,
        Action<MainDocumentPart>? configure = null)
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, type))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new DocumentFormat.OpenXml.Wordprocessing.Document(documentXml);
            if (header is not null)
            {
                main.AddNewPart<HeaderPart>().Header = new Header(header);
            }

            if (footer is not null)
            {
                main.AddNewPart<FooterPart>().Footer = new Footer(footer);
            }

            configure?.Invoke(main);
        }

        return stream.ToArray();
    }

    private static WordprocessingDocument Open(byte[] docx) => WordprocessingDocument.Open(new MemoryStream(docx), false);

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}

/// <summary>Word itself is only started when SUPEROFC_WORD_TESTS=1, so the suite runs on PCs and CI without Office.</summary>
public class WordPdfConverterTests
{
    [Fact]
    public void IsAvailable_does_not_throw()
    {
        using var converter = new WordPdfConverter();

        var act = () => converter.IsAvailable;

        act.Should().NotThrow();
    }

    [Fact]
    public async Task Empty_batch_does_nothing()
    {
        using var converter = new WordPdfConverter();
        if (!converter.IsAvailable)
        {
            return;
        }

        await converter.ConvertAsync([]);
    }

    [Fact]
    public async Task Filled_sample_converts_to_pdf_with_Word()
    {
        if (Environment.GetEnvironmentVariable("SUPEROFC_WORD_TESTS") != "1")
        {
            return;
        }

        var folder = Path.Combine(Path.GetTempPath(), $"superofc-word-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var service = new WordLetterService();
            var template = File.ReadAllBytes(Path.Combine(WordLetterServiceTests.RepositoryRoot(), "doc-files", "TSK Certificate of Employment.docx"));
            var jobs = new List<WordPdfJob>();
            for (var i = 0; i < 2; i++)
            {
                var docx = Path.Combine(folder, $"letter{i}.docx");
                File.WriteAllBytes(docx, service.Fill(template, (_, _) => null, new List<string>()));
                jobs.Add(new WordPdfJob(docx, Path.Combine(folder, "out", $"letter{i}.pdf")));
            }

            // Word must also accept what filling writes: cloned paragraphs, line breaks, split runs.
            var built = WordLetterServiceTests.Document(WordLetterServiceTests.Body(
                "<w:p><w:r><w:rPr><w:b/></w:rPr><w:t>{Employee.</w:t></w:r><w:proofErr w:type=\"spellStart\"/><w:r><w:t>Name}</w:t></w:r></w:p>"
                + "<w:p w14:paraId=\"1A2B3C4D\"><w:r><w:t>{Employee.Responsibilities}</w:t></w:r></w:p>"
                + "<w:p><w:r><w:t xml:space=\"preserve\">Address: {Employee.Address}</w:t></w:r></w:p>"));
            var values = new Dictionary<string, string?>
            {
                ["Employee.Name"] = "Rafa Islam",
                ["Employee.Responsibilities"] = "One\nTwo",
                ["Employee.Address"] = "Road 5\nDhaka",
            };
            var builtPath = Path.Combine(folder, "built.docx");
            File.WriteAllBytes(builtPath, service.Fill(built, (key, _) => values.GetValueOrDefault(key), new List<string>()));
            jobs.Add(new WordPdfJob(builtPath, Path.Combine(folder, "out", "built.pdf")));

            using var converter = new WordPdfConverter();
            converter.IsAvailable.Should().BeTrue();
            await converter.ConvertAsync(jobs);

            foreach (var job in jobs)
            {
                var head = new byte[4];
                await using (var pdf = File.OpenRead(job.PdfPath))
                {
                    await pdf.ReadExactlyAsync(head);
                }

                head.Should().Equal("%PDF"u8.ToArray());
            }

            var missing = Path.Combine(folder, "missing.docx");
            var failing = () => converter.ConvertAsync([new WordPdfJob(missing, Path.Combine(folder, "missing.pdf"))]);
            await failing.Should().ThrowAsync<WordConversionException>().WithMessage("*missing.docx*");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
