using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;
using SuperOfc.Application;
using SuperOfc.Application.Abstractions;
using SuperOfc.Desktop.Mvvm;
using SuperOfc.Desktop.Services;
using SuperOfc.Desktop.ViewModels;
using SuperOfc.Infrastructure;
using SuperOfc.Infrastructure.Services;
using Wpf.Ui;

namespace SuperOfc.Desktop.Hosting;

/// <summary>Composition root: the only place where layers are wired together.</summary>
public static class AppHost
{
    public static IHost Build(AppEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        Directory.CreateDirectory(environment.LogDirectory);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(environment.LogDirectory, "app-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                restrictedToMinimumLevel: LogEventLevel.Information,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .WriteTo.Debug()
            .CreateLogger();

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = AppContext.BaseDirectory });
        builder.Services.AddSerilog(dispose: true);
        builder.Services.AddApplication();
        builder.Services.AddInfrastructure(environment);
        builder.Services.AddDesktop();
        return builder.Build();
    }

    public static IServiceCollection AddDesktop(this IServiceCollection services)
    {
        services.AddSingleton<IContentDialogService, ContentDialogService>();
        services.AddSingleton<ISnackbarService, SnackbarService>();
        services.AddSingleton<IAppNavigator, AppNavigator>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<INotificationService, NotificationService>();
        services.AddSingleton<IBusyService, BusyService>();
        services.AddSingleton<IAppThemeService, AppThemeService>();
        services.AddSingleton<IFileDialogService, FileDialogService>();
        services.AddSingleton<UnsavedChangesTracker>();
        services.AddSingleton<IUpdateBlocker>(sp => sp.GetRequiredService<UnsavedChangesTracker>());
        services.AddSingleton<PageServices>();
        services.AddSingleton<AppReadySignal>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<ShellViewModel>();
        services.AddHostedService<BackgroundJobs>();
        services.AddSingleton<DeviceAutoSync>();
        services.AddHostedService(sp => sp.GetRequiredService<DeviceAutoSync>());

        // Every other view model is transient and resolved by type during navigation.
        foreach (var type in typeof(AppHost).Assembly.GetTypes()
                     .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ViewModelBase).IsAssignableFrom(t))
                     .Where(t => t != typeof(MainViewModel) && t != typeof(ShellViewModel) && t != typeof(StartupViewModel) && t != typeof(LockViewModel)))
        {
            services.AddTransient(type);
        }

        // Module tabs on the employee page, in display order.
        ViewModels.Employees.EmployeeTabs.Register<ViewModels.Payroll.EmployeeSalaryTabViewModel>();
        ViewModels.Employees.EmployeeTabs.Register<ViewModels.Employees.EmployeeAttendanceTabViewModel>();
        ViewModels.Employees.EmployeeTabs.Register<ViewModels.Employees.EmployeeLeaveTabViewModel>();
        ViewModels.Employees.EmployeeTabs.Register<ViewModels.Loans.EmployeeLoansTabViewModel>();
        ViewModels.Employees.EmployeeTabs.Register<ViewModels.Tax.EmployeeTaxTabViewModel>();
        ViewModels.Employees.EmployeeTabs.Register<ViewModels.Letters.EmployeeLettersTabViewModel>();
        return services;
    }
}
