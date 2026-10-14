using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SuperOfc.Application.Abstractions;
using SuperOfc.DeviceBridge.Contracts;

namespace SuperOfc.Infrastructure.Devices;

public sealed class DeviceBridgeOptions
{
    /// <summary>Environment variable that points at a fake device session file (tests, demos, no hardware).</summary>
    public const string FakeEnvironmentVariable = "SUPEROFC_FAKE_DEVICES";

    public string BridgePath { get; set; } = Path.Combine(AppContext.BaseDirectory, "bridge", "SuperOfc.DeviceBridge.exe");

    public string? FakeSessionPath { get; set; } = Environment.GetEnvironmentVariable(FakeEnvironmentVariable);

    /// <summary>The first start after install or update can be slow while antivirus scans the bridge.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan PullTimeout { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan OperationTimeout { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// <see cref="IDeviceGateway"/> over the 32-bit bridge process (ADR-06). The bridge starts on first use, responses are
/// routed by message id, and any pipe failure or timeout kills the bridge so the next call starts a fresh one.
/// </summary>
public sealed class DeviceBridgeClient(DeviceBridgeOptions options, ILogger<DeviceBridgeClient> logger) : IDeviceGateway, IAsyncDisposable, IDisposable
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string StoppedMessage = "The device bridge stopped unexpectedly. Try again.";

    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, Channel<BridgeMessage>> _pending = new(StringComparer.Ordinal);
    private Session? _session;

    /// <summary>The process id of the running bridge (tests use it to simulate a crash).</summary>
    internal int? BridgeProcessId => _session is { Closed: false } s ? s.Process.Id : null;

    public Task<PingResult> PingAsync(CancellationToken cancellationToken)
        => CallAsync<PingResult>(BridgeOps.Ping, new DeviceRequest(), options.OperationTimeout, cancellationToken);

    public Task<DeviceInfo> TestConnectionAsync(DeviceEndpoint device, CancellationToken cancellationToken)
        => CallAsync<DeviceInfo>(BridgeOps.TestConnection, Request(device), options.OperationTimeout, cancellationToken);

    public async Task<DateTime> GetTimeAsync(DeviceEndpoint device, CancellationToken cancellationToken)
        => (await CallAsync<TimeResult>(BridgeOps.GetTime, Request(device), options.OperationTimeout, cancellationToken).ConfigureAwait(false)).DeviceTime;

    public async Task<DateTime> SetTimeAsync(DeviceEndpoint device, DateTime local, CancellationToken cancellationToken)
    {
        var request = Request(device);
        request.Time = local;
        return (await CallAsync<TimeResult>(BridgeOps.SetTime, request, options.OperationTimeout, cancellationToken).ConfigureAwait(false)).DeviceTime;
    }

    public async IAsyncEnumerable<LogBatch> PullLogsAsync(DeviceEndpoint device, DateTime? fromLocal, DateTime? toLocal, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var request = Request(device);
        request.FromLocal = fromLocal;
        request.ToLocal = toLocal;
        await foreach (var message in StreamAsync(BridgeOps.PullLogs, request, options.PullTimeout, cancellationToken).ConfigureAwait(false))
        {
            yield return Read<LogBatch>(message);
        }
    }

    public async Task<IReadOnlyList<DeviceUser>> GetUsersAsync(DeviceEndpoint device, CancellationToken cancellationToken)
        => (await CallAsync<UsersResult>(BridgeOps.GetUsers, Request(device), options.OperationTimeout, cancellationToken).ConfigureAwait(false)).Users;

