namespace SuperOfc.Application.Abstractions;

/// <summary>Machine-wide key/value settings stored in the database (system scope).</summary>
public interface ISettingsStore
{
    Task<T> GetAsync<T>(string key, CancellationToken cancellationToken = default)
        where T : new();

    Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default);
}

public enum UpdateMode
{
    Automatic = 0,
    NotifyOnly = 1,
    Manual = 2,
}

public sealed record UpdateSettings
{
    public const string Key = "Updates";

    /// <summary>Placeholder feed until the hosting decision is made (docs/10, Q20).</summary>
    public const string DefaultFeedUrl = "https://updates.superofc.invalid/superofc";

    public UpdateMode Mode { get; init; } = UpdateMode.Automatic;

    public string Channel { get; init; } = "stable";

    public string FeedUrl { get; init; } = DefaultFeedUrl;

    public DateTime? LastCheckUtc { get; init; }
}

public enum UpdateState
{
    NotInstalled,
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    ReadyToRestart,
    Failed,
}

public sealed record UpdateStatus(UpdateState State, string CurrentVersion, string? NewVersion = null, int Progress = 0, string? Error = null);

/// <summary>Something that can prevent an update restart right now (payroll run, unsaved edits, device sync).</summary>
public interface IUpdateBlocker
{
    /// <summary>Null when not blocking; otherwise a short reason for the user.</summary>
    string? BlockReason { get; }
}

public interface IUpdateService
{
    event EventHandler<UpdateStatus>? StatusChanged;

    UpdateStatus Status { get; }

    bool IsInstalled { get; }

    Task<UpdateStatus> CheckAsync(bool downloadIfAvailable, CancellationToken cancellationToken = default);

    /// <summary>Returns a reason if blocked; otherwise restarts the app into the new version.</summary>
    string? ApplyAndRestart();
}
