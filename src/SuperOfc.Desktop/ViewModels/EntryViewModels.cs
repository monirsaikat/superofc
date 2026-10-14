using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Auth;
using SuperOfc.Application.Common;
using SuperOfc.Application.Setup;
using SuperOfc.Desktop.Mvvm;
using SuperOfc.Desktop.Resources;
using SuperOfc.Desktop.Services;
using SuperOfc.Domain.Organization;
using SuperOfc.Infrastructure.Persistence;
using SuperOfc.Infrastructure.Services;

namespace SuperOfc.Desktop.ViewModels;

public sealed partial class StartupViewModel(MainViewModel main) : ViewModelBase
{
    [ObservableProperty]
    public partial string Message { get; set; } = Strings.App_Starting;

    [ObservableProperty]
    public partial bool HasError { get; set; }

    [ObservableProperty]
    public partial string? ErrorTitle { get; set; }

    [ObservableProperty]
    public partial bool CanRestore { get; set; }

    public void ShowError(DatabaseInitResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        HasError = true;
        (ErrorTitle, ErrorMessage, CanRestore) = result.State switch
        {
            DatabaseState.Corrupt => (Strings.App_DbCorruptTitle, string.Format(CultureInfo.CurrentCulture, Strings.App_DbCorruptMessage, result.Message), true),
            DatabaseState.TooNew => (Strings.App_DbTooNewTitle, result.Message, false),
            _ => (Strings.App_DbMigrationFailedTitle, result.Message, true),
        };
    }

    [RelayCommand]
    private async Task RestoreAsync()
    {
        var app = (App)System.Windows.Application.Current;
        var env = app.Services.GetRequiredService<AppEnvironment>();
        var files = app.Services.GetRequiredService<IFileDialogService>();
        var path = files.OpenFile("SuperOfc backup (*.db)|*.db", env.BackupDirectory);
        if (path is null)
        {
            return;
        }

        var result = await Task.Run(() => DatabaseRecovery.ReplaceWithBackup(env, path)).ConfigureAwait(true);
        if (result.IsFailure)
        {
            ErrorMessage = ErrorText.For(result.Errors);
            return;
        }

        app.Restart();
    }

    [RelayCommand]
    private static void OpenDataFolder()
    {
        var app = (App)System.Windows.Application.Current;
        app.Services.GetRequiredService<IFileDialogService>().OpenFolder(app.Services.GetRequiredService<AppEnvironment>().Root);
    }

    [RelayCommand]
    private static void Exit() => System.Windows.Application.Current.Shutdown();

    public MainViewModel Main => main;
}

