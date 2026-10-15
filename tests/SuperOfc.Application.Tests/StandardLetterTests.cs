using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Employees;
using SuperOfc.Application.Letters;
using SuperOfc.Application.Organization;
using SuperOfc.Application.Payroll;
using SuperOfc.Application.Tests.Support;
using SuperOfc.Domain.Employees;
using SuperOfc.Domain.Letters;
using SuperOfc.Domain.Organization;
using SuperOfc.Infrastructure.Persistence;

namespace SuperOfc.Application.Tests;

/// <summary>
/// The standard letters (ADR-24): four of them are the company's Word letters embedded from doc-files/templates, and
/// companies set up before them are upgraded at startup. Clock: 15 March 2026.
/// </summary>
public class StandardLetterTests
{
    private static readonly DateOnly Today = new(2026, 3, 15);

    private static readonly string[] WordNames = ["Salary certificate", "Experience certificate", "NOC letter", "Certificate of employment"];

    private static async Task<(TestHost Host, FakeWordPdfConverter Word)> CreateHostAsync()
    {
        var word = new FakeWordPdfConverter();
        var host = await TestHost.CreateAsync(configure: s => s.AddSingleton<IWordPdfConverter>(word));
        return (host, word);
    }

    private static IWordLetterService Word(TestHost host) => host.Services.GetRequiredService<IWordLetterService>();

    private static Task<List<LetterTemplate>> TemplatesInDbAsync(TestHost host)
        => host.WithDbAsync(db => db.LetterTemplates.IgnoreQueryFilters().AsNoTracking().Where(t => t.CompanyId == host.CompanyId).ToListAsync());

    /// <summary>Runs the startup sequence again, as when the app starts: migrations (none pending) and every data seeder.</summary>
    private static async Task RestartAsync(TestHost host)
    {
        var init = await host.Services.GetRequiredService<DatabaseInitializer>().InitializeAsync();
        init.State.Should().Be(DatabaseState.Ready, init.Message);
    }

    /// <summary>
    /// Makes the company look like one set up before the Word letters: the four Word letters are gone without a trace and the
    /// salary and experience certificates are the old text ones (saved through the editor, so stored as any office has them).
    /// </summary>
    private static async Task<(Guid Salary, Guid Experience)> MakeTextEraCompanyAsync(TestHost host, string? salaryBody = null)
    {
        await host.WithDbAsync(async db =>
        {
            db.LetterTemplates.RemoveRange(await db.LetterTemplates.IgnoreQueryFilters().Where(t => t.Document != null).ToListAsync());
            return await db.SaveChangesAsync();
        });
        var salary = await host.SendOkAsync(new SaveLetterTemplateCommand(null, "Salary certificate", LetterKind.SalaryCertificate, "Salary certificate",
            salaryBody ?? LetterTestData.LegacySalaryBody, "HR", true));
        var experience = await host.SendOkAsync(new SaveLetterTemplateCommand(null, "Experience certificate", LetterKind.ExperienceCertificate,
            "Experience certificate", LetterTestData.LegacyExperienceBody, "HR", true));
        return (salary, experience);
    }

    [Fact]
    public async Task Setup_adds_nine_standard_letters_four_of_them_the_word_letters()
    {
        var (host, _) = await CreateHostAsync();
        await using var _ = host;
        var templates = await host.SendOkAsync(new GetLetterTemplatesQuery());

        templates.Select(t => t.Name).Should().Equal("Appointment letter", "Confirmation letter", "Increment letter", "Salary certificate",
            "Experience certificate", "NOC letter", "Certificate of employment", "Relieving letter", "Warning letter");
        templates.Where(t => t.IsWord).Select(t => t.Name).Should().BeEquivalentTo(WordNames);
        foreach (var standard in LetterDefaults.WordLetters)
        {
            var t = templates.Single(x => x.Name == standard.Name);
            t.Should().Match<LetterTemplateDto>(x => x.Kind == standard.Kind && x.Subject == standard.Subject && x.DocumentFileName == standard.FileName
                && x.NumberPrefix == "HR" && x.IsActive);
            t.Body.Should().Be(Word(host).Text(standard.Document()), "the body is the document text");
            t.Body.Should().Contain("{Company.SignatoryName}").And.Contain("{Company.SignatoryTitle}").And.Contain("{Company.Name");
        }

        templates.Single(t => t.Name == "Salary certificate").UsesSalary.Should().BeTrue();
        templates.Single(t => t.Name == "Experience certificate").UsesSalary.Should().BeFalse();
        var stored = await TemplatesInDbAsync(host);
        stored.Should().OnlyContain(t => !t.IsWord || t.DocumentSha256 == Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(t.Document!)));

