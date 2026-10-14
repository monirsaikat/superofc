using System.Diagnostics;
using Xunit.Abstractions;
using Microsoft.EntityFrameworkCore;
using SuperOfc.Application.Attendance;
using SuperOfc.Application.Leave;
using SuperOfc.Application.Tests.Support;
using SuperOfc.Domain.Attendance;
using SuperOfc.Domain.Employees;
using SuperOfc.Domain.Leave;
using SuperOfc.Domain.Security;

namespace SuperOfc.Application.Tests;

// Test clock: 2026-03-15 10:00 in Dhaka (Sunday). Working week: Friday and Saturday off. Default shift 09:00-18:00.
public class AttendanceEngineTests(ITestOutputHelper output)
{
    private static DateTime At(int day, int hour, int minute = 0) => new(2026, 3, day, hour, minute, 0);

    private static Task PunchAsync(TestHost host, Guid employee, DateTime at)
        => host.SendOkAsync(new AddManualPunchCommand(employee, at, PunchType.Unknown, "Forgot card"));

    private static Task<AttendanceStatus> StatusAsync(TestHost host, Guid employee, int day)
        => host.WithDbAsync(db => db.AttendanceDays.Where(d => d.EmployeeId == employee && d.Date == new DateOnly(2026, 3, day)).Select(d => d.Status).SingleAsync());

    [Fact]
    public async Task Manual_punches_produce_present_late_absent_and_offs()
    {
        await using var host = await TestHost.CreateAsync();
        var e = await EmployeeTestData.CreateAsync(host, "E1");
        await PunchAsync(host, e, At(10, 9, 5));
        await PunchAsync(host, e, At(10, 18));
        await PunchAsync(host, e, At(11, 9, 30));
        await PunchAsync(host, e, At(11, 18));

        var written = await host.SendOkAsync(new RecomputeAttendanceCommand(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 14)));

        written.Should().Be(14);
        (await StatusAsync(host, e, 9)).Should().Be(AttendanceStatus.Absent);
        (await StatusAsync(host, e, 10)).Should().Be(AttendanceStatus.Present);
        (await StatusAsync(host, e, 11)).Should().Be(AttendanceStatus.Late);
        (await StatusAsync(host, e, 6)).Should().Be(AttendanceStatus.WeeklyOff);
        (await StatusAsync(host, e, 7)).Should().Be(AttendanceStatus.WeeklyOff);

