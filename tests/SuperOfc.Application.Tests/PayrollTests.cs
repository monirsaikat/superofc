using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using SuperOfc.Application.Attendance;
using SuperOfc.Application.Leave;
using SuperOfc.Application.Payroll;
using SuperOfc.Application.Tests.Support;
using SuperOfc.Domain.Attendance;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Employees;
using SuperOfc.Domain.Payroll;
using SuperOfc.Domain.Security;
using Xunit.Abstractions;

namespace SuperOfc.Application.Tests;

// Test clock: 2026-03-15 (Dhaka). February 2026 has 28 days; Fridays and Saturdays off (8 days), 20 working days.
public class SalarySetupTests
{
    [Fact]
    public async Task New_company_has_default_components_and_a_standard_structure()
    {
        await using var host = await TestHost.CreateAsync();
        var components = await host.SendOkAsync(new GetSalaryComponentsQuery());
        components.Select(c => c.Code).Should().Contain(["BASIC", "HRA", "MEDICAL", "CONV", "PF"]);
        components.Single(c => c.IsBasic).Code.Should().Be("BASIC");
        components.Single(c => c.Code == "PF").IsActive.Should().BeFalse("provident fund is a placeholder until the company decides (Q13)");

        var structure = (await host.SendOkAsync(new GetSalaryStructuresQuery())).Single();
        structure.Lines.Select(l => l.Code).Should().Equal("BASIC", "HRA", "MEDICAL", "CONV");
    }

    [Fact]
    public async Task Formula_components_are_validated_and_cycles_rejected()
    {
        await using var host = await TestHost.CreateAsync();
        static SaveSalaryComponentCommand Formula(string code, string formula, Guid? id = null)
            => new(id, code, code, ComponentKind.Earning, ComponentCalc.Formula, 0, 0, formula, false, true, true, 10, true);

        var a = await host.SendOkAsync(Formula("SPECIAL", "(BASIC + HRA) * 0.1"));
        (await host.SendFailAsync(Formula("BAD", "BASIC ^ 2"))).Code.Should().Be("Validation.Formula");
        (await host.SendFailAsync(Formula("BAD", "NOPE * 2"))).Code.Should().Be("Validation.Formula");
        (await host.SendFailAsync(Formula("SELF", "SELF + 1"))).Code.Should().Be("Validation.Formula");

        // SPECIAL2 uses SPECIAL; making SPECIAL use SPECIAL2 closes a loop.
        await host.SendOkAsync(Formula("SPECIAL2", "SPECIAL * 2"));
        (await host.SendFailAsync(Formula("SPECIAL", "SPECIAL2 + 1", a))).Code.Should().Be("Validation.Formula");

        // A code used by a formula cannot be renamed or deleted.
        (await host.SendFailAsync(Formula("RENAMED", "(BASIC + HRA) * 0.1", a))).Code.Should().Be("Validation.Code");
        (await host.SendFailAsync(new DeleteSalaryComponentCommand(a))).Message.Should().Contain("SPECIAL2");

        (await host.SendFailAsync(new SaveSalaryComponentCommand(null, "hra", "Duplicate", ComponentKind.Earning, ComponentCalc.Fixed, 0, 0, null, false, true, true, 1, true)))
            .Code.Should().Be("Validation.Code");
        (await host.SendFailAsync(new SaveSalaryComponentCommand(null, "1X", "Bad code", ComponentKind.Earning, ComponentCalc.Fixed, 0, 0, null, false, true, true, 1, true)))
            .Code.Should().Be("Validation.Code");
    }

    [Fact]
    public async Task Structure_preview_shows_monthly_amounts()
    {
        await using var host = await TestHost.CreateAsync();
        var components = (await host.SendOkAsync(new GetSalaryComponentsQuery(ActiveOnly: true))).ToDictionary(c => c.Code);
        var lines = PayrollTestData.Lines(components, 30_000);

        var preview = await host.SendOkAsync(new PreviewSalaryQuery(lines));

        preview.Lines.Select(l => (l.Code, l.Amount.Major)).Should().Equal(("BASIC", 30_000m), ("HRA", 15_000m), ("MEDICAL", 1_500m), ("CONV", 1_000m));
        preview.Gross.Major.Should().Be(47_500m);
        preview.Net.Should().Be(preview.Gross);

        var id = await host.SendOkAsync(new SaveSalaryStructureCommand(null, "Managers", "Senior staff", lines));
        (await host.SendOkAsync(new GetSalaryStructuresQuery())).Should().Contain(s => s.Id == id && s.Lines.Count == 4);
        (await host.SendFailAsync(new SaveSalaryStructureCommand(null, "No basic", null, lines.Skip(1).ToList()))).Code.Should().Be("Validation.Lines");
    }
}

