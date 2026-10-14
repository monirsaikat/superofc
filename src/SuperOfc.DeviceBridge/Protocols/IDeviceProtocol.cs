using System;
using System.Collections.Generic;
using System.Threading;
using SuperOfc.DeviceBridge.Contracts;

namespace SuperOfc.DeviceBridge.Protocols;

/// <summary>
/// One connection to one device. Created per operation, used from a single thread, always disposed (which disconnects).
/// The SDK sits behind this interface so a managed TCP implementation can replace it later (docs/06).
/// </summary>
internal interface IDeviceProtocol : IDisposable
{
    string Name { get; }

    /// <summary>Connects and returns basic information. Throws <see cref="DeviceException"/>.</summary>
    DeviceInfo Connect(DeviceAddress address);

    DateTime GetTime();

    void SetTime(DateTime local);

    /// <summary>Reads attendance logs, optionally limited to a local time range. Yields as it reads.</summary>
    IEnumerable<DeviceLog> ReadLogs(DateTime? fromLocal, DateTime? toLocal, CancellationToken cancellationToken);

    List<DeviceUser> GetUsers();

    void SetUser(DeviceUser user);

    void DeleteUser(string deviceUserId);

    List<FingerTemplate> GetTemplates(string deviceUserId);

    void SetTemplates(string deviceUserId, IReadOnlyList<FingerTemplate> templates);

    void ClearLogs();
}

internal interface IDeviceProtocolFactory
{
    string Name { get; }

    bool IsAvailable { get; }

    IDeviceProtocol Create();
}

/// <summary>A device problem with a stable code from <see cref="BridgeErrors"/> and a message for people.</summary>
internal sealed class DeviceException : Exception
{
    public DeviceException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public DeviceException(string code, string message, Exception inner)
        : base(message, inner)
    {
        Code = code;
    }

    public string Code { get; }
}
