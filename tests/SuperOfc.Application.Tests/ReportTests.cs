using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SuperOfc.Application.Attendance;
using SuperOfc.Application.Employees;
using SuperOfc.Application.Leave;
using SuperOfc.Application.Loans;
using SuperOfc.Application.Organization;
using SuperOfc.Application.Payroll;
using SuperOfc.Application.Reports;
using SuperOfc.Application.Tests.Support;
using SuperOfc.Domain.Attendance;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Loans;
using SuperOfc.Domain.Organization;
using SuperOfc.Domain.Policies;
using SuperOfc.Domain.Security;

namespace SuperOfc.Application.Tests;

/// <summary>Report framework and core reports (P7-T1, P7-T2). Each report's totals are checked against the underlying data. Clock: 15 March 2026.</summary>
public class ReportTests
{
    private static readonly DateRange February = DateRange.Month(2026, 2);

    private static Task<ReportTable> RunAsync(TestHost host, string id, params (string, string?)[] args) => host.SendOkAsync(new RunReportQuery(id, ReportArgs.Of(args)));

    private static string Feb => ReportArgs.MonthValue(2026, 2);

    private static Task PunchAsync(TestHost host, Guid employee, DateOnly date, TimeOnly @in, TimeOnly @out)
        => host.WithDbAsync(async db =>
        {
            foreach (var time in new[] { @in, @out })
            {
                var at = date.ToDateTime(time);
                db.AttendancePunches.Add(new AttendancePunch
                {
                    Id = Guid.CreateVersion7(), CompanyId = host.CompanyId, EmployeeId = employee, LocalTime = at, PunchAtUtc = at.AddHours(-6),
                    Source = PunchSource.Device, DedupKey = AttendancePunch.DeviceKey("SN", employee.ToString("N"), at),
                });
            }

            return await db.SaveChangesAsync();
        });

    [Fact]
    public async Task Catalog_respects_roles_and_bad_choices_are_validation_errors()
    {
        await using var host = await TestHost.CreateAsync();
        var all = await host.SendOkAsync(new GetReportsQuery());
        all.Select(r => r.Id).Should().BeEquivalentTo(["attendance-summary", "late-absent", "overtime", "headcount", "salary-cost", "loan-ledger", "leave-balance"]);
        all.Should().OnlyContain(r => r.Parameters.Count > 0 && r.Description.Length > 0);

        (await host.SendFailAsync(new RunReportQuery("attendance-summary", ReportArgs.Of()))).Code.Should().Be("Validation.Month");
        (await host.SendFailAsync(new RunReportQuery("late-absent", ReportArgs.Of(("Period", "2026-03-10..2026-03-01"))))).Code.Should().Be("Validation.Period");
        (await host.SendFailAsync(new RunReportQuery("nope", ReportArgs.Of()))).Code.Should().Be("Report.NotFound");

        await host.SignInAsAsync(Role.Viewer);
        (await host.SendOkAsync(new GetReportsQuery())).Select(r => r.Id).Should().NotContain(["salary-cost", "loan-ledger"]);
        (await host.SendFailAsync(new RunReportQuery("salary-cost", ReportArgs.Of(("Month", Feb))))).Code.Should().Be("Auth.Forbidden");
        var overtime = await RunAsync(host, "overtime", ("Month", Feb));
        overtime.Columns.Should().NotContain(c => c.Header == "Overtime paid", "viewers cannot see pay");
    }