        var month = await host.SendOkAsync(new GetAttendanceMonthQuery(2026, 3));
        var row = month.Rows.Single();
        row.Present.Should().Be(2);
        row.Late.Should().Be(1);
        month.Dates.Should().HaveCount(31);
    }

    [Fact]
    public async Task Recompute_is_idempotent()
    {
        await using var host = await TestHost.CreateAsync();
        var e = await EmployeeTestData.CreateAsync(host, "E1");
        await PunchAsync(host, e, At(10, 9));
        await PunchAsync(host, e, At(10, 18));

        await host.SendOkAsync(new RecomputeAttendanceCommand(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 14)));
        var first = await host.WithDbAsync(db => db.AttendanceDays.AsNoTracking().OrderBy(d => d.Date).Select(d => new { d.Date, d.Status, d.WorkedMinutes, d.RowVersion }).ToListAsync());
        await host.SendOkAsync(new RecomputeAttendanceCommand(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 14)));
        var second = await host.WithDbAsync(db => db.AttendanceDays.AsNoTracking().OrderBy(d => d.Date).Select(d => new { d.Date, d.Status, d.WorkedMinutes, d.RowVersion }).ToListAsync());

        second.Should().BeEquivalentTo(first, o => o.Excluding(x => x.RowVersion));
        second.Should().HaveCount(14);
    }

    [Fact]
    public async Task Duplicate_and_future_manual_punches_are_rejected_and_device_punches_cannot_be_deleted()
    {
        await using var host = await TestHost.CreateAsync();
        var e = await EmployeeTestData.CreateAsync(host, "E1");
        await PunchAsync(host, e, At(10, 9));

        (await host.SendFailAsync(new AddManualPunchCommand(e, At(10, 9), PunchType.In, "Again"))).Code.Should().Be("Validation.LocalTime");
        (await host.SendFailAsync(new AddManualPunchCommand(e, At(16, 9), PunchType.In, "Tomorrow"))).Code.Should().Be("Validation.LocalTime");
        (await host.SendFailAsync(new AddManualPunchCommand(e, At(10, 10), PunchType.In, ""))).Code.Should().Be("Validation.Reason");

        var deviceId = Guid.CreateVersion7();
        await host.WithDbAsync(async db =>
        {
            db.AttendancePunches.Add(new AttendancePunch
            {
                Id = deviceId, CompanyId = host.CompanyId, EmployeeId = e, LocalTime = At(10, 18), PunchAtUtc = At(10, 12),
                Source = PunchSource.Device, DedupKey = AttendancePunch.DeviceKey("SN1", "1", At(10, 18)),
            });
            return await db.SaveChangesAsync();
        });

        (await host.SendFailAsync(new DeleteManualPunchCommand(deviceId, "wrong"))).Code.Should().Be("Validation");
        var punches = await host.SendOkAsync(new GetPunchesQuery(new DateOnly(2026, 3, 10), new DateOnly(2026, 3, 10)));
        punches.TotalCount.Should().Be(2);
        var manual = punches.Items.Single(p => p.Source == PunchSource.Manual);
        await host.SendOkAsync(new DeleteManualPunchCommand(manual.Id, "Entered by mistake"));
        (await host.SendOkAsync(new GetPunchesQuery(new DateOnly(2026, 3, 10), new DateOnly(2026, 3, 10)))).TotalCount.Should().Be(1);
    }

    [Fact]
    public async Task Correction_survives_recompute_until_reset()
    {
        await using var host = await TestHost.CreateAsync();
        var e = await EmployeeTestData.CreateAsync(host, "E1");
        await host.SendOkAsync(new RecomputeAttendanceCommand(new DateOnly(2026, 3, 9), new DateOnly(2026, 3, 9)));

        await host.SendOkAsync(new CorrectAttendanceDayCommand(e, new DateOnly(2026, 3, 9), AttendanceStatus.Present, 0, 0, "Client visit"));
        await host.SendOkAsync(new RecomputeAttendanceCommand(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 14)));

        (await StatusAsync(host, e, 9)).Should().Be(AttendanceStatus.Present);
        var detail = await host.SendOkAsync(new GetAttendanceDayQuery(e, new DateOnly(2026, 3, 9)));
        detail.IsManuallyCorrected.Should().BeTrue();
        detail.CorrectionNote.Should().Be("Client visit");
        (await host.WithDbAsync(db => db.AttendanceCorrections.CountAsync())).Should().Be(1);

        await host.SendOkAsync(new ResetAttendanceCorrectionCommand(e, new DateOnly(2026, 3, 9)));
        (await StatusAsync(host, e, 9)).Should().Be(AttendanceStatus.Absent);
    }

    [Fact]
    public async Task Night_shift_assignment_keeps_morning_punch_on_the_start_day()
    {
        await using var host = await TestHost.CreateAsync();
        var e = await EmployeeTestData.CreateAsync(host, "E1");
        var night = await host.SendOkAsync(new SaveShiftCommand(null, "Night", "N", new TimeOnly(22, 0), new TimeOnly(6, 0), 10, 0, 30, 210, 390, 0, false));
        await host.SendOkAsync(new AssignShiftCommand([e], night, new DateOnly(2026, 3, 1), null));
        await PunchAsync(host, e, At(10, 21, 55));
        await PunchAsync(host, e, At(11, 6, 2));

        (await StatusAsync(host, e, 10)).Should().Be(AttendanceStatus.Present);
        (await StatusAsync(host, e, 11)).Should().Be(AttendanceStatus.Absent);

        // A second assignment starting earlier than the existing one overlaps and is rejected.
        (await host.SendFailAsync(new AssignShiftCommand([e], night, new DateOnly(2026, 2, 1), new DateOnly(2026, 3, 5)))).Code.Should().Be("Validation.EffectiveFrom");

        // A later assignment closes the open one automatically.
        var general = (await host.SendOkAsync(new GetShiftsQuery())).Single(s => s.IsDefault).Id;
        await host.SendOkAsync(new AssignShiftCommand([e], general, new DateOnly(2026, 3, 12), null));
        var history = await host.SendOkAsync(new GetShiftAssignmentsQuery(e));
        history.Should().HaveCount(2);
        history.Single(h => h.ShiftId == night).EffectiveTo.Should().Be(new DateOnly(2026, 3, 11));
    }

    [Fact]
    public async Task Default_shift_cannot_be_deleted_and_viewer_cannot_add_punches()
    {
        await using var host = await TestHost.CreateAsync();
        var general = (await host.SendOkAsync(new GetShiftsQuery())).Single();
        general.IsDefault.Should().BeTrue();
        (await host.SendFailAsync(new DeleteShiftCommand(general.Id))).Code.Should().Be("Validation");

        var e = await EmployeeTestData.CreateAsync(host, "E1");
        await host.SignInAsAsync(Role.Viewer);
        (await host.SendFailAsync(new AddManualPunchCommand(e, At(10, 9), PunchType.In, "x"))).Code.Should().Be("Auth.Forbidden");
    }

    [Fact]
    public async Task Five_hundred_employees_for_a_month_recompute_quickly()
    {
        await using var host = await TestHost.CreateAsync();
        await host.WithDbAsync(async db =>
        {
            for (var i = 0; i < 500; i++)
            {
                var employee = new Employee { Id = Guid.CreateVersion7(), CompanyId = host.CompanyId, Code = $"P{i:0000}", FullName = $"Perf {i}", JoinDate = new DateOnly(2025, 1, 1) };
                db.Employees.Add(employee);
                for (var day = 1; day <= 14; day++)
                {
                    foreach (var at in new[] { At(day, 8, 50).AddMinutes(i % 20), At(day, 18, 5) })
                    {
                        db.AttendancePunches.Add(new AttendancePunch
                        {
                            Id = Guid.CreateVersion7(), CompanyId = host.CompanyId, EmployeeId = employee.Id, LocalTime = at, PunchAtUtc = at.AddHours(-6),
                            Source = PunchSource.Device, DedupKey = AttendancePunch.DeviceKey("SN", employee.Code, at),
                        });
                    }
                }
            }

            return await db.SaveChangesAsync();
        });

        var watch = Stopwatch.StartNew();
        var written = await host.SendOkAsync(new RecomputeAttendanceCommand(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31)));
        watch.Stop();

        output.WriteLine($"Recompute 500 x 31: {watch.ElapsedMilliseconds} ms");
        written.Should().Be(500 * 31);
        watch.ElapsedMilliseconds.Should().BeLessThan(3000);
        var month = await host.SendOkAsync(new GetAttendanceMonthQuery(2026, 3));
        month.Rows.Should().HaveCount(500);
        month.MissingDays.Should().Be(0);
    }
}

