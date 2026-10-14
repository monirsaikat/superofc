using SuperOfc.DeviceBridge.Contracts;

namespace SuperOfc.Application.Abstractions;

/// <summary>How the app reaches a device; the comm key is already decrypted.</summary>
public sealed record DeviceEndpoint(string Ip, int Port, int CommKey, int MachineNumber = 1)
{
    public DeviceAddress ToAddress() => new() { Ip = Ip, Port = Port, CommKey = CommKey, MachineNumber = MachineNumber };
}

/// <summary>
/// Talks to attendance devices (through the 32-bit bridge process, ADR-06). Every method throws
/// <see cref="DeviceGatewayException"/> with a readable message on failure.
/// </summary>
public interface IDeviceGateway
{
    Task<PingResult> PingAsync(CancellationToken cancellationToken);

    Task<DeviceInfo> TestConnectionAsync(DeviceEndpoint device, CancellationToken cancellationToken);

    Task<DateTime> GetTimeAsync(DeviceEndpoint device, CancellationToken cancellationToken);

    Task<DateTime> SetTimeAsync(DeviceEndpoint device, DateTime local, CancellationToken cancellationToken);

    /// <summary>Streams logs in batches of up to 500 as the device delivers them.</summary>
    IAsyncEnumerable<LogBatch> PullLogsAsync(DeviceEndpoint device, DateTime? fromLocal, DateTime? toLocal, CancellationToken cancellationToken);

    Task<IReadOnlyList<DeviceUser>> GetUsersAsync(DeviceEndpoint device, CancellationToken cancellationToken);

    Task SetUserAsync(DeviceEndpoint device, DeviceUser user, CancellationToken cancellationToken);

    Task DeleteUserAsync(DeviceEndpoint device, string deviceUserId, CancellationToken cancellationToken);

    Task<IReadOnlyList<FingerTemplate>> GetTemplatesAsync(DeviceEndpoint device, string deviceUserId, CancellationToken cancellationToken);

    Task SetTemplatesAsync(DeviceEndpoint device, string deviceUserId, IReadOnlyList<FingerTemplate> templates, CancellationToken cancellationToken);
}

public sealed class DeviceGatewayException : Exception
{
    public DeviceGatewayException()
        : this(BridgeErrors.Failed, "The device operation failed.")
    {
    }

    public DeviceGatewayException(string message)
        : this(BridgeErrors.Failed, message)
    {
    }

    public DeviceGatewayException(string message, Exception inner)
        : base(message, inner)
    {
        Code = BridgeErrors.Failed;
    }

    public DeviceGatewayException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }

    public bool IsUnreachable => Code == BridgeErrors.Unreachable;
}

/// <summary>Pauses automatic device sync (for example while payroll is being finalized).</summary>
public interface IDeviceSyncGate
{
    bool IsPaused { get; }

    IDisposable Pause(string reason);
}