    [Fact]
    public async Task Attendance_summary_late_absent_and_overtime_match_the_attendance_engine()
    {
        await using var host = await TestHost.CreateAsync();
        var sales = await host.SendOkAsync(new SaveMasterDataCommand(MasterDataKind.Department, null, "Sales", "SAL", null, 0));
        var e1 = await EmployeeTestData.CreateAsync(host, "E1", new DateOnly(2025, 1, 1));
        var e2 = await EmployeeTestData.CreateAsync(host, "E2", new DateOnly(2025, 1, 1), sales);
        await PayrollTestData.PunchWorkingDaysAsync(host, [e1], February);
        foreach (var date in February.EachDay().Where(d => d.DayOfWeek is not (DayOfWeek.Friday or DayOfWeek.Saturday) && d.Day != 10))
        {
            // E2: late on the 3rd, absent on the 10th, two hours' overtime on the 17th.
            await PunchAsync(host, e2, date, date.Day == 3 ? new TimeOnly(9, 40) : new TimeOnly(9, 0), date.Day == 17 ? new TimeOnly(20, 0) : new TimeOnly(18, 0));
        }

        var engine = await host.WithScopeAsync(sp => sp.GetRequiredService<AttendanceEngine>().PreviewAsync([e1, e2], February, CancellationToken.None));
        var summary = await RunAsync(host, "attendance-summary", ("Month", Feb));
        summary.Rows.Should().HaveCount(2);
        foreach (var status in new[] { (AttendanceStatus.Present, "Present"), (AttendanceStatus.Late, "Late"), (AttendanceStatus.Absent, "Absent"), (AttendanceStatus.WeeklyOff, "Weekly off") })
        {
            summary.Sum(summary.ColumnIndex(status.Item2)).Should().Be(engine.Count(r => r.Result.Status == status.Item1), status.Item2);
            summary.Totals![summary.ColumnIndex(status.Item2)].Should().Be(engine.Count(r => r.Result.Status == status.Item1));
        }

        var e2Row = summary.Rows.Single(r => (string)r[0]! == "E2");
        e2Row[summary.ColumnIndex("Late")].Should().Be(1);
        e2Row[summary.ColumnIndex("Absent")].Should().Be(1);
        e2Row[summary.ColumnIndex("Department")].Should().Be("Sales");

        var byDepartment = await RunAsync(host, "attendance-summary", ("Month", Feb), ("GroupBy", "department"));
        byDepartment.Rows.Select(r => r[0]).Should().Equal("No department", "Sales");
        byDepartment.Totals![byDepartment.ColumnIndex("Present")].Should().Be(summary.Totals![summary.ColumnIndex("Present")]);
        (await RunAsync(host, "attendance-summary", ("Month", Feb), ("Department", sales.ToString()))).Rows.Should().ContainSingle();

        var list = await RunAsync(host, "late-absent", ("Period", ReportArgs.RangeValue(February.From, February.To!.Value)));
        list.Rows.Should().HaveCount(2);
        list.Rows[0][list.ColumnIndex("Status")].Should().Be("Late");
        list.Rows[0][list.ColumnIndex("First in")].Should().Be("09:40");
        list.Totals![list.ColumnIndex("Minutes late")].Should().Be(engine.Sum(r => r.Result.Status == AttendanceStatus.Late ? r.Result.LateMinutes : 0));
        (await RunAsync(host, "late-absent", ("Period", ReportArgs.RangeValue(February.From, February.To!.Value)), ("Show", "absent"))).Rows.Should().ContainSingle();

        var overtime = await RunAsync(host, "overtime", ("Month", Feb));
        overtime.Rows.Should().ContainSingle().Which[0].Should().Be("E2");
        overtime.Totals![overtime.ColumnIndex("Total hours")].Should().Be(engine.Sum(r => r.Result.OvertimeMinutes) / 60m);
        overtime.Notes.Should().Contain(n => n.Contains("not finalized", StringComparison.Ordinal));

        // March is only worked out up to today (the 15th).
        var march = await RunAsync(host, "attendance-summary", ("Month", ReportArgs.MonthValue(2026, 3)));
        march.Notes.Should().Contain(n => n.Contains("not happened yet", StringComparison.Ordinal));
        var marchDays = march.Rows.Sum(r => Enumerable.Range(3, 7).Where(i => i != 7).Sum(i => Convert.ToDecimal(r[i], System.Globalization.CultureInfo.InvariantCulture)));
        marchDays.Should().BeLessThanOrEqualTo(2 * 15);
    }

    [Fact]
    public async Task Headcount_opening_plus_joiners_minus_leavers_equals_closing()
    {
        await using var host = await TestHost.CreateAsync();
        var sales = await host.SendOkAsync(new SaveMasterDataCommand(MasterDataKind.Department, null, "Sales", "SAL", null, 0));
        await EmployeeTestData.CreateAsync(host, "E1", new DateOnly(2024, 1, 1));
        var leaver = await EmployeeTestData.CreateAsync(host, "E2", new DateOnly(2024, 6, 1), sales);
        await EmployeeTestData.CreateAsync(host, "E3", new DateOnly(2026, 1, 15), sales);
        var shortStay = await EmployeeTestData.CreateAsync(host, "E4", new DateOnly(2026, 1, 5));
        await EmployeeTestData.CreateAsync(host, "E5", new DateOnly(2026, 3, 1));
        await host.SendOkAsync(new RecordEmploymentEventCommand(leaver, EmploymentAction.Exit, new DateOnly(2026, 2, 28), "Resigned"));
        await host.SendOkAsync(new RecordEmploymentEventCommand(shortStay, EmploymentAction.Exit, new DateOnly(2026, 2, 10), "Resigned"));

        var report = await RunAsync(host, "headcount", ("Period", ReportArgs.RangeValue(new DateOnly(2026, 1, 1), new DateOnly(2026, 2, 28))));
        int Total(string column) => (int)report.Totals![report.ColumnIndex(column)]!;
        Total("At start").Should().Be(2);
        Total("Joined").Should().Be(2);
        Total("Left").Should().Be(2);
        Total("At end").Should().Be(2);
        foreach (var row in report.Rows)
        {
            ((int)row[1]! + (int)row[2]! - (int)row[3]!).Should().Be((int)row[4]!);
        }

        var active = await host.WithDbAsync(db => db.Employees.CountAsync(e => e.JoinDate <= new DateOnly(2026, 2, 28) && (e.ExitDate == null || e.ExitDate > new DateOnly(2026, 2, 28))));
        Total("At end").Should().Be(active);
    }

