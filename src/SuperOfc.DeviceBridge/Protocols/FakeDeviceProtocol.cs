using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using SuperOfc.DeviceBridge.Contracts;

namespace SuperOfc.DeviceBridge.Protocols;

/// <summary>Devices described in a JSON file, for tests, demos and CI (docs/06 Testing). State changes live in memory.</summary>
internal sealed class FakeSession
{
    public List<FakeDevice> Devices { get; set; } = new();

    public static FakeSession Load(string path)
    {
        var json = File.ReadAllText(path);
        var session = JsonSerializer.Deserialize<FakeSession>(json, BridgeJson.Options) ?? new FakeSession();
        foreach (var device in session.Devices)
        {
            device.Expand();
        }

        return session;
    }
}

internal sealed class FakeDevice
{
    public string Ip { get; set; } = string.Empty;

    public int Port { get; set; } = 4370;

    public int CommKey { get; set; }

    public string Serial { get; set; } = "FAKE0001";

    public string Name { get; set; } = "Fake device";

    public string Firmware { get; set; } = "Ver 6.60 (fake)";

    /// <summary>Device clock minus PC clock, to test the drift warning.</summary>
    public int ClockOffsetMinutes { get; set; }

    /// <summary>Delay after every 500 logs so tests can interrupt a pull.</summary>
    public int BatchDelayMs { get; set; }

    public List<DeviceUser> Users { get; set; } = new();

    public List<DeviceLog> Logs { get; set; } = new();

    public Dictionary<string, List<FingerTemplate>> Templates { get; set; } = new();

    public FakeLogGenerator? GenerateLogs { get; set; }

    public void Expand()
    {
        if (GenerateLogs is not { Count: > 0 } g)
        {
            return;
        }

        var time = g.Start;
        for (var i = 0; i < g.Count; i++)
        {
            Logs.Add(new DeviceLog { DeviceUserId = ((i % Math.Max(1, g.Users)) + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), TimestampLocal = time, VerifyMode = 1 });
            time = time.AddMinutes(g.MinutesApart);
        }
    }
}

internal sealed class FakeLogGenerator
{
    public int Count { get; set; }

    public DateTime Start { get; set; } = new(2026, 1, 1, 8, 0, 0);

    public int Users { get; set; } = 10;

    public int MinutesApart { get; set; } = 1;
}

internal sealed class FakeDeviceProtocolFactory : IDeviceProtocolFactory
{
    private readonly FakeSession _session;

    public FakeDeviceProtocolFactory(FakeSession session)
    {
        _session = session;
    }

    public string Name => "fake";

    public bool IsAvailable => true;

    public IDeviceProtocol Create() => new FakeDeviceProtocol(_session);
}

internal sealed class FakeDeviceProtocol : IDeviceProtocol
{
    private readonly FakeSession _session;
    private FakeDevice? _device;

    public FakeDeviceProtocol(FakeSession session)
    {
        _session = session;
    }

    public string Name => "fake";

    private FakeDevice Device => _device ?? throw new InvalidOperationException("Not connected.");

    public DeviceInfo Connect(DeviceAddress address)
    {
        var device = _session.Devices.FirstOrDefault(d => d.Ip == address.Ip && d.Port == address.Port)
            ?? throw new DeviceException(BridgeErrors.Unreachable, "No device answered at " + address.Ip + ":" + address.Port + ".");
        if (device.CommKey != address.CommKey)
        {
            throw new DeviceException(BridgeErrors.WrongCommKey, "The device refused the communication key.");
        }

        _device = device;
        lock (device)
        {
            return new DeviceInfo
            {
                SerialNumber = device.Serial,
                DeviceName = device.Name,
                Firmware = device.Firmware,
                Platform = "FAKE",
                UserCount = device.Users.Count,
                LogCount = device.Logs.Count,
                FingerCount = device.Templates.Values.Sum(t => t.Count),
                DeviceTime = GetTime(),
            };
        }
    }

    public DateTime GetTime() => DateTime.Now.AddMinutes(Device.ClockOffsetMinutes);

    public void SetTime(DateTime local) => Device.ClockOffsetMinutes = (int)Math.Round((local - DateTime.Now).TotalMinutes);

    public IEnumerable<DeviceLog> ReadLogs(DateTime? fromLocal, DateTime? toLocal, CancellationToken cancellationToken)
    {
        List<DeviceLog> snapshot;
        lock (Device)
        {
            snapshot = Device.Logs.ToList();
        }

        var count = 0;
        foreach (var log in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((fromLocal is null || log.TimestampLocal >= fromLocal) && (toLocal is null || log.TimestampLocal <= toLocal))
            {
                yield return log;
                if (++count % 500 == 0 && Device.BatchDelayMs > 0)
                {
                    Thread.Sleep(Device.BatchDelayMs);
                }
            }
        }
    }

    public List<DeviceUser> GetUsers()
    {
        lock (Device)
        {
            return Device.Users.Select(u => new DeviceUser
            {
                DeviceUserId = u.DeviceUserId, Name = u.Name, Privilege = u.Privilege, CardNumber = u.CardNumber, Enabled = u.Enabled,
                FingerCount = Device.Templates.TryGetValue(u.DeviceUserId, out var t) ? t.Count : 0,
            }).ToList();
        }
    }

    public void SetUser(DeviceUser user)
    {
        lock (Device)
        {
            Device.Users.RemoveAll(u => u.DeviceUserId == user.DeviceUserId);
            Device.Users.Add(user);
        }
    }

    public void DeleteUser(string deviceUserId)
    {
        lock (Device)
        {
            Device.Users.RemoveAll(u => u.DeviceUserId == deviceUserId);
            Device.Templates.Remove(deviceUserId);
        }
    }

    public List<FingerTemplate> GetTemplates(string deviceUserId)
    {
        lock (Device)
        {
            return Device.Templates.TryGetValue(deviceUserId, out var list)
                ? list.Select(t => new FingerTemplate { FingerIndex = t.FingerIndex, Data = t.Data, Flag = t.Flag }).ToList()
                : new List<FingerTemplate>();
        }
    }

    public void SetTemplates(string deviceUserId, IReadOnlyList<FingerTemplate> templates)
    {
        lock (Device)
        {
            if (Device.Users.All(u => u.DeviceUserId != deviceUserId))
            {
                throw new DeviceException(BridgeErrors.Failed, "User " + deviceUserId + " does not exist on the device.");
            }

            Device.Templates[deviceUserId] = templates.Select(t => new FingerTemplate { FingerIndex = t.FingerIndex, Data = t.Data, Flag = t.Flag }).ToList();
        }
    }

    public void ClearLogs()
    {
        lock (Device)
        {
            Device.Logs.Clear();
        }
    }

    public void Dispose() => _device = null;
}