// Leave year = fiscal year starting 1 July, so March 2026 is in leave year 2025.
public class LeaveTests
{
    private static async Task<Guid> TypeAsync(TestHost host, string code)
        => (await host.SendOkAsync(new GetLeaveTypesQuery())).Single(t => t.Code == code).Id;

    private static async Task<LeaveBalanceDto> BalanceAsync(TestHost host, Guid employee, string code, int? year = null)
        => (await host.SendOkAsync(new GetLeaveBalancesQuery(employee, year))).Single(b => b.Code == code);

    [Fact]
    public async Task Approving_uses_balance_marks_attendance_and_cancelling_restores_both()
    {
        await using var host = await TestHost.CreateAsync();
        var e = await EmployeeTestData.CreateAsync(host, "E1");
        var cl = await TypeAsync(host, "CL");
        (await BalanceAsync(host, e, "CL")).Closing.Should().Be(10);

        // Sunday 8 to Tuesday 10 March: three working days.
        var id = await host.SendOkAsync(new CreateLeaveRequestCommand(e, cl, new DateOnly(2026, 3, 8), new DateOnly(2026, 3, 10), false, "Family", ApproveNow: true));

        var balance = await BalanceAsync(host, e, "CL");
        balance.Used.Should().Be(3);
        balance.Closing.Should().Be(7);
        var day = await host.SendOkAsync(new GetAttendanceDayQuery(e, new DateOnly(2026, 3, 9)));
        day.Status.Should().Be(AttendanceStatus.Leave);

        await host.SendOkAsync(new DecideLeaveRequestCommand(id, LeaveDecision.Cancel, "Plans changed"));

        (await BalanceAsync(host, e, "CL")).Closing.Should().Be(10);
        (await host.SendOkAsync(new GetAttendanceDayQuery(e, new DateOnly(2026, 3, 9)))).Status.Should().Be(AttendanceStatus.Absent);
        (await host.SendOkAsync(new GetLeaveRequestsQuery(EmployeeId: e))).Single().Status.Should().Be(LeaveRequestStatus.Cancelled);
    }