    public async Task SetUserAsync(DeviceEndpoint device, DeviceUser user, CancellationToken cancellationToken)
    {
        var request = Request(device);
        request.User = user;
        await CallAsync<JsonElement>(BridgeOps.SetUser, request, options.OperationTimeout, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteUserAsync(DeviceEndpoint device, string deviceUserId, CancellationToken cancellationToken)
    {
        var request = Request(device);
        request.DeviceUserId = deviceUserId;
        await CallAsync<JsonElement>(BridgeOps.DeleteUser, request, options.OperationTimeout, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<FingerTemplate>> GetTemplatesAsync(DeviceEndpoint device, string deviceUserId, CancellationToken cancellationToken)
    {
        var request = Request(device);
        request.DeviceUserId = deviceUserId;
        return (await CallAsync<TemplatesResult>(BridgeOps.GetTemplates, request, options.OperationTimeout, cancellationToken).ConfigureAwait(false)).Templates;
    }

    public async Task SetTemplatesAsync(DeviceEndpoint device, string deviceUserId, IReadOnlyList<FingerTemplate> templates, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(templates);
        var request = Request(device);
        request.DeviceUserId = deviceUserId;
        request.Templates = [.. templates];
        await CallAsync<JsonElement>(BridgeOps.SetTemplates, request, options.OperationTimeout, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _startLock.WaitAsync().ConfigureAwait(false);
        try
        {
            Stop("disposed");
        }
        finally
        {
            _startLock.Release();
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private static DeviceRequest Request(DeviceEndpoint device) => new() { Device = device.ToAddress() };

    private static T Read<T>(BridgeMessage message)
        => string.IsNullOrEmpty(message.Body) ? default! : JsonSerializer.Deserialize<T>(message.Body, Json)!;

    private async Task<T> CallAsync<T>(string op, DeviceRequest request, TimeSpan timeout, CancellationToken cancellationToken)
    {
        BridgeMessage? last = null;
        await foreach (var message in StreamAsync(op, request, timeout, cancellationToken).ConfigureAwait(false))
        {
            last = message;
        }

        return Read<T>(last ?? throw new DeviceGatewayException(BridgeErrors.Failed, "The bridge sent no reply."));
    }

    /// <summary>Sends one request and yields every reply until the final one. Error replies throw.</summary>
    private async IAsyncEnumerable<BridgeMessage> StreamAsync(string op, DeviceRequest request, TimeSpan timeout, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var session = await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        var id = Guid.NewGuid().ToString("N");
        var inbox = Channel.CreateUnbounded<BridgeMessage>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        _pending[id] = inbox;
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timer.CancelAfter(timeout);
        var finished = false;
        try
        {
            await SendAsync(session, new BridgeMessage { Id = id, Op = op, Body = JsonSerializer.Serialize(request, Json) }, timer.Token).ConfigureAwait(false);
            while (!finished)
            {
                BridgeMessage message;
                try
                {
                    // Every reply restarts the timeout, so a long pull only fails when the bridge goes quiet.
                    message = await inbox.Reader.ReadAsync(timer.Token).ConfigureAwait(false);
                    timer.CancelAfter(timeout);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    logger.LogWarning("Bridge operation {Op} timed out after {Timeout}; restarting the bridge", op, timeout);
                    await RestartAsync(session, "timeout").ConfigureAwait(false);
                    throw new DeviceGatewayException(BridgeErrors.Unreachable, "The device did not answer in time.");
                }
                catch (ChannelClosedException)
                {
                    finished = true;
                    throw new DeviceGatewayException(BridgeErrors.Failed, StoppedMessage);
                }

                finished = message.Final;
                if (message.ErrorCode is { } code)
                {
                    throw new DeviceGatewayException(code, message.ErrorMessage ?? "The device operation failed.");
                }

                yield return message;
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
            if (!finished && !session.Closed)
            {
                // The caller stopped early (cancel or exception): ask the bridge to stop the device work.
                _ = TrySendCancelAsync(session, id);
            }
        }
    }

    private async Task TrySendCancelAsync(Session session, string id)
    {
        try
        {
            var body = JsonSerializer.Serialize(new DeviceRequest { TargetId = id }, Json);
            await SendAsync(session, new BridgeMessage { Id = Guid.NewGuid().ToString("N"), Op = BridgeOps.Cancel, Body = body }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (DeviceGatewayException)
        {
            // The bridge is already gone.
        }
    }

    private async Task SendAsync(Session session, BridgeMessage message, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var failed = false;
        try
        {
            await BridgeFraming.WriteAsync(session.Pipe, bytes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            failed = true;
        }
        finally
        {
            _writeLock.Release();
        }

        if (failed)
        {
            await RestartAsync(session, "write failed").ConfigureAwait(false);
            throw new DeviceGatewayException(BridgeErrors.Failed, StoppedMessage);
        }
    }

    private async Task<Session> EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_session is { Closed: false } current)
        {
            return current;
        }

        await _startLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_session is { Closed: false } again)
            {
                return again;
            }

            Stop("replaced");
            _session = await StartAsync(cancellationToken).ConfigureAwait(false);
            return _session;
        }
        finally
        {
            _startLock.Release();
        }
    }

    private async Task<Session> StartAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(options.BridgePath))
        {
            throw new DeviceGatewayException(BridgeErrors.SdkMissing, "The device bridge is not installed. Reinstall SuperOfc.");
        }

        var suffix = Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(options.BridgePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(options.BridgePath)!,
        };
        start.ArgumentList.Add("--pipe");
        start.ArgumentList.Add(suffix);
        start.ArgumentList.Add("--parent");
        start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(options.FakeSessionPath))
        {
            start.ArgumentList.Add("--fake");
            start.ArgumentList.Add(options.FakeSessionPath);
        }

        var process = Process.Start(start) ?? throw new DeviceGatewayException(BridgeErrors.Failed, "The device bridge could not start.");
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                logger.LogWarning("Bridge: {Line}", e.Data);
            }
        };
        process.BeginErrorReadLine();

        var pipe = new NamedPipeClientStream(".", BridgeFraming.PipeName(suffix), PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timer.CancelAfter(options.ConnectTimeout);
            await pipe.ConnectAsync(timer.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or TimeoutException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            TryKill(process);
            process.Dispose();
            cancellationToken.ThrowIfCancellationRequested();
            throw new DeviceGatewayException(BridgeErrors.Failed, "The device bridge did not start in time.");
        }

        var session = new Session(process, pipe);
        _ = Task.Run(() => ReadLoopAsync(session), CancellationToken.None);
        logger.LogInformation("Device bridge started (pid {Pid}, fake {Fake})", process.Id, !string.IsNullOrWhiteSpace(options.FakeSessionPath));
        return session;
    }

    private async Task ReadLoopAsync(Session session)
    {
        try
        {
            while (true)
            {
                var frame = await BridgeFraming.ReadAsync(session.Pipe, CancellationToken.None).ConfigureAwait(false);
                if (frame is null)
                {
                    break;
                }

                var message = JsonSerializer.Deserialize<BridgeMessage>(frame, Json);
                if (message is not null && _pending.TryGetValue(message.Id, out var inbox))
                {
                    inbox.Writer.TryWrite(message);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or JsonException or InvalidDataException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Device bridge pipe closed");
        }

        session.Closed = true;
        foreach (var inbox in _pending.Values)
        {
            inbox.Writer.TryComplete();
        }

        TryKill(session.Process);
    }

    private async Task RestartAsync(Session session, string reason)
    {
        await _startLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(_session, session))
            {
                Stop(reason);
            }
        }
        finally
        {
            _startLock.Release();
        }
    }

    private void Stop(string reason)
    {
        if (_session is not { } session)
        {
            return;
        }

        _session = null;
        session.Closed = true;
        logger.LogInformation("Stopping device bridge: {Reason}", reason);
        session.Pipe.Dispose();
        TryKill(session.Process);
        session.Process.Dispose();
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private sealed class Session(Process process, NamedPipeClientStream pipe)
    {
        private volatile bool _closed;

        public Process Process { get; } = process;

        public NamedPipeClientStream Pipe { get; } = pipe;

        public bool Closed
        {
            get => _closed;
            set => _closed = value;
        }
    }
}

/// <summary>In-memory pause switch for automatic sync; pauses nest.</summary>
public sealed class DeviceSyncGate : IDeviceSyncGate
{
    private readonly ConcurrentDictionary<Guid, string> _reasons = new();

    public bool IsPaused => !_reasons.IsEmpty;

    public IDisposable Pause(string reason)
    {
        var id = Guid.NewGuid();
        _reasons[id] = reason;
        return new Release(() => _reasons.TryRemove(id, out _));
    }

    private sealed class Release(Action action) : IDisposable
    {
        private Action? _action = action;

        public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
    }
}