public class SalaryAssignmentTests
{
    [Fact]
    public async Task New_salary_closes_the_previous_one_and_overlaps_are_rejected()
    {
        await using var host = await TestHost.CreateAsync();
        var e = await EmployeeTestData.CreateAsync(host, "E1");
        var components = await PayrollTestData.ComponentsAsync(host);
        var first = await host.SendOkAsync(new SaveSalaryAssignmentCommand(null, e, new DateOnly(2025, 1, 1), null, "Joining", PayrollTestData.Lines(components, 30_000)));
        await host.SendOkAsync(new SaveSalaryAssignmentCommand(null, e, new DateOnly(2026, 1, 1), null, "Raise", PayrollTestData.Lines(components, 33_000)));

        var history = await host.SendOkAsync(new GetSalaryAssignmentsQuery(e));
        history.Should().HaveCount(2);
        history.Single(h => h.Id == first).EffectiveTo.Should().Be(new DateOnly(2025, 12, 31));
        history[0].Gross.Major.Should().Be(33_000 + 16_500 + 1_500 + 1_000);

        (await host.SendFailAsync(new SaveSalaryAssignmentCommand(null, e, new DateOnly(2025, 6, 1), null, null, PayrollTestData.Lines(components, 1)))).Code.Should().Be("Validation.EffectiveFrom");
        (await host.SendFailAsync(new SaveSalaryAssignmentCommand(null, e, new DateOnly(2026, 1, 1), null, null, PayrollTestData.Lines(components, 1)))).Code.Should().Be("Validation.EffectiveFrom");

        // Deleting the latest lets the previous one run on again.
        await host.SendOkAsync(new DeleteSalaryAssignmentCommand(history[0].Id));
        (await host.SendOkAsync(new GetSalaryAssignmentsQuery(e))).Single().EffectiveTo.Should().BeNull();
    }

    [Fact]
    public async Task Bulk_revision_adds_new_salaries_and_keeps_history()
    {
        await using var host = await TestHost.CreateAsync();
        var components = await PayrollTestData.ComponentsAsync(host);
        var e1 = await EmployeeTestData.CreateAsync(host, "E1");
        var e2 = await EmployeeTestData.CreateAsync(host, "E2");
        var e3 = await EmployeeTestData.CreateAsync(host, "E3");
        await host.SendOkAsync(new SaveSalaryAssignmentCommand(null, e1, new DateOnly(2025, 1, 1), null, null, PayrollTestData.Lines(components, 30_000)));
        await host.SendOkAsync(new SaveSalaryAssignmentCommand(null, e2, new DateOnly(2025, 1, 1), null, null, PayrollTestData.Lines(components, 25_555)));

        var preview = await host.SendOkAsync(new PreviewSalaryRevisionQuery([e1, e2, e3], new DateOnly(2026, 4, 1), 10, AllFixedEarnings: false));
        preview.Single(r => r.EmployeeId == e1).NewBasic.Major.Should().Be(33_000);
        preview.Single(r => r.EmployeeId == e2).NewBasic.Major.Should().Be(28_111, "25,555 + 10 % rounded to whole units");
        preview.Single(r => r.EmployeeId == e3).Problem.Should().NotBeNull();

        var count = await host.SendOkAsync(new ApplySalaryRevisionCommand([e1, e2, e3], new DateOnly(2026, 4, 1), 10, false, null));

        count.Should().Be(2);
        var history = await host.SendOkAsync(new GetSalaryAssignmentsQuery(e1));
        history.Should().HaveCount(2);
        history[1].Lines.Single(l => l.Code == "BASIC").Amount.Major.Should().Be(30_000, "the old salary is not edited");
        history[1].EffectiveTo.Should().Be(new DateOnly(2026, 3, 31));
        history[0].Lines.Single(l => l.Code == "MEDICAL").Amount.Major.Should().Be(1_500, "only basic was revised");
    }
}

public class PayrollRunTests(ITestOutputHelper output)
{
    private static readonly DateRange February = DateRange.Month(2026, 2);

