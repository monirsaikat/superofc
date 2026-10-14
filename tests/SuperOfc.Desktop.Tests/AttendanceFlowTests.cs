using NSubstitute;
using SuperOfc.Application.Common;
using SuperOfc.Application.Employees;
using SuperOfc.Application.Leave;
using SuperOfc.Desktop.Services;
using SuperOfc.Desktop.Tests.Support;
using SuperOfc.Desktop.ViewModels;
using SuperOfc.Desktop.ViewModels.Attendance;
using SuperOfc.Desktop.ViewModels.Employees;
using SuperOfc.Desktop.ViewModels.Leave;
using SuperOfc.Desktop.ViewModels.Settings;
using SuperOfc.Domain.Attendance;
using SuperOfc.Domain.Employees;

namespace SuperOfc.Desktop.Tests;

public class AttendanceFlowTests
{
    private static async Task<Guid> CreateEmployeeAsync(DesktopTestHost host, string code)
    {
        var input = new EmployeeInput(code, $"Person {code}", Gender.Unspecified, null, null, null, null, null, null, new DateOnly(2025, 1, 1),
            null, null, null, null, PaymentMode.Bank, null, null, null, null, true, null);
        var result = await host.Get<IDispatcher>().SendAsync(new CreateEmployeeCommand(input));
        result.IsSuccess.Should().BeTrue(result.ToString());
        return result.Value;
    }

    [Fact]
    public async Task Sheet_fills_missing_days_and_day_panel_corrects_a_day()
    {
        await using var host = await DesktopTestHost.CreateAsync(completeSetup: true);
        await host.SignInAsync();
        await CreateEmployeeAsync(host, "E0001");
        var sheet = host.Get<AttendanceViewModel>();
        sheet.Month = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(-1);

        await sheet.OnNavigatedToAsync(null);

        sheet.Rows.Should().ContainSingle();
        sheet.HasMissingDays.Should().BeFalse();
        sheet.Days.Should().HaveCount(DateTime.DaysInMonth(sheet.Month.Year, sheet.Month.Month));
        var row = sheet.Rows[0];
        var workingIndex = sheet.Days.First(d => !d.IsOff).Index;
        row.Cells[workingIndex].Status.Should().Be(AttendanceStatus.Absent);

        await sheet.OpenDayAsync(row, workingIndex);
        sheet.IsDayOpen.Should().BeTrue();
        sheet.CorrectStatus = AttendanceStatus.Present;
        sheet.CorrectReason = "Worked from client site";
        await sheet.SaveCorrectionCommand.ExecuteAsync(null);

        sheet.Rows[0].Cells[workingIndex].Status.Should().Be(AttendanceStatus.Present);
        sheet.Rows[0].Cells[workingIndex].IsCorrected.Should().BeTrue();
        sheet.Day!.IsManuallyCorrected.Should().BeTrue();

        await sheet.ResetCorrectionCommand.ExecuteAsync(null);
        sheet.Rows[0].Cells[workingIndex].Status.Should().Be(AttendanceStatus.Absent);
    }

