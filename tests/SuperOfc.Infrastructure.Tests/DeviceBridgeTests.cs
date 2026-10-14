using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using SuperOfc.Application.Abstractions;
using SuperOfc.DeviceBridge.Contracts;
using SuperOfc.Infrastructure.Devices;
using Xunit.Abstractions;

namespace SuperOfc.Infrastructure.Tests;

/// <summary>Runs the real net48 bridge in fake mode (docs/06 Testing, P3-T6).</summary>
public sealed class DeviceBridgeTests(ITestOutputHelper output) : IDisposable
{
    private static readonly DeviceEndpoint Fast = new("10.0.0.1", 4370, 0);
    private static readonly DeviceEndpoint Slow = new("10.0.0.2", 4370, 0);
    private static readonly DeviceEndpoint Keyed = new("10.0.0.3", 4370, 1234);

    private readonly string _session = WriteSession();

    public void Dispose() => File.Delete(_session);

    [Fact]
    public async Task Ping_reports_fake_protocol()
    {
        await using var client = CreateClient();
        var ping = await client.PingAsync(CancellationToken.None);
        ping.Protocol.Should().Be("fake");
        ping.SdkAvailable.Should().BeTrue();
    }

    [Fact]
    public async Task Test_connection_reads_device_info_and_rejects_wrong_key()
    {
        await using var client = CreateClient();
        var info = await client.TestConnectionAsync(Fast, CancellationToken.None);
        info.SerialNumber.Should().Be("FAST0001");
        info.LogCount.Should().Be(10_000);

        var wrongKey = () => client.TestConnectionAsync(Keyed with { CommKey = 1 }, CancellationToken.None);
        (await wrongKey.Should().ThrowAsync<DeviceGatewayException>()).Which.Code.Should().Be(BridgeErrors.WrongCommKey);

        var missing = () => client.TestConnectionAsync(new DeviceEndpoint("10.9.9.9", 4370, 0), CancellationToken.None);
        (await missing.Should().ThrowAsync<DeviceGatewayException>()).Which.IsUnreachable.Should().BeTrue();
    }