    [Fact]
    public async Task Compute_finalize_lock_void_and_pay()
    {
        await using var host = await TestHost.CreateAsync();
        var components = await PayrollTestData.ComponentsAsync(host);
        var e1 = await EmployeeTestData.CreateAsync(host, "E1");
        var e2 = await EmployeeTestData.CreateAsync(host, "E2", join: new DateOnly(2026, 2, 16));
        await host.SendOkAsync(new SaveSalaryAssignmentCommand(null, e1, new DateOnly(2025, 1, 1), null, null, PayrollTestData.Lines(components, 28_000)));
        await host.SendOkAsync(new SaveSalaryAssignmentCommand(null, e2, new DateOnly(2026, 2, 16), null, null, PayrollTestData.Lines(components, 28_000)));
        await PayrollTestData.PunchWorkingDaysAsync(host, [e1, e2], February);
        await host.SendOkAsync(new SavePayrollAdjustmentCommand(null, e1, 2026, 2, "Festival bonus", ComponentKind.Earning, 500_000, true, "Eid"));

        (await host.SendFailAsync(new ComputePayrollCommand(2026, 3))).Code.Should().Be("Validation.Month", "March has not ended");
        var summary = await host.SendOkAsync(new ComputePayrollCommand(2026, 2));

        summary.Employees.Should().Be(2);
        var run = await host.SendOkAsync(new GetPayrollRunQuery(summary.RunId));
        run.IsDraft.Should().BeTrue();
        var p1 = await host.SendOkAsync(new GetPayslipQuery(run.Payslips.Single(p => p.EmployeeId == e1).Id));
        p1.PayableDays.Should().Be(28);
        p1.EarningLines.Select(l => (l.Code, l.Amount.Major)).Should().Equal(("BASIC", 28_000m), ("HRA", 14_000m), ("MEDICAL", 1_500m), ("CONV", 1_000m), ("ADJ", 5_000m));
        p1.Gross.Major.Should().Be(49_500);
        var p2 = await host.SendOkAsync(new GetPayslipQuery(run.Payslips.Single(p => p.EmployeeId == e2).Id));
        p2.CalendarDays.Should().Be(13);
        p2.EarningLines.Single(l => l.Code == "BASIC").Amount.Major.Should().Be(13_000, "28,000 x 13/28");
        run.Net.Should().Be(new Money(run.Payslips.Sum(p => p.Net.Minor)));
        (await host.WithDbAsync(db => db.Payslips.AllAsync(p => p.NetMinor == p.GrossMinor - p.DeductionsMinor))).Should().BeTrue();

        // HR can compute but not finalize.
        await host.SignInAsAsync(Role.HR);
        (await host.SendFailAsync(new FinalizePayrollRunCommand(run.Id))).Code.Should().Be("Auth.Forbidden");
        await host.SignInAsAsync(Role.Admin);

        // A change after computing must be computed again before finalizing.
        host.Clock.Advance(TimeSpan.FromMinutes(5));
        await host.SendOkAsync(new SavePayrollAdjustmentCommand(null, e2, 2026, 2, "Canteen", ComponentKind.Deduction, 20_000, false, null));
        (await host.SendFailAsync(new FinalizePayrollRunCommand(run.Id))).Code.Should().Be("Concurrency.Conflict");
        host.Clock.Advance(TimeSpan.FromMinutes(5));
        summary = await host.SendOkAsync(new ComputePayrollCommand(2026, 2));
        summary.RunNumber.Should().Be(1, "the draft was replaced");
        await host.SendOkAsync(new FinalizePayrollRunCommand(summary.RunId));

        // Finalized February is locked everywhere.
        var date = new DateOnly(2026, 2, 10);
        (await host.SendFailAsync(new CorrectAttendanceDayCommand(e1, date, AttendanceStatus.Absent, 0, 0, "test"))).Code.Should().Be("Validation.Date");
        (await host.SendFailAsync(new AddManualPunchCommand(e1, new DateTime(2026, 2, 10, 20, 0, 0), PunchType.Out, "late entry"))).Code.Should().Be("Validation.LocalTime");
        (await host.SendFailAsync(new SaveSalaryAssignmentCommand(null, e1, new DateOnly(2026, 2, 20), null, null, PayrollTestData.Lines(components, 1)))).Code.Should().Be("Validation.EffectiveFrom");
        (await host.SendFailAsync(new SavePayrollAdjustmentCommand(null, e1, 2026, 2, "Late bonus", ComponentKind.Earning, 100, true, null))).Code.Should().Be("Validation.Month");
        (await host.SendFailAsync(new ComputePayrollCommand(2026, 2))).Code.Should().Be("Validation.Month");
        var leaveType = (await host.SendOkAsync(new GetLeaveTypesQuery())).First();
        (await host.SendFailAsync(new CreateLeaveRequestCommand(e1, leaveType.Id, date, date, false, "x", null, true))).Code.Should().Be("Validation.FromDate");
        var dayStart = date.ToDateTime(TimeOnly.MinValue);
        await host.WithDbAsync(db => db.AttendancePunches.Where(p => p.EmployeeId == e1 && p.LocalTime >= dayStart && p.LocalTime < dayStart.AddDays(1)).ExecuteDeleteAsync());
        await host.SendOkAsync(new RecomputeAttendanceCommand(February.From, February.To!.Value));
        (await host.WithDbAsync(db => db.AttendanceDays.CountAsync(d => d.EmployeeId == e1 && d.Date == date && d.Status == AttendanceStatus.Present))).Should().Be(1, "recompute leaves locked days alone");

        // Paid payslips must be marked unpaid before voiding.
        var finalRun = await host.SendOkAsync(new GetPayrollRunQuery(summary.RunId));
        await host.SendOkAsync(new MarkPayslipsPaidCommand([finalRun.Payslips[0].Id], true, new DateOnly(2026, 3, 1), "BANK-1"));
        (await host.SendOkAsync(new GetPayrollPeriodsQuery())).Periods.Single().Status.Should().Be(PayrollPeriodStatus.Locked);
        await host.SendOkAsync(new MarkPayslipsPaidCommand([finalRun.Payslips[1].Id], true, new DateOnly(2026, 3, 1), "BANK-1"));
        (await host.SendOkAsync(new GetPayrollPeriodsQuery())).Periods.Single().Status.Should().Be(PayrollPeriodStatus.Paid);
        (await host.SendFailAsync(new VoidPayrollRunCommand(summary.RunId, "Wrong rates"))).Message.Should().Contain("paid");
        await host.SendOkAsync(new MarkPayslipsPaidCommand(finalRun.Payslips.Select(p => p.Id).ToList(), false, null, null));

        await host.SendOkAsync(new VoidPayrollRunCommand(summary.RunId, "Wrong rates"));
        var periods = await host.SendOkAsync(new GetPayrollPeriodsQuery());
        periods.Periods.Single().Status.Should().Be(PayrollPeriodStatus.Open);
        await host.SendOkAsync(new CorrectAttendanceDayCommand(e1, date, AttendanceStatus.Absent, 0, 0, "Was on unpaid trip"));
        var second = await host.SendOkAsync(new ComputePayrollCommand(2026, 2));
        second.RunNumber.Should().Be(2);
        var audit = await host.WithDbAsync(db => db.AuditEntries.Where(a => a.Action.StartsWith("Payroll.")).Select(a => a.Action).ToListAsync());
        audit.Should().Contain(["Payroll.Computed", "Payroll.Finalized", "Payroll.MarkedPaid", "Payroll.Voided"]);
    }

