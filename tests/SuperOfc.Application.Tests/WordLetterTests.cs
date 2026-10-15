using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SuperOfc.Application.Employees;
using SuperOfc.Application.Letters;
using SuperOfc.Application.Organization;
using SuperOfc.Application.Payroll;
using SuperOfc.Application.Tests.Support;
using SuperOfc.Domain.Employees;
using SuperOfc.Domain.Letters;
using SuperOfc.Domain.Organization;
using SuperOfc.Domain.Payroll;
using SuperOfc.Domain.Security;

namespace SuperOfc.Application.Tests;

/// <summary>
/// Word letter templates (docs/specs/word-letter-templates.md) with the real Open XML filling and a fake Word for PDFs.
/// The inputs are the converted company letters in doc-files/templates. Clock: 15 March 2026.
/// </summary>
public class WordLetterTests
{
    private static readonly DateOnly Today = new(2026, 3, 15);

    private static readonly string[] TemplateFiles = ["NOC letter.docx", "Certificate of employment.docx", "Experience certificate.docx", "Salary certificate.docx"];

    private static async Task<(TestHost Host, FakeWordPdfConverter Word)> CreateHostAsync()
    {
        var word = new FakeWordPdfConverter();
        var host = await TestHost.CreateAsync(configure: s => s.AddSingleton<IWordPdfConverter>(word));
        return (host, word);
    }

