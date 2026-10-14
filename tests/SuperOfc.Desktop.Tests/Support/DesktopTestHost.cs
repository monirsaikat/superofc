using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using SuperOfc.Application;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Setup;
using SuperOfc.Desktop.Hosting;
using SuperOfc.Desktop.Services;
using SuperOfc.Domain.Organization;
using SuperOfc.Infrastructure;
using SuperOfc.Infrastructure.Persistence;
using SuperOfc.Infrastructure.Services;

namespace SuperOfc.Desktop.Tests.Support;

/// <summary>Real application stack plus desktop view models, with dialogs replaced by a substitute.</summary>
public sealed class DesktopTestHost : IAsyncDisposable
{
    public const string Password = "Admin12345";

    private DesktopTestHost(string root, ServiceProvider services, IDialogService dialogs, IFileDialogService files)
    {
        Root = root;
        Services = services;
        Dialogs = dialogs;
        Files = files;
    }

    public string Root { get; }

    public ServiceProvider Services { get; }

    public IDialogService Dialogs { get; }

    public IFileDialogService Files { get; }

    public T Get<T>()
        where T : notnull
        => Services.GetRequiredService<T>();

    public static async Task<DesktopTestHost> CreateAsync(bool completeSetup, Action<IServiceCollection>? configure = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "superofc-desktop-tests", Guid.NewGuid().ToString("N"));
        var dialogs = Substitute.For<IDialogService>();
        var files = Substitute.For<IFileDialogService>();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        configure?.Invoke(services);
        services.AddApplication();
        services.AddInfrastructure(new AppEnvironment(root));
        services.AddDesktop();
        services.AddSingleton(dialogs);
        services.AddSingleton(files);
        services.AddSingleton(Substitute.For<INotificationService>());
        var provider = services.BuildServiceProvider();
        var init = await provider.GetRequiredService<DatabaseInitializer>().InitializeAsync();
        init.State.Should().Be(DatabaseState.Ready);

        if (completeSetup)
        {
            var result = await provider.GetRequiredService<SuperOfc.Application.Common.IDispatcher>().SendAsync(new CompleteSetupCommand(
                "Desk Co", "BDT", "Tk", NumberGrouping.SouthAsian, "Bangladesh Standard Time", 7, "dd MMM yyyy",
                WorkingWeek.Default, "admin", "Admin User", Password, false));
            result.IsSuccess.Should().BeTrue(result.ToString());
            provider.GetRequiredService<IUserSession>().SignOut();
        }

        await provider.GetRequiredService<ICompanyContext>().RefreshAsync();
        return new DesktopTestHost(root, provider, dialogs, files);
    }

    /// <summary>Signs in through the login screen and returns the shell.</summary>
    public async Task<ViewModels.ShellViewModel> SignInAsync(string user = "admin", string password = Password)
    {
        var main = Get<ViewModels.MainViewModel>();
        main.ShowLogin();
        var login = (ViewModels.LoginViewModel)main.CurrentScreen!;
        login.Username = user;
        login.Password = password;
        await login.SignInCommand.ExecuteAsync(null);
        login.ErrorMessage.Should().BeNull();
        return (ViewModels.ShellViewModel)main.CurrentScreen!;
    }

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }
}
