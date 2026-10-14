using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Auth;
using SuperOfc.Application.Common;
using SuperOfc.Application.Setup;
using SuperOfc.Desktop.Mvvm;
using SuperOfc.Desktop.Resources;
using SuperOfc.Desktop.Services;
using SuperOfc.Infrastructure.Persistence;

namespace SuperOfc.Desktop.ViewModels;

/// <summary>Owns the top-level screen: startup, setup wizard, login or the main shell, plus the lock overlay.</summary>
public sealed partial class MainViewModel : ViewModelBase
{
    private readonly IServiceProvider _services;
    private readonly IDispatcher _dispatcher;
    private readonly IUserSession _session;
    private readonly ICompanyContext _company;
    private readonly IPolicyProvider _policies;
    private readonly IClock _clock;
    private readonly ILogger<MainViewModel> _logger;
    private readonly DispatcherTimer _idleTimer;
    private DateTime _lastActivityUtc = DateTime.UtcNow;

    public MainViewModel(
        IServiceProvider services,
        IDispatcher dispatcher,
        IUserSession session,
        ICompanyContext company,
        IPolicyProvider policies,
        IClock clock,
        INotificationService notifications,
        IBusyService busy,
        AppReadySignal ready,
        ILogger<MainViewModel> logger)
    {
        _services = services;
        _dispatcher = dispatcher;
        _session = session;
        _company = company;
        _policies = policies;
        _clock = clock;
        _logger = logger;
        Ready = ready;
        InfoBars = notifications.InfoBars;
        busy.Changed += (_, _) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => IsBusy = busy.IsBusy);
        company.Changed += (_, _) => OnPropertyChanged(nameof(WindowTitle));
        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _idleTimer.Tick += async (_, _) => await CheckIdleAsync().ConfigureAwait(true);
        CurrentScreen = new StartupViewModel(this);
    }

    public AppReadySignal Ready { get; }

    public ObservableCollection<InfoBarItem> InfoBars { get; }

    public string WindowTitle => _company.Company is { } c ? $"{Strings.App_Name} · {c.Name}" : Strings.App_Name;

    [ObservableProperty]
    public partial object? CurrentScreen { get; set; }

    [ObservableProperty]
    public partial LockViewModel? Lock { get; set; }

    public StartupViewModel? Startup => CurrentScreen as StartupViewModel;

    /// <summary>Runs the startup sequence: data checks and migrations, then setup or sign-in.</summary>
    public async Task StartAsync()
    {
        var startup = new StartupViewModel(this) { Message = Strings.App_CheckingData };
        CurrentScreen = startup;
        DatabaseInitResult init;
        try
        {
            init = await Task.Run(() => _services.GetRequiredService<DatabaseInitializer>().InitializeAsync()).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            _logger.LogError(ex, "Data folder could not be opened");
            init = new DatabaseInitResult(DatabaseState.Corrupt, ex.Message);
        }

        if (init.State != DatabaseState.Ready)
        {
            startup.ShowError(init);
            return;
        }

        await _company.RefreshAsync().ConfigureAwait(true);
        Ready.SetReady();
        await ShowEntryScreenAsync().ConfigureAwait(true);
    }

    public async Task ShowEntryScreenAsync()
    {
        var status = await _dispatcher.SendAsync(new GetSetupStatusQuery()).ConfigureAwait(true);
        if (status.IsSuccess && !status.Value.IsSetupComplete)
        {
            CurrentScreen = _services.GetRequiredService<SetupWizardViewModel>();
            return;
        }

        ShowLogin();
    }

    public void ShowLogin()
    {
        var login = _services.GetRequiredService<LoginViewModel>();
        login.CompanyName = _company.Company?.Name ?? Strings.App_Name;
        CurrentScreen = login;
    }

    /// <summary>Called by the login and setup screens after a successful sign-in.</summary>
    public async Task OnSignedInAsync()
    {
        _lastActivityUtc = _clock.UtcNow;
        var shell = _services.GetRequiredService<ShellViewModel>();
        CurrentScreen = shell;
        await shell.ActivateAsync().ConfigureAwait(true);
        _idleTimer.Start();
        Ready.SetSignedIn();
    }

    [RelayCommand]
    public async Task SignOutAsync()
    {
        var navigation = _services.GetRequiredService<IAppNavigator>();
        if (navigation.Current is INavigationAware aware && !await aware.CanNavigateAwayAsync().ConfigureAwait(true))
        {
            return;
        }

        await _dispatcher.SendAsync(new LogoutCommand()).ConfigureAwait(true);
        navigation.Reset();
        _idleTimer.Stop();
        Lock = null;
        ShowLogin();
    }

    [RelayCommand]
    public void LockNow()
    {
        if (_session.IsAuthenticated && Lock is null)
        {
            Lock = new LockViewModel(this, _dispatcher, _session.DisplayName);
        }
    }

    public void Unlocked()
    {
        Lock = null;
        _lastActivityUtc = _clock.UtcNow;
    }

    /// <summary>Called by the window on any keyboard or mouse input.</summary>
    public void RegisterActivity() => _lastActivityUtc = _clock.UtcNow;

    [RelayCommand]
    private async Task RefreshPageAsync()
    {
        if (Lock is null && _services.GetRequiredService<IAppNavigator>().Current is IRefreshable page)
        {
            await page.RefreshAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task NewOnPageAsync()
    {
        if (Lock is null && _services.GetRequiredService<IAppNavigator>().Current is ICreatesNew page)
        {
            await page.CreateNewAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task GoBackAsync()
    {
        if (Lock is null)
        {
            await _services.GetRequiredService<IAppNavigator>().GoBackAsync().ConfigureAwait(true);
        }
    }

    private async Task CheckIdleAsync()
    {
        if (!_session.IsAuthenticated || Lock is not null)
        {
            return;
        }

        var policy = await _policies.GetSnapshotAsync(DateOnly.FromDateTime(_clock.UtcNow)).ConfigureAwait(true);
        if (policy.General.IdleLockMinutes > 0 && _clock.UtcNow - _lastActivityUtc > TimeSpan.FromMinutes(policy.General.IdleLockMinutes))
        {
            LockNow();
        }
    }
}

/// <summary>Lets background jobs wait until the database is ready and a user has signed in.</summary>
public sealed class AppReadySignal
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _signedIn = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WhenReady => _ready.Task;

    public Task WhenSignedIn => _signedIn.Task;

    public void SetReady() => _ready.TrySetResult();

    public void SetSignedIn() => _signedIn.TrySetResult();
}
