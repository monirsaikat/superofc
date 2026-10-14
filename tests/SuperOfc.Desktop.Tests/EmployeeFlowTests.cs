using System.IO;
using System.Text;
using NSubstitute;
using SuperOfc.Application.Common;
using SuperOfc.Application.Employees;
using SuperOfc.Desktop.Services;
using SuperOfc.Desktop.Tests.Support;
using SuperOfc.Desktop.ViewModels;
using SuperOfc.Desktop.ViewModels.Employees;
using SuperOfc.Desktop.ViewModels.Settings;
using SuperOfc.Domain.Employees;
using SuperOfc.Domain.Organization;

namespace SuperOfc.Desktop.Tests;

public class EmployeeFlowTests
{
    [Fact]
    public async Task New_employee_gets_next_code_saves_and_appears_in_list()
    {
        await using var host = await DesktopTestHost.CreateAsync(completeSetup: true);
        await host.SignInAsync();
        var navigation = host.Get<IAppNavigator>();

        await navigation.NavigateToAsync<EmployeeDetailViewModel>();
        var detail = (EmployeeDetailViewModel)navigation.Current!;
        detail.IsNew.Should().BeTrue();
        detail.Code.Should().Be("E0001");
        detail.IsDirty.Should().BeFalse();

        detail.FullName = "Rahim Uddin";
        detail.IsDirty.Should().BeTrue();
        await detail.SaveCommand.ExecuteAsync(null);

        detail.ErrorMessage.Should().BeNull();
        detail.EmployeeId.Should().NotBeNull();
        detail.IsDirty.Should().BeFalse();
        detail.Status.Should().Be(EmployeeStatus.Probation);
        detail.History.Should().NotBeEmpty();

        await navigation.NavigateToAsync<EmployeesViewModel>();
        var list = (EmployeesViewModel)navigation.Current!;
        list.TotalCount.Should().Be(1);
        list.Employees.Single().FullName.Should().Be("Rahim Uddin");
        list.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public async Task Saving_without_name_shows_a_field_error()
    {
        await using var host = await DesktopTestHost.CreateAsync(completeSetup: true);
        await host.SignInAsync();
        var detail = host.Get<EmployeeDetailViewModel>();
        await detail.OnNavigatedToAsync(null);

        await detail.SaveCommand.ExecuteAsync(null);

        detail.EmployeeId.Should().BeNull();
        detail.GetErrors(nameof(EmployeeDetailViewModel.FullName)).Cast<string>().Should().NotBeEmpty();
    }

    [Fact]
    public async Task Confirm_moves_probation_employee_to_active()
    {
        await using var host = await DesktopTestHost.CreateAsync(completeSetup: true);
        await host.SignInAsync();
        var id = await CreateAsync(host, "E0001", "Karim");
        host.Dialogs.PickDateAsync(default!, default!, default).ReturnsForAnyArgs(DateTime.Today);
        var detail = host.Get<EmployeeDetailViewModel>();
        await detail.OnNavigatedToAsync(id);
        detail.IsOnProbation.Should().BeTrue();

        await detail.ConfirmCommand.ExecuteAsync(null);

        detail.Status.Should().Be(EmployeeStatus.Active);
        detail.ConfirmationText.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task List_filters_by_search_and_hides_people_who_left()
    {
        await using var host = await DesktopTestHost.CreateAsync(completeSetup: true);
        await host.SignInAsync();
        await CreateAsync(host, "E0001", "Alpha One");
        await CreateAsync(host, "E0002", "Beta Two");
        var leaver = await CreateAsync(host, "E0003", "Gamma Three");
        (await host.Get<IDispatcher>().SendAsync(new RecordEmploymentEventCommand(leaver, EmploymentAction.Exit, DateOnly.FromDateTime(DateTime.Today), "Resigned"))).IsSuccess.Should().BeTrue();

        var list = host.Get<EmployeesViewModel>();
        await list.OnNavigatedToAsync(null);
        list.TotalCount.Should().Be(2);

        list.IncludeLeft = true;
        await WaitUntilAsync(() => list.TotalCount == 3);

        list.Search = "beta";
        await WaitUntilAsync(() => list.TotalCount == 1);
        list.Employees.Single().Code.Should().Be("E0002");
    }

    [Fact]
    public async Task Import_previews_problems_then_imports_a_clean_file()
    {
        await using var host = await DesktopTestHost.CreateAsync(completeSetup: true);
        await host.SignInAsync();
        var path = Path.Combine(host.Root, "import.csv");
        await File.WriteAllTextAsync(path, "Code,Full name,Join date,Department\nE0101,Ann,2026-01-05,Sales\nE0102,,2026-01-06,Sales\n", Encoding.UTF8);
        host.Files.OpenFile(default!).ReturnsForAnyArgs(path);
        var import = host.Get<EmployeeImportViewModel>();

        await import.ChooseFileCommand.ExecuteAsync(null);

        import.IsPreviewStep.Should().BeTrue();
        import.ErrorCount.Should().Be(1);
        import.RunCommand.CanExecute(null).Should().BeFalse();
        import.NewLookupsText.Should().Contain("Sales");

        await File.WriteAllTextAsync(path, "Code,Full name,Join date,Department\nE0101,Ann,2026-01-05,Sales\nE0102,Bob,2026-01-06,Sales\n", Encoding.UTF8);
        await import.CheckCommand.ExecuteAsync(null);
        import.ErrorCount.Should().Be(0);
        import.RunCommand.CanExecute(null).Should().BeTrue();

        await import.RunCommand.ExecuteAsync(null);

        import.IsDoneStep.Should().BeTrue();
        var list = await host.Get<IDispatcher>().SendAsync(new GetEmployeeListQuery());
        list.Value.TotalCount.Should().Be(2);
    }

    [Fact]
    public async Task Admin_manages_departments_and_holidays_in_settings()
    {
        await using var host = await DesktopTestHost.CreateAsync(completeSetup: true);
        await host.SignInAsync();
        var navigation = host.Get<IAppNavigator>();
        await navigation.NavigateToAsync<SettingsViewModel>("Organization");
        var settings = (SettingsViewModel)navigation.Current!;
        settings.Sections.Select(s => s.Key).Should().Contain(["Organization", "Holidays", "AuditLog"]);

        var org = settings.Content.Should().BeOfType<OrganizationSettingsViewModel>().Subject;
        await WaitUntilAsync(() => true);
        org.NewCommand.Execute(null);
        org.EditName = "Finance";
        await org.SaveCommand.ExecuteAsync(null);
        org.ErrorMessage.Should().BeNull();
        org.Items.Select(i => i.Name).Should().Contain("Finance");

        var holidays = host.Get<HolidaysSettingsViewModel>();
        await holidays.OnNavigatedToAsync(null);
        holidays.NewCommand.Execute(null);
        holidays.EditDate = new DateTime(holidays.Year, 12, 16);
        holidays.EditName = "Victory Day";
        await holidays.SaveCommand.ExecuteAsync(null);
        holidays.Holidays.Should().ContainSingle(h => h.Name == "Victory Day");

        var audit = host.Get<AuditLogViewModel>();
        await audit.OnNavigatedToAsync(null);
        audit.Entries.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Dashboard_shows_headcount()
    {
        await using var host = await DesktopTestHost.CreateAsync(completeSetup: true);
        await host.SignInAsync();
        await CreateAsync(host, "E0001", "One");
        var dashboard = host.Get<DashboardViewModel>();

        await dashboard.RefreshAsync();

        dashboard.Tiles.Should().Contain(t => t.Title == Resources.Strings.Dashboard_Headcount && t.Value == "1");
    }

    private static async Task<Guid> CreateAsync(DesktopTestHost host, string code, string name)
    {
        var input = new EmployeeInput(code, name, Gender.Unspecified, null, null, null, null, null, null, new DateOnly(2026, 1, 1),
            null, null, null, null, PaymentMode.Bank, null, null, null, null, true, null);
        var result = await host.Get<IDispatcher>().SendAsync(new CreateEmployeeCommand(input));
        result.IsSuccess.Should().BeTrue(result.ToString());
        return result.Value;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 50 && !condition(); i++)
        {
            await Task.Delay(50);
        }

        condition().Should().BeTrue();
    }
}