    [Fact]
    public async Task Salary_cost_and_loan_ledger_match_payroll_and_loans_and_export_to_pdf_and_excel()
    {
        await using var host = await TestHost.CreateAsync();
        var sales = await host.SendOkAsync(new SaveMasterDataCommand(MasterDataKind.Department, null, "Sales", "SAL", null, 0));
        var components = await PayrollTestData.ComponentsAsync(host);
        var e1 = await EmployeeTestData.CreateAsync(host, "E1", new DateOnly(2025, 1, 1));
        var e2 = await EmployeeTestData.CreateAsync(host, "E2", new DateOnly(2025, 1, 1), sales);
        foreach (var (e, basic) in new[] { (e1, 30_000m), (e2, 40_000m) })
        {
            await host.SendOkAsync(new SaveSalaryAssignmentCommand(null, e, new DateOnly(2025, 1, 1), null, null, PayrollTestData.Lines(components, basic)));
        }

        var loan = await host.SendOkAsync(new SaveLoanCommand(null, e1, LoanKind.Loan, Money.FromMajor(6_000).Minor, InterestMethod.None, 0, 6, 2026, 2, new DateOnly(2026, 1, 20), null));
        await host.SendOkAsync(new ApproveLoanCommand(loan));
        await host.SendOkAsync(new DisburseLoanCommand(loan, new DateOnly(2026, 1, 25), "CHQ-1"));
        await PayrollTestData.PunchWorkingDaysAsync(host, [e1, e2], February);
        var run = await host.SendOkAsync(new ComputePayrollCommand(2026, 2));
        await host.SendOkAsync(new FinalizePayrollRunCommand(run.RunId));
        await host.SendOkAsync(new RecordLoanRepaymentCommand(loan, new DateOnly(2026, 3, 5), Money.FromMajor(500).Minor, "CASH-1", null));

        var cost = await RunAsync(host, "salary-cost", ("Month", Feb));
        cost.Rows.Select(r => r[0]).Should().Equal("No department", "Sales");
        cost.Totals![cost.ColumnIndex("Gross")].Should().Be(run.Gross.Major);
        cost.Totals![cost.ColumnIndex("Net pay")].Should().Be(run.Net.Major);
        cost.Totals![cost.ColumnIndex("Deductions")].Should().Be(run.Deductions.Major);
        cost.Totals![cost.ColumnIndex("Employees")].Should().Be(2);
        (await RunAsync(host, "salary-cost", ("Month", ReportArgs.MonthValue(2026, 1)))).Notes.Should().Contain(n => n.Contains("not finalized", StringComparison.Ordinal));

        var ledger = await RunAsync(host, "loan-ledger", ("Period", ReportArgs.RangeValue(new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 31))));
        ledger.Rows.Select(r => r[ledger.ColumnIndex("Entry")]).Should().Equal("Paid out", "Salary deduction", "Cash repayment");
        ledger.Totals![ledger.ColumnIndex("Paid out")].Should().Be(6_000m);
        var repaid = await host.WithDbAsync(db => db.LoanTransactions.Where(t => t.Kind != LoanTransactionKind.Disbursement).SumAsync(t => t.AmountMinor));
        ledger.Totals![ledger.ColumnIndex("Repaid or cleared")].Should().Be(new Money(repaid).Major);

