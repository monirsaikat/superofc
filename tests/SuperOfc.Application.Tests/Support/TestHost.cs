using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SuperOfc.Application;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Common;
using SuperOfc.Application.Setup;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Organization;
using SuperOfc.Domain.Security;
using SuperOfc.Infrastructure;
using SuperOfc.Infrastructure.Persistence;
using SuperOfc.Infrastructure.Services;

namespace SuperOfc.Application.Tests.Support;

public sealed class FakeClock : IClock
{
    public DateTime UtcNow { get; set; } = new(2026, 3, 15, 4, 0, 0, DateTimeKind.Utc);

    public void Advance(TimeSpan by) => UtcNow += by;
}

/// <summary>Full application stack on a temporary SQLite file with a fake clock.</summary>
public sealed class TestHost : IAsyncDisposable
{
    public const string AdminPassword = "Admin12345";

    private TestHost(string root, ServiceProvider provider, FakeClock clock)
    {
        Root = root;
        Services = provider;
        Clock = clock;
    }

    public string Root { get; }

    public ServiceProvider Services { get; }

    public FakeClock Clock { get; }

    public IDispatcher Dispatcher => Services.GetRequiredService<IDispatcher>();

    public IUserSession Session => Services.GetRequiredService<IUserSession>();

    public Guid CompanyId => Session.CompanyId;

    public static async Task<TestHost> CreateAsync(bool completeSetup = true, Action<IServiceCollection>? configure = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "superofc-tests", Guid.NewGuid().ToString("N"));
        var clock = new FakeClock();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<IClock>(clock);
        services.AddApplication();
        services.AddInfrastructure(new AppEnvironment(root));
        configure?.Invoke(services);
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        var init = await provider.GetRequiredService<DatabaseInitializer>().InitializeAsync();
        init.State.Should().Be(DatabaseState.Ready, init.Message);

        var host = new TestHost(root, provider, clock);
        if (completeSetup)
        {
            await host.CompleteSetupAsync();
        }

        return host;
    }

    public async Task CompleteSetupAsync(bool sampleData = false)
    {
        var result = await Dispatcher.SendAsync(new CompleteSetupCommand(
            "Test Co", "BDT", "Tk", NumberGrouping.SouthAsian, "Bangladesh Standard Time", 7, "dd MMM yyyy",
            WorkingWeek.Default, "admin", "Administrator", AdminPassword, sampleData));
        result.IsSuccess.Should().BeTrue(result.ToString());
        await SignInAsAsync(Role.Admin);
    }

    /// <summary>Signs in a user with the given role, creating it if needed.</summary>
    public async Task SignInAsAsync(Role role)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IAppDbContext>();
        var username = role == Role.Admin ? "admin" : role.ToString().ToLowerInvariant();
        var user = db.Users.FirstOrDefault(u => u.Username == username);
        if (user is null)
        {
            user = new SuperOfc.Domain.Security.User
            {
                Id = Guid.CreateVersion7(),
                CompanyId = CompanyId,
                Username = username,
                DisplayName = role.ToString(),
                PasswordHash = scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Hash(AdminPassword),
                Role = role,
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
        }

        Session.SignIn(user);
    }

    public async Task<T> SendOkAsync<T>(IRequest<T> request)
    {
        var result = await Dispatcher.SendAsync(request);
        result.IsSuccess.Should().BeTrue(result.ToString());
        return result.Value;
    }

    public async Task<Error> SendFailAsync<T>(IRequest<T> request)
    {
        var result = await Dispatcher.SendAsync(request);
        result.IsFailure.Should().BeTrue("the request was expected to fail");
        return result.FirstError!;
    }

    /// <summary>Runs a block with a fresh scoped DbContext.</summary>
    public async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    /// <summary>Runs a block with services from a fresh scope (for example an application service such as the attendance engine).</summary>
    public async Task<T> WithScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
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
            // Temp folder cleanup is best effort.
        }
    }
}
