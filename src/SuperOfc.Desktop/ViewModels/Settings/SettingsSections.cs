using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Auth;
using SuperOfc.Application.Backups;
using SuperOfc.Application.Policies;
using SuperOfc.Application.Setup;
using SuperOfc.Application.Users;
using SuperOfc.Desktop.Mvvm;
using SuperOfc.Desktop.Resources;
using SuperOfc.Desktop.Services;
using SuperOfc.Domain.Organization;
using SuperOfc.Domain.Policies;
using SuperOfc.Domain.Security;

namespace SuperOfc.Desktop.ViewModels.Settings;

public sealed partial class CompanySettingsViewModel(PageServices services) : EditableViewModel(services)
{
    public override string Title => Strings.Settings_Company;

    public IReadOnlyList<Choice<string>> TimeZones => CompanyChoices.TimeZones;

    public IReadOnlyList<Choice<int>> Months => CompanyChoices.Months;

    public IReadOnlyList<Choice<NumberGrouping>> Groupings => CompanyChoices.Groupings;

    public IReadOnlyList<string> DateFormats => CompanyChoices.DateFormats;

    [ObservableProperty]
    public partial WorkingWeekEditor? Week { get; set; }

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? Address { get; set; }

    [ObservableProperty]
    public partial string? Phone { get; set; }

    [ObservableProperty]
    public partial string? Email { get; set; }

    [ObservableProperty]
    public partial string? TaxIdentifier { get; set; }

    [ObservableProperty]
    public partial string CurrencyCode { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CurrencySymbol { get; set; } = string.Empty;

    [ObservableProperty]
    public partial NumberGrouping Grouping { get; set; }

    [ObservableProperty]
    public partial string TimeZoneId { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int FiscalYearStartMonth { get; set; }

    [ObservableProperty]
    public partial string DateFormat { get; set; } = string.Empty;

    private string? _logoPath;

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        var result = await SendAsync(new GetCompanyQuery()).ConfigureAwait(true);
        if (result.IsFailure)
        {
            return;
        }

        var c = result.Value;
        using (Loading())
        {
            Name = c.Name;
            Address = c.Address;
            Phone = c.Phone;
            Email = c.Email;
            TaxIdentifier = c.TaxIdentifier;
            CurrencyCode = c.CurrencyCode;
            CurrencySymbol = c.CurrencySymbol;
            Grouping = c.Grouping;
            TimeZoneId = c.TimeZoneId;
            FiscalYearStartMonth = c.FiscalYearStartMonth;
            DateFormat = c.DateFormat;
            _logoPath = c.LogoPath;
            Week = new WorkingWeekEditor(c.WorkingWeek);
            Week.Changed += (_, _) => IsDirty = true;
        }
    }

    protected override bool IsTrackedProperty(string? propertyName) => propertyName is not nameof(Week);

    [RelayCommand]
    private async Task SaveAsync()
    {
        var result = await SendAsync(
            new UpdateCompanyCommand(
                Name, Address, Phone, Email, TaxIdentifier, _logoPath, CurrencyCode, CurrencySymbol, Grouping,
                TimeZoneId, FiscalYearStartMonth, DateFormat, Week?.ToWeek() ?? WorkingWeek.Default),
            showErrors: false).ConfigureAwait(true);
        if (result.IsFailure)
        {
            ShowErrors(result.Errors);
            return;
        }

        IsDirty = false;
        Toast(Strings.App_Saved);
    }
}

public sealed partial class UsersSettingsViewModel(PageServices services) : PageViewModel(services)
{
    public override string Title => Strings.Users_Title;

    public ObservableCollection<UserDto> Users { get; } = [];

