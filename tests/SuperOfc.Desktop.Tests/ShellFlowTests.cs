using NSubstitute;
using SuperOfc.Application.Users;
using SuperOfc.Desktop.Services;
using SuperOfc.Desktop.Tests.Support;
using SuperOfc.Desktop.ViewModels;
using SuperOfc.Desktop.ViewModels.Settings;
using SuperOfc.Domain.Security;

namespace SuperOfc.Desktop.Tests;

public class SetupWizardTests
{
    [Fact]
    public async Task Completing_the_wizard_signs_in_and_opens_the_dashboard()
    {
        await using var host = await DesktopTestHost.CreateAsync(completeSetup: false);
        var main = host.Get<MainViewModel>();
        await main.ShowEntryScreenAsync();
        var wizard = main.CurrentScreen.Should().BeOfType<SetupWizardViewModel>().Subject;

        wizard.CompanyName = "Wizard Co";
        await wizard.NextCommand.ExecuteAsync(null);
        await wizard.NextCommand.ExecuteAsync(null);
        wizard.Step.Should().Be(2);
        wizard.AdminDisplayName = "Owner";
        wizard.AdminPassword = "Secret123";
        wizard.ConfirmPassword = "Secret123";
        await wizard.NextCommand.ExecuteAsync(null);
        wizard.IsLastStep.Should().BeTrue();
        await wizard.NextCommand.ExecuteAsync(null);

        var shell = main.CurrentScreen.Should().BeOfType<ShellViewModel>().Subject;
        shell.CurrentPage.Should().BeOfType<DashboardViewModel>();
        shell.Items.Should().NotBeEmpty();
        host.Get<SuperOfc.Application.Abstractions.ICompanyContext>().Company!.Name.Should().Be("Wizard Co");
    }

    [Fact]
    public async Task Wizard_blocks_next_without_company_name_and_shows_field_error()
    {
        await using var host = await DesktopTestHost.CreateAsync(completeSetup: false);
        var wizard = host.Get<SetupWizardViewModel>();

        await wizard.NextCommand.ExecuteAsync(null);

        wizard.Step.Should().Be(0);
        wizard.HasErrors.Should().BeTrue();
        wizard.GetErrors(nameof(SetupWizardViewModel.CompanyName)).Cast<string>().Should().NotBeEmpty();
        wizard.Steps.Count(s => s.IsReached).Should().Be(1);
    }

    [Fact]
    public async Task Mismatched_passwords_stay_on_admin_step()
    {
        await using var host = await DesktopTestHost.CreateAsync(completeSetup: false);
        var wizard = host.Get<SetupWizardViewModel>();
        wizard.CompanyName = "X";
        wizard.Step = 2;
        wizard.AdminDisplayName = "Owner";
        wizard.AdminPassword = "Secret123";
        wizard.ConfirmPassword = "Different1";

        await wizard.NextCommand.ExecuteAsync(null);

        wizard.Step.Should().Be(2);
        wizard.GetErrors(nameof(SetupWizardViewModel.ConfirmPassword)).Cast<string>().Should().NotBeEmpty();
    }
}

public class LoginAndShellTests
{
    [Fact]
    public async Task Wrong_password_shows_error_and_clears_password()
    {
        await using var host = await DesktopTestHost.CreateAsync(completeSetup: true);
        var main = host.Get<MainViewModel>();
        await main.ShowEntryScreenAsync();
        var login = main.CurrentScreen.Should().BeOfType<LoginViewModel>().Subject;
        login.CompanyName.Should().Be("Desk Co");

        login.Username = "admin";
        login.Password = "nope";
        await login.SignInCommand.ExecuteAsync(null);

        login.ErrorMessage.Should().NotBeNullOrEmpty();
        login.Password.Should().BeEmpty();
        main.CurrentScreen.Should().BeSameAs(login);
    }

    [Fact]
    public async Task Viewer_sees_only_permitted_areas()
    {
        await using var host = await DesktopTestHost.CreateAsync(completeSetup: true);
        await SignInAsync(host, "admin", DesktopTestHost.Password);
        await host.Get<SuperOfc.Application.Common.IDispatcher>().SendAsync(new CreateUserCommand("viewer", "View Only", "Viewer123", Role.Viewer));
        await host.Get<MainViewModel>().SignOutAsync();

        var shell = await SignInAsync(host, "viewer", "Viewer123");

        shell.Items.Select(i => i.Title).Should().NotContain([Resources.Strings.Nav_Payroll, Resources.Strings.Nav_Devices]);
        await host.Get<IAppNavigator>().NavigateToAsync<SettingsViewModel>();
        var settings = (SettingsViewModel)host.Get<IAppNavigator>().Current!;
        settings.Sections.Select(s => s.Key).Should().BeEquivalentTo(["Updates", "Appearance", "About"]);
    }

    [Fact]
    public async Task Unsaved_company_changes_block_navigation_when_user_cancels()
    {
        await using var host = await DesktopTestHost.CreateAsync(completeSetup: true);
        await SignInAsync(host, "admin", DesktopTestHost.Password);
        var navigation = host.Get<IAppNavigator>();
        await navigation.NavigateToAsync<SettingsViewModel>("Company");
        var settings = (SettingsViewModel)navigation.Current!;
        var company = settings.Content.Should().BeOfType<CompanySettingsViewModel>().Subject;
        company.IsDirty.Should().BeFalse();

        company.Name = "Changed";
        company.IsDirty.Should().BeTrue();
        host.Dialogs.ConfirmAsync(default!, default!, default!, default).ReturnsForAnyArgs(false);
        var moved = await navigation.NavigateToAsync<DashboardViewModel>();

        moved.Should().BeFalse();
        navigation.Current.Should().BeSameAs(settings);

        await company.SaveCommand.ExecuteAsync(null);
        company.IsDirty.Should().BeFalse();
        (await navigation.NavigateToAsync<DashboardViewModel>()).Should().BeTrue();
    }

    [Fact]
    public async Task Lock_and_unlock_with_password()
    {
        await using var host = await DesktopTestHost.CreateAsync(completeSetup: true);
        await SignInAsync(host, "admin", DesktopTestHost.Password);
        var main = host.Get<MainViewModel>();

        main.LockNow();
        main.Lock.Should().NotBeNull();
        main.Lock!.Password = "wrong";
        await main.Lock.UnlockCommand.ExecuteAsync(null);
        main.Lock.Should().NotBeNull();
        main.Lock!.ErrorMessage.Should().NotBeNullOrEmpty();

        main.Lock.Password = DesktopTestHost.Password;
        await main.Lock.UnlockCommand.ExecuteAsync(null);
        main.Lock.Should().BeNull();
    }

    private static async Task<ShellViewModel> SignInAsync(DesktopTestHost host, string user, string password)
    {
        var main = host.Get<MainViewModel>();
        main.ShowLogin();
        var login = (LoginViewModel)main.CurrentScreen!;
        login.Username = user;
        login.Password = password;
        await login.SignInCommand.ExecuteAsync(null);
        login.ErrorMessage.Should().BeNull();
        return (ShellViewModel)main.CurrentScreen!;
    }
}
