using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using SuperOfc.Desktop.Mvvm;
using SuperOfc.Desktop.Resources;
using SuperOfc.Domain.Security;
using Wpf.Ui.Controls;

namespace SuperOfc.Desktop.ViewModels.Settings;

public sealed record SettingsSection(string Key, string Title, SymbolRegular Icon, Type ViewModelType, Permission Permission);

/// <summary>Settings page with its own section list. Sections are page view models shown inside it.</summary>
public sealed partial class SettingsViewModel(PageServices services, IServiceProvider provider) : PageViewModel(services)
{
    private bool _switching;

    /// <summary>Sections in display order; modules append theirs (shifts, salary components, ...).</summary>
    public static List<SettingsSection> AllSections { get; } =
    [
        new("Company", Strings.Settings_Company, SymbolRegular.Building24, typeof(CompanySettingsViewModel), Permission.ManageSettings),
        new("Users", Strings.Settings_Users, SymbolRegular.PeopleTeam24, typeof(UsersSettingsViewModel), Permission.ManageUsers),
        new("Organization", Strings.Settings_Organization, SymbolRegular.Organization24, typeof(OrganizationSettingsViewModel), Permission.ManageEmployees),
        new("Shifts", Strings.Settings_Shifts, SymbolRegular.Clock24, typeof(ShiftsSettingsViewModel), Permission.ManageAttendance),
        new("LeaveTypes", Strings.Settings_LeaveTypes, SymbolRegular.CalendarPerson20, typeof(LeaveTypesSettingsViewModel), Permission.ManageLeave),
        new("Holidays", Strings.Settings_Holidays, SymbolRegular.CalendarStar24, typeof(HolidaysSettingsViewModel), Permission.ManageAttendance),
        new("SalaryComponents", Strings.Settings_SalaryComponents, SymbolRegular.Money24, typeof(Payroll.SalaryComponentsSettingsViewModel), Permission.ViewSalaries),
        new("SalaryStructures", Strings.Settings_SalaryStructures, SymbolRegular.TextBulletListSquare24, typeof(Payroll.SalaryStructuresSettingsViewModel), Permission.ViewSalaries),
        new("LetterTemplates", Strings.Settings_LetterTemplates, SymbolRegular.DocumentText24, typeof(Letters.LetterTemplatesSettingsViewModel), Permission.ManageEmployees),
        new("Policies", Strings.Settings_Policies, SymbolRegular.Gavel24, typeof(PoliciesSettingsViewModel), Permission.ManageSettings),
        new("Backup", Strings.Settings_Backup, SymbolRegular.CloudArrowUp24, typeof(BackupSettingsViewModel), Permission.ManageBackups),
        new("AuditLog", Strings.Settings_AuditLog, SymbolRegular.History24, typeof(AuditLogViewModel), Permission.ViewAuditLog),
        new("Updates", Strings.Settings_Updates, SymbolRegular.ArrowSync24, typeof(UpdatesSettingsViewModel), Permission.ViewData),
        new("Appearance", Strings.Settings_Appearance, SymbolRegular.PaintBrush24, typeof(AppearanceSettingsViewModel), Permission.ViewData),
        new("About", Strings.Settings_About, SymbolRegular.Info24, typeof(AboutSettingsViewModel), Permission.ViewData),
    ];

    public override string Title => Strings.Settings_Title;

    public ObservableCollection<SettingsSection> Sections { get; } = [];

    [ObservableProperty]
    public partial SettingsSection? SelectedSection { get; set; }

    [ObservableProperty]
    public partial PageViewModel? Content { get; set; }

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        Sections.Clear();
        foreach (var section in AllSections.Where(s => Can(s.Permission)))
        {
            Sections.Add(section);
        }

        var key = parameter as string;
        SelectedSection = Sections.FirstOrDefault(s => s.Key == key) ?? Sections.FirstOrDefault();
        await Task.CompletedTask.ConfigureAwait(true);
    }

    public override Task<bool> CanNavigateAwayAsync() => Content?.CanNavigateAwayAsync() ?? Task.FromResult(true);

    async partial void OnSelectedSectionChanged(SettingsSection? oldValue, SettingsSection? newValue)
    {
        if (_switching || newValue is null)
        {
            return;
        }

        if (Content is not null && !await Content.CanNavigateAwayAsync().ConfigureAwait(true))
        {
            _switching = true;
            SelectedSection = oldValue;
            _switching = false;
            return;
        }

        var page = (PageViewModel)provider.GetRequiredService(newValue.ViewModelType);
        Content = page;
        await page.OnNavigatedToAsync(null).ConfigureAwait(true);
    }
}
