using SuperOfc.Domain.Common;

namespace SuperOfc.Domain.Devices;

public enum DeviceStatus
{
    Unknown = 0,
    Online = 1,
    Offline = 2,
    Error = 3,
}

/// <summary>A fingerprint or face terminal on the LAN (docs/06).</summary>
public sealed class Device : CompanyEntity, ISoftDeletable
{
    public string Name { get; set; } = string.Empty;

    public string Ip { get; set; } = string.Empty;

    public int Port { get; set; } = 4370;

    /// <summary>Communication key, encrypted with DPAPI; null means 0 (no key).</summary>
    public string? CommKeyProtected { get; set; }

    public int MachineNumber { get; set; } = 1;

    public string? SerialNumber { get; set; }

    public string? Firmware { get; set; }

    public string? Model { get; set; }

    public Guid? LocationId { get; set; }

    /// <summary>Time zone of the device clock; null means the company time zone.</summary>
    public string? TimeZoneId { get; set; }

    public bool IsEnabled { get; set; } = true;

    /// <summary>Latest punch time seen on the device (device local time). Next pull starts 24 hours earlier.</summary>
    public DateTime? LastSyncLocal { get; set; }

    public DateTime? LastSyncAtUtc { get; set; }

    public DeviceStatus Status { get; set; }

    public string? StatusMessage { get; set; }

    /// <summary>Device clock minus PC clock at the last check.</summary>
    public int? ClockOffsetSeconds { get; set; }

    public DateTime? DeletedAtUtc { get; set; }

    /// <summary>Where the next incremental pull starts (overlap protects against clock changes and partial reads).</summary>
    public DateTime? NextPullFromLocal => LastSyncLocal?.AddHours(-24);
}

/// <summary>Links a user id on one device to an employee.</summary>
public sealed class DeviceUserMap : CompanyEntity
{
    public Guid DeviceId { get; set; }

    public string DeviceUserId { get; set; } = string.Empty;

    public Guid EmployeeId { get; set; }

    public string? NameOnDevice { get; set; }
}

/// <summary>A punch from a device user who is not linked to an employee yet. Moved to AttendancePunch when mapped.</summary>
public sealed class UnmappedPunch : CompanyEntity
{
    public Guid? DeviceId { get; set; }

    public string DeviceUserId { get; set; } = string.Empty;

    public DateTime LocalTime { get; set; }

    public DateTime PunchAtUtc { get; set; }

    public string DedupKey { get; set; } = string.Empty;

    public int VerifyMode { get; set; }

    public int InOutMode { get; set; }

    public Attendance.PunchSource Source { get; set; }
}

public enum SyncTrigger
{
    Manual = 0,
    Automatic = 1,
    FileImport = 2,
}

public sealed class DeviceSyncLog : CompanyEntity
{
    public Guid? DeviceId { get; set; }

    public SyncTrigger Trigger { get; set; }

    public DateTime StartedAtUtc { get; set; }

    public DateTime? FinishedAtUtc { get; set; }

    public int Pulled { get; set; }

    public int Inserted { get; set; }

    public int Duplicates { get; set; }

    public int Unmapped { get; set; }

    public string? Warning { get; set; }

    public string? Error { get; set; }

    public bool Succeeded => Error is null && FinishedAtUtc is not null;
}

/// <summary>Encrypted backup of one finger of one employee, to restore or copy to another device.</summary>
public sealed class FingerprintBackup : CompanyEntity
{
    public Guid EmployeeId { get; set; }

    public int FingerIndex { get; set; }

    public int Flag { get; set; } = 1;

    /// <summary>Template data encrypted with DPAPI.</summary>
    public string DataProtected { get; set; } = string.Empty;

    public string? SourceDeviceSerial { get; set; }

    public DateTime CapturedAtUtc { get; set; }
}

/// <summary>Turns device user ids into employees: explicit maps first, then the employee code, then matching numbers.</summary>
public sealed class DeviceUserResolver
{
    private readonly Dictionary<string, Guid> _maps;
    private readonly Dictionary<string, Guid> _byCode;
    private readonly Dictionary<long, Guid> _byNumber;

    public DeviceUserResolver(IEnumerable<(string DeviceUserId, Guid EmployeeId)> maps, IEnumerable<(string Code, Guid EmployeeId)> employees)
    {
        _maps = maps.GroupBy(m => m.DeviceUserId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().EmployeeId, StringComparer.OrdinalIgnoreCase);
        var list = employees.ToList();
        _byCode = list.GroupBy(e => e.Code, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single().EmployeeId, StringComparer.OrdinalIgnoreCase);

        // "E0012" and device user "12" match when no other employee code has the number 12.
        _byNumber = list.Select(e => (Number: NumberIn(e.Code), e.EmployeeId)).Where(x => x.Number is not null)
            .GroupBy(x => x.Number!.Value).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single().EmployeeId);
    }

    /// <summary>Returns the employee and whether the match came from an explicit map.</summary>
    public (Guid? EmployeeId, bool IsMapped) Resolve(string deviceUserId)
    {
        if (_maps.TryGetValue(deviceUserId, out var mapped))
        {
            return (mapped, true);
        }

        if (_byCode.TryGetValue(deviceUserId, out var byCode))
        {
            return (byCode, false);
        }

        return NumberIn(deviceUserId) is { } n && _byNumber.TryGetValue(n, out var byNumber) ? (byNumber, false) : (null, false);
    }

    public void Remember(string deviceUserId, Guid employeeId) => _maps[deviceUserId] = employeeId;

    /// <summary>The digits of a code as a number ("E0012" -> 12), or null when there are none.</summary>
    public static long? NumberIn(string code)
    {
        var digits = new string(code.Where(char.IsAsciiDigit).ToArray());
        return digits.Length is > 0 and <= 15 ? long.Parse(digits, System.Globalization.CultureInfo.InvariantCulture) : null;
    }
}
