using SuperOfc.Application.Common;
using SuperOfc.Application.Employees;
using SuperOfc.Application.Payroll;
using SuperOfc.Desktop.Tests.Support;
using SuperOfc.Desktop.ViewModels.Tax;
using SuperOfc.Domain.Employees;

namespace SuperOfc.Desktop.Tests;

/// <summary>Tax rules from a pack, the quick check, declarations and the employee projection through the view models (P6).</summary>
public class TaxFlowTests
{
    [Fact]
    public async Task Rules_from_the_sample_pack_declarations_and_the_employee_tab()
    {
        await using var host = await DesktopTestHost.CreateAsync(completeSetup: true);
        await host.SignInAsync();
        var dispatcher = host.Get<IDispatcher>();
        var input = new EmployeeInput("E1", "Person E1", Gender.Unspecified, null, null, null, null, null, null, new DateOnly(2024, 1, 1),
            null, null, null, null, PaymentMode.Cash, null, null, null, null, true, null);
        var employee = (await dispatcher.SendAsync(new CreateEmployeeCommand(input))).Value;
        var components = (await dispatcher.SendAsync(new GetSalaryComponentsQuery(ActiveOnly: true))).Value.ToDictionary(c => c.Code);
        (await dispatcher.SendAsync(new SaveSalaryAssignmentCommand(null, employee, new DateOnly(2024, 1, 1), null, null,
            [new SalaryLineInput(components["BASIC"].Id, 10_000_000, 0)]))).IsSuccess.Should().BeTrue();

        var tab = host.Get<EmployeeTaxTabViewModel>();
        await tab.LoadAsync(employee);
        tab.NoRulesText.Should().NotBeNullOrEmpty("there are no rules yet");
        tab.ProjectionText.Should().BeNull();

        var page = host.Get<TaxViewModel>();
        await page.OnNavigatedToAsync(null);
        page.NoRules.Should().BeTrue();
        page.SelectedPack!.Id.Should().Be("xx-sample");
        page.Profiles.Should().ContainSingle();

        var editor = host.Get<TaxRulesEditorViewModel>();
        await editor.OnNavigatedToAsync(new TaxRulesTarget(null, page.FiscalYear, page.SelectedPack));
        editor.Slabs.Should().HaveCountGreaterThan(2);
        editor.Slabs[^1].Width.Should().BeNull();
        editor.TrialResult.Should().StartWith("Yearly tax");
        editor.TrialLines.Should().NotBeEmpty();

        // A broken slab table is caught before saving.
        editor.Slabs[^1].Width = 10;
        await editor.SaveCommand.ExecuteAsync(null);
        editor.ErrorMessage.Should().Contain("last slab");
        editor.Slabs[^1].Width = null;
        await editor.SaveCommand.ExecuteAsync(null);
        editor.ErrorMessage.Should().BeNull();

        await page.RefreshAsync();
        page.HasRules.Should().BeTrue();
        page.Categories.Should().Contain("General");

        page.Profiles[0].IsExempt = true;
        page.Profiles[0].IsDirty.Should().BeTrue();
        await page.SaveProfilesCommand.ExecuteAsync(null);
        page.ErrorMessage.Should().BeNull();
        page.Profiles[0].IsDirty.Should().BeFalse();

        await tab.LoadAsync(employee);
        tab.NoRulesText.Should().BeNull();
        tab.ProjectionText.Should().Contain("exempt");

        page.Profiles[0].IsExempt = false;
        await page.SaveProfilesCommand.ExecuteAsync(null);
        await tab.LoadAsync(employee);
        tab.ProjectionText.Should().StartWith("About");
        tab.Explanation.Should().NotBeEmpty();
        tab.HasMonths.Should().BeFalse("no payroll is finalized yet");
    }
}
