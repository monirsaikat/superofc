// Performance benchmark (P8-T1). Builds a large office in a temporary data folder and times the operations that
// doc 01 "Product principles" and doc 08 put numbers on. Usage:
//   dotnet run -c Release --project tools/SuperOfc.Bench -- [employees=500] [output=docs/testing/perf.md]
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SuperOfc.Application;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Attendance;
using SuperOfc.Application.Backups;
using SuperOfc.Application.Common;
using SuperOfc.Application.Employees;
using SuperOfc.Application.Organization;
using SuperOfc.Application.Payroll;
using SuperOfc.Application.Reports;
using SuperOfc.Application.Setup;
using SuperOfc.Domain.Attendance;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Employees;
using SuperOfc.Domain.Organization;
using SuperOfc.Infrastructure;
using SuperOfc.Infrastructure.Persistence;
using SuperOfc.Infrastructure.Services;

var employeeCount = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 500;
var output = args.Length > 1 ? args[1] : null;
var root = Path.Combine(Path.GetTempPath(), "superofc-bench", Guid.NewGuid().ToString("N"));
var results = new List<(string Area, string Operation, double Ms, string Target, bool? Ok)>();

void Record(string area, string operation, double ms, string target = "", double? limitMs = null)
{
    bool? ok = limitMs is { } limit ? ms <= limit : null;
    results.Add((area, operation, ms, target, ok));
    Console.WriteLine($"{area,-12} {operation,-58} {ms,10:N0} ms {(ok is null ? string.Empty : ok.Value ? "OK" : "SLOW")}");
}

async Task<T> Time<T>(string area, string operation, Func<Task<T>> action, string target = "", double? limitMs = null)
{
    var watch = Stopwatch.StartNew();
    var value = await action();
    Record(area, operation, watch.Elapsed.TotalMilliseconds, target, limitMs);
    return value;
}

static T Ok<T>(Result<T> result) => result.IsSuccess ? result.Value : throw new InvalidOperationException(string.Join("; ", result.Errors.Select(e => e.Code + ": " + e.Message)));

var services = new ServiceCollection();
services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
services.AddApplication();
services.AddInfrastructure(new AppEnvironment(root));
await using var provider = services.BuildServiceProvider();
var dispatcher = provider.GetRequiredService<IDispatcher>();
var clock = provider.GetRequiredService<IClock>();

