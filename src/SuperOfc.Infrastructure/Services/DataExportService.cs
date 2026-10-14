using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Backups;
using SuperOfc.Infrastructure.Persistence;

namespace SuperOfc.Infrastructure.Services;

/// <summary>Names of columns that hold secrets; they never leave the database through exports or diagnostics.</summary>
internal static partial class SecretColumns
{
    public static bool IsSecret(string name) => Pattern().IsMatch(name);

    [GeneratedRegex(@"Password|CommKey|Protected|Secret|Template|Token|Salt", RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();
}

/// <summary>
/// Exports every table (P8-T3): one Excel workbook per module with a sheet per table, and one JSON file with the same
/// rows. Amounts stored in minor units ("…Minor" columns) are written in major units. Secret columns are left out.
/// </summary>
public sealed class DataExportService(AppDbContext db, IAppEnvironment environment) : IDataExportService
{
    private const int LargeTable = 50_000;

    private sealed record Table(string Module, string Name, string SqlName, IReadOnlyList<(string Header, string Column, bool Minor)> Columns);

    public async Task<DataExportResult> ExportAllAsync(string folder, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        Directory.CreateDirectory(folder);
        var tables = Tables();
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        long total = 0;
        var files = 0;
        var workbooks = new List<Task>();
        var jsonPath = Path.Combine(folder, "superofc-data.json");
        try
        {
            await using var json = File.Create(jsonPath);
            await using var writer = new Utf8JsonWriter(json, new JsonWriterOptions { Indented = false });
            writer.WriteStartObject();
            writer.WriteString("exportedAtUtc", DateTime.UtcNow);
            writer.WriteString("appVersion", environment.AppVersion);
            writer.WriteString("note", "Amounts are in major currency units. Passwords, device keys and fingerprint templates are not included.");
            writer.WriteStartObject("modules");
            foreach (var module in tables.GroupBy(t => t.Module).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                writer.WriteStartObject(module.Key);
                var sheetsForModule = new List<SheetData>();
                foreach (var table in module.OrderBy(t => t.Name, StringComparer.Ordinal))
                {
                    var rows = await ReadAsync(connection, table, cancellationToken).ConfigureAwait(false);
                    total += rows.Count;
                    writer.WriteStartArray(table.Name);
                    foreach (var row in rows)
                    {
                        writer.WriteStartObject();
                        for (var i = 0; i < table.Columns.Count; i++)
                        {
                            writer.WritePropertyName(table.Columns[i].Header);
                            JsonSerializer.Serialize(writer, row[i]);
                        }

                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                    var sheet = new SheetData(table.Name, [.. table.Columns.Select(c => c.Header)], rows);
                    if (rows.Count > LargeTable)
                    {
                        // Very large tables get their own workbook so they are written in parallel with the rest.
                        var own = Path.Combine(folder, $"superofc-{module.Key.ToLowerInvariant()}-{table.Name.ToLowerInvariant()}.xlsx");
                        workbooks.Add(Task.Run(() => WriteWorkbook(own, [sheet]), cancellationToken));
                        files++;
                    }
                    else
                    {
                        sheetsForModule.Add(sheet);
                    }
                }

                writer.WriteEndObject();

                // Workbooks are independent, so they are written in parallel while the next module is read.
                if (sheetsForModule.Count > 0)
                {
                    var path = Path.Combine(folder, $"superofc-{module.Key.ToLowerInvariant()}.xlsx");
                    workbooks.Add(Task.Run(() => WriteWorkbook(path, sheetsForModule), cancellationToken));
                    files++;
                }
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            files++;
            await Task.WhenAll(workbooks).ConfigureAwait(false);
        }
        finally
        {
            if (opened)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }

        await File.WriteAllTextAsync(Path.Combine(folder, "README.txt"),
            "SuperOfc data export\r\n\r\nOne Excel workbook per module (one sheet per table) and superofc-data.json with the same rows.\r\n"
            + "Amounts are in major currency units. Dates are ISO (yyyy-MM-dd). Ids link rows between tables.\r\n"
            + "Passwords, device communication keys and fingerprint templates are not included.\r\n", cancellationToken).ConfigureAwait(false);
        files++;
        return new DataExportResult(folder, files, tables.Count, total, watch.Elapsed);
    }

    private List<Table> Tables()
        => [.. db.Model.GetEntityTypes()
            .Where(e => !e.IsOwned() && e.GetTableName() is not null && e.ClrType.Namespace?.StartsWith("SuperOfc.Domain", StringComparison.Ordinal) == true)
            .Select(e =>
            {
                var store = StoreObjectIdentifier.Table(e.GetTableName()!, e.GetSchema());
                var columns = e.GetProperties()
                    .Where(p => !SecretColumns.IsSecret(p.Name) && p.ClrType != typeof(byte[]) && p.GetColumnName(store) is not null)
                    .Select(p => p.Name.EndsWith("Minor", StringComparison.Ordinal)
                        ? (p.Name[..^5], p.GetColumnName(store)!, true)
                        : (p.Name, p.GetColumnName(store)!, false))
                    .ToList();
                return new Table(e.ClrType.Namespace!.Split('.')[^1], e.ClrType.Name, e.GetTableName()!, columns);
            })];

    private static async Task<List<IReadOnlyList<object?>>> ReadAsync(System.Data.Common.DbConnection connection, Table table, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {string.Join(", ", table.Columns.Select(c => Quote(c.Column)))} FROM {Quote(table.SqlName)}";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<IReadOnlyList<object?>>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var cells = new object?[table.Columns.Count];
            for (var i = 0; i < cells.Length; i++)
            {
                var value = reader.IsDBNull(i) ? null : reader.GetValue(i);
                cells[i] = table.Columns[i].Minor && value is long minor ? minor / 100m : value;
            }

            rows.Add(cells);
        }

        return rows;
    }

    /// <summary>Bulk insert per sheet: much faster than cell by cell for tables with 100,000+ rows.</summary>
    private static void WriteWorkbook(string path, IReadOnlyList<SheetData> sheets)
    {
        using var workbook = new ClosedXML.Excel.XLWorkbook();
        foreach (var sheet in sheets)
        {
            var name = sheet.Name.Length > 31 ? sheet.Name[..31] : sheet.Name;
            var ws = workbook.Worksheets.Add(name);
            for (var c = 0; c < sheet.Headers.Count; c++)
            {
                ws.Cell(1, c + 1).Value = sheet.Headers[c];
            }

            ws.Row(1).Style.Font.Bold = true;
            var rows = sheet.Rows.Select(r => r.ToArray()).ToList();
            if (rows.Count > 0)
            {
                ws.Cell(2, 1).InsertData(rows);
            }

            ws.SheetView.FreezeRows(1);
        }

        workbook.SaveAs(path);
    }

    private static string Quote(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}

/// <summary>Diagnostics zip (doc 07): logs from the last 7 days, versions, settings and devices without secrets. Works on a damaged database.</summary>
public sealed class DiagnosticsService(AppDbContext db, IAppEnvironment environment) : IDiagnosticsService
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public async Task<string> CreateAsync(string zipPath, CancellationToken cancellationToken)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(zipPath));
        if (folder is not null)
        {
            Directory.CreateDirectory(folder);
        }

        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        var info = new StringBuilder();
        info.AppendLine(CultureInfo.InvariantCulture, $"SuperOfc {environment.AppVersion}");
        info.AppendLine(CultureInfo.InvariantCulture, $"Created (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
        info.AppendLine(CultureInfo.InvariantCulture, $"Windows: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        info.AppendLine(CultureInfo.InvariantCulture, $".NET: {RuntimeInformation.FrameworkDescription}");
        info.AppendLine(CultureInfo.InvariantCulture, $"Data folder: {environment.DataDirectory}");
        if (File.Exists(environment.DatabasePath))
        {
            info.AppendLine(CultureInfo.InvariantCulture, $"Database size: {new FileInfo(environment.DatabasePath).Length / 1024.0 / 1024.0:0.0} MB");
        }

        try
        {
            var schema = await db.SchemaInfos.AsNoTracking().OrderByDescending(s => s.MigratedAtUtc).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            info.AppendLine(CultureInfo.InvariantCulture, $"Schema: {schema?.LastMigration ?? "unknown"} (by {schema?.AppVersion ?? "?"})");
            info.AppendLine(CultureInfo.InvariantCulture, $"Employees: {await db.Employees.CountAsync(cancellationToken).ConfigureAwait(false)}");
            info.AppendLine(CultureInfo.InvariantCulture, $"Punches: {await db.AttendancePunches.CountAsync(cancellationToken).ConfigureAwait(false)}");

            var settings = await db.Settings.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
            Add(zip, "settings.json", JsonSerializer.Serialize(settings.Select(s => new
            {
                s.Key,
                s.Scope,
                Value = SecretColumns.IsSecret(s.Key) ? "(hidden)" : s.ValueJson,
            }), Json));

            var devices = await db.Devices.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
            Add(zip, "devices.json", JsonSerializer.Serialize(devices.Select(d => new
            {
                d.Name, d.Ip, d.Port, d.MachineNumber, d.Model, d.Firmware, d.SerialNumber, d.IsEnabled, d.Status, d.StatusMessage,
                d.LastSyncAtUtc, d.ClockOffsetSeconds, HasCommKey = d.CommKeyProtected is not null,
            }), Json));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            info.AppendLine(CultureInfo.InvariantCulture, $"The database could not be read: {ex.GetType().Name}: {ex.Message}");
        }

        Add(zip, "info.txt", info.ToString());

        if (Directory.Exists(environment.LogDirectory))
        {
            var since = DateTime.Now.AddDays(-7);
            foreach (var log in new DirectoryInfo(environment.LogDirectory).EnumerateFiles().Where(f => f.LastWriteTime >= since))
            {
                // The app may be writing the current log; share the file instead of locking it.
                await using var source = new FileStream(log.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var entry = zip.CreateEntry("logs/" + log.Name, CompressionLevel.Optimal);
                await using var target = entry.Open();
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }
        }

        return zipPath;
    }

    private static void Add(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