    [Fact]
    public async Task Employee_without_salary_gets_a_warning_and_negative_net_blocks_finalize()
    {
        await using var host = await TestHost.CreateAsync();
        var components = await PayrollTestData.ComponentsAsync(host);
        var e1 = await EmployeeTestData.CreateAsync(host, "E1");
        var e2 = await EmployeeTestData.CreateAsync(host, "E2");
        await host.SendOkAsync(new SaveSalaryAssignmentCommand(null, e1, new DateOnly(2025, 1, 1), null, null, PayrollTestData.Lines(components, 10_000)));
        await host.SendOkAsync(new SavePayrollAdjustmentCommand(null, e1, 2026, 2, "Damage recovery", ComponentKind.Deduction, 5_000_000, false, null));

        var summary = await host.SendOkAsync(new ComputePayrollCommand(2026, 2));

        summary.WithWarnings.Should().Be(2);
        var run = await host.SendOkAsync(new GetPayrollRunQuery(summary.RunId));
        run.Payslips.Single(p => p.EmployeeId == e2).Warnings.Should().Contain("No salary");
        (await host.SendFailAsync(new FinalizePayrollRunCommand(summary.RunId))).Message.Should().Contain("negative");
    }

    [Fact]
    public async Task Viewer_cannot_see_salaries()
    {
        await using var host = await TestHost.CreateAsync();
        await host.SignInAsAsync(Role.Viewer);
        (await host.SendFailAsync(new GetPayrollPeriodsQuery())).Code.Should().Be("Auth.Forbidden");
        (await host.SendFailAsync(new GetSalaryComponentsQuery())).Code.Should().Be("Auth.Forbidden");
    }

