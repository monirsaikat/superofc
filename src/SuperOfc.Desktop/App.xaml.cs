using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SuperOfc.Desktop.Hosting;
using SuperOfc.Desktop.Services;
using SuperOfc.Desktop.ViewModels;
using SuperOfc.Desktop.Views;
using SuperOfc.Infrastructure.Services;

namespace SuperOfc.Desktop;

public partial class App : System.Windows.Application
{
    private IHost? _host;
    private ILogger<App>? _logger;

    public IServiceProvider Services => _host?.Services ?? throw new InvalidOperationException("The host is not started.");

    protected override void OnStartup(StartupEventArgs e)
    {
        var stopwatch = Stopwatch.StartNew();
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        var environment = new AppEnvironment();
        environment.EnsureDirectories();
        _host = AppHost.Build(environment);
        _logger = Services.GetRequiredService<ILogger<App>>();
        _logger.LogInformation("SuperOfc {Version} starting (data: {Root})", environment.AppVersion, environment.Root);
        var logger = _logger;
        SuperOfc.Desktop.Resources.Strings.MissingTranslation = (textName, culture) => logger.LogWarning("No {Culture} text for {TextName}; showing English", culture, textName);

        // Translators can try a language before it is offered in Settings (P8-T4), e.g. SUPEROFC_UI_CULTURE=bn.
        if (Environment.GetEnvironmentVariable("SUPEROFC_UI_CULTURE") is { Length: > 0 } uiCulture)
        {
            SuperOfc.Desktop.Resources.Strings.Culture = System.Globalization.CultureInfo.GetCultureInfo(uiCulture);
        }

        Services.GetRequiredService<IAppThemeService>().ApplySaved();
        var storage = Services.GetRequiredService<SuperOfc.Application.Abstractions.IFileStorage>();
        Mvvm.ImagePathConverter.Resolve = storage.GetFullPath;
        var companyContext = Services.GetRequiredService<SuperOfc.Application.Abstractions.ICompanyContext>();
        Mvvm.MoneyConverter.Current = () => SuperOfc.Application.Common.MoneyFormatter.For(companyContext.Company);
        Mvvm.MoneyConverter.DateFormat = () => companyContext.Company?.DateFormat ?? "dd MMM yyyy";
        var main = Services.GetRequiredService<MainViewModel>();
        var window = new MainWindow(main, Services);
        MainWindow = window;
        window.Show();
        _logger.LogInformation("Shell visible after {ElapsedMs} ms", stopwatch.ElapsedMilliseconds);

        _ = StartAsync(main);
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        _logger?.LogInformation("SuperOfc exiting");
        if (_host is not null)
        {
            try
            {
                await _host.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            }
            finally
            {
                _host.Dispose();
            }
        }

        Serilog.Log.CloseAndFlush();
        base.OnExit(e);
    }

    public void BringToFront()
    {
        if (MainWindow is null)
        {
            return;
        }

        if (MainWindow.WindowState == WindowState.Minimized)
        {
            MainWindow.WindowState = WindowState.Normal;
        }

        MainWindow.Activate();
        MainWindow.Topmost = true;
        MainWindow.Topmost = false;
        MainWindow.Focus();
    }

    /// <summary>Restarts the app (after restore). The new process waits for this one to exit.</summary>
    public void Restart()
    {
        SingleInstance.Restart();
        Shutdown();
    }

    private async Task StartAsync(MainViewModel main)
    {
        await _host!.StartAsync().ConfigureAwait(true);
        await main.StartAsync().ConfigureAwait(true);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogCritical(e.Exception, "Unhandled UI exception");
        e.Handled = true;
        ErrorWindow.Show(e.Exception);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _logger?.LogCritical(e.Exception, "Unobserved task exception");
        e.SetObserved();
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var details = new StringBuilder().Append(e.ExceptionObject).ToString();
        _logger?.LogCritical("Fatal unhandled exception: {Details}", details);
        Serilog.Log.CloseAndFlush();
    }
}