    /// <summary>Copies a converted template from doc-files/templates into the test folder.</summary>
    private static string Sample(TestHost host, string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SuperOfc.sln")))
        {
            dir = dir.Parent;
        }

        dir.Should().NotBeNull("the tests run inside the repository");
        var target = Path.Combine(host.Root, "upload", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(Path.Combine(dir!.FullName, "doc-files", "templates", fileName), target, overwrite: true);
        return target;
    }

    private static string Text(TestHost host, byte[] docx) => host.Services.GetRequiredService<IWordLetterService>().Text(docx);

    private static Task<Guid> AddOtherComponentAsync(TestHost host)
        => host.SendOkAsync(new SaveSalaryComponentCommand(null, "OTHER", "Other allowance", ComponentKind.Earning, ComponentCalc.Fixed, 0, 0, null, false, true, false, 10, true));

    /// <summary>Karim (male, father Abdul Karim, left on 9 March 2026) and Rina (female, no father's name); both Accounts Officers.</summary>
    private static async Task<(Guid Karim, Guid Rina)> EmployeesAsync(TestHost host)
    {
        var officer = await host.SendOkAsync(new SaveMasterDataCommand(MasterDataKind.Designation, null, "Accounts Officer", "AO", "Prepare accounts\nCheck vouchers", 0));
        var karim = await host.SendOkAsync(new CreateEmployeeCommand(EmployeeTestData.Input("E1", "Karim Uddin", new DateOnly(2023, 6, 1)) with
        {
            Gender = Gender.Male, DesignationId = officer, FatherName = "Abdul Karim",
        }));
        var rina = await host.SendOkAsync(new CreateEmployeeCommand(EmployeeTestData.Input("E2", "Rina Das", new DateOnly(2024, 3, 22)) with
        {
            Gender = Gender.Female, DesignationId = officer,
        }));
        foreach (var e in new[] { karim, rina })
        {
            await host.SendOkAsync(new RecordEmploymentEventCommand(e, EmploymentAction.Exit, new DateOnly(2026, 3, 9), "Resigned"));
        }

        return (karim, rina);
    }

    [Fact]
    public async Task Each_converted_template_uploads_once_its_fields_exist()
    {
        var (host, _) = await CreateHostAsync();
        await using var _ = host;

        var salary = Sample(host, "Salary certificate.docx");
        var unknown = await host.SendFailAsync(new ImportLetterTemplateDocumentCommand(null, salary));
        unknown.Code.Should().Be("Validation.SourceFilePath");
        unknown.Message.Should().Contain("{Salary.OTHER}").And.NotContain("Salary.BASIC");

        await AddOtherComponentAsync(host);
        var ids = new Dictionary<string, Guid>();
        foreach (var file in TemplateFiles)
        {
            ids[file] = await host.SendOkAsync(new ImportLetterTemplateDocumentCommand(null, Sample(host, file)));
        }

        var templates = await host.SendOkAsync(new GetLetterTemplatesQuery());
        var noc = templates.Single(t => t.Id == ids["NOC letter.docx"]);
        noc.Should().Match<LetterTemplateDto>(t => t.Name == "NOC letter" && t.Kind == LetterKind.Other && t.Subject == "NOC letter" && t.IsWord
            && t.DocumentFileName == "NOC letter.docx" && t.NumberPrefix == "HR" && t.IsActive);
        noc.Body.Should().Contain("{Employee.Name}");
        templates.Max(t => t.SortOrder).Should().Be(noc.SortOrder + 3, "uploads go to the end of the list in order");

        // The standard letters already use these names, so the uploads get a number.
        var experience = templates.Single(t => t.Id == ids["Experience certificate.docx"]);
        experience.Name.Should().Be("Experience certificate (2)");
        experience.Kind.Should().Be(LetterKind.ExperienceCertificate);
        templates.Single(t => t.Id == ids["Salary certificate.docx"]).Should().Match<LetterTemplateDto>(t => t.Name == "Salary certificate (2)"
            && t.Kind == LetterKind.SalaryCertificate && t.UsesSalary);
        templates.Single(t => t.Id == ids["Certificate of employment.docx"]).Inputs.Should().BeEmpty();
        templates.Where(t => t.Name == "Appointment letter").Should().OnlyContain(t => !t.IsWord && t.DocumentFileName == null);

        var stored = await host.WithDbAsync(db => db.LetterTemplates.SingleAsync(t => t.Id == ids["NOC letter.docx"]));
        stored.Document.Should().Equal(File.ReadAllBytes(Sample(host, "NOC letter.docx")));
        stored.DocumentSha256.Should().Be(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stored.Document!)));
        var audits = await host.WithDbAsync(db => db.AuditEntries.Where(a => a.Action == "Letter.TemplateFileUploaded").ToListAsync());
        audits.Should().HaveCount(4);
        audits.Should().Contain(a => a.Summary.Contains("NOC letter: NOC letter.docx (SHA-256 " + stored.DocumentSha256 + ")"));
    }

    [Fact]
    public async Task Upload_rejects_files_that_are_not_safe_docx_templates()
    {
        var (host, _) = await CreateHostAsync();
        await using var _ = host;
        var folder = Path.Combine(host.Root, "upload");
        Directory.CreateDirectory(folder);

        var docm = Path.Combine(folder, "macro.docm");
        File.Copy(Sample(host, "NOC letter.docx"), docm);
        (await host.SendFailAsync(new ImportLetterTemplateDocumentCommand(null, docm))).Code.Should().Be("Validation.SourceFilePath");

        var big = Path.Combine(folder, "big.docx");
        await using (var stream = File.Create(big))
        {
            stream.SetLength((10 * 1024 * 1024) + 1);
        }

        (await host.SendFailAsync(new ImportLetterTemplateDocumentCommand(null, big))).Message.Should().Contain("10 MB");

        var fake = Path.Combine(folder, "notes.DOCX");
        await File.WriteAllTextAsync(fake, "just text, not a Word file");
        (await host.SendFailAsync(new ImportLetterTemplateDocumentCommand(null, fake))).Code.Should().Be("Validation.SourceFilePath");

        (await host.SendFailAsync(new ImportLetterTemplateDocumentCommand(null, Path.Combine(folder, "missing.docx")))).Code.Should().Be("Validation.SourceFilePath");
        (await host.SendOkAsync(new GetLetterTemplatesQuery())).Should().OnlyContain(t => !t.IsWord);

        await host.SignInAsAsync(Role.Viewer);
        (await host.SendFailAsync(new ImportLetterTemplateDocumentCommand(null, Sample(host, "NOC letter.docx")))).Code.Should().Be("Auth.Forbidden");
    }

    [Fact]
    public async Task Experience_certificate_is_filled_in_word_kept_and_reprinted_identically()
    {
        var (host, word) = await CreateHostAsync();
        await using var _ = host;
        var (karim, rina) = await EmployeesAsync(host);
        var template = await host.SendOkAsync(new ImportLetterTemplateDocumentCommand(null, Sample(host, "Experience certificate.docx")));

        var blank = await host.SendFailAsync(new IssueLettersCommand(template, [karim, rina], Today, null, Path.Combine(host.Root, "out"), Output: LetterOutput.Word));
        blank.Code.Should().Be("Validation.EmployeeIds");
        blank.Message.Should().Contain("E2 Rina Das: no father").And.NotContain("E1");

        // A preview may have blanks and is not numbered.
        var previewPath = Path.Combine(host.Root, "preview.docx");
        await host.SendOkAsync(new PreviewLetterDocumentCommand(rina, template, Today, null, previewPath));
        Text(host, File.ReadAllBytes(previewPath)).Should().Contain("Rina Das").And.Contain("{Employee.FatherName}");

        var path = Path.Combine(host.Root, "karim.docx");
        var issued = await host.SendOkAsync(new IssueLettersCommand(template, [karim], Today, null, path, Output: LetterOutput.Word));
        issued.Numbers.Should().Equal("HR/2026/0001");
        issued.Files.Should().Equal(path);
        var bytes = File.ReadAllBytes(path);
        var text = Text(host, bytes);
        text.Should().Contain("Karim Uddin").And.Contain("Son of Abdul Karim").And.Contain("Accounts Officer").And.Contain("1").And.Contain("st").And.Contain("June 2023")
            .And.NotContain("{Employee.");
        word.Calls.Should().Be(0, "a Word file needs no conversion");

        var kept = await host.WithDbAsync(db => db.IssuedLetters.SingleAsync(l => l.Number == "HR/2026/0001"));
        kept.HasDocument.Should().BeTrue();
        kept.Subject.Should().Be("Experience certificate (2)");
        kept.Body.Should().Contain("Son of Abdul Karim");
        (await host.WithDbAsync(db => db.IssuedLetterDocuments.SingleAsync(d => d.IssuedLetterId == kept.Id))).Content.Should().Equal(bytes);
        (await host.SendOkAsync(new GetIssuedLettersQuery())).Single().HasDocument.Should().BeTrue();

        await host.WithDbAsync(async db =>
        {
            var e = await db.Employees.SingleAsync(x => x.Id == karim);
            e.FullName = "Karim Uddin Ahmed";
            return await db.SaveChangesAsync();
        });
        var reprint = Path.Combine(host.Root, "reprint.docx");
        await host.SendOkAsync(new ReprintLettersCommand([kept.Id], reprint));
        File.ReadAllBytes(reprint).Should().Equal(bytes, "a reprint is the kept file, never filled again");
        var reprintPdf = Path.Combine(host.Root, "reprint.pdf");
        await host.SendOkAsync(new ReprintLettersCommand([kept.Id], reprintPdf));
        File.ReadAllBytes(reprintPdf).Should().Equal([.. FakeWordPdfConverter.Marker, .. bytes]);
        (await host.WithDbAsync(db => db.AuditEntries.CountAsync(a => a.Action == "Letter.Reprinted" && a.EntityId == kept.Id))).Should().Be(2);

        // Word letters are reprinted one at a time, also when mixed with text letters.
        var text2 = await host.SendOkAsync(new SaveLetterTemplateCommand(null, "Plain", LetterKind.Other, "Plain", "Dear {Employee.Name}", "HR", true));
        var plain = await host.SendOkAsync(new IssueLettersCommand(text2, [karim], Today, null, Path.Combine(host.Root, "plain.pdf")));
        var plainId = (await host.WithDbAsync(db => db.IssuedLetters.SingleAsync(l => l.Number == plain.Numbers[0]))).Id;
        (await host.SendFailAsync(new ReprintLettersCommand([kept.Id, plainId], reprint))).Code.Should().Be("Validation.LetterIds");
        await host.SendOkAsync(new ReprintLettersCommand([plainId], Path.Combine(host.Root, "plain-again.pdf")));

        await host.SignInAsAsync(Role.Viewer);
        (await host.SendFailAsync(new ReprintLettersCommand([kept.Id], reprint))).Code.Should().Be("Auth.Forbidden");
        (await host.SendFailAsync(new IssueLettersCommand(template, [karim], Today, null, path, Output: LetterOutput.Word))).Code.Should().Be("Auth.Forbidden");
        (await host.SendOkAsync(new GetLetterOutputOptionsQuery())).WordInstalled.Should().BeTrue();
    }

    [Fact]
    public async Task Pdf_output_makes_one_file_per_employee_through_word()
    {
        var (host, word) = await CreateHostAsync();
        await using var _ = host;
        var (karim, rina) = await EmployeesAsync(host);
        var template = await host.SendOkAsync(new ImportLetterTemplateDocumentCommand(null, Sample(host, "NOC letter.docx")));

        var folder = Path.Combine(host.Root, "noc");
        var issued = await host.SendOkAsync(new IssueLettersCommand(template, [karim, rina], Today, null, folder));
        issued.Numbers.Should().Equal("HR/2026/0001", "HR/2026/0002");
        issued.Files.Should().Equal(Path.Combine(folder, "NOC letter E1 Karim Uddin.pdf"), Path.Combine(folder, "NOC letter E2 Rina Das.pdf"));
        word.Calls.Should().Be(1, "the batch is converted in one Word session");
        foreach (var file in issued.Files)
        {
            File.ReadAllBytes(file)[..FakeWordPdfConverter.Marker.Length].Should().Equal(FakeWordPdfConverter.Marker);
        }

        var pdfOfKarim = File.ReadAllBytes(issued.Files[0])[FakeWordPdfConverter.Marker.Length..];
        Text(host, pdfOfKarim).Should().Contain("Karim Uddin");

        // One employee and a file path: exactly that file.
        var single = Path.Combine(host.Root, "karim-noc.pdf");
        (await host.SendOkAsync(new IssueLettersCommand(template, [karim], Today, null, single))).Files.Should().Equal(single);
        File.Exists(single).Should().BeTrue();

        // A preview as PDF goes through Word too.
        var preview = Path.Combine(host.Root, "preview.pdf");
        await host.SendOkAsync(new PreviewLetterDocumentCommand(karim, template, Today, null, preview));
        File.ReadAllBytes(preview)[..FakeWordPdfConverter.Marker.Length].Should().Equal(FakeWordPdfConverter.Marker);
    }

    [Fact]
    public async Task Word_must_be_installed_for_pdf_and_text_templates_stay_pdf()
    {
        var (host, word) = await CreateHostAsync();
        await using var _ = host;
        var (karim, _) = await EmployeesAsync(host);
        var template = await host.SendOkAsync(new ImportLetterTemplateDocumentCommand(null, Sample(host, "NOC letter.docx")));

        word.IsAvailable = false;
        (await host.SendOkAsync(new GetLetterOutputOptionsQuery())).WordInstalled.Should().BeFalse();
        var path = Path.Combine(host.Root, "noc.pdf");
        var missing = await host.SendFailAsync(new IssueLettersCommand(template, [karim], Today, null, path));
        missing.Code.Should().Be("Validation.Output");
        missing.Message.Should().Contain("Microsoft Word is not installed");
        File.Exists(path).Should().BeFalse();
        (await host.WithDbAsync(db => db.IssuedLetters.CountAsync())).Should().Be(0, "nothing is numbered");

        // Word files still work without Word.
        await host.SendOkAsync(new IssueLettersCommand(template, [karim], Today, null, Path.Combine(host.Root, "noc.docx"), Output: LetterOutput.Word));

        var text = await host.SendOkAsync(new SaveLetterTemplateCommand(null, "Plain", LetterKind.Other, "Plain", "Dear {Employee.Name}", "HR", true));
        var rejected = await host.SendFailAsync(new IssueLettersCommand(text, [karim], Today, null, Path.Combine(host.Root, "plain.docx"), Output: LetterOutput.Word));
        rejected.Code.Should().Be("Validation.Output");
        rejected.Message.Should().Be("Text templates are saved as PDF.");
    }

    [Fact]
    public async Task Word_templates_can_be_edited_exported_and_turned_back_into_text()
    {
        var (host, _) = await CreateHostAsync();
        await using var _ = host;
        var source = Sample(host, "NOC letter.docx");
        var id = await host.SendOkAsync(new ImportLetterTemplateDocumentCommand(null, source));
        var before = (await host.SendOkAsync(new GetLetterTemplatesQuery())).Single(t => t.Id == id);

        // The editor sends the body back; a Word template keeps the document text whatever is sent.
        await host.SendOkAsync(new SaveLetterTemplateCommand(id, "No objection", LetterKind.Relieving, "NOC for {Employee.Name}", "edited text", "noc", false));
        var saved = (await host.SendOkAsync(new GetLetterTemplatesQuery())).Single(t => t.Id == id);
        saved.Should().Match<LetterTemplateDto>(t => t.Name == "No objection" && t.Kind == LetterKind.Relieving && t.Subject == "NOC for {Employee.Name}"
            && t.NumberPrefix == "NOC" && !t.IsActive && t.IsWord && t.Body == before.Body);

        var export = Path.Combine(host.Root, "export.docx");
        (await host.SendOkAsync(new ExportLetterTemplateDocumentCommand(id, export))).Should().Be(export);
        File.ReadAllBytes(export).Should().Equal(File.ReadAllBytes(source));

        await host.SendOkAsync(new RemoveLetterTemplateDocumentCommand(id));
        var removed = (await host.SendOkAsync(new GetLetterTemplatesQuery())).Single(t => t.Id == id);
        removed.IsWord.Should().BeFalse();
        removed.DocumentFileName.Should().BeNull();
        removed.Body.Should().Be(before.Body, "the document text stays as a text template");
        (await host.WithDbAsync(db => db.AuditEntries.CountAsync(a => a.Action == "Letter.TemplateFileRemoved" && a.EntityId == id))).Should().Be(1);
        (await host.SendFailAsync(new ExportLetterTemplateDocumentCommand(id, export))).Code.Should().Be("LetterTemplate.NotFound");
        (await host.SendFailAsync(new RemoveLetterTemplateDocumentCommand(id))).Code.Should().Be("LetterTemplate.NotFound");

        // Uploading onto an existing template makes it a Word template again.
        (await host.SendOkAsync(new ImportLetterTemplateDocumentCommand(id, source))).Should().Be(id);
        (await host.SendOkAsync(new GetLetterTemplatesQuery())).Single(t => t.Id == id).Should().Match<LetterTemplateDto>(t => t.IsWord && t.Name == "No objection");
    }

    [Fact]
    public async Task Salary_certificate_fills_component_amounts()
    {
        var (host, _) = await CreateHostAsync();
        await using var _ = host;
        await AddOtherComponentAsync(host);
        var (karim, _) = await EmployeesAsync(host);
        var components = await PayrollTestData.ComponentsAsync(host);
        await host.SendOkAsync(new SaveSalaryAssignmentCommand(null, karim, new DateOnly(2025, 1, 1), null, null,
            [.. PayrollTestData.Lines(components, 30_000), new SalaryLineInput(components["OTHER"].Id, 200_000, 0)]));
        var template = await host.SendOkAsync(new ImportLetterTemplateDocumentCommand(null, Sample(host, "Salary certificate.docx")));

        var path = Path.Combine(host.Root, "salary.docx");
        await host.SendOkAsync(new IssueLettersCommand(template, [karim], Today, null, path, Output: LetterOutput.Word));
        var text = Text(host, File.ReadAllBytes(path));
        text.Should().Contain("30,000").And.Contain("15,000").And.Contain("1,500").And.Contain("2,000").And.Contain("49,500").And.Contain("Forty-Nine Thousand Five Hundred");
        (await host.WithDbAsync(db => db.IssuedLetters.SingleAsync())).IncludesSalary.Should().BeTrue();
    }
}