    [Fact]
    public async Task Leave_page_creates_and_approves_a_request_and_shows_balances()
    {
        await using var host = await DesktopTestHost.CreateAsync(completeSetup: true);
        await host.SignInAsync();
        var employee = await CreateEmployeeAsync(host, "E0001");
        var page = host.Get<LeaveViewModel>();
        await page.OnNavigatedToAsync(null);
        page.IsEmpty.Should().BeTrue();

        page.StartNewCommand.Execute(null);
        page.NewEmployeeId.Should().Be(employee);
        page.NewTypeId = page.Types.Single(t => t.Code == "CL").Id;
        var start = NextWeekday(DateTime.Today.AddDays(7));
        page.NewFrom = start;
        page.NewTo = start;
        page.NewApproveNow = false;
        await page.SubmitCommand.ExecuteAsync(null);

        page.ErrorMessage.Should().BeNull();
        page.Requests.Should().ContainSingle();
        page.Selected = page.Requests[0];
        page.CanDecide.Should().BeTrue();
        await page.ApproveCommand.ExecuteAsync(null);

        page.Requests.Should().BeEmpty(); // the default filter shows pending requests
        page.Status = null;
        await WaitUntilAsync(() => page.Requests.Count == 1);
        page.Requests[0].Status.Should().Be(Domain.Leave.LeaveRequestStatus.Approved);
        page.Balances.Single(b => b.Code == "CL").Used.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Employee_page_has_attendance_and_leave_tabs()
    {
        await using var host = await DesktopTestHost.CreateAsync(completeSetup: true);
        await host.SignInAsync();
        var id = await CreateEmployeeAsync(host, "E0001");
        var detail = host.Get<EmployeeDetailViewModel>();

        await detail.OnNavigatedToAsync(id);

        detail.ModuleTabs.Select(t => t.GetType()).Should().Equal(typeof(ViewModels.Payroll.EmployeeSalaryTabViewModel), typeof(EmployeeAttendanceTabViewModel), typeof(EmployeeLeaveTabViewModel), typeof(ViewModels.Loans.EmployeeLoansTabViewModel),
            typeof(ViewModels.Tax.EmployeeTaxTabViewModel), typeof(ViewModels.Letters.EmployeeLettersTabViewModel));
        var attendance = (EmployeeAttendanceTabViewModel)detail.ModuleTabs[1];
        attendance.CurrentShift.Should().Contain("General");
        var leave = (EmployeeLeaveTabViewModel)detail.ModuleTabs[2];
        leave.Balances.Select(b => b.Code).Should().Contain(["CL", "SL", "EL", "LWP"]);

        attendance.StartAssignCommand.Execute(null);
        await attendance.SaveAssignCommand.ExecuteAsync(null);
        attendance.Assignments.Should().ContainSingle();
    }

    [Fact]
    public async Task Settings_show_shifts_leave_types_and_attendance_policy()
    {
        await using var host = await DesktopTestHost.CreateAsync(completeSetup: true);
        await host.SignInAsync();
        var navigation = host.Get<IAppNavigator>();
        await navigation.NavigateToAsync<SettingsViewModel>("Shifts");
        var settings = (SettingsViewModel)navigation.Current!;
        settings.Sections.Select(s => s.Key).Should().Contain(["Shifts", "LeaveTypes"]);

        var shifts = settings.Content.Should().BeOfType<ShiftsSettingsViewModel>().Subject;
        await WaitUntilAsync(() => shifts.Shifts.Count > 0);
        shifts.NewCommand.Execute(null);
        shifts.EditName = "Evening";
        shifts.EditStart = "14:00";
        shifts.EditEnd = "22:00";
        await shifts.SaveCommand.ExecuteAsync(null);
        shifts.Shifts.Select(s => s.Name).Should().Contain("Evening");

        shifts.NewCommand.Execute(null);
        shifts.EditName = "Bad";
        shifts.EditStart = "25:99";
        await shifts.SaveCommand.ExecuteAsync(null);
        shifts.ErrorMessage.Should().NotBeNullOrEmpty();

        var policies = host.Get<PoliciesSettingsViewModel>();
        await policies.OnNavigatedToAsync(null);
        var editor = policies.Editors.OfType<AttendancePolicyEditor>().Single();
        editor.SampleIn = "09:25";
        editor.SampleOut = "18:00";
        policies.IsDirty.Should().BeFalse();
        editor.SampleResult.Should().StartWith(Resources.Strings.AttStatus_Late);
        editor.SampleOut = null;
        editor.SampleResult.Should().StartWith(Resources.Strings.AttStatus_HalfDay);
        editor.OvertimeRoundingMinutes = 15;
        policies.IsDirty.Should().BeTrue();
        await policies.SaveCommand.ExecuteAsync(null);
        var snapshot = await host.Get<IDispatcher>().SendAsync(new Application.Policies.GetPolicySnapshotQuery(DateOnly.FromDateTime(DateTime.Today)));
        snapshot.Value.Attendance.OvertimeRoundingMinutes.Should().Be(15);
    }

    [Fact]
    public async Task Dashboard_shows_today_and_pending_leave()
    {
        await using var host = await DesktopTestHost.CreateAsync(completeSetup: true);
        await host.SignInAsync();
        var employee = await CreateEmployeeAsync(host, "E0001");
        var types = await host.Get<IDispatcher>().SendAsync(new GetLeaveTypesQuery());
        var start = DateOnly.FromDateTime(NextWeekday(DateTime.Today.AddDays(3)));
        (await host.Get<IDispatcher>().SendAsync(new CreateLeaveRequestCommand(employee, types.Value.Single(t => t.Code == "CL").Id, start, start, false, "x"))).IsSuccess.Should().BeTrue();

        var dashboard = host.Get<DashboardViewModel>();
        await dashboard.RefreshAsync();

        dashboard.Tiles.Should().Contain(t => t.Title == Resources.Strings.Dashboard_PendingLeave && t.Value == "1");
    }

    private static DateTime NextWeekday(DateTime date)
    {
        // The default working week has Friday and Saturday off.
        while (date.DayOfWeek is DayOfWeek.Friday or DayOfWeek.Saturday)
        {
            date = date.AddDays(1);
        }

        return date;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 60 && !condition(); i++)
        {
            await Task.Delay(50);
        }

        condition().Should().BeTrue();
    }
}