    public static IReadOnlyList<Role> Roles { get; } = Enum.GetValues<Role>();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveUserCommand), nameof(ResetPasswordCommand))]
    public partial UserDto? Selected { get; set; }

    [ObservableProperty]
    public partial bool IsEditing { get; set; }

    [ObservableProperty]
    public partial bool IsNew { get; set; }

    [ObservableProperty]
    public partial string EditUsername { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EditDisplayName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Role EditRole { get; set; } = Role.Viewer;

    [ObservableProperty]
    public partial bool EditActive { get; set; } = true;

    [ObservableProperty]
    public partial string EditPassword { get; set; } = string.Empty;

    public override Task OnNavigatedToAsync(object? parameter) => LoadAsync();

    partial void OnSelectedChanged(UserDto? value)
    {
        if (value is null)
        {
            IsEditing = false;
            return;
        }

        IsNew = false;
        IsEditing = true;
        EditUsername = value.Username;
        EditDisplayName = value.DisplayName;
        EditRole = value.Role;
        EditActive = value.IsActive;
        EditPassword = string.Empty;
        ErrorMessage = null;
    }

    [RelayCommand]
    private void NewUser()
    {
        Selected = null;
        IsNew = true;
        IsEditing = true;
        EditUsername = EditDisplayName = EditPassword = string.Empty;
        EditRole = Role.HR;
        EditActive = true;
        ErrorMessage = null;
    }

    [RelayCommand]
    private async Task SaveUserAsync()
    {
        if (IsNew)
        {
            var created = await SendAsync(new CreateUserCommand(EditUsername, EditDisplayName, EditPassword, EditRole)).ConfigureAwait(true);
            if (created.IsFailure)
            {
                return;
            }
        }
        else if (Selected is not null)
        {
            var updated = await SendAsync(new UpdateUserCommand(Selected.Id, EditDisplayName, EditRole, EditActive)).ConfigureAwait(true);
            if (updated.IsFailure)
            {
                return;
            }
        }

        Toast(Strings.App_Saved);
        await LoadAsync().ConfigureAwait(true);
        IsEditing = false;
    }

    private bool CanResetPassword() => Selected is not null;

    [RelayCommand(CanExecute = nameof(CanResetPassword))]
    private async Task ResetPasswordAsync()
    {
        if (Selected is null)
        {
            return;
        }

        var password = await Services.Dialogs.PromptAsync(Strings.Users_ResetPassword, Strings.Setup_PasswordHint, Strings.Users_NewPassword).ConfigureAwait(true);
        if (password is null)
        {
            return;
        }

        var result = await SendAsync(new ResetPasswordCommand(Selected.Id, password)).ConfigureAwait(true);
        if (result.IsSuccess)
        {
            Toast(Strings.App_Saved);
            await LoadAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
        Selected = null;
    }

    private async Task LoadAsync()
    {
        var result = await SendAsync(new GetUsersQuery()).ConfigureAwait(true);
        if (result.IsFailure)
        {
            return;
        }

        Users.Clear();
        foreach (var user in result.Value)
        {
            Users.Add(user);
        }
    }
}

public sealed partial class PoliciesSettingsViewModel(PageServices services, IClock clock) : EditableViewModel(services)
{
    public override string Title => Strings.Settings_Policies;

    [ObservableProperty]
    public partial DateTime EffectiveFrom { get; set; } = DateTime.Today;

    [ObservableProperty]
    public partial int IdleLockMinutes { get; set; }

    [ObservableProperty]
    public partial int MaxFailedLogins { get; set; }

    [ObservableProperty]
    public partial int LockoutMinutes { get; set; }

    [ObservableProperty]
    public partial int AutoSyncMinutes { get; set; }

    public ObservableCollection<PolicyVersionDto> History { get; } = [];

    /// <summary>Extra policy editors contributed by modules (attendance, payroll, loans).</summary>
    public ObservableCollection<PolicyEditorBase> Editors { get; } = [];

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        var today = DateOnly.FromDateTime(clock.UtcNow.ToLocalTime());
        var snapshot = await SendAsync(new GetPolicySnapshotQuery(today)).ConfigureAwait(true);
        if (snapshot.IsFailure)
        {
            return;
        }

        using (Loading())
        {
            var g = snapshot.Value.General;
            IdleLockMinutes = g.IdleLockMinutes;
            MaxFailedLogins = g.MaxFailedLogins;
            LockoutMinutes = g.LockoutMinutes;
            AutoSyncMinutes = g.AutoSyncMinutes;
            EffectiveFrom = DateTime.Today;
            Editors.Clear();
            foreach (var editor in PolicyEditors.Create(snapshot.Value))
            {
                editor.Changed += (_, _) => IsDirty = true;
                Editors.Add(editor);
            }
        }

        var history = await SendAsync(new GetPolicyHistoryQuery(PolicySnapshot.GeneralSection)).ConfigureAwait(true);
        History.Clear();
        if (history.IsSuccess)
        {
            foreach (var item in history.Value)
            {
                History.Add(item);
            }
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var from = DateOnly.FromDateTime(EffectiveFrom);
        var general = new GeneralPolicy
        {
            IdleLockMinutes = Math.Max(0, IdleLockMinutes),
            MaxFailedLogins = Math.Clamp(MaxFailedLogins, 1, 50),
            LockoutMinutes = Math.Clamp(LockoutMinutes, 1, 1440),
            AutoSyncMinutes = Math.Clamp(AutoSyncMinutes, 1, 1440),
        };
        var result = await SendAsync(new SavePolicyCommand(PolicySnapshot.GeneralSection, from, general, null)).ConfigureAwait(true);
        if (result.IsFailure)
        {
            return;
        }

        foreach (var editor in Editors)
        {
            var saved = await SendAsync(new SavePolicyCommand(editor.Section, from, editor.Build(), null)).ConfigureAwait(true);
            if (saved.IsFailure)
            {
                return;
            }
        }

        IsDirty = false;
        Toast(Strings.App_Saved);
        await OnNavigatedToAsync(null).ConfigureAwait(true);
    }
}

/// <summary>Base for module policy editors shown on the policies page.</summary>
public abstract partial class PolicyEditorBase : ObservableObject
{
    public event EventHandler? Changed;

    public abstract string Section { get; }

    public abstract string Header { get; }

    public abstract object Build();

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (IsPolicyProperty(e?.PropertyName))
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Properties that are part of the saved policy; helper properties (previews) return false.</summary>
    protected virtual bool IsPolicyProperty(string? propertyName) => true;
}

/// <summary>Modules register their policy editors here.</summary>
public static partial class PolicyEditors
{
    public static IEnumerable<PolicyEditorBase> Create(PolicySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return ModuleEditors(snapshot);
    }

    private static partial IEnumerable<PolicyEditorBase> ModuleEditors(PolicySnapshot snapshot);
}

public sealed partial class BackupSettingsViewModel(PageServices services, IAppEnvironment environment, IFileDialogService files) : PageViewModel(services)
{
    public override string Title => Strings.Backup_Title;

    public ObservableCollection<BackupRow> Backups { get; } = [];

    [ObservableProperty]
    public partial BackupRow? Selected { get; set; }

    public bool CanRestore => Can(Permission.ManageSettings);

    public override Task OnNavigatedToAsync(object? parameter) => LoadAsync();

    [RelayCommand]
    private async Task BackupNowAsync()
    {
        var result = await SendAsync(new CreateBackupCommand(null)).ConfigureAwait(true);
        if (result.IsSuccess)
        {
            Toast(Strings.Backup_Created);
            await LoadAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task RestoreSelectedAsync(BackupRow? row)
    {
        var target = row ?? Selected;
        if (target is not null)
        {
            await RestoreAsync(target.FilePath, target.CreatedText).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task RestoreFromFileAsync()
    {
        var path = files.OpenFile("SuperOfc backup (*.db)|*.db", environment.BackupDirectory);
        if (path is not null)
        {
            await RestoreAsync(path, Path.GetFileName(path)).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void OpenFolder() => files.OpenFolder(environment.BackupDirectory);

    private async Task RestoreAsync(string path, string label)
    {
        var confirmed = await Services.Dialogs.ConfirmAsync(
            Strings.Backup_RestoreTitle,
            string.Format(CultureInfo.CurrentCulture, Strings.Backup_RestoreMessage, label),
            Strings.Backup_RestoreThis,
            danger: true).ConfigureAwait(true);
        if (!confirmed)
        {
            return;
        }

        var result = await SendAsync(new RestoreBackupCommand(path)).ConfigureAwait(true);
        if (result.IsSuccess)
        {
            await Services.Dialogs.AlertAsync(Strings.Backup_Title, Strings.Backup_RestoreDone).ConfigureAwait(true);
            ((App)System.Windows.Application.Current).Restart();
        }
    }

    private async Task LoadAsync()
    {
        var result = await SendAsync(new GetBackupsQuery()).ConfigureAwait(true);
        if (result.IsFailure)
        {
            return;
        }

        Backups.Clear();
        foreach (var b in result.Value)
        {
            Backups.Add(new BackupRow(
                b.FilePath,
                b.Type.ToString(),
                b.CreatedAtUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
                FormatSize(b.SizeBytes),
                b.AppVersion,
                File.Exists(b.FilePath)));
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.#} MB",
    };
}

public sealed record BackupRow(string FilePath, string Type, string CreatedText, string SizeText, string AppVersion, bool Exists)
{
    public string FileName => Path.GetFileName(FilePath);
}

public sealed partial class UpdatesSettingsViewModel : PageViewModel
{
    private readonly IUpdateService _updates;
    private readonly ISettingsStore _settings;
    private bool _loading;

    public UpdatesSettingsViewModel(PageServices services, IUpdateService updates, ISettingsStore settings, IAppEnvironment environment)
        : base(services)
    {
        _updates = updates;
        _settings = settings;
        CurrentVersion = environment.AppVersion;
        _updates.StatusChanged += (_, s) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => ApplyStatus(s));
        ApplyStatus(_updates.Status);
    }

    public override string Title => Strings.Updates_Title;

    public string CurrentVersion { get; }

    public bool IsInstalled => _updates.IsInstalled;

    public static IReadOnlyList<Choice<UpdateMode>> Modes { get; } =
    [
        new(UpdateMode.Automatic, Strings.Updates_ModeAutomatic),
        new(UpdateMode.NotifyOnly, Strings.Updates_ModeNotify),
        new(UpdateMode.Manual, Strings.Updates_ModeManual),
    ];

    public static IReadOnlyList<Choice<string>> Channels { get; } =
    [
        new("stable", Strings.Updates_ChannelStable),
        new("beta", Strings.Updates_ChannelBeta),
    ];

    [ObservableProperty]
    public partial UpdateMode Mode { get; set; }

    [ObservableProperty]
    public partial string Channel { get; set; } = "stable";

    [ObservableProperty]
    public partial string FeedUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool CanRestart { get; set; }

    [ObservableProperty]
    public partial int Progress { get; set; }

    public bool IsAdmin => Can(Permission.ManageSettings);

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        _loading = true;
        var config = await _settings.GetAsync<UpdateSettings>(UpdateSettings.Key).ConfigureAwait(true);
        Mode = config.Mode;
        Channel = config.Channel ?? _updates.InstalledChannel;
        FeedUrl = config.FeedUrl;
        _loading = false;
        if (config.LastCheckUtc is { } last && _updates.Status.State is UpdateState.Idle)
        {
            StatusText = string.Format(CultureInfo.CurrentCulture, Strings.Updates_LastCheck, last.ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
        }
    }

    partial void OnModeChanged(UpdateMode value) => _ = SaveAsync();

    partial void OnChannelChanged(string value) => _ = SaveAsync();

    [RelayCommand]
    private Task SaveFeedAsync() => SaveAsync();

    [RelayCommand]
    private async Task CheckAsync() => await _updates.CheckAsync(downloadIfAvailable: true).ConfigureAwait(true);

    [RelayCommand]
    private void Restart()
    {
        var reason = _updates.ApplyAndRestart();
        if (reason is not null)
        {
            Services.Notify.ToastError(reason);
        }
    }

    private async Task SaveAsync()
    {
        if (_loading || !IsAdmin)
        {
            return;
        }

        var current = await _settings.GetAsync<UpdateSettings>(UpdateSettings.Key).ConfigureAwait(true);
        var url = string.IsNullOrWhiteSpace(FeedUrl) ? UpdateSettings.DefaultFeedUrl : FeedUrl.Trim();
        await _settings.SetAsync(UpdateSettings.Key, current with { Mode = Mode, Channel = Channel, FeedUrl = url }).ConfigureAwait(true);
        Toast(Strings.App_Saved);
    }

    private void ApplyStatus(UpdateStatus status)
    {
        Progress = status.Progress;
        CanRestart = status.State == UpdateState.ReadyToRestart;
        StatusText = status.State switch
        {
            UpdateState.NotInstalled => Strings.Updates_NotInstalled,
            UpdateState.Checking => Strings.Updates_Checking,
            UpdateState.UpToDate => Strings.Updates_UpToDate,
            UpdateState.Available => string.Format(CultureInfo.CurrentCulture, Strings.Updates_Available, status.NewVersion),
            UpdateState.Downloading => $"{string.Format(CultureInfo.CurrentCulture, Strings.Updates_Available, status.NewVersion)} {status.Progress}%",
            UpdateState.ReadyToRestart => string.Format(CultureInfo.CurrentCulture, Strings.Updates_Ready, status.NewVersion),
            UpdateState.Failed => string.Format(CultureInfo.CurrentCulture, Strings.Updates_Failed, status.Error),
            _ => StatusText,
        };
    }
}

public sealed partial class AppearanceSettingsViewModel : PageViewModel
{
    private readonly IAppThemeService _theme;

    public AppearanceSettingsViewModel(PageServices services, IAppThemeService theme)
        : base(services)
    {
        _theme = theme;
        Theme = theme.Theme;
    }

    public override string Title => Strings.Settings_Appearance;

    public static IReadOnlyList<Choice<AppTheme>> Themes { get; } =
    [
        new(AppTheme.System, Strings.Settings_ThemeSystem),
        new(AppTheme.Light, Strings.Settings_ThemeLight),
        new(AppTheme.Dark, Strings.Settings_ThemeDark),
    ];

    [ObservableProperty]
    public partial AppTheme Theme { get; set; }

    [ObservableProperty]
    public partial string CurrentPassword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewPassword { get; set; } = string.Empty;

    partial void OnThemeChanged(AppTheme value) => _theme.Apply(value);

    [RelayCommand]
    private async Task ChangePasswordAsync()
    {
        var result = await SendAsync(new ChangeOwnPasswordCommand(CurrentPassword, NewPassword)).ConfigureAwait(true);
        if (result.IsSuccess)
        {
            CurrentPassword = NewPassword = string.Empty;
            Toast(Strings.App_Saved);
        }
    }
}

public sealed partial class AboutSettingsViewModel(PageServices services, IAppEnvironment environment, IFileDialogService files) : PageViewModel(services)
{
    public override string Title => Strings.Settings_About;

    public string Version => environment.AppVersion;

    public string DataFolder => environment.DataDirectory;

    public string LogFolder => environment.LogDirectory;

    public string Runtime => System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;

    public string OperatingSystem => System.Runtime.InteropServices.RuntimeInformation.OSDescription;

    [RelayCommand]
    private void OpenLogs() => files.OpenFolder(environment.LogDirectory);

    [RelayCommand]
    private void OpenData() => files.OpenFolder(environment.DataDirectory);

    public bool CanExportData => Can(Permission.ManageBackups);

    /// <summary>Zip of recent logs and versions to send to support (doc 07). Holds no salaries or secrets.</summary>
    [RelayCommand]
    private async Task ExportDiagnosticsAsync()
    {
        var path = files.SaveFile("Zip (*.zip)|*.zip", $"SuperOfc diagnostics {DateTime.Now:yyyy-MM-dd}.zip");
        if (path is not null && (await SendAsync(new Application.Backups.ExportDiagnosticsCommand(path)).ConfigureAwait(true)).IsSuccess)
        {
            Toast(Strings.Settings_DiagnosticsSaved);
            files.OpenFolder(System.IO.Path.GetDirectoryName(path)!);
        }
    }

    /// <summary>Every table as Excel and JSON, without passwords or device keys (P8-T3).</summary>
    [RelayCommand]
    private async Task ExportAllDataAsync()
    {
        var folder = files.PickFolder(Strings.Settings_ExportAllDataPick);
        if (folder is null)
        {
            return;
        }

        var target = System.IO.Path.Combine(folder, $"SuperOfc data {DateTime.Now:yyyy-MM-dd HHmm}");
        var result = await SendAsync(new Application.Backups.ExportAllDataCommand(target)).ConfigureAwait(true);
        if (result.IsSuccess)
        {
            Toast(string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Settings_ExportAllDataDone, result.Value.Rows, result.Value.Elapsed.TotalSeconds));
            files.OpenFolder(target);
        }
    }
}