        // Starting again changes nothing.
        await RestartAsync(host);
        (await host.SendOkAsync(new GetLetterTemplatesQuery())).Should().BeEquivalentTo(templates);
    }

    [Fact]
    public async Task Upgrade_turns_untouched_text_certificates_into_word_letters_in_place_and_adds_the_new_ones()
    {
        var (host, _) = await CreateHostAsync();
        await using var _ = host;
        var (salaryId, experienceId) = await MakeTextEraCompanyAsync(host);
        var existing = await TemplatesInDbAsync(host);
        existing.Should().HaveCount(7).And.OnlyContain(t => !t.IsWord);
        var word = Word(host);

        var added = LetterDefaults.Upgrade(existing, host.CompanyId, Guid.CreateVersion7, word);

        var salary = existing.Single(t => t.Id == salaryId);
        salary.Should().Match<LetterTemplate>(t => t.IsWord && t.DocumentFileName == "Salary certificate.docx" && t.Name == "Salary certificate"
            && t.Kind == LetterKind.SalaryCertificate && t.DocumentSha256 != null);
        salary.Body.Should().Be(word.Text(salary.Document!)).And.Contain("{Salary.OTHER:n0}");
        existing.Single(t => t.Id == experienceId).Should().Match<LetterTemplate>(t => t.IsWord && t.DocumentFileName == "Experience certificate.docx"
            && t.Name == "Experience certificate");
        added.Select(t => t.Name).Should().Equal("NOC letter", "Certificate of employment");
        added.Should().OnlyContain(t => t.IsWord && t.CompanyId == host.CompanyId && t.Id != Guid.Empty);
        added.Select(t => t.DocumentFileName).Should().Equal("NOC letter.docx", "Certificate of employment.docx");
        added.Select(t => t.SortOrder).Should().OnlyContain(o => o > existing.Max(t => t.SortOrder), "added letters go to the end");

        // Running it again finds everything in place.
        LetterDefaults.Upgrade([.. existing, .. added], host.CompanyId, Guid.CreateVersion7, word).Should().BeEmpty();
    }

    [Fact]
    public async Task Upgrade_keeps_an_edited_text_certificate_and_adds_the_word_letter_beside_it()
    {
        var (host, _) = await CreateHostAsync();
        await using var _ = host;
        var edited = LetterTestData.LegacySalaryBody.Replace("To whom it may concern", "To the bank manager", StringComparison.Ordinal);
        var (salaryId, experienceId) = await MakeTextEraCompanyAsync(host, edited);
        var existing = await TemplatesInDbAsync(host);
        var word = Word(host);

        var added = LetterDefaults.Upgrade(existing, host.CompanyId, Guid.CreateVersion7, word);

        existing.Single(t => t.Id == salaryId).Should().Match<LetterTemplate>(t => !t.IsWord && t.DocumentFileName == null && t.Name == "Salary certificate"
            && t.Body == edited, "the company's own wording is kept");
        existing.Single(t => t.Id == experienceId).IsWord.Should().BeTrue("the untouched one is still converted");
        added.Select(t => t.Name).Should().BeEquivalentTo("Salary certificate (Word)", "NOC letter", "Certificate of employment");
        added.Single(t => t.Name == "Salary certificate (Word)").Should().Match<LetterTemplate>(t => t.IsWord && t.DocumentFileName == "Salary certificate.docx"
            && t.Kind == LetterKind.SalaryCertificate);

        LetterDefaults.Upgrade([.. existing, .. added], host.CompanyId, Guid.CreateVersion7, word).Should().BeEmpty("running it twice adds nothing");
    }

    [Fact]
    public async Task Upgrade_converts_untouched_text_certificates_even_when_the_word_file_was_uploaded_by_hand()
    {
        var (host, _) = await CreateHostAsync();
        await using var _ = host;
        var (salaryId, experienceId) = await MakeTextEraCompanyAsync(host);
        var existing = await TemplatesInDbAsync(host);
        var word = Word(host);

        // In 1.0.0-beta.4 offices uploaded doc-files/templates themselves; the copy got "(2)" because the text letter had the name.
        var uploaded = LetterDefaults.WordLetters.Single(w => w.Name == "Experience certificate").Create(host.CompanyId, Guid.CreateVersion7(), word);
        uploaded.Name = "Experience certificate (2)";
        existing.Add(uploaded);

        var added = LetterDefaults.Upgrade(existing, host.CompanyId, Guid.CreateVersion7, word);

        existing.Single(t => t.Id == experienceId).IsWord.Should().BeTrue("the built-in text letter nobody changed is still replaced");
        existing.Single(t => t.Id == salaryId).IsWord.Should().BeTrue();
        uploaded.Name.Should().Be("Experience certificate (2)", "the office's own upload is left as it is");
        added.Select(t => t.Name).Should().Equal("NOC letter", "Certificate of employment");
        LetterDefaults.Upgrade([.. existing, .. added], host.CompanyId, Guid.CreateVersion7, word).Should().BeEmpty();
    }

    [Fact]
    public async Task Upgrade_does_not_bring_back_a_word_letter_that_was_deleted()
    {
        var (host, _) = await CreateHostAsync();
        await using var _ = host;
        var noc = (await host.SendOkAsync(new GetLetterTemplatesQuery())).Single(t => t.Name == "NOC letter").Id;
        await host.SendOkAsync(new DeleteLetterTemplateCommand(noc));
        var existing = await TemplatesInDbAsync(host);
        existing.Single(t => t.Id == noc).DeletedAtUtc.Should().NotBeNull();

        LetterDefaults.Upgrade(existing, host.CompanyId, Guid.CreateVersion7, Word(host)).Should().BeEmpty();

        // Also not when the deleted copy had been renamed and its name is free again.
        existing.Single(t => t.Id == noc).Name = "Old NOC";
        LetterDefaults.Upgrade(existing, host.CompanyId, Guid.CreateVersion7, Word(host)).Should().BeEmpty();
    }

    [Fact]
    public async Task Startup_upgrades_existing_companies_once_and_respects_deletions()
    {
        var (host, _) = await CreateHostAsync();
        await using var _ = host;
        var (salaryId, experienceId) = await MakeTextEraCompanyAsync(host);

        await RestartAsync(host);
        var templates = await host.SendOkAsync(new GetLetterTemplatesQuery());
        templates.Should().HaveCount(9);
        templates.Where(t => t.IsWord).Select(t => t.Name).Should().BeEquivalentTo(WordNames);
        templates.Single(t => t.Id == salaryId).Should().Match<LetterTemplateDto>(t => t.IsWord && t.DocumentFileName == "Salary certificate.docx");
        templates.Single(t => t.Id == experienceId).Should().Match<LetterTemplateDto>(t => t.IsWord && t.DocumentFileName == "Experience certificate.docx");

        await RestartAsync(host);
        (await host.SendOkAsync(new GetLetterTemplatesQuery())).Should().BeEquivalentTo(templates, "the upgrade runs once");

        await host.SendOkAsync(new DeleteLetterTemplateCommand(templates.Single(t => t.Name == "Certificate of employment").Id));
        await RestartAsync(host);
        (await host.SendOkAsync(new GetLetterTemplatesQuery())).Select(t => t.Name).Should().HaveCount(8).And.NotContain("Certificate of employment");
    }

    /// <summary>Karim Uddin: male, father Abdul Karim, Accounts Officer from 1 June 2023 to 9 March 2026.</summary>
    private static async Task<Guid> KarimAsync(TestHost host)
    {
        var officer = await host.SendOkAsync(new SaveMasterDataCommand(MasterDataKind.Designation, null, "Accounts Officer", "AO", null, 0));
        var karim = await host.SendOkAsync(new CreateEmployeeCommand(EmployeeTestData.Input("E1", "Karim Uddin", new DateOnly(2023, 6, 1)) with
        {
            Gender = Gender.Male, DesignationId = officer, FatherName = "Abdul Karim",
        }));
        await host.SendOkAsync(new RecordEmploymentEventCommand(karim, EmploymentAction.Exit, new DateOnly(2026, 3, 9), "Resigned"));
        return karim;
    }

    [Fact]
    public async Task The_standard_experience_certificate_needs_the_signatory_and_prints_it()
    {
        var (host, word) = await CreateHostAsync();
        await using var _ = host;
        var karim = await KarimAsync(host);
        var template = (await host.SendOkAsync(new GetLetterTemplatesQuery())).Single(t => t.Name == "Experience certificate");
        template.IsWord.Should().BeTrue();
        var path = Path.Combine(host.Root, "experience.docx");

        var blank = await host.SendFailAsync(new IssueLettersCommand(template.Id, [karim], Today, null, path, Output: LetterOutput.Word));
        blank.Code.Should().Be("Validation.EmployeeIds");
        blank.Message.Should().Contain("E1 Karim Uddin").And.Contain("who signs letters (settings > company)").And.Contain("the signatory's title (settings > company)");
        File.Exists(path).Should().BeFalse();

        await LetterTestData.SetSignatoryAsync(host, "Md. Hasibur Rahman Hasan", "Head Of Operations");
        var issued = await host.SendOkAsync(new IssueLettersCommand(template.Id, [karim], Today, null, path, Output: LetterOutput.Word));
        issued.Files.Should().Equal(path);
        var text = Word(host).Text(File.ReadAllBytes(path));
        text.Should().Contain("Karim Uddin, Son of Abdul Karim").And.Contain("Accounts Officer").And.Contain("1st June 2023 to 9th March 2026")
            .And.Contain("Md. Hasibur Rahman Hasan").And.Contain("Head Of Operations").And.Contain("at Test Co.").And.NotContain("{");
        word.Calls.Should().Be(0);
        (await host.WithDbAsync(db => db.IssuedLetters.SingleAsync())).Should().Match<IssuedLetter>(l => l.TemplateName == "Experience certificate" && !l.IncludesSalary);
    }

    [Fact]
    public async Task The_standard_salary_certificate_prints_zero_for_components_the_salary_does_not_have()
    {
        var (host, _) = await CreateHostAsync();
        await using var _ = host;
        await LetterTestData.SetSignatoryAsync(host);
        var officer = await host.SendOkAsync(new SaveMasterDataCommand(MasterDataKind.Designation, null, "Accounts Officer", "AO", null, 0));
        var rina = await host.SendOkAsync(new CreateEmployeeCommand(EmployeeTestData.Input("E1", "Rina Das", new DateOnly(2023, 1, 10)) with
        {
            Gender = Gender.Female, DesignationId = officer,
        }));
        var components = await PayrollTestData.ComponentsAsync(host);
        components.Should().ContainKey("OTHER", "setup adds the Other allowance the certificate prints");
        await host.SendOkAsync(new SaveSalaryAssignmentCommand(null, rina, new DateOnly(2025, 1, 1), null, null, [new SalaryLineInput(components["BASIC"].Id, 3_000_000, 0)]));
        var template = (await host.SendOkAsync(new GetLetterTemplatesQuery())).Single(t => t.Name == "Salary certificate").Id;

        var path = Path.Combine(host.Root, "salary.docx");
        await host.SendOkAsync(new IssueLettersCommand(template, [rina], Today, null, path, Output: LetterOutput.Word));
        var text = Word(host).Text(File.ReadAllBytes(path));
        text.Should().Contain("Basic Salary\n\n30,000BDT").And.Contain("Housing Allowance\n\n0 BDT").And.Contain("Medical Allowance\n\n0 BDT")
            .And.Contain("Other Allowance\n\n0 BDT").And.Contain("Total\n\n30,000 BDT").And.Contain("Thirty Thousand").And.NotContain("{");
    }
}
