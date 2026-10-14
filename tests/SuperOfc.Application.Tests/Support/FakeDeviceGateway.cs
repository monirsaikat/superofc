using System.Runtime.CompilerServices;
using SuperOfc.Application.Abstractions;
using SuperOfc.DeviceBridge.Contracts;

namespace SuperOfc.Application.Tests.Support;

/// <summary>In-memory devices for application tests (the bridge itself is covered by DeviceBridgeTests).</summary>
public sealed class FakeDeviceGateway(FakeClock clock) : IDeviceGateway
{
    public sealed class Device
    {
        public required string Ip { get; init; }

        public string Serial { get; init; } = "SN-TEST";

        public int CommKey { get; init; }

        public int ClockOffsetMinutes { get; set; }

        public List<DeviceLog> Logs { get; } = [];

        public List<DeviceUser> Users { get; } = [];

        public Dictionary<string, List<FingerTemplate>> Templates { get; } = [];

        /// <summary>Throw "unreachable" after this many batches of a pull (null = never).</summary>
        public int? FailAfterBatches { get; set; }

        public bool Offline { get; set; }

        public List<DateTime?> PullsFrom { get; } = [];

        public void Log(string user, DateTime local, int status = 0) => Logs.Add(new DeviceLog { DeviceUserId = user, TimestampLocal = local, VerifyMode = 1, InOutMode = status });
    }

    public Dictionary<string, Device> Devices { get; } = new(StringComparer.Ordinal);

    public TimeZoneInfo Zone { get; set; } = TimeZoneInfo.FindSystemTimeZoneById("Bangladesh Standard Time");

    public Device Add(string ip, string serial = "SN-TEST", int commKey = 0)
    {
        var device = new Device { Ip = ip, Serial = serial, CommKey = commKey };
        Devices[ip] = device;
        return device;
    }

    public Task<PingResult> PingAsync(CancellationToken cancellationToken) => Task.FromResult(new PingResult { Protocol = "test", SdkAvailable = true });

    public Task<DeviceInfo> TestConnectionAsync(DeviceEndpoint device, CancellationToken cancellationToken)
    {
        var d = Find(device);
        return Task.FromResult(new DeviceInfo { SerialNumber = d.Serial, DeviceName = "Test", UserCount = d.Users.Count, LogCount = d.Logs.Count, DeviceTime = Now(d) });
    }

    public Task<DateTime> GetTimeAsync(DeviceEndpoint device, CancellationToken cancellationToken) => Task.FromResult(Now(Find(device)));

    public Task<DateTime> SetTimeAsync(DeviceEndpoint device, DateTime local, CancellationToken cancellationToken)
    {
        var d = Find(device);
        d.ClockOffsetMinutes = (int)Math.Round((local - Now(d).AddMinutes(-d.ClockOffsetMinutes)).TotalMinutes);
        return Task.FromResult(Now(d));
    }

    public async IAsyncEnumerable<LogBatch> PullLogsAsync(DeviceEndpoint device, DateTime? fromLocal, DateTime? toLocal, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var d = Find(device);
        d.PullsFrom.Add(fromLocal);
        var logs = d.Logs.Where(l => (fromLocal is null || l.TimestampLocal >= fromLocal) && (toLocal is null || l.TimestampLocal <= toLocal)).ToList();
        var sent = 0;
        foreach (var chunk in logs.Chunk(500))
        {
            if (d.FailAfterBatches is { } n && sent >= n)
            {
                throw new DeviceGatewayException(BridgeErrors.Unreachable, "Connection lost.");
            }

            await Task.Yield();
            sent++;
            yield return new LogBatch { Logs = [.. chunk], SerialNumber = d.Serial, ReadSoFar = sent * 500 };
        }
    }

    public Task<IReadOnlyList<DeviceUser>> GetUsersAsync(DeviceEndpoint device, CancellationToken cancellationToken)
    {
        var d = Find(device);
        return Task.FromResult<IReadOnlyList<DeviceUser>>(d.Users.Select(u => new DeviceUser
        {
            DeviceUserId = u.DeviceUserId, Name = u.Name, Enabled = u.Enabled, FingerCount = d.Templates.TryGetValue(u.DeviceUserId, out var t) ? t.Count : 0,
        }).ToList());
    }

    public Task SetUserAsync(DeviceEndpoint device, DeviceUser user, CancellationToken cancellationToken)
    {
        var d = Find(device);
        d.Users.RemoveAll(u => u.DeviceUserId == user.DeviceUserId);
        d.Users.Add(user);
        return Task.CompletedTask;
    }

    public Task DeleteUserAsync(DeviceEndpoint device, string deviceUserId, CancellationToken cancellationToken)
    {
        var d = Find(device);
        d.Users.RemoveAll(u => u.DeviceUserId == deviceUserId);
        d.Templates.Remove(deviceUserId);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<FingerTemplate>> GetTemplatesAsync(DeviceEndpoint device, string deviceUserId, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<FingerTemplate>>(Find(device).Templates.TryGetValue(deviceUserId, out var t) ? [.. t] : []);

    public Task SetTemplatesAsync(DeviceEndpoint device, string deviceUserId, IReadOnlyList<FingerTemplate> templates, CancellationToken cancellationToken)
    {
        Find(device).Templates[deviceUserId] = [.. templates];
        return Task.CompletedTask;
    }

    private DateTime Now(Device d) => TimeZoneInfo.ConvertTimeFromUtc(clock.UtcNow, Zone).AddMinutes(d.ClockOffsetMinutes);

    private Device Find(DeviceEndpoint endpoint)
    {
        if (!Devices.TryGetValue(endpoint.Ip, out var d) || d.Offline)
        {
            throw new DeviceGatewayException(BridgeErrors.Unreachable, $"No device answered at {endpoint.Ip}.");
        }

        return d.CommKey == endpoint.CommKey ? d : throw new DeviceGatewayException(BridgeErrors.WrongCommKey, "Wrong key.");
    }
}
