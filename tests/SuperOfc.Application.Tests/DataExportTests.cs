using System.IO.Compression;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using SuperOfc.Application.Backups;
using SuperOfc.Application.Devices;
using SuperOfc.Application.Tests.Support;
using SuperOfc.Domain.Security;

namespace SuperOfc.Application.Tests;

/// <summary>Export all data and the diagnostics zip (P8-T3). Neither may contain secrets.</summary>
public class DataExportTests
{
    private static string AllText(string folder)
        => string.Join('\n', Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Select(f => f.EndsWith(".xlsx", StringComparison.Ordinal)
            ? string.Join('\n', new XLWorkbook(f).Worksheets.SelectMany(w => w.CellsUsed()).Select(c => c.GetFormattedString()))
            : File.ReadAllText(f)));

    [Fact]
    public async Task Every_table_is_exported_to_excel_and_json_without_secrets()
    {
        await using var host = await TestHost.CreateAsync();
        await EmployeeTestData.CreateAsync(host, "E1");
        await EmployeeTestData.CreateAsync(host, "E2");
        await host.SendOkAsync(new SaveDeviceCommand(null, "Gate", "10.0.0.9", 4370, 987654, 1, null, true));
        var passwordHash = await host.WithDbAsync(db => db.Users.Select(u => u.PasswordHash).FirstAsync());
        var protectedKey = await host.WithDbAsync(db => db.Devices.Select(d => d.CommKeyProtected).FirstAsync());

        var folder = Path.Combine(host.Root, "export");
        var result = await host.SendOkAsync(new ExportAllDataCommand(folder));
        result.Tables.Should().BeGreaterThan(30);
        result.Rows.Should().BeGreaterThan(0);
        File.Exists(Path.Combine(folder, "superofc-employees.xlsx")).Should().BeTrue();
        File.Exists(Path.Combine(folder, "superofc-payroll.xlsx")).Should().BeTrue();
        File.Exists(Path.Combine(folder, "README.txt")).Should().BeTrue();
        using (var book = new XLWorkbook(Path.Combine(folder, "superofc-employees.xlsx")))
        {
            var sheet = book.Worksheet("Employee");
            sheet.LastRowUsed()!.RowNumber().Should().Be(3, "a header and one row per employee");
            sheet.Row(1).Cells().Select(c => c.GetString()).Should().Contain("FullName");
            sheet.Column(sheet.Row(1).Cells().First(c => c.GetString() == "Code").Address.ColumnNumber).Cells(2, 3).Select(c => c.GetString()).Should().BeEquivalentTo(["E1", "E2"]);
        }


        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "superofc-data.json")));
        var employees = json.RootElement.GetProperty("modules").GetProperty("Employees").GetProperty("Employee");
        employees.GetArrayLength().Should().Be(2);
        employees[0].TryGetProperty("FullName", out _).Should().BeTrue();
        json.RootElement.GetProperty("modules").GetProperty("Devices").GetProperty("Device")[0].TryGetProperty("CommKeyProtected", out _).Should().BeFalse();

        var text = AllText(folder);
        text.Should().NotContain(passwordHash);
        text.Should().NotContain(protectedKey!);
        text.Should().NotContain("987654");
        (await host.WithDbAsync(db => db.AuditEntries.CountAsync(a => a.Action == "Data.Exported"))).Should().Be(1);

        await host.SignInAsAsync(Role.Viewer);
        (await host.SendFailAsync(new ExportAllDataCommand(folder))).Code.Should().Be("Auth.Forbidden");
    }

    [Fact]
    public async Task Diagnostics_zip_has_logs_versions_and_devices_but_no_keys()
    {
        await using var host = await TestHost.CreateAsync();
        await host.SendOkAsync(new SaveDeviceCommand(null, "Gate", "10.0.0.9", 4370, 987654, 1, null, true));
        var protectedKey = await host.WithDbAsync(db => db.Devices.Select(d => d.CommKeyProtected).FirstAsync());
        var logs = Path.Combine(host.Root, "logs");
        Directory.CreateDirectory(logs);
        await File.WriteAllTextAsync(Path.Combine(logs, "superofc-recent.log"), "recent line");
        var old = Path.Combine(logs, "superofc-old.log");
        await File.WriteAllTextAsync(old, "old line");
        File.SetLastWriteTime(old, DateTime.Now.AddDays(-30));

        // The current log is open for writing by the app; diagnostics must still read it.
        await using (new FileStream(Path.Combine(logs, "superofc-open.log"), FileMode.Create, FileAccess.Write, FileShare.Read))
        {
            await host.SignInAsAsync(Role.Viewer);
            var zipPath = await host.SendOkAsync(new ExportDiagnosticsCommand(Path.Combine(host.Root, "out", "diag.zip")));
            using var zip = ZipFile.OpenRead(zipPath);
            zip.Entries.Select(e => e.FullName).Should().Contain(["info.txt", "settings.json", "devices.json", "logs/superofc-recent.log", "logs/superofc-open.log"])
                .And.NotContain("logs/superofc-old.log");
            var all = string.Join('\n', zip.Entries.Select(e =>
            {
                using var reader = new StreamReader(e.Open());
                return reader.ReadToEnd();
            }));
            all.Should().Contain("10.0.0.9").And.Contain("\"HasCommKey\": true").And.Contain("Employees: ");
            all.Should().NotContain(protectedKey!).And.NotContain("987654");
        }
    }
}
