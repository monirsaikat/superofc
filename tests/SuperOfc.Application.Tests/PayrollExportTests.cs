using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Common;
using SuperOfc.Application.Payroll;
using SuperOfc.Application.Tests.Support;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Employees;
using SuperOfc.Domain.Organization;
using SuperOfc.Domain.Payroll;
using Xunit.Abstractions;

namespace SuperOfc.Application.Tests;

public class AmountInWordsTests
{
    [Theory]
    [InlineData(4_950_000, "BDT", NumberGrouping.SouthAsian, "Taka Forty-Nine Thousand Five Hundred Only")]
    [InlineData(12_345_678_950, "BDT", NumberGrouping.SouthAsian, "Taka Twelve Crore Thirty-Four Lakh Fifty-Six Thousand Seven Hundred Eighty-Nine and Fifty Paisa Only")]
    [InlineData(100_000_000, "INR", NumberGrouping.SouthAsian, "Rupees Ten Lakh Only")]
    [InlineData(123_456_700, "USD", NumberGrouping.Western, "Dollars One Million Two Hundred Thirty-Four Thousand Five Hundred Sixty-Seven Only")]
    [InlineData(0, "USD", NumberGrouping.Western, "Dollars Zero Only")]
    [InlineData(-1_500, "EUR", NumberGrouping.Western, "Minus Euros Fifteen Only")]
    [InlineData(11_900, "XYZ", NumberGrouping.Western, "XYZ One Hundred Nineteen Only")]
    public void Spells_amounts(long minor, string currency, NumberGrouping grouping, string expected)
        => AmountInWords.Format(new Money(minor), currency, grouping).Should().Be(expected);
}

public class PayrollExportTests(ITestOutputHelper output)
{
    private static readonly DateRange February = DateRange.Month(2026, 2);

    private static async Task<(TestHost Host, PayrollRunSummary Summary)> ComputedAsync(int employees, Action<int, Employee>? configure = null)
    {
        var host = await TestHost.CreateAsync();
        var components = await PayrollTestData.ComponentsAsync(host);
        var lines = PayrollTestData.Lines(components, 30_000);
        var ids = new List<Guid>();
        var departments = new[] { Guid.CreateVersion7(), Guid.CreateVersion7() };
        await host.WithDbAsync(async db =>
        {
            db.Departments.Add(new Department { Id = departments[0], CompanyId = host.CompanyId, Name = "Accounts" });
            db.Departments.Add(new Department { Id = departments[1], CompanyId = host.CompanyId, Name = "Sales" });
            for (var i = 0; i < employees; i++)
            {
                var employee = new Employee
                {
                    Id = Guid.CreateVersion7(), CompanyId = host.CompanyId, Code = $"E{i:0000}", FullName = $"Person {i}", JoinDate = new DateOnly(2025, 1, 1),
                    DepartmentId = departments[i % 2], PaymentMode = i % 3 == 0 ? PaymentMode.Cash : PaymentMode.Bank, BankName = "City Bank", BankAccount = $"100{i:00000}",
                };
                configure?.Invoke(i, employee);
                ids.Add(employee.Id);
                db.Employees.Add(employee);
                var assignment = new SalaryAssignment { Id = Guid.CreateVersion7(), CompanyId = host.CompanyId, EmployeeId = employee.Id, EffectiveFrom = new DateOnly(2025, 1, 1) };
                assignment.Lines = lines.Select(l => new SalaryAssignmentLine { Id = Guid.CreateVersion7(), AssignmentId = assignment.Id, ComponentId = l.ComponentId, AmountMinor = l.AmountMinor + (i * 1_000), Rate = l.Rate }).ToList();
                db.SalaryAssignments.Add(assignment);
            }

            return await db.SaveChangesAsync();
        });
        await PayrollTestData.PunchWorkingDaysAsync(host, ids, February);
        return (host, await host.SendOkAsync(new ComputePayrollCommand(2026, 2)));
    }

