using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Common;
using SuperOfc.Domain.Attendance;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Devices;
using SuperOfc.Domain.Security;

namespace SuperOfc.Application.Devices;

public sealed record ParsedPunchFile(IReadOnlyList<RawPunch> Punches, IReadOnlyList<string> Errors, int Lines);

/// <summary>
/// Reads the two import formats of docs/06: the ZKTeco USB export (<c>*_attlog.dat</c>, tab-separated user id,
/// <c>yyyy-MM-dd HH:mm:ss</c>, verify, status, work code) and CSV/Excel with columns EmployeeCode, DateTime, Type.
/// </summary>
public static class PunchFileParser
{
    public const int MaxErrors = 50;

    private static readonly string[] DateFormats =
    [
        "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm", "yyyy/MM/dd HH:mm:ss", "yyyy/MM/dd HH:mm",
        "dd/MM/yyyy HH:mm:ss", "dd/MM/yyyy HH:mm", "dd-MM-yyyy HH:mm:ss", "dd-MM-yyyy HH:mm", "dd.MM.yyyy HH:mm",
    ];

    public static bool IsDeviceExport(string path)
        => Path.GetExtension(path).ToLowerInvariant() is ".dat" or ".txt";

    /// <summary>Parses a device export. <paramref name="keyPrefix"/> is the device serial when known, so keys match a later sync.</summary>
    public static ParsedPunchFile ParseDeviceExport(IEnumerable<string> lines, string keyPrefix)
    {
        var punches = new List<RawPunch>();
        var errors = new List<string>();
        var number = 0;
        foreach (var raw in lines)
        {
            number++;
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var parts = raw.Split('\t').Select(p => p.Trim()).ToArray();
            if (parts.Length < 2 || parts[0].Length == 0 || !TryParseTime(parts[1], out var time))
            {
                AddError(errors, $"Line {number}: expected user id and date-time separated by tabs.");
                continue;
            }

            var verify = parts.Length > 2 && int.TryParse(parts[2], CultureInfo.InvariantCulture, out var v) ? v : 0;
            var status = parts.Length > 3 && int.TryParse(parts[3], CultureInfo.InvariantCulture, out var s) ? s : 0;
            punches.Add(new RawPunch(parts[0], time, verify, status, AttendancePunch.DeviceKey(keyPrefix, parts[0], time)));
        }

        return new ParsedPunchFile(punches, errors, number);
    }

    /// <summary>Parses spreadsheet rows (EmployeeCode, DateTime, optional Type In/Out).</summary>
    public static ParsedPunchFile ParseSheet(IReadOnlyList<IReadOnlyDictionary<string, string>> rows)
    {
        var punches = new List<RawPunch>();
        var errors = new List<string>();
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var line = i + 2; // header is row 1
            var code = Get(row, "EmployeeCode", "Employee Code", "Code")?.Trim();
            var when = Get(row, "DateTime", "Date Time", "Time");
            if (string.IsNullOrEmpty(code))
            {
                AddError(errors, $"Row {line}: EmployeeCode is empty.");
                continue;
            }

            if (when is null || !TryParseTime(when, out var time))
            {
                AddError(errors, $"Row {line}: DateTime \"{when}\" is not a date and time (use yyyy-MM-dd HH:mm).");
                continue;
            }

            var type = Get(row, "Type", "InOut", "Direction")?.Trim().ToUpperInvariant() switch
            {
                "IN" or "I" or "0" => 0,
                "OUT" or "O" or "1" => 1,
                _ => -1,
            };
            punches.Add(new RawPunch(code, time, 0, type, $"F:{code}:{time:yyyyMMddHHmmss}"));
        }

