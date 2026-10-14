using System;
using System.Collections.Generic;

namespace SuperOfc.DeviceBridge.Contracts;

/// <summary>
/// Wire format between the app and SuperOfc.DeviceBridge.exe (docs/06, ADR-06): each message is a 4-byte
/// little-endian length followed by UTF-8 JSON of a <see cref="BridgeMessage"/>. One request may produce several
/// responses with the same <see cref="BridgeMessage.Id"/> (log batches); the last one has <see cref="BridgeMessage.Final"/> set.
/// </summary>
public sealed class BridgeMessage
{
    public string Id { get; set; } = string.Empty;

    /// <summary>One of <see cref="BridgeOps"/>.</summary>
    public string Op { get; set; } = string.Empty;

    /// <summary>Operation-specific JSON (request or response body).</summary>
    public string? Body { get; set; }

    public bool Final { get; set; } = true;

    /// <summary>Set on failure; see <see cref="BridgeErrors"/>.</summary>
    public string? ErrorCode { get; set; }

    public string? ErrorMessage { get; set; }
}

public static class BridgeOps
{
    public const string Ping = "Ping";
    public const string TestConnection = "TestConnection";
    public const string GetTime = "GetTime";
    public const string SetTime = "SetTime";
    public const string PullLogs = "PullLogs";
    public const string GetUsers = "GetUsers";
    public const string SetUser = "SetUser";
    public const string DeleteUser = "DeleteUser";
    public const string GetTemplates = "GetTemplates";
    public const string SetTemplates = "SetTemplates";
    public const string ClearLogs = "ClearLogs";
    public const string Cancel = "Cancel";
}

public static class BridgeErrors
{
    public const string Unreachable = "Device.Unreachable";
    public const string WrongCommKey = "Device.WrongCommKey";
    public const string Busy = "Device.Busy";
    public const string NotSupported = "Device.NotSupported";
    public const string SdkMissing = "Device.SdkMissing";
    public const string Cancelled = "Device.Cancelled";
    public const string Failed = "Device.Failed";
    public const string BadRequest = "Bridge.BadRequest";
}

/// <summary>How to reach a device. The comm key travels in clear text only over the local, user-restricted pipe.</summary>
public sealed class DeviceAddress
{
    public string Ip { get; set; } = string.Empty;

    public int Port { get; set; } = 4370;

    public int CommKey { get; set; }

    /// <summary>Machine number used by the SDK (almost always 1).</summary>
    public int MachineNumber { get; set; } = 1;

    public string Key => Ip + ":" + Port;
}

/// <summary>Request body for every device operation.</summary>
public sealed class DeviceRequest
{
    public DeviceAddress Device { get; set; } = new();

    /// <summary>PullLogs: first local time to include (null = everything).</summary>
    public DateTime? FromLocal { get; set; }

    /// <summary>PullLogs: last local time to include (null = up to now).</summary>
    public DateTime? ToLocal { get; set; }

    /// <summary>SetTime: new device local time.</summary>
    public DateTime? Time { get; set; }

    public DeviceUser? User { get; set; }

    public string? DeviceUserId { get; set; }

    public List<FingerTemplate>? Templates { get; set; }

    /// <summary>ClearLogs: must equal "CLEAR " + device serial, typed by an administrator.</summary>
    public string? ConfirmToken { get; set; }

    /// <summary>Cancel: id of the request to cancel.</summary>
    public string? TargetId { get; set; }
}

public sealed class PingResult
{
    public string BridgeVersion { get; set; } = string.Empty;

    public bool SdkAvailable { get; set; }

    public string Protocol { get; set; } = string.Empty;
}

public sealed class DeviceInfo
{
    public string SerialNumber { get; set; } = string.Empty;

    public string? DeviceName { get; set; }

    public string? Firmware { get; set; }

    public string? Platform { get; set; }

    public int UserCount { get; set; }

    public int LogCount { get; set; }

    public int FingerCount { get; set; }

    public bool SupportsFace { get; set; }

    public DateTime DeviceTime { get; set; }
}

public sealed class DeviceLog
{
    public string DeviceUserId { get; set; } = string.Empty;

    public DateTime TimestampLocal { get; set; }

    public int VerifyMode { get; set; }

    /// <summary>0 check-in, 1 check-out, 2 break-out, 3 break-in, 4 OT-in, 5 OT-out (device dependent).</summary>
    public int InOutMode { get; set; }

    public int WorkCode { get; set; }
}

/// <summary>One batch of a PullLogs stream.</summary>
public sealed class LogBatch
{
    public List<DeviceLog> Logs { get; set; } = new();

    /// <summary>Total records read so far in this pull.</summary>
    public int ReadSoFar { get; set; }

    /// <summary>Serial of the device (sent with every batch so the app can build dedup keys).</summary>
    public string SerialNumber { get; set; } = string.Empty;
}

public sealed class DeviceUser
{
    public string DeviceUserId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>0 user, 14 administrator (SDK values).</summary>
    public int Privilege { get; set; }

    public string? CardNumber { get; set; }

    public string? Password { get; set; }

    public bool Enabled { get; set; } = true;

    public int FingerCount { get; set; }
}

public sealed class FingerTemplate
{
    /// <summary>0-9 (left little finger to right little finger).</summary>
    public int FingerIndex { get; set; }

    /// <summary>Template data as the SDK returns it (Base64 or the SDK's string form).</summary>
    public string Data { get; set; } = string.Empty;

    public int Flag { get; set; } = 1;
}

public sealed class TimeResult
{
    public DateTime DeviceTime { get; set; }
}

public sealed class UsersResult
{
    public List<DeviceUser> Users { get; set; } = new();
}

public sealed class TemplatesResult
{
    public List<FingerTemplate> Templates { get; set; } = new();
}
