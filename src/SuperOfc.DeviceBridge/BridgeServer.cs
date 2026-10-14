using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SuperOfc.DeviceBridge.Contracts;
using SuperOfc.DeviceBridge.Protocols;

namespace SuperOfc.DeviceBridge;

internal static class BridgeJson
{
    public static JsonSerializerOptions Options { get; } = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(string? json)
        where T : new()
        => string.IsNullOrEmpty(json) ? new T() : JsonSerializer.Deserialize<T>(json!, Options) ?? new T();
}

/// <summary>
/// Serves one client (the app) over a named pipe restricted to the current Windows user. Requests run in parallel
/// across devices (at most four) and one at a time per device (docs/06 Bridge lifecycle).
/// </summary>
internal sealed class BridgeServer : IDisposable
{
    public const int LogBatchSize = 500;

    private readonly string _pipeName;
    private readonly IDeviceProtocolFactory _factory;
    private readonly SemaphoreSlim _parallel = new(4, 4);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _deviceLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private Stream? _stream;

    public BridgeServer(string pipeName, IDeviceProtocolFactory factory)
    {
        _pipeName = pipeName;
        _factory = factory;
    }

    public void Dispose()
    {
        _parallel.Dispose();
        _writeLock.Dispose();
        foreach (var gate in _deviceLocks.Values)
        {
            gate.Dispose();
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65536, 65536, security);
        await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
        _stream = pipe;
        var tasks = new List<Task>();
        while (!cancellationToken.IsCancellationRequested)
        {
            byte[]? frame;
            try
            {
                frame = await BridgeFraming.ReadAsync(pipe, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                break;
            }

            if (frame is null)
            {
                break; // client closed
            }

            BridgeMessage? request;
            try
            {
                request = JsonSerializer.Deserialize<BridgeMessage>(frame, BridgeJson.Options);
            }
            catch (JsonException)
            {
                continue;
            }

            if (request is null)
            {
                continue;
            }

            tasks.RemoveAll(t => t.IsCompleted);
            tasks.Add(Task.Run(() => HandleAsync(request, cancellationToken), cancellationToken));
        }

        foreach (var running in _running.Values)
        {
            running.Cancel();
        }
    }

    private async Task HandleAsync(BridgeMessage request, CancellationToken cancellationToken)
    {
        try
        {
            switch (request.Op)
            {
                case BridgeOps.Ping:
                    await ReplyAsync(request, new PingResult { BridgeVersion = typeof(BridgeServer).Assembly.GetName().Version?.ToString() ?? "0", SdkAvailable = _factory.IsAvailable, Protocol = _factory.Name }).ConfigureAwait(false);
                    return;
                case BridgeOps.Cancel:
                    var target = BridgeJson.Deserialize<DeviceRequest>(request.Body).TargetId;
                    if (target is not null && _running.TryGetValue(target, out var cts))
                    {
                        cts.Cancel();
                    }

                    await ReplyAsync(request, new object()).ConfigureAwait(false);
                    return;
            }

            var body = BridgeJson.Deserialize<DeviceRequest>(request.Body);
            if (string.IsNullOrWhiteSpace(body.Device.Ip))
            {
                await FailAsync(request, BridgeErrors.BadRequest, "No device address.").ConfigureAwait(false);
                return;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _running[request.Id] = linked;
            var deviceLock = _deviceLocks.GetOrAdd(body.Device.Key, _ => new SemaphoreSlim(1, 1));
            await _parallel.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                await deviceLock.WaitAsync(linked.Token).ConfigureAwait(false);
                try
                {
                    await RunDeviceOperationAsync(request, body, linked.Token).ConfigureAwait(false);
                }
                finally
                {
                    deviceLock.Release();
                }
            }
            finally
            {
                _parallel.Release();
                _running.TryRemove(request.Id, out _);
            }
        }
        catch (DeviceException ex)
        {
            await FailAsync(request, ex.Code, ex.Message).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await FailAsync(request, BridgeErrors.Cancelled, "The operation was cancelled.").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await FailAsync(request, BridgeErrors.Failed, ex.Message).ConfigureAwait(false);
        }
    }

    private Task RunDeviceOperationAsync(BridgeMessage request, DeviceRequest body, CancellationToken cancellationToken)
    {
        // All SDK work for one request happens on one STA thread; batches are sent from that thread.
        var result = StaRunner.Run<object?>(() =>
        {
            using var device = _factory.Create();
            var info = device.Connect(body.Device);
            switch (request.Op)
            {
                case BridgeOps.TestConnection:
                    return info;
                case BridgeOps.GetTime:
                    return new TimeResult { DeviceTime = device.GetTime() };
                case BridgeOps.SetTime:
                    device.SetTime(body.Time ?? DateTime.Now);
                    return new TimeResult { DeviceTime = device.GetTime() };
                case BridgeOps.PullLogs:
                    var batch = new List<DeviceLog>(LogBatchSize);
                    var total = 0;
                    foreach (var log in device.ReadLogs(body.FromLocal, body.ToLocal, cancellationToken))
                    {
                        batch.Add(log);
                        total++;
                        if (batch.Count == LogBatchSize)
                        {
                            SendAsync(new BridgeMessage { Id = request.Id, Op = request.Op, Final = false, Body = BridgeJson.Serialize(new LogBatch { Logs = batch, ReadSoFar = total, SerialNumber = info.SerialNumber }) })
                                .GetAwaiter().GetResult();
                            batch = new List<DeviceLog>(LogBatchSize);
                        }
                    }

                    return new LogBatch { Logs = batch, ReadSoFar = total, SerialNumber = info.SerialNumber };
                case BridgeOps.GetUsers:
                    return new UsersResult { Users = device.GetUsers() };
                case BridgeOps.SetUser:
                    device.SetUser(body.User ?? throw new DeviceException(BridgeErrors.BadRequest, "No user."));
                    return new object();
                case BridgeOps.DeleteUser:
                    device.DeleteUser(body.DeviceUserId ?? throw new DeviceException(BridgeErrors.BadRequest, "No user id."));
                    return new object();
                case BridgeOps.GetTemplates:
                    return new TemplatesResult { Templates = device.GetTemplates(body.DeviceUserId ?? throw new DeviceException(BridgeErrors.BadRequest, "No user id.")) };
                case BridgeOps.SetTemplates:
                    device.SetTemplates(body.DeviceUserId ?? throw new DeviceException(BridgeErrors.BadRequest, "No user id."), body.Templates ?? new List<FingerTemplate>());
                    return new object();
                case BridgeOps.ClearLogs:
                    if (body.ConfirmToken != "CLEAR " + info.SerialNumber)
                    {
                        throw new DeviceException(BridgeErrors.BadRequest, "Clearing logs needs the confirmation text.");
                    }

                    device.ClearLogs();
                    return new object();
                default:
                    throw new DeviceException(BridgeErrors.NotSupported, "Unknown operation " + request.Op + ".");
            }
        });
        return ReplyAsync(request, result ?? new object());
    }

    private Task ReplyAsync(BridgeMessage request, object body)
        => SendAsync(new BridgeMessage { Id = request.Id, Op = request.Op, Final = true, Body = BridgeJson.Serialize(body) });

    private Task FailAsync(BridgeMessage request, string code, string message)
        => SendAsync(new BridgeMessage { Id = request.Id, Op = request.Op, Final = true, ErrorCode = code, ErrorMessage = message });

    private async Task SendAsync(BridgeMessage message)
    {
        var stream = _stream;
        if (stream is null)
        {
            return;
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, BridgeJson.Options);
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await BridgeFraming.WriteAsync(stream, bytes, CancellationToken.None).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // Client went away; nothing to report to.
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