    [Fact]
    public async Task Pull_of_10000_logs_streams_in_batches_under_10_seconds()
    {
        await using var client = CreateClient();
        await client.PingAsync(CancellationToken.None); // start-up is not part of the pull budget

        var watch = Stopwatch.StartNew();
        var batches = 0;
        var total = 0;
        await foreach (var batch in client.PullLogsAsync(Fast, null, null, CancellationToken.None))
        {
            batches++;
            total += batch.Logs.Count;
            batch.Logs.Count.Should().BeLessThanOrEqualTo(500);
        }

        watch.Stop();
        output.WriteLine($"10000 logs in {batches} batches: {watch.ElapsedMilliseconds} ms");
        total.Should().Be(10_000);
        batches.Should().BeGreaterThanOrEqualTo(20);
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Pull_honours_the_from_time()
    {
        await using var client = CreateClient();
        var from = new DateTime(2026, 1, 1, 8, 0, 0).AddMinutes(9_000);
        var total = 0;
        await foreach (var batch in client.PullLogsAsync(Fast, from, null, CancellationToken.None))
        {
            total += batch.Logs.Count;
        }

        total.Should().Be(1_000);
    }

    [Fact]
    public async Task Killed_bridge_fails_the_pull_and_the_next_call_starts_a_new_one()
    {
        await using var client = CreateClient();
        await client.PingAsync(CancellationToken.None);
        var firstPid = client.BridgeProcessId!.Value;

        var received = 0;
        var pull = async () =>
        {
            await foreach (var batch in client.PullLogsAsync(Slow, null, null, CancellationToken.None))
            {
                received += batch.Logs.Count;
                if (received == 500)
                {
                    Process.GetProcessById(firstPid).Kill();
                }
            }
        };

        (await pull.Should().ThrowAsync<DeviceGatewayException>()).Which.Code.Should().Be(BridgeErrors.Failed);
        received.Should().BeLessThan(5_000);

        var info = await client.TestConnectionAsync(Fast, CancellationToken.None);
        info.SerialNumber.Should().Be("FAST0001");
        client.BridgeProcessId.Should().NotBe(firstPid);
    }

    [Fact]
    public async Task Cancelling_a_pull_leaves_the_bridge_usable()
    {
        await using var client = CreateClient();
        using var cts = new CancellationTokenSource();
        var pull = async () =>
        {
            await foreach (var _ in client.PullLogsAsync(Slow, null, null, cts.Token))
            {
                await cts.CancelAsync();
            }
        };

        await pull.Should().ThrowAsync<OperationCanceledException>();
        var pid = client.BridgeProcessId;
        (await client.GetTimeAsync(Fast, CancellationToken.None)).Should().BeCloseTo(DateTime.Now, TimeSpan.FromMinutes(1));
        client.BridgeProcessId.Should().Be(pid);
    }

    [Fact]
    public async Task Users_and_templates_round_trip()
    {
        await using var client = CreateClient();
        await client.SetUserAsync(Keyed, new DeviceUser { DeviceUserId = "77", Name = "Test User" }, CancellationToken.None);
        await client.SetTemplatesAsync(Keyed, "77", [new FingerTemplate { FingerIndex = 6, Data = "QUJD" }], CancellationToken.None);

        var users = await client.GetUsersAsync(Keyed, CancellationToken.None);
        users.Should().ContainSingle(u => u.DeviceUserId == "77").Which.FingerCount.Should().Be(1);
        var templates = await client.GetTemplatesAsync(Keyed, "77", CancellationToken.None);
        templates.Should().ContainSingle().Which.Data.Should().Be("QUJD");

        await client.DeleteUserAsync(Keyed, "77", CancellationToken.None);
        (await client.GetUsersAsync(Keyed, CancellationToken.None)).Should().NotContain(u => u.DeviceUserId == "77");
    }

    [Fact]
    public async Task Clock_offset_is_reported_and_set_time_fixes_it()
    {
        await using var client = CreateClient();
        var before = await client.GetTimeAsync(Keyed, CancellationToken.None);
        (before - DateTime.Now).TotalMinutes.Should().BeApproximately(7, 1);

        var after = await client.SetTimeAsync(Keyed, DateTime.Now, CancellationToken.None);
        after.Should().BeCloseTo(DateTime.Now, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Missing_bridge_reports_a_readable_error()
    {
        await using var client = new DeviceBridgeClient(new DeviceBridgeOptions { BridgePath = Path.Combine(Path.GetTempPath(), "nope.exe"), FakeSessionPath = _session }, NullLogger<DeviceBridgeClient>.Instance);
        var ping = () => client.PingAsync(CancellationToken.None);
        (await ping.Should().ThrowAsync<DeviceGatewayException>()).Which.Code.Should().Be(BridgeErrors.SdkMissing);
    }

    // CI machines can take well over the 10 s production limit to start the bridge the first time (antivirus scan).
    private DeviceBridgeClient CreateClient()
        => new(new DeviceBridgeOptions { BridgePath = Path.Combine(AppContext.BaseDirectory, "bridge", "SuperOfc.DeviceBridge.exe"), FakeSessionPath = _session, ConnectTimeout = TimeSpan.FromSeconds(60) }, NullLogger<DeviceBridgeClient>.Instance);

    private static string WriteSession()
    {
        var path = Path.Combine(Path.GetTempPath(), "superofc-fake-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """
        {
          "devices": [
            { "ip": "10.0.0.1", "serial": "FAST0001", "generateLogs": { "count": 10000, "start": "2026-01-01T08:00:00", "users": 50, "minutesApart": 1 } },
            { "ip": "10.0.0.2", "serial": "SLOW0001", "batchDelayMs": 300, "generateLogs": { "count": 10000, "users": 50 } },
            { "ip": "10.0.0.3", "serial": "KEY00001", "commKey": 1234, "clockOffsetMinutes": 7 }
          ]
        }
        """);
        return path;
    }
}
