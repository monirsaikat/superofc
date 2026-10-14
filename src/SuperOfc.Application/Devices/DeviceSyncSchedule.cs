namespace SuperOfc.Application.Devices;

/// <summary>
/// Decides when each device syncs automatically (docs/06 Auto-sync scheduler). After a success the next sync is one
/// interval later; after failures it retries after 1, 5 and then every 15 minutes (never later than the interval).
/// Pure logic with an injected time so tests can drive it.
/// </summary>
public sealed class DeviceSyncSchedule
{
    public static readonly IReadOnlyList<TimeSpan> Backoff = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)];

    private readonly Dictionary<Guid, State> _states = [];

    public sealed record State(DateTime NextDueUtc, int Failures);

    public State? Get(Guid deviceId) => _states.GetValueOrDefault(deviceId);

    /// <summary>The devices due at <paramref name="nowUtc"/>. Devices seen for the first time are due right away.</summary>
    public IReadOnlyList<Guid> Due(IEnumerable<Guid> enabledDevices, DateTime nowUtc)
    {
        var enabled = enabledDevices.ToHashSet();
        foreach (var gone in _states.Keys.Where(k => !enabled.Contains(k)).ToList())
        {
            _states.Remove(gone);
        }

        return enabled.Where(id => !_states.TryGetValue(id, out var s) || s.NextDueUtc <= nowUtc).ToList();
    }

    /// <summary>Records a result and returns the number of failures in a row (0 after a success).</summary>
    public int Record(Guid deviceId, bool succeeded, DateTime nowUtc, TimeSpan interval)
    {
        if (succeeded)
        {
            _states[deviceId] = new State(nowUtc + interval, 0);
            return 0;
        }

        var failures = (_states.GetValueOrDefault(deviceId)?.Failures ?? 0) + 1;
        var wait = Backoff[Math.Min(failures, Backoff.Count) - 1];
        _states[deviceId] = new State(nowUtc + (wait < interval ? wait : interval), failures);
        return failures;
    }

    /// <summary>Makes every device due now (after "Sync all", the interval restarts from the manual sync).</summary>
    public void Reset() => _states.Clear();
}