try
{
    // ---- Startup: create and migrate an empty database (the cold part of app start that is ours).
    await Time("Startup", "Create and migrate an empty database", async () => await provider.GetRequiredService<DatabaseInitializer>().InitializeAsync());
    Ok(await dispatcher.SendAsync(new CompleteSetupCommand("Bench Co", "BDT", "Tk", NumberGrouping.SouthAsian, "Bangladesh Standard Time", 7, "dd MMM yyyy",
        WorkingWeek.Default, "admin", "Administrator", "Bench12345!", false)));
    using (var scope = provider.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        provider.GetRequiredService<IUserSession>().SignIn(await db.Users.FirstAsync());
    }

    // ---- Seed: departments, employees, salaries.
    var departments = new List<Guid>();
    foreach (var name in new[] { "Accounts", "Sales", "Production", "Stores", "Admin" })
    {
        departments.Add(Ok(await dispatcher.SendAsync(new SaveMasterDataCommand(MasterDataKind.Department, null, name, name[..3].ToUpperInvariant(), null, 0))));
    }

    var today = clock.TodayIn(provider.GetRequiredService<ICompanyContext>().TimeZone);
    var firstMonth = new DateOnly(today.Year, today.Month, 1).AddMonths(-12);
    var ids = await Time("Seed", $"Create {employeeCount} employees through the app", async () =>
    {
        var list = new List<Guid>(employeeCount);
        for (var i = 1; i <= employeeCount; i++)
        {
            var input = new EmployeeInput($"E{i:0000}", $"Employee {i}", i % 2 == 0 ? Gender.Female : Gender.Male, null, null, null, null, null, null, firstMonth.AddMonths(-1),
                departments[i % departments.Count], null, null, null, PaymentMode.Bank, "Bank", null, $"ACC{i:000000}", null, true, null);
            list.Add(Ok(await dispatcher.SendAsync(new CreateEmployeeCommand(input, StartConfirmed: true))));
        }

        return list;
    });

    var components = Ok(await dispatcher.SendAsync(new GetSalaryComponentsQuery(ActiveOnly: true))).ToDictionary(c => c.Code);
    await Time("Seed", "Give every employee a salary", async () =>
    {
        foreach (var (id, i) in ids.Select((id, i) => (id, i)))
        {
            Ok(await dispatcher.SendAsync(new SaveSalaryAssignmentCommand(null, id, firstMonth.AddMonths(-1), null, null,
                [new SalaryLineInput(components["BASIC"].Id, Money.FromMajor(20_000 + (i % 50) * 1_000).Minor, 0)])));
        }

        return 0;
    });

    // ---- Punches: in and out on every working day of the last months until there are about 100,000.
    var punchCount = await Time("Seed", "Insert punches directly", async () =>
    {
        var working = new List<DateOnly>();
        for (var d = new DateOnly(today.Year, today.Month, 1).AddDays(-1); working.Count * employeeCount * 2 < 100_000; d = d.AddDays(-1))
        {
            if (d.DayOfWeek is not (DayOfWeek.Friday or DayOfWeek.Saturday))
            {
                working.Add(d);
            }
        }

        var company = provider.GetRequiredService<IUserSession>().CompanyId;
        var rng = new Random(42);
        var total = 0;
        foreach (var chunk in ids.Chunk(50))
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ChangeTracker.AutoDetectChangesEnabled = false;
            foreach (var employee in chunk)
            {
                foreach (var date in working)
                {
                    foreach (var at in new[] { date.ToDateTime(new TimeOnly(8, 45)).AddMinutes(rng.Next(30)), date.ToDateTime(new TimeOnly(18, rng.Next(50))) })
                    {
                        db.AttendancePunches.Add(new AttendancePunch
                        {
                            Id = Guid.CreateVersion7(), CompanyId = company, EmployeeId = employee, LocalTime = at, PunchAtUtc = at.AddHours(-6),
                            Source = PunchSource.Device, DedupKey = AttendancePunch.DeviceKey("BENCH", employee.ToString("N"), at),
                        });
                        total++;
                    }
                }
            }

            await db.SaveChangesAsync();
        }

        return total;
    });
    Console.WriteLine($"{punchCount:N0} punches");

    // ---- Lists and attendance.
    await Time("Lists", "Employees page: first 200 sorted by name", async () =>
        Ok(await dispatcher.SendAsync(new GetEmployeeListQuery(Sort: EmployeeSort.Name))), "< 300 ms", 300);
    await Time("Lists", "Employees page: search 'Employee 4'", async () =>
        Ok(await dispatcher.SendAsync(new GetEmployeeListQuery(Search: "Employee 4"))), "< 300 ms", 300);
    var lastMonth = new DateOnly(today.Year, today.Month, 1).AddMonths(-1);
    await Time("Attendance", $"Recompute one month for {employeeCount} employees", async () =>
    {
        using var scope = provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AttendanceEngine>().RecomputeAsync(null, DateRange.Month(lastMonth.Year, lastMonth.Month), CancellationToken.None);
    }, "< 5 s", 5_000);
    await Time("Attendance", "Monthly attendance sheet (all employees)", async () =>
        Ok(await dispatcher.SendAsync(new GetAttendanceMonthQuery(lastMonth.Year, lastMonth.Month))), "< 2 s", 2_000);

    // ---- Payroll: 12 months, compute and finalize each.
    var computeTimes = new List<double>();
    PayrollRunSummary? last = null;
    for (var m = firstMonth; m <= lastMonth; m = m.AddMonths(1))
    {
        var watch = Stopwatch.StartNew();
        last = Ok(await dispatcher.SendAsync(new ComputePayrollCommand(m.Year, m.Month)));
        computeTimes.Add(watch.Elapsed.TotalMilliseconds);
        Ok(await dispatcher.SendAsync(new FinalizePayrollRunCommand(last.RunId)));
    }

    Record("Payroll", $"Compute payroll for {employeeCount} employees (slowest of 12 months)", computeTimes.Max(), "< 5 s", 5_000);
    Record("Payroll", "Compute payroll (average of 12 months)", computeTimes.Average(), "< 5 s", 5_000);
    await Time("Payroll", $"Payslip PDFs for {employeeCount} employees, one file", async () =>
        Ok(await dispatcher.SendAsync(new ExportPayslipsPdfCommand(last!.RunId, Path.Combine(root, "out", "payslips.pdf"), false))), "< 30 s", 30_000);
    await Time("Payroll", "Payroll register (one month)", async () => Ok(await dispatcher.SendAsync(new GetPayrollRegisterQuery(last!.RunId))), "< 2 s", 2_000);

    // ---- Reports and dashboard.
    var month = ReportArgs.MonthValue(lastMonth.Year, lastMonth.Month);
    var period = ReportArgs.RangeValue(lastMonth, lastMonth.AddMonths(1).AddDays(-1));
    foreach (var (id, reportArgs) in new (string, ReportArgs)[]
             {
                 ("attendance-summary", ReportArgs.Of(("Month", month))),
                 ("late-absent", ReportArgs.Of(("Period", period))),
                 ("overtime", ReportArgs.Of(("Month", month))),
                 ("headcount", ReportArgs.Of(("Period", ReportArgs.RangeValue(firstMonth, lastMonth.AddMonths(1).AddDays(-1))))),
                 ("salary-cost", ReportArgs.Of(("Month", month))),
                 ("loan-ledger", ReportArgs.Of(("Period", period))),
                 ("leave-balance", ReportArgs.Of(("Year", (lastMonth.Month >= 7 ? lastMonth.Year : lastMonth.Year - 1).ToString(CultureInfo.InvariantCulture)))),
             })
    {
        await Time("Reports", $"Preview {id} (one month)", async () => Ok(await dispatcher.SendAsync(new RunReportQuery(id, reportArgs))), "< 2 s", 2_000);
    }

    await Time("Reports", "Attendance summary as PDF", async () =>
        Ok(await dispatcher.SendAsync(new ExportReportCommand("attendance-summary", ReportArgs.Of(("Month", month)), Path.Combine(root, "out", "summary.pdf"), ReportFormat.Pdf))));

    // Warm once (first query compiles), then time what the user waits for on every visit.
    await DashboardAsync();
    await Time("Dashboard", "All dashboard queries together", async () => { await DashboardAsync(); return 0; }, "< 300 ms", 300);

    // ---- Data export and backup.
    await Time("Data", "Export all data (Excel per module + JSON)", async () => Ok(await dispatcher.SendAsync(new ExportAllDataCommand(Path.Combine(root, "out", "export")))),
        "< 30 s", 30_000);
    await Time("Data", "Manual backup", async () => Ok(await dispatcher.SendAsync(new CreateBackupCommand("bench"))));
    var dbSize = new FileInfo(new AppEnvironment(root).DatabasePath).Length / 1024.0 / 1024.0;

    // ---- Report.
    var md = new StringBuilder();
    md.AppendLine("# Performance results (P8-T1)");
    md.AppendLine();
    md.AppendLine(CultureInfo.InvariantCulture, $"Measured {DateTime.Now:yyyy-MM-dd} with `dotnet run -c Release --project tools/SuperOfc.Bench -- {employeeCount}`.");
    md.AppendLine();
    md.AppendLine(CultureInfo.InvariantCulture, $"- Machine: {Environment.ProcessorCount} logical processors, {RuntimeInformation.OSDescription}, {RuntimeInformation.FrameworkDescription}.");
    md.AppendLine(CultureInfo.InvariantCulture, $"- Data: {employeeCount} employees, {punchCount:N0} punches, 12 finalized payroll months, database {dbSize:0.0} MB.");
    md.AppendLine("- Times are wall-clock for one run, after the database was created. First-query compilation is included except for the dashboard.");
    md.AppendLine();
    md.AppendLine("| Area | Operation | Time | Target | Result |");
    md.AppendLine("|------|-----------|-----:|--------|--------|");
    foreach (var r in results)
    {
        var time = r.Ms >= 10_000 ? $"{r.Ms / 1000:0.0} s" : $"{r.Ms:N0} ms";
        md.AppendLine(CultureInfo.InvariantCulture, $"| {r.Area} | {r.Operation} | {time} | {r.Target} | {(r.Ok is null ? string.Empty : r.Ok.Value ? "Met" : "**Missed**")} |");
    }

    md.AppendLine();
    md.AppendLine("Not measured here: WPF cold start to a visible window (the app logs \"Shell visible after N ms\" at every start; check it on the release test machine) and device sync over the LAN (needs a terminal, see `device-matrix.md`).");
    Console.WriteLine();
    Console.WriteLine(md.ToString());
    if (output is not null)
    {
        await File.WriteAllTextAsync(output, md.ToString());
        Console.WriteLine($"Written to {output}");
    }

    return results.Any(r => r.Ok == false) ? 1 : 0;
}
finally
{
    await provider.DisposeAsync();
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    try
    {
        Directory.Delete(root, recursive: true);
    }
    catch (IOException)
    {
    }
}

async Task DashboardAsync()
{
    var today = clock.TodayIn(provider.GetRequiredService<ICompanyContext>().TimeZone);
    Ok(await dispatcher.SendAsync(new GetHeadcountQuery(today)));
    Ok(await dispatcher.SendAsync(new GetTodayAttendanceQuery()));
    Ok(await dispatcher.SendAsync(new GetDashboardQuery()));
    Ok(await dispatcher.SendAsync(new GetBackupsQuery()));
}