    [Fact]
    public async Task Five_hundred_employees_compute_in_under_five_seconds()
    {
        await using var host = await TestHost.CreateAsync();
        var components = await PayrollTestData.ComponentsAsync(host);
        var lines = PayrollTestData.Lines(components, 30_000);
        var ids = new List<Guid>();
        await host.WithDbAsync(async db =>
        {
            for (var i = 0; i < 500; i++)
            {
                var employee = new Employee { Id = Guid.CreateVersion7(), CompanyId = host.CompanyId, Code = $"P{i:0000}", FullName = $"Perf {i}", JoinDate = new DateOnly(2025, 1, 1) };
                ids.Add(employee.Id);
                db.Employees.Add(employee);
                var assignment = new SalaryAssignment { Id = Guid.CreateVersion7(), CompanyId = host.CompanyId, EmployeeId = employee.Id, EffectiveFrom = new DateOnly(2025, 1, 1) };
                assignment.Lines = lines.Select(l => new SalaryAssignmentLine { Id = Guid.CreateVersion7(), AssignmentId = assignment.Id, ComponentId = l.ComponentId, AmountMinor = l.AmountMinor + (i * 100), Rate = l.Rate }).ToList();
                db.SalaryAssignments.Add(assignment);
            }

            return await db.SaveChangesAsync();
        });
        await PayrollTestData.PunchWorkingDaysAsync(host, ids, February);

        var watch = Stopwatch.StartNew();
        var summary = await host.SendOkAsync(new ComputePayrollCommand(2026, 2));
        watch.Stop();

        output.WriteLine($"Payroll 500 employees: {watch.ElapsedMilliseconds} ms (handler {summary.Elapsed.TotalMilliseconds:0} ms)");
        summary.Employees.Should().Be(500);
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        var run = await host.SendOkAsync(new GetPayrollRunQuery(summary.RunId));
        run.Net.Minor.Should().Be(run.Payslips.Sum(p => p.Net.Minor));
        run.Gross.Minor.Should().Be(run.Payslips.Sum(p => p.Gross.Minor));
    }
}

public static class PayrollTestData
{
    public static async Task<Dictionary<string, SalaryComponentDto>> ComponentsAsync(TestHost host)
        => (await host.SendOkAsync(new GetSalaryComponentsQuery(ActiveOnly: true))).ToDictionary(c => c.Code);

    /// <summary>Basic as given, HRA 50 %, medical 1,500, conveyance 1,000.</summary>
    public static List<SalaryLineInput> Lines(IReadOnlyDictionary<string, SalaryComponentDto> c, decimal basic) =>
    [
        new(c["BASIC"].Id, (long)(basic * 100), 0),
        new(c["HRA"].Id, 0, 50),
        new(c["MEDICAL"].Id, 150_000, 0),
        new(c["CONV"].Id, 100_000, 0),
    ];

    /// <summary>Punches 09:00 and 18:00 on every working day (Sunday to Thursday) of the range.</summary>
    public static Task PunchWorkingDaysAsync(TestHost host, IReadOnlyList<Guid> employees, DateRange range)
        => host.WithDbAsync(async db =>
        {
            foreach (var employee in employees)
            {
                foreach (var date in range.EachDay().Where(d => d.DayOfWeek is not (DayOfWeek.Friday or DayOfWeek.Saturday)))
                {
                    foreach (var hour in new[] { 9, 18 })
                    {
                        var at = date.ToDateTime(new TimeOnly(hour, 0));
                        db.AttendancePunches.Add(new AttendancePunch
                        {
                            Id = Guid.CreateVersion7(), CompanyId = host.CompanyId, EmployeeId = employee, LocalTime = at, PunchAtUtc = at.AddHours(-6),
                            Source = PunchSource.Device, DedupKey = AttendancePunch.DeviceKey("SN", employee.ToString("N"), at),
                        });
                    }
                }
            }

            return await db.SaveChangesAsync();
        });
}
