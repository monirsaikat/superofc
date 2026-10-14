using Microsoft.EntityFrameworkCore;
using SuperOfc.Application.Employees;
using SuperOfc.Application.Letters;
using SuperOfc.Application.Organization;
using SuperOfc.Application.Payroll;
using SuperOfc.Application.Tests.Support;
using SuperOfc.Domain.Employees;
using SuperOfc.Domain.Letters;
using SuperOfc.Domain.Organization;
using SuperOfc.Domain.Security;

namespace SuperOfc.Application.Tests;

/// <summary>HR letters from templates (doc 03 R8). Clock: 15 March 2026. Basic 30,000 gives gross 47,500.</summary>
public class LetterTests
{
    private static readonly DateOnly Today = new(2026, 3, 15);

    private static async Task<(TestHost Host, Guid Rina, Guid Kamal)> SetUpAsync()
    {
        var host = await TestHost.CreateAsync();
        var officer = await host.SendOkAsync(new SaveMasterDataCommand(MasterDataKind.Designation, null, "Accounts Officer", "AO", null, 0));
        var accounts = await host.SendOkAsync(new SaveMasterDataCommand(MasterDataKind.Department, null, "Accounts", "ACC", null, 0));
        var rina = await host.SendOkAsync(new CreateEmployeeCommand(EmployeeTestData.Input("E1", "Rina Das", new DateOnly(2023, 1, 10)) with
        {
            Gender = Gender.Female, DesignationId = officer, DepartmentId = accounts,
        }));
        var kamal = await host.SendOkAsync(new CreateEmployeeCommand(EmployeeTestData.Input("E2", "Kamal Roy", new DateOnly(2024, 6, 1)) with { DesignationId = officer }));
        var components = await PayrollTestData.ComponentsAsync(host);
        foreach (var e in new[] { rina, kamal })
        {
            await host.SendOkAsync(new SaveSalaryAssignmentCommand(null, e, new DateOnly(2025, 1, 1), null, null, PayrollTestData.Lines(components, 30_000)));
        }

        return (host, rina, kamal);
    }

    private static async Task<Guid> TemplateAsync(TestHost host, string name)
        => (await host.SendOkAsync(new GetLetterTemplatesQuery())).Single(t => t.Name == name).Id;

    [Fact]
    public async Task Standard_letters_come_with_setup_and_can_be_restored()
    {
        await using var host = await TestHost.CreateAsync();
        var templates = await host.SendOkAsync(new GetLetterTemplatesQuery());
        templates.Select(t => t.Name).Should().Contain(["Appointment letter", "Salary certificate", "Experience certificate", "Increment letter", "Warning letter"]);
        templates.Single(t => t.Name == "Warning letter").Inputs.Should().Equal("Reason", "Details");
        templates.Single(t => t.Name == "Salary certificate").UsesSalary.Should().BeTrue();
        templates.Single(t => t.Name == "Experience certificate").UsesSalary.Should().BeFalse();

        (await host.SendOkAsync(new AddStandardLetterTemplatesCommand())).Should().Be(0);
        await host.SendOkAsync(new DeleteLetterTemplateCommand(templates[0].Id));
        (await host.SendOkAsync(new AddStandardLetterTemplatesCommand())).Should().Be(1);
    }

    [Fact]
    public async Task Templates_only_accept_known_fields()
    {
        await using var host = await TestHost.CreateAsync();
        var bad = await host.SendFailAsync(new SaveLetterTemplateCommand(null, "Bonus", LetterKind.Other, "Bonus for {Employee.Name}", "Dear {Employee.Nmae}, {Salry.Gross}", "HR", true));
        bad.Code.Should().Be("Validation.Body");
        bad.Message.Should().Contain("{Employee.Nmae}").And.Contain("{Salry.Gross}");

        var id = await host.SendOkAsync(new SaveLetterTemplateCommand(null, "Bonus", LetterKind.Other, "Bonus for {Employee.Name}",
            "Dear {Employee.Name}, your basic is {Salary.BASIC} and the bonus is {Input.Amount}.", "bon", true));
        (await host.SendOkAsync(new GetLetterTemplatesQuery())).Single(t => t.Id == id).NumberPrefix.Should().Be("BON");
        (await host.SendFailAsync(new SaveLetterTemplateCommand(null, "bonus", LetterKind.Other, "x", "y", "HR", true))).Code.Should().Be("Validation.Name");

        var fields = await host.SendOkAsync(new GetLetterFieldsQuery());
        fields.Should().Contain(f => f.Key == "Salary.BASIC" && f.Private);
        fields.Should().Contain(f => f.Key == "Employee.Name" && !f.Private);
    }