        var pdf = Path.Combine(host.Root, "cost.pdf");
        await host.SendOkAsync(new ExportReportCommand("salary-cost", ReportArgs.Of(("Month", Feb)), pdf, ReportFormat.Pdf));
        File.ReadAllBytes(pdf)[..5].Should().Equal("%PDF-"u8.ToArray());
        var xlsx = Path.Combine(host.Root, "ledger.xlsx");
        await host.SendOkAsync(new ExportReportCommand("loan-ledger", ReportArgs.Of(("Period", ReportArgs.RangeValue(new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 31)))), xlsx, ReportFormat.Excel));
        File.Exists(xlsx).Should().BeTrue();
        (await host.WithDbAsync(db => db.AuditEntries.CountAsync(a => a.Action == "Report.Exported"))).Should().Be(2);
    }

    [Fact]
    public async Task Leave_balances_match_each_employees_balance()
    {
        await using var host = await TestHost.CreateAsync();
        var e1 = await EmployeeTestData.CreateAsync(host, "E1", new DateOnly(2024, 1, 1));
        await EmployeeTestData.CreateAsync(host, "E2", new DateOnly(2024, 1, 1));
        var cl = (await host.SendOkAsync(new GetLeaveTypesQuery())).Single(t => t.Code == "CL").Id;
        await host.SendOkAsync(new CreateLeaveRequestCommand(e1, cl, new DateOnly(2026, 3, 8), new DateOnly(2026, 3, 10), false, "Family", ApproveNow: true));
        await host.SendOkAsync(new AdjustLeaveBalanceCommand(e1, cl, 2025, 2, "Carry-over correction"));

        var report = await RunAsync(host, "leave-balance", ("Year", "2025"));
        var types = (await host.SendOkAsync(new GetLeaveTypesQuery())).Count;
        report.Rows.Should().HaveCount(2 * types);
        var balances = await host.SendOkAsync(new GetLeaveBalancesQuery(e1, 2025));
        var clRow = report.Rows.Single(r => (string)r[0]! == "E1" && (string)r[3]! == "Casual leave");
        var clBalance = balances.Single(b => b.Code == "CL");
        clRow[report.ColumnIndex("Taken")].Should().Be(clBalance.Used);
        clRow[report.ColumnIndex("Adjusted")].Should().Be(2m);
        clRow[report.ColumnIndex("Left")].Should().Be(clBalance.Closing);
        foreach (var row in report.Rows)
        {
            ((decimal)row[4]! + (decimal)row[5]! - (decimal)row[6]! + (decimal)row[7]!).Should().Be((decimal)row[8]!);
        }

        (await RunAsync(host, "leave-balance", ("Year", "2025"), ("LeaveType", cl.ToString()))).Rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task Dashboard_figures_follow_payroll_loans_documents_and_roles()
    {
        await using var host = await TestHost.CreateAsync();
        var components = await PayrollTestData.ComponentsAsync(host);
        var e1 = await EmployeeTestData.CreateAsync(host, "E1", new DateOnly(2025, 1, 1));
        await host.SendOkAsync(new SaveSalaryAssignmentCommand(null, e1, new DateOnly(2025, 1, 1), null, null, PayrollTestData.Lines(components, 30_000)));

        var empty = await host.SendOkAsync(new GetDashboardQuery());
        empty.Payroll!.Status.Should().BeNull("February payroll has not started");
        empty.Loans!.Open.Should().Be(0);
        empty.TaxRulesMissing.Should().BeTrue();
        empty.Devices!.Devices.Should().Be(0);
        empty.DocumentsExpiring.Should().Be(0);

        var loan = await host.SendOkAsync(new SaveLoanCommand(null, e1, LoanKind.Loan, Money.FromMajor(6_000).Minor, InterestMethod.None, 0, 6, 2026, 2, new DateOnly(2026, 1, 20), null));
        await host.SendOkAsync(new ApproveLoanCommand(loan));
        await host.SendOkAsync(new DisburseLoanCommand(loan, new DateOnly(2026, 1, 25), null));
        await PayrollTestData.PunchWorkingDaysAsync(host, [e1], February);
        var run = await host.SendOkAsync(new ComputePayrollCommand(2026, 2));
        await host.SendOkAsync(new FinalizePayrollRunCommand(run.RunId));
        await host.WithDbAsync(async db =>
        {
            db.EmployeeDocuments.Add(new Domain.Employees.EmployeeDocument
            {
                Id = Guid.CreateVersion7(), CompanyId = host.CompanyId, EmployeeId = e1, Title = "Work permit", FilePath = "x", FileName = "x.pdf", ExpiryDate = new DateOnly(2026, 4, 1),
            });
            return await db.SaveChangesAsync();
        });

        var figures = await host.SendOkAsync(new GetDashboardQuery());
        figures.Payroll!.Status.Should().Be(Domain.Payroll.PayrollPeriodStatus.Locked);
        figures.Payroll.Net.Should().Be(run.Net);
        figures.Payroll.Employees.Should().Be(1);
        figures.Loans!.Open.Should().Be(1);
        figures.Loans.Outstanding.Should().Be(Money.FromMajor(5_000), "one 1,000 installment was deducted in February");
        figures.DocumentsExpiring.Should().Be(1);

        await host.SignInAsAsync(Role.Viewer);
        var viewer = await host.SendOkAsync(new GetDashboardQuery());
        viewer.Payroll.Should().BeNull();
        viewer.Loans.Should().BeNull();
        viewer.Devices.Should().BeNull();
        viewer.TaxRulesMissing.Should().BeFalse("viewers do not see tax");
        viewer.DocumentsExpiring.Should().Be(1);
    }
}