    [Fact]
    public async Task Pending_request_is_approved_or_rejected_by_an_approver()
    {
        await using var host = await TestHost.CreateAsync();
        var e = await EmployeeTestData.CreateAsync(host, "E1");
        var sl = await TypeAsync(host, "SL");
        var first = await host.SendOkAsync(new CreateLeaveRequestCommand(e, sl, new DateOnly(2026, 3, 2), new DateOnly(2026, 3, 2), false, "Fever"));
        var second = await host.SendOkAsync(new CreateLeaveRequestCommand(e, sl, new DateOnly(2026, 3, 3), new DateOnly(2026, 3, 3), true, "Doctor"));
        (await BalanceAsync(host, e, "SL")).Used.Should().Be(0);

        await host.SendOkAsync(new DecideLeaveRequestCommand(first, LeaveDecision.Approve, null));
        (await host.SendFailAsync(new DecideLeaveRequestCommand(second, LeaveDecision.Reject, null))).Code.Should().Be("Validation.Note");
        await host.SendOkAsync(new DecideLeaveRequestCommand(second, LeaveDecision.Reject, "Busy week"));
        (await host.SendFailAsync(new DecideLeaveRequestCommand(second, LeaveDecision.Approve, null))).Code.Should().Be("Validation");

        (await BalanceAsync(host, e, "SL")).Used.Should().Be(1);

        await host.SignInAsAsync(Role.Accounts);
        (await host.SendFailAsync(new CreateLeaveRequestCommand(e, sl, new DateOnly(2026, 3, 4), new DateOnly(2026, 3, 4), false, null))).Code.Should().Be("Auth.Forbidden");
    }

    [Fact]
    public async Task Balance_overlap_half_day_and_attachment_rules()
    {
        await using var host = await TestHost.CreateAsync();
        var e = await EmployeeTestData.CreateAsync(host, "E1");
        var cl = await TypeAsync(host, "CL");
        var el = await TypeAsync(host, "EL");
        var lwp = await TypeAsync(host, "LWP");

        // CL has 10 days; 1 to 19 March has 15 working days.
        (await host.SendFailAsync(new CreateLeaveRequestCommand(e, cl, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 19), false, null))).Code.Should().Be("Validation.Days");