    [Fact]
    public async Task Register_totals_equal_the_sum_of_payslips_exactly()
    {
        var (host, summary) = await ComputedAsync(7);
        await using var _ = host;

        var register = await host.SendOkAsync(new GetPayrollRegisterQuery(summary.RunId));

        register.Rows.Should().HaveCount(7);
        register.Columns.Select(c => c.Code).Should().Equal("BASIC", "HRA", "MEDICAL", "CONV");
        register.Total.Net.Should().Be(summary.Net);
        register.Total.Gross.Should().Be(new Money(register.Rows.Sum(r => r.Gross.Minor)));
        register.Departments.Select(d => d.EmployeeName).Should().Equal("Accounts", "Sales");
        new Money(register.Departments.Sum(d => d.Net.Minor)).Should().Be(register.Total.Net);
        foreach (var column in register.Columns)
        {
            register.Total.Amounts[column.Code].Minor.Should().Be(register.Rows.Sum(r => r.Amounts[column.Code].Minor), column.Code);
        }

        var path = Path.Combine(host.Root, "register.xlsx");
        (await host.SendOkAsync(new ExportPayrollRegisterCommand(summary.RunId, path))).Rows.Should().Be(7);
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public async Task Payment_sheets_split_bank_and_cash()
    {
        var (host, summary) = await ComputedAsync(6);
        await using var _ = host;
        var sheets = host.Services.GetService(typeof(ISpreadsheetService)) as ISpreadsheetService;

        var bank = Path.Combine(host.Root, "bank.csv");
        (await host.SendOkAsync(new ExportPaymentSheetCommand(summary.RunId, bank, PaymentSheetKind.Bank))).Rows.Should().Be(4);
        var rows = sheets!.Read(bank);
        rows.Should().HaveCount(5, "four employees and a total row");
        rows[0]["Account number"].Should().Be("10000001");
        rows[0]["Reference"].Should().Be("Salary Feb 2026");

        var cash = Path.Combine(host.Root, "cash.csv");
        (await host.SendOkAsync(new ExportPaymentSheetCommand(summary.RunId, cash, PaymentSheetKind.Cash))).Rows.Should().Be(2);
        sheets.Read(cash).Select(r => r["Employee code"]).Should().Equal("E0000", "E0003", string.Empty);
    }

    [Fact]
    public async Task Five_hundred_payslips_render_to_pdf_in_under_twenty_seconds()
    {
        var (host, summary) = await ComputedAsync(500);
        await using var _ = host;
        var merged = Path.Combine(host.Root, "payslips.pdf");

        var watch = Stopwatch.StartNew();
        var result = await host.SendOkAsync(new ExportPayslipsPdfCommand(summary.RunId, merged, OneFilePerEmployee: false));
        watch.Stop();

        output.WriteLine($"500 payslips to one PDF: {watch.ElapsedMilliseconds} ms, {new FileInfo(merged).Length / 1024} KB");
        result.Rows.Should().Be(500);
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20));
        var bytes = await File.ReadAllBytesAsync(merged);
        System.Text.Encoding.ASCII.GetString(bytes, 0, 5).Should().Be("%PDF-");

        var folder = Path.Combine(host.Root, "split");
        var run = await host.SendOkAsync(new GetPayrollRunQuery(summary.RunId));
        var split = await host.SendOkAsync(new ExportPayslipsPdfCommand(summary.RunId, folder, OneFilePerEmployee: true, [run.Payslips[0].Id, run.Payslips[1].Id]));
        split.Files.Should().Be(2);
        Directory.GetFiles(folder).Select(Path.GetFileName).Should().BeEquivalentTo(["2026-02 E0000 Person 0.pdf", "2026-02 E0001 Person 1.pdf"]);
        (await host.WithDbAsync(db => db.AuditEntries.CountAsync(a => a.Action == "Payroll.PayslipsExported"))).Should().Be(2);
    }
}