public sealed partial class LoginViewModel(IDispatcher dispatcher, MainViewModel main) : ViewModelBase
{
    [ObservableProperty]
    public partial string CompanyName { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    public partial string Username { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    public partial string Password { get; set; } = string.Empty;

    public string Subtitle => string.Format(CultureInfo.CurrentCulture, Strings.Login_Subtitle, CompanyName);

    public string VersionText => string.Format(CultureInfo.CurrentCulture, Strings.App_Version, ((App)System.Windows.Application.Current).Services.GetRequiredService<IAppEnvironment>().AppVersion);

    partial void OnCompanyNameChanged(string value) => OnPropertyChanged(nameof(Subtitle));

    private bool CanSignIn() => !IsBusy && Username.Length > 0 && Password.Length > 0;

    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private async Task SignInAsync()
    {
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var result = await dispatcher.SendAsync(new LoginCommand(Username, Password)).ConfigureAwait(true);
            if (result.IsFailure)
            {
                ErrorMessage = ErrorText.For(result.Errors);
                Password = string.Empty;
                return;
            }

            Password = string.Empty;
            await main.OnSignedInAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }
}

public sealed partial class LockViewModel(MainViewModel main, IDispatcher dispatcher, string displayName) : ViewModelBase
{
    public string DisplayName { get; } = displayName;

    [ObservableProperty]
    public partial string Password { get; set; } = string.Empty;

    [RelayCommand]
    private async Task UnlockAsync()
    {
        var result = await dispatcher.SendAsync(new UnlockCommand(Password)).ConfigureAwait(true);
        Password = string.Empty;
        if (result.IsFailure)
        {
            ErrorMessage = ErrorText.For(result.Errors);
            return;
        }

        main.Unlocked();
    }

    [RelayCommand]
    private Task SignOutAsync() => main.SignOutAsync();
}

public sealed record Choice<T>(T Value, string Text);

/// <summary>Editable working week used by setup and company settings.</summary>
public sealed partial class WorkingWeekEditor : ObservableObject
{
    private static readonly DayOfWeek[] Order =
        [DayOfWeek.Saturday, DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];

    public WorkingWeekEditor(WorkingWeek week)
    {
        Days = new ObservableCollection<WorkDayItem>(Order.Select(d => new WorkDayItem(d, week.For(d))));
        foreach (var day in Days)
        {
            day.PropertyChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? Changed;

    public static IReadOnlyList<Choice<WorkDayType>> Types { get; } =
    [
        new(WorkDayType.Full, Strings.Week_Full),
        new(WorkDayType.Half, Strings.Week_Half),
        new(WorkDayType.Off, Strings.Week_Off),
    ];

    public ObservableCollection<WorkDayItem> Days { get; }

    public WorkingWeek ToWeek() => Days.Aggregate(WorkingWeek.Default, (week, d) => week.With(d.Day, d.Type));
}

public sealed partial class WorkDayItem(DayOfWeek day, WorkDayType type) : ObservableObject
{
    public DayOfWeek Day { get; } = day;

    public string Name { get; } = CultureInfo.CurrentCulture.DateTimeFormat.GetDayName(day);

    [ObservableProperty]
    public partial WorkDayType Type { get; set; } = type;
}

/// <summary>Shared lookup lists for company forms.</summary>
public static class CompanyChoices
{
    public static IReadOnlyList<Choice<string>> TimeZones { get; } =
        TimeZoneInfo.GetSystemTimeZones().Select(z => new Choice<string>(z.Id, z.DisplayName)).ToList();

    public static IReadOnlyList<Choice<int>> Months { get; } =
        Enumerable.Range(1, 12).Select(m => new Choice<int>(m, CultureInfo.CurrentCulture.DateTimeFormat.GetMonthName(m))).ToList();

    public static IReadOnlyList<Choice<NumberGrouping>> Groupings { get; } =
    [
        new(NumberGrouping.Western, Strings.Setup_GroupingWestern),
        new(NumberGrouping.SouthAsian, Strings.Setup_GroupingSouthAsian),
    ];

    public static IReadOnlyList<string> DateFormats { get; } = ["dd MMM yyyy", "dd/MM/yyyy", "MM/dd/yyyy", "yyyy-MM-dd", "d MMMM yyyy"];
}

public sealed partial class SetupWizardViewModel : EditableViewModel
{
    private readonly MainViewModel _main;

    public SetupWizardViewModel(PageServices services, MainViewModel main)
        : base(services)
    {
        _main = main;
        var region = SafeRegion();
        CurrencyCode = region?.ISOCurrencySymbol ?? "USD";
        CurrencySymbol = region?.CurrencySymbol ?? "$";
        Grouping = region?.TwoLetterISORegionName is "BD" or "IN" or "PK" or "NP" or "LK" ? NumberGrouping.SouthAsian : NumberGrouping.Western;
        TimeZoneId = TimeZoneInfo.Local.Id;
        Week = new WorkingWeekEditor(WorkingWeek.Default);
    }

    public override string Title => Strings.Setup_Title;

    public static IReadOnlyList<string> StepTitles { get; } = [Strings.Setup_StepCompany, Strings.Setup_StepRegion, Strings.Setup_StepAdmin, Strings.Setup_StepFinish];

    public WorkingWeekEditor Week { get; }

    public ObservableCollection<WizardStep> Steps { get; } = WizardStep.Create(StepTitles);

    public IReadOnlyList<Choice<string>> TimeZones => CompanyChoices.TimeZones;

    public IReadOnlyList<Choice<int>> Months => CompanyChoices.Months;

    public IReadOnlyList<Choice<NumberGrouping>> Groupings => CompanyChoices.Groupings;

    public IReadOnlyList<string> DateFormats => CompanyChoices.DateFormats;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFirstStep), nameof(IsLastStep), nameof(StepTitle))]
    [NotifyCanExecuteChangedFor(nameof(BackCommand))]
    public partial int Step { get; set; }

    public string StepTitle => StepTitles[Step];

    public bool IsFirstStep => Step == 0;

    public bool IsLastStep => Step == StepTitles.Count - 1;

    [ObservableProperty]
    public partial string CompanyName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CurrencyCode { get; set; }

    [ObservableProperty]
    public partial string CurrencySymbol { get; set; }

    [ObservableProperty]
    public partial NumberGrouping Grouping { get; set; }

    [ObservableProperty]
    public partial string TimeZoneId { get; set; }

    [ObservableProperty]
    public partial int FiscalYearStartMonth { get; set; } = 7;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DatePreview))]
    public partial string DateFormat { get; set; } = "dd MMM yyyy";

    [ObservableProperty]
    public partial string AdminUsername { get; set; } = "admin";

    [ObservableProperty]
    public partial string AdminDisplayName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AdminPassword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ConfirmPassword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool LoadSampleData { get; set; }

    public string DatePreview
    {
        get
        {
            try
            {
                return string.Format(CultureInfo.CurrentCulture, Strings.Setup_DatePreview, DateTime.Today.ToString(DateFormat, CultureInfo.CurrentCulture));
            }
            catch (FormatException)
            {
                return string.Empty;
            }
        }
    }

    public string Summary => string.Format(CultureInfo.CurrentCulture, Strings.Setup_Summary, CompanyName);

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back() => Step--;

    private bool CanGoBack() => Step > 0;

    partial void OnStepChanged(int value) => WizardStep.Update(Steps, value);

    [RelayCommand]
    private async Task NextAsync()
    {
        ClearErrors();
        ErrorMessage = null;
        if (!ValidateStep())
        {
            return;
        }

        if (!IsLastStep)
        {
            Step++;
            OnPropertyChanged(nameof(Summary));
            return;
        }

        var command = new CompleteSetupCommand(
            CompanyName, CurrencyCode, CurrencySymbol, Grouping, TimeZoneId, FiscalYearStartMonth, DateFormat,
            Week.ToWeek(), AdminUsername, AdminDisplayName, AdminPassword, LoadSampleData);
        var result = await SendAsync(command, showErrors: false).ConfigureAwait(true);
        if (result.IsFailure)
        {
            ShowErrors(result.Errors);
            Step = StepFor(result.Errors[0].Code);
            return;
        }

        var login = await Dispatcher.SendAsync(new LoginCommand(AdminUsername, AdminPassword)).ConfigureAwait(true);
        AdminPassword = ConfirmPassword = string.Empty;
        IsDirty = false;
        if (login.IsSuccess)
        {
            await _main.OnSignedInAsync().ConfigureAwait(true);
        }
        else
        {
            _main.ShowLogin();
        }
    }

    protected override string MapField(string field) => field switch
    {
        "Name" => nameof(CompanyName),
        _ => field,
    };

    private static int StepFor(string code) => code switch
    {
        _ when code.Contains("Admin", StringComparison.Ordinal) => 2,
        _ when code.Contains("TimeZone", StringComparison.Ordinal) || code.Contains("Fiscal", StringComparison.Ordinal) || code.Contains("DateFormat", StringComparison.Ordinal) => 1,
        _ => 0,
    };

    private bool ValidateStep()
    {
        var errors = new List<SuperOfc.Domain.Common.Error>();
        switch (Step)
        {
            case 0 when string.IsNullOrWhiteSpace(CompanyName):
                errors.Add(SuperOfc.Domain.Common.Error.Validation(nameof(CompanyName), "Enter the company name."));
                break;
            case 0 when CurrencyCode.Trim().Length != 3:
                errors.Add(SuperOfc.Domain.Common.Error.Validation(nameof(CurrencyCode), "Use a 3-letter currency code such as BDT or USD."));
                break;
            case 2 when AdminPassword != ConfirmPassword:
                errors.Add(SuperOfc.Domain.Common.Error.Validation(nameof(ConfirmPassword), Strings.Setup_PasswordMismatch));
                break;
            case 2 when string.IsNullOrWhiteSpace(AdminDisplayName):
                errors.Add(SuperOfc.Domain.Common.Error.Validation(nameof(AdminDisplayName), "Enter your name."));
                break;
        }

        if (errors.Count > 0)
        {
            ShowErrors(errors);
            return false;
        }

        return true;
    }

    private static RegionInfo? SafeRegion()
    {
        try
        {
            return RegionInfo.CurrentRegion;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