        // Unpaid leave may go negative.
        await host.SendOkAsync(new CreateLeaveRequestCommand(e, lwp, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 19), false, null, ApproveNow: true));
        (await BalanceAsync(host, e, "LWP")).Closing.Should().BeNegative();

        // Overlapping dates are rejected.
        (await host.SendFailAsync(new CreateLeaveRequestCommand(e, cl, new DateOnly(2026, 3, 19), new DateOnly(2026, 3, 22), false, null))).Code.Should().Be("Validation.FromDate");

        // Earned leave does not allow half days.
        (await host.SendFailAsync(new CreateLeaveRequestCommand(e, el, new DateOnly(2026, 3, 22), new DateOnly(2026, 3, 22), true, null))).Code.Should().Be("Validation.IsHalfDay");

        // Only weekly offs.
        (await host.SendFailAsync(new CreateLeaveRequestCommand(e, cl, new DateOnly(2026, 3, 20), new DateOnly(2026, 3, 21), false, null))).Code.Should().Be("Validation.FromDate");
    }

    [Fact]
    public async Task Earned_leave_accrues_monthly_and_carries_forward_with_cap()
    {
        await using var host = await TestHost.CreateAsync();
        var e = await EmployeeTestData.CreateAsync(host, "E1");
        var el = await TypeAsync(host, "EL");

        // July 2025 to March 2026 = 9 months x 1.5.
        (await BalanceAsync(host, e, "EL")).Accrued.Should().Be(13.5m);
        await host.SendOkAsync(new CreateLeaveRequestCommand(e, el, new DateOnly(2026, 3, 2), new DateOnly(2026, 3, 3), false, null, ApproveNow: true));
        await host.SendOkAsync(new AdjustLeaveBalanceCommand(e, el, 2025, -0.5m, "Correction"));

        // Next leave year: 18 earned - 2 used - 0.5 adjusted = 15.5 carried; July adds 1.5.
        host.Clock.UtcNow = new DateTime(2026, 7, 5, 4, 0, 0, DateTimeKind.Utc);
        var next = await BalanceAsync(host, e, "EL");
        next.Year.Should().Be(2026);
        next.Opening.Should().Be(15.5m);
        next.Accrued.Should().Be(1.5m);
        next.Closing.Should().Be(17m);

        // Casual leave has no carry-forward.
        (await BalanceAsync(host, e, "CL")).Opening.Should().Be(0);
    }

    [Fact]
    public async Task Leave_types_have_unique_codes()
    {
        await using var host = await TestHost.CreateAsync();
        (await host.SendOkAsync(new GetLeaveTypesQuery())).Select(t => t.Code).Should().BeEquivalentTo(["CL", "SL", "EL", "LWP"]);

        (await host.SendFailAsync(new SaveLeaveTypeCommand(null, "Casual again", "cl", true, 5, LeaveAccrual.Yearly, 0, true, false, false, 9))).Code.Should().Be("Validation.Code");
        var id = await host.SendOkAsync(new SaveLeaveTypeCommand(null, "Maternity", "ml", true, 112, LeaveAccrual.Yearly, 0, false, true, false, 9));
        (await host.SendOkAsync(new GetLeaveTypesQuery())).Should().Contain(t => t.Id == id && t.Code == "ML" && t.RequiresAttachment);
    }
}

public class AttendancePolicyTests
{
    [Fact]
    public async Task Changing_missing_punch_policy_changes_recomputed_status()
    {
        await using var host = await TestHost.CreateAsync();
        var e = await EmployeeTestData.CreateAsync(host, "E1");
        await host.SendOkAsync(new AddManualPunchCommand(e, new DateTime(2026, 3, 10, 9, 0, 0), PunchType.In, "Card"));
        var day = new DateOnly(2026, 3, 10);
        (await host.SendOkAsync(new GetAttendanceDayQuery(e, day))).Status.Should().Be(AttendanceStatus.HalfDay);

        var policy = new SuperOfc.Domain.Policies.AttendancePolicy { MissingPunch = SuperOfc.Domain.Policies.MissingPunchRule.TreatAsAbsent };
        await host.SendOkAsync(new SuperOfc.Application.Policies.SavePolicyCommand(SuperOfc.Domain.Policies.PolicySnapshot.AttendanceSection, new DateOnly(2026, 3, 1), policy, "Stricter"));
        await host.SendOkAsync(new RecomputeAttendanceCommand(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 14)));

        (await host.SendOkAsync(new GetAttendanceDayQuery(e, day))).Status.Should().Be(AttendanceStatus.Absent);
    }
}
