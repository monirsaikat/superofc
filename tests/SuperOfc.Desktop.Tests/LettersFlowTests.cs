using System.IO;
using NSubstitute;
using SuperOfc.Application.Common;
using SuperOfc.Application.Employees;
using SuperOfc.Application.Letters;
using SuperOfc.Desktop.Tests.Support;
using SuperOfc.Desktop.ViewModels.Letters;
using SuperOfc.Domain.Employees;
using SuperOfc.Domain.Letters;

namespace SuperOfc.Desktop.Tests;

/// <summary>Letter templates, issuing letters and the employee Letters tab through the view models.</summary>
public class LettersFlowTests
{
    private static async Task<Guid> CreateEmployeeAsync(DesktopTestHost host, string code, string name)
    {
        var input = new EmployeeInput(code, name, Gender.Female, null, null, null, null, null, null, new DateOnly(2023, 1, 10),
            null, null, null, null, PaymentMode.Cash, null, null, null, null, true, null);
        var result = await host.Get<IDispatcher>().SendAsync(new CreateEmployeeCommand(input));
        result.IsSuccess.Should().BeTrue();
        return result.Value;
    }

    [Fact]
    public async Task Template_editor_previews_and_inserts_fields()
    {
        await using var host = await DesktopTestHost.CreateAsync(completeSetup: true);
        await host.SignInAsync();
        await CreateEmployeeAsync(host, "E1", "Rina Das");
        var page = host.Get<LetterTemplatesSettingsViewModel>();
        page.PreviewDelay = TimeSpan.Zero;

        await page.OnNavigatedToAsync(null);
        page.Templates.Should().HaveCount(9, "setup adds the standard letters");
        page.Templates.Where(t => t.IsWord).Select(t => t.Name).Should().BeEquivalentTo(
            ["Salary certificate", "Experience certificate", "NOC letter", "Certificate of employment"], "four of them are the company's Word letters");
        page.Fields.Should().Contain(f => f.Key == "Employee.Name");

        page.FieldSearch = "words";
        page.Fields.Should().OnlyContain(f => f.Key.Contains("Words", StringComparison.OrdinalIgnoreCase) || f.Description.Contains("words", StringComparison.OrdinalIgnoreCase));

        string? inserted = null;
        page.InsertRequested += (_, token) => inserted = token;
        page.InsertCommand.Execute(page.Fields[0]);
        inserted.Should().StartWith("{").And.EndWith("}");

        page.NewCommand.Execute(null);
        page.Name = "Thanks";
        page.Subject = "Thank you, {Employee.Name}";
        page.Body = "Dear {Employee.Name},\n\nThanks. {Employee.Nickname}";
        await page.RefreshPreviewAsync();
        page.PreviewSubject.Should().Be("Thank you, Rina Das");
        page.PreviewMissing.Should().Contain("{Employee.Nickname}");

        await page.SaveCommand.ExecuteAsync(null);
        page.ErrorMessage.Should().BeNull();
        page.Templates.Should().NotContain(t => t.Name == "Thanks", "unknown fields are refused");

        page.Body = "Dear {Employee.Name},\n\nThanks.";
        await page.SaveCommand.ExecuteAsync(null);
        page.Templates.Should().Contain(t => t.Name == "Thanks");
    }

    [Fact]
    public async Task Issue_letters_then_see_them_in_the_register_and_employee_tab()
    {
        await using var host = await DesktopTestHost.CreateAsync(completeSetup: true);
        var shell = await host.SignInAsync();
        var rina = await CreateEmployeeAsync(host, "E1", "Rina Das");
        await CreateEmployeeAsync(host, "E2", "Mita Roy");
        (await host.Get<IDispatcher>().SendAsync(new SaveLetterTemplateCommand(null, "Thanks", LetterKind.Other, "Thank you",
            "Dear {Employee.Name},\n\nThank you for your work at {Company.Name}.", "TY", true))).IsSuccess.Should().BeTrue();

        var tab = host.Get<EmployeeLettersTabViewModel>();
        await tab.LoadAsync(rina);
        tab.IsEmpty.Should().BeTrue();
        await tab.NewCommand.ExecuteAsync(null);
        var page = shell.CurrentPage.Should().BeOfType<LettersViewModel>().Subject;
        page.SelectedIds.Should().Equal(rina);
        page.SelectionText.Should().Be("1 selected");

        page.Template = page.Templates.Single(t => t.Name == "Experience certificate");
        await page.RefreshPreviewAsync();
        page.PreviewBody.Should().Contain("Rina Das");

        page.SelectAllCommand.Execute(true);
        page.SelectedIds.Should().HaveCount(2);
        var pdf = Path.Combine(host.Root, "letters.pdf");
        host.Files.SaveFile(default!, default!).ReturnsForAnyArgs(pdf);
        await page.IssueCommand.ExecuteAsync(null);
        page.ErrorMessage.Should().Contain("E1 Rina Das").And.Contain("designation", "letters are never printed with blanks");
        File.Exists(pdf).Should().BeFalse();

        page.Template = page.Templates.Single(t => t.Name == "Thanks");
        await page.IssueCommand.ExecuteAsync(null);
        page.ErrorMessage.Should().BeNull();
        File.Exists(pdf).Should().BeTrue();
        page.Issued.Should().HaveCount(2);
        page.IsRegisterEmpty.Should().BeFalse();

        page.RegisterSearch = "Mita";
        await page.RefreshAsync();
        page.Issued.Should().ContainSingle().Which.EmployeeName.Should().Be("Mita Roy");

        await tab.LoadAsync(rina);
        var letter = tab.Letters.Should().ContainSingle().Subject;
        var copy = Path.Combine(host.Root, "copy.pdf");
        host.Files.SaveFile(default!, default!).ReturnsForAnyArgs(copy);
        await tab.ReprintCommand.ExecuteAsync(letter);
        File.Exists(copy).Should().BeTrue();
    }

    [Fact]
    public async Task Issue_without_people_shows_an_error()
    {
        await using var host = await DesktopTestHost.CreateAsync(completeSetup: true);
        await host.SignInAsync();
        var page = host.Get<LettersViewModel>();
        await page.OnNavigatedToAsync(null);
        await page.IssueCommand.ExecuteAsync(null);
        page.ErrorMessage.Should().NotBeNullOrEmpty();
    }
}