        return new ParsedPunchFile(punches, errors, rows.Count);
    }

    public static bool TryParseTime(string text, out DateTime time)
    {
        if (DateTime.TryParseExact(text.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out time))
        {
            return true;
        }

        // Excel cells come back as the OA date text or a culture-formatted date.
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var oa) && oa is > 20000 and < 80000)
        {
            time = DateTime.FromOADate(oa);
            return true;
        }

        return DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out time) && time.Year > 2000;
    }

    private static string? Get(IReadOnlyDictionary<string, string> row, params string[] names)
    {
        foreach (var name in names)
        {
            if (row.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static void AddError(List<string> errors, string message)
    {
        if (errors.Count < MaxErrors)
        {
            errors.Add(message);
        }
    }
}

public sealed record AttendanceFilePreview(int Lines, int Valid, int Duplicates, int Unmapped, int Invalid, DateOnly? First, DateOnly? Last, IReadOnlyList<string> Errors, IReadOnlyList<string> UnmappedIds);

/// <summary>Reads a punch file and counts what an import would do, without saving anything.</summary>
[RequiresPermission(Permission.ManageAttendance)]
public sealed record PreviewAttendanceFileQuery(string Path, Guid? DeviceId) : IQuery<AttendanceFilePreview>;

public sealed class PreviewAttendanceFileHandler(IAppDbContext db, ICurrentUser user, ISpreadsheetService sheets, DeviceAccess access) : IRequestHandler<PreviewAttendanceFileQuery, AttendanceFilePreview>
{
    public async Task<Result<AttendanceFilePreview>> HandleAsync(PreviewAttendanceFileQuery request, CancellationToken cancellationToken)
    {
        var loaded = await AttendanceFileLoader.LoadAsync(request.Path, request.DeviceId, sheets, access, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<AttendanceFilePreview>.Failure(loaded.Errors);
        }

        var (file, device) = loaded.Value;
        var unique = file.Punches.GroupBy(p => p.DedupKey, StringComparer.Ordinal).Select(g => g.First()).ToList();
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in unique.Select(p => p.DedupKey).Chunk(500))
        {
            known.UnionWith(await db.AttendancePunches.AsNoTracking().Where(p => chunk.Contains(p.DedupKey)).Select(p => p.DedupKey).ToListAsync(cancellationToken).ConfigureAwait(false));
            known.UnionWith(await db.UnmappedPunches.AsNoTracking().Where(p => chunk.Contains(p.DedupKey)).Select(p => p.DedupKey).ToListAsync(cancellationToken).ConfigureAwait(false));
        }

        var maps = device is null ? [] : await db.DeviceUserMaps.AsNoTracking().Where(m => m.DeviceId == device.Id)
            .Select(m => new { m.DeviceUserId, m.EmployeeId }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var employees = await db.Employees.AsNoTracking().Where(e => e.CompanyId == user.CompanyId && e.DeletedAtUtc == null)
            .Select(e => new { e.Code, e.Id }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var resolver = new DeviceUserResolver(maps.Select(m => (m.DeviceUserId, m.EmployeeId)), employees.Select(e => (e.Code, e.Id)));

        var fresh = unique.Where(p => !known.Contains(p.DedupKey)).ToList();
        var unmappedIds = fresh.Select(p => p.DeviceUserId).Distinct(StringComparer.OrdinalIgnoreCase).Where(id => resolver.Resolve(id).EmployeeId is null).ToList();
        var unmapped = fresh.Count(p => unmappedIds.Contains(p.DeviceUserId, StringComparer.OrdinalIgnoreCase));
        return new AttendanceFilePreview(
            file.Lines, fresh.Count - unmapped, file.Punches.Count - fresh.Count, unmapped, file.Errors.Count,
            file.Punches.Count == 0 ? null : DateOnly.FromDateTime(file.Punches.Min(p => p.LocalTime)),
            file.Punches.Count == 0 ? null : DateOnly.FromDateTime(file.Punches.Max(p => p.LocalTime)),
            file.Errors, unmappedIds.Take(50).ToList());
    }
}

/// <summary>Imports a punch file through the same dedup and mapping path as a device sync, with Source = Import.</summary>
[RequiresPermission(Permission.ManageAttendance)]
public sealed record ImportAttendanceFileCommand(string Path, Guid? DeviceId) : ICommand<SyncSummary>;

public sealed class ImportAttendanceFileHandler(IAppDbContext db, ICurrentUser user, IIdGenerator ids, IClock clock, ISpreadsheetService sheets, IAuditWriter audit, DeviceAccess access, PunchIntake intake, ICompanyContext company)
    : IRequestHandler<ImportAttendanceFileCommand, SyncSummary>
{
    public async Task<Result<SyncSummary>> HandleAsync(ImportAttendanceFileCommand request, CancellationToken cancellationToken)
    {
        var loaded = await AttendanceFileLoader.LoadAsync(request.Path, request.DeviceId, sheets, access, cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
        {
            return Result<SyncSummary>.Failure(loaded.Errors);
        }

        var (file, device) = loaded.Value;
        var log = new DeviceSyncLog { Id = ids.NewId(), CompanyId = user.CompanyId, DeviceId = device?.Id, Trigger = SyncTrigger.FileImport, StartedAtUtc = clock.UtcNow };
        var zone = device is null ? company.TimeZone : access.ZoneOf(device);
        await intake.BeginAsync(device?.Id, cancellationToken).ConfigureAwait(false);
        foreach (var chunk in file.Punches.Chunk(500))
        {
            await intake.AddBatchAsync(device?.Id, chunk, PunchSource.Import, zone, cancellationToken).ConfigureAwait(false);
        }

        var result = await intake.FinishAsync(cancellationToken).ConfigureAwait(false);
        log.Pulled = result.Received;
        log.Inserted = result.Inserted;
        log.Duplicates = result.Duplicates;
        log.Unmapped = result.Unmapped;
        log.Warning = file.Errors.Count > 0 ? $"{file.Errors.Count} lines could not be read." : null;
        log.FinishedAtUtc = clock.UtcNow;
        db.DeviceSyncLogs.Add(log);
        audit.Record("Attendance.FileImported", $"{System.IO.Path.GetFileName(request.Path)}: {result.Inserted} punches imported, {result.Duplicates} duplicates, {result.Unmapped} unmapped");
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new SyncSummary(device?.Id ?? Guid.Empty, result.Received, result.Inserted, result.Duplicates, result.Unmapped, null, log.Warning);
    }
}

internal static class AttendanceFileLoader
{
    public static async Task<Result<(ParsedPunchFile File, Device? Device)>> LoadAsync(string path, Guid? deviceId, ISpreadsheetService sheets, DeviceAccess access, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return Error.Validation("Path", "The file was not found.");
        }

        Device? device = null;
        if (deviceId is { } id)
        {
            device = await access.FindAsync(id, cancellationToken).ConfigureAwait(false);
            if (device is null)
            {
                return Error.NotFound("Device");
            }
        }

        try
        {
            ParsedPunchFile file;
            if (PunchFileParser.IsDeviceExport(path))
            {
                var lines = await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false);

                // With a device, keys match its sync keys, so importing a USB export and syncing later never doubles punches.
                file = PunchFileParser.ParseDeviceExport(lines, device?.SerialNumber ?? "FILE");
            }
            else
            {
                file = PunchFileParser.ParseSheet(sheets.Read(path));
            }

            return file.Punches.Count == 0 && file.Errors.Count > 0
                ? Error.Validation("Path", "No punches could be read from this file. " + file.Errors[0])
                : (file, device);
        }
        catch (IOException ex)
        {
            return Error.Validation("Path", "The file could not be read: " + ex.Message);
        }
        catch (InvalidDataException ex)
        {
            return Error.Validation("Path", "The file could not be read: " + ex.Message);
        }
    }
}