    [Fact]
    public async Task Issuing_fills_each_employee_numbers_the_letters_and_keeps_the_text()
    {
        var (host, rina, kamal) = await SetUpAsync();
        await using var _ = host;
        var certificate = await TemplateAsync(host, "Salary certificate");

        var preview = await host.SendOkAsync(new PreviewLetterQuery(rina, certificate, null, null, Today, null));
        preview.Body.Should().Contain("**Rina Das** (E1)").And.Contain("Accounts Officer").And.Contain("47,500").And.Contain("Her current monthly");
        preview.Missing.Should().Equal("Input.Purpose");

        var path = Path.Combine(host.Root, "certificates.pdf");
        var blank = await host.SendFailAsync(new IssueLettersCommand(certificate, [rina, kamal], Today, null, path));
        blank.Message.Should().Contain("E1 Rina Das: no Purpose (typed when issuing)");
        File.Exists(path).Should().BeFalse("nothing is issued while letters would have blanks");

        var inputs = new Dictionary<string, string?> { ["Purpose"] = "a bank loan" };
        var issued = await host.SendOkAsync(new IssueLettersCommand(certificate, [rina, kamal], Today, inputs, path));
        issued.Numbers.Should().Equal("HR/2026/0001", "HR/2026/0002");
        File.ReadAllBytes(path)[..5].Should().Equal("%PDF-"u8.ToArray());

        var folder = Path.Combine(host.Root, "each");
        var again = await host.SendOkAsync(new IssueLettersCommand(certificate, [rina, kamal], Today, inputs, folder, OneFilePerEmployee: true));
        again.Numbers.Should().Equal("HR/2026/0003", "HR/2026/0004");
        again.Files.Should().HaveCount(2).And.OnlyContain(f => File.Exists(f));

        var register = await host.SendOkAsync(new GetIssuedLettersQuery(EmployeeId: rina));
        register.Should().HaveCount(2);
        register.Should().OnlyContain(l => l.IncludesSalary && l.TemplateName == "Salary certificate" && l.IssuedBy.Length > 0);

        // The kept text does not change when the employee record does; a reprint is the same letter.
        await host.WithDbAsync(async db =>
        {
            var e = await db.Employees.SingleAsync(x => x.Id == rina);
            e.FullName = "Rina Das Gupta";
            return await db.SaveChangesAsync();
        });
        var kept = await host.WithDbAsync(db => db.IssuedLetters.SingleAsync(l => l.Number == "HR/2026/0001"));
        kept.Body.Should().Contain("**Rina Das** (E1)").And.Contain("for a bank loan.");
        var reprint = Path.Combine(host.Root, "reprint.pdf");
        await host.SendOkAsync(new ReprintLettersCommand([kept.Id], reprint));
        File.Exists(reprint).Should().BeTrue();
        (await host.WithDbAsync(db => db.AuditEntries.CountAsync(a => a.Action == "Letter.Issued"))).Should().Be(2);

        await host.SignInAsAsync(Role.Viewer);
        (await host.SendOkAsync(new GetIssuedLettersQuery())).Should().HaveCount(4, "viewers can see the register");
        (await host.SendFailAsync(new ReprintLettersCommand([kept.Id], reprint))).Code.Should().Be("Auth.Forbidden");
        (await host.SendFailAsync(new IssueLettersCommand(certificate, [rina], Today, inputs, path))).Code.Should().Be("Auth.Forbidden");
    }

    [Fact]
    public async Task Experience_and_increment_letters_use_exit_dates_and_salary_history()
    {
        var (host, rina, _) = await SetUpAsync();
        await using var _ = host;
        var experience = await TemplateAsync(host, "Experience certificate");
        var path = Path.Combine(host.Root, "x.pdf");

        (await host.SendFailAsync(new IssueLettersCommand(experience, [rina], Today, null, path))).Message.Should().Contain("last working day");
        await host.SendOkAsync(new RecordEmploymentEventCommand(rina, EmploymentAction.Exit, new DateOnly(2026, 3, 9), "Resigned"));
        var preview = await host.SendOkAsync(new PreviewLetterQuery(rina, experience, null, null, Today, null));
        preview.Missing.Should().BeEmpty();
        preview.Body.Should().Contain("a period of 3 years 2 months").And.Contain("she was");

        var components = await PayrollTestData.ComponentsAsync(host);
        await host.SendOkAsync(new SaveSalaryAssignmentCommand(null, rina, new DateOnly(2026, 4, 1), null, null, PayrollTestData.Lines(components, 32_000)));
        var increment = await host.SendOkAsync(new PreviewLetterQuery(rina, await TemplateAsync(host, "Increment letter"), null, null, Today, null));
        increment.Body.Should().Contain("47,500").And.Contain("50,500").And.Contain("increase of").And.Contain("3,000");

        // The editor previews unsaved text the same way.
        var draft = await host.SendOkAsync(new PreviewLetterQuery(rina, null, "To {Employee.Title} {Employee.Name}", "Gross today {Salary.Gross}; new {Salary.NewGross}", Today, null));
        draft.Subject.Should().Be("To Ms. Rina Das");
        draft.Body.Should().Contain("47,500").And.Contain("50,500");
    }
}
