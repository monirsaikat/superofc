using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SuperOfc.Application.Abstractions;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace SuperOfc.Infrastructure.Updates;

/// <summary>Velopack-based updater (ADR-05). Disabled when the app runs unpacked (developer build).</summary>
public sealed class UpdateService : IUpdateService
{
    private readonly ISettingsStore _settings;
    private readonly IServiceProvider _services;
    private readonly IClock _clock;
    private readonly ILogger<UpdateService> _logger;
    private readonly string _currentVersion;
    private readonly bool _isInstalled;
    private UpdateInfo? _pending;
    private UpdateManager? _manager;

    public UpdateService(ISettingsStore settings, IServiceProvider services, IClock clock, IAppEnvironment environment, ILogger<UpdateService> logger)
    {
        _settings = settings;
        _services = services;
        _clock = clock;
        _logger = logger;
        _currentVersion = environment.AppVersion;
        try
        {
            _isInstalled = new UpdateManager(UpdateSettings.DefaultFeedUrl).IsInstalled;
            InstalledChannel = _isInstalled ? VelopackLocator.Current?.Channel ?? "stable" : "stable";
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or IOException)
        {
            _logger.LogInformation(ex, "Velopack locator unavailable; updates disabled");
            _isInstalled = false;
        }

        Status = new UpdateStatus(_isInstalled ? UpdateState.Idle : UpdateState.NotInstalled, _currentVersion);
    }

    public event EventHandler<UpdateStatus>? StatusChanged;

    public UpdateStatus Status { get; private set; }

    public bool IsInstalled => _isInstalled;

    public string InstalledChannel { get; } = "stable";

    public async Task<UpdateStatus> CheckAsync(bool downloadIfAvailable, CancellationToken cancellationToken = default)
    {
        if (!_isInstalled)
        {
            return Publish(Status with { State = UpdateState.NotInstalled });
        }

        if (Status.State is UpdateState.Checking or UpdateState.Downloading)
        {
            return Status;
        }

        var settings = await _settings.GetAsync<UpdateSettings>(UpdateSettings.Key, cancellationToken).ConfigureAwait(false);
        Publish(new UpdateStatus(UpdateState.Checking, _currentVersion));
        try
        {
            var channel = settings.Channel ?? InstalledChannel;
            // Copies set up before the feed existed saved the old placeholder address.
            var feed = settings.FeedUrl.Contains(".invalid", StringComparison.OrdinalIgnoreCase) ? UpdateSettings.DefaultFeedUrl : settings.FeedUrl;
            _manager = new UpdateManager(CreateSource(feed, channel), new UpdateOptions { ExplicitChannel = channel });
            var info = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
            await _settings.SetAsync(UpdateSettings.Key, settings with { LastCheckUtc = _clock.UtcNow }, cancellationToken).ConfigureAwait(false);
            if (info is null)
            {
                return Publish(new UpdateStatus(UpdateState.UpToDate, _currentVersion));
            }

            var newVersion = info.TargetFullRelease.Version.ToString();
            if (!downloadIfAvailable)
            {
                _pending = info;
                return Publish(new UpdateStatus(UpdateState.Available, _currentVersion, newVersion));
            }

            Publish(new UpdateStatus(UpdateState.Downloading, _currentVersion, newVersion));
            await _manager.DownloadUpdatesAsync(
                info,
                p => Publish(new UpdateStatus(UpdateState.Downloading, _currentVersion, newVersion, p)),
                cancellationToken).ConfigureAwait(false);
            _pending = info;
            _logger.LogInformation("Update {Version} downloaded", newVersion);
            return Publish(new UpdateStatus(UpdateState.ReadyToRestart, _currentVersion, newVersion, 100));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or TaskCanceledException or UriFormatException)
        {
            _logger.LogWarning(ex, "Update check failed");
            return Publish(new UpdateStatus(UpdateState.Failed, _currentVersion, Error: ex.Message));
        }
    }

    public string? ApplyAndRestart()
    {
        if (_manager is null || _pending is null)
        {
            return "No update is ready.";
        }

        var reason = _services.GetServices<IUpdateBlocker>().Select(b => b.BlockReason).FirstOrDefault(r => r is not null);
        if (reason is not null)
        {
            return reason;
        }

        _logger.LogInformation("Applying update {Version} and restarting", _pending.TargetFullRelease.Version);
        _manager.ApplyUpdatesAndRestart(_pending.TargetFullRelease);
        return null;
    }

    /// <summary>GitHub release pages for a github.com repository URL; any other URL is a plain web folder with the feed files.</summary>
    private static IUpdateSource CreateSource(string url, string channel)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            ? new GithubSource(url, accessToken: null, prerelease: channel != "stable")
            : new SimpleWebSource(url);

    private UpdateStatus Publish(UpdateStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
        return status;
    }
}
