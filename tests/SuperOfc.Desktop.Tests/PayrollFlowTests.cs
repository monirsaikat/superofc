using System.IO;
using NSubstitute;
using SuperOfc.Application.Common;
using SuperOfc.Application.Employees;
using SuperOfc.Application.Payroll;
using SuperOfc.Application.Users;
using SuperOfc.Desktop.Tests.Support;
using SuperOfc.Desktop.ViewModels.Attendance;
using SuperOfc.Desktop.ViewModels.Payroll;
using SuperOfc.Domain.Employees;
using SuperOfc.Domain.Payroll;
using SuperOfc.Domain.Security;

namespace SuperOfc.Desktop.Tests;

/// <summary>Salary setup and a full payroll month through the view models (P4-T1, T2, T4, T5, T6).</summary>
public class PayrollFlowTests
{
    private static readonly DateOnly LastMonth = new DateOnly(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(-1);

    private static async Task<Guid> CreateEmployeeAsync(DesktopTestHost host, string code)
    {
        var input = new EmployeeInput(code, $"Person {code}", Gender.Unspecified, null, null, null, null, null, null, new DateOnly(2025, 1, 1),
            null, null, null, null, PaymentMode.Bank, "City Bank", null, "1001", null, true, null);
        var result = await host.Get<IDispatcher>().SendAsync(new CreateEmployeeCommand(input));
        result.IsSuccess.Should().BeTrue(result.ToString());
        return result.Value;
    }

    private static async Task<EmployeeSalaryTabViewModel> SetSalaryAsync(DesktopTestHost host, Guid employeeId, double basic)
    {
        var tab = host.Get<EmployeeSalaryTabViewModel>();
        tab.IsAllowed.Should().BeTrue();
        await tab.LoadAsync(employeeId);
        tab.IsEmpty.Should().BeTrue();
        await tab.NewCommand.ExecuteAsync(null);
        tab.IsEditing.Should().BeTrue();
        tab.Editor!.PreviewDelay = TimeSpan.Zero;
        tab.Editor.Basic!.Amount = basic;
        tab.EditFrom = new DateTime(2025, 1, 1);
        await tab.Editor.RefreshPreviewAsync();
        tab.Editor.Gross.Should().BeGreaterThan(Domain.Common.Money.FromMajor((decimal)basic));
        await tab.SaveCommand.ExecuteAsync(null);
        tab.ErrorMessage.Should().BeNull();
        tab.IsEditing.Should().BeFalse();
        tab.History.Should().ContainSingle();
        return tab;
    }

    [Fact]
    public async Task Components_structures_and_employee_salary()
    {
        await using var host = await DesktopTestHost.CreateAsync(completeSetup: true);
        await host.SignInAsync();
        var id = await CreateEmployeeAsync(host, "E1");

        var components = host.Get<SalaryComponentsSettingsViewModel>();
        await components.OnNavigatedToAsync(null);
        components.Components.Select(c => c.Code).Should().Contain(["BASIC", "HRA", "MEDICAL", "CONV", "PF"]);
        components.NewCommand.Execute(null);
        components.EditCode = "bonus";
        components.EditName = "Attendance bonus";
        components.EditCalc = ComponentCalc.Formula;
        components.EditFormula = "BASIC * 0.05";
        await components.SaveCommand.ExecuteAsync(null);
        components.ErrorMessage.Should().BeNull();
        components.Components.Should().Contain(c => c.Code == "BONUS" && c.Formula == "BASIC * 0.05");

        components.NewCommand.Execute(null);
        components.EditCode = "BAD";
        components.EditName = "Broken";
        components.EditCalc = ComponentCalc.Formula;
        components.EditFormula = "BASIC + UNKNOWN";
        await components.SaveCommand.ExecuteAsync(null);
        components.ErrorMessage.Should().NotBeNull("unknown codes are rejected");

        var structures = host.Get<SalaryStructuresSettingsViewModel>();
        await structures.OnNavigatedToAsync(null);
        structures.Selected = structures.Structures.Single(s => s.Name == "Standard");
        await WaitForAsync(() => structures.Editor is not null);
        structures.Editor!.PreviewDelay = TimeSpan.Zero;
        structures.Editor.Basic!.Amount = 20_000;
        await structures.Editor.RefreshPreviewAsync();
        structures.Editor.Preview.Single(l => l.Code == "HRA").Amount.Should().Be(Domain.Common.Money.FromMajor(10_000), "house rent is 50 % of basic");

        var tab = await SetSalaryAsync(host, id, 30_000);
        tab.Current.Should().NotBeNull();
        tab.Current!.Lines.Single(l => l.Code == "BASIC").Amount.Should().Be(Domain.Common.Money.FromMajor(30_000));

        var revision = host.Get<SalaryRevisionViewModel>();
        await revision.OnNavigatedToAsync(null);
        revision.Candidates.Should().ContainSingle();
        await revision.NextCommand.ExecuteAsync(null);
        revision.ErrorMessage.Should().NotBeNull("nobody is selected yet");
        revision.Candidates[0].IsSelected = true;
        revision.SelectedCount.Should().Be(1);
        await revision.NextCommand.ExecuteAsync(null);
        revision.IsSetting.Should().BeTrue();
        revision.Percent = 10;
        revision.EffectiveFrom = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(1);
        await revision.NextCommand.ExecuteAsync(null);
        revision.IsReviewing.Should().BeTrue(revision.ErrorMessage);
        revision.Preview.Single().NewBasic.Should().Be(Domain.Common.Money.FromMajor(33_000));
        host.Dialogs.ConfirmAsync(default!, default!, default!, default).ReturnsForAnyArgs(true);
        await revision.ApplyCommand.ExecuteAsync(null);
        revision.ErrorMessage.Should().BeNull();

        await tab.LoadAsync(id);
        tab.History.Should().HaveCount(2, "a revision adds a salary and keeps the history");
    }

    [Fact]
    public async Task Compute_review_finalize_pay_export_and_void()
    {
        await using var host = await DesktopTestHost.CreateAsync(completeSetup: true);
        await host.SignInAsync();
        var id = await CreateEmployeeAsync(host, "E1");
        await SetSalaryAsync(host, id, 30_000);
        host.Dialogs.ConfirmAsync(default!, default!, default!, default).ReturnsForAnyArgs(true);

        var list = host.Get<PayrollViewModel>();
        await list.OnNavigatedToAsync(null);
        list.IsEmpty.Should().BeTrue();
        list.NextText.Should().Contain(PayrollViewModel.MonthName(LastMonth.Year, LastMonth.Month));

        var run = host.Get<PayrollRunViewModel>();
        await run.OnNavigatedToAsync(new PayrollRunTarget(LastMonth.Year, LastMonth.Month));
        run.HasRun.Should().BeFalse();
        run.CanCompute.Should().BeTrue();
        run.ReadinessText.Should().NotBeNullOrEmpty();

        run.StartAdjustmentCommand.Execute(null);
        run.NewAdjEmployeeId = id;
        run.NewAdjName = "Festival bonus";
        run.NewAdjAmount = 5_000;
        await run.SaveAdjustmentCommand.ExecuteAsync(null);
        run.ErrorMessage.Should().BeNull();
        run.Adjustments.Should().ContainSingle();

        await run.ComputeCommand.ExecuteAsync(null);
        run.ErrorMessage.Should().BeNull();
        run.IsDraft.Should().BeTrue();
        run.CanFinalize.Should().BeTrue();
        run.Payslips.Should().ContainSingle();
        run.ComputeText.Should().NotBeNull();

        run.Selected = run.Payslips[0];
        await WaitForAsync(() => run.PreviewImage is not null);
        run.PreviewImage![..4].Should().Equal(0x89, 0x50, 0x4E, 0x47);

        await run.FinalizeCommand.ExecuteAsync(null);
        run.ErrorMessage.Should().BeNull();
        run.IsFinalized.Should().BeTrue();
        run.CanEditAdjustments.Should().BeFalse();
        run.CanPay.Should().BeTrue();

        var attendance = host.Get<AttendanceViewModel>();
        await attendance.OnNavigatedToAsync(null);
        attendance.Month = LastMonth.ToDateTime(TimeOnly.MinValue);
        await WaitForAsync(() => attendance.LockText is not null);

        host.Dialogs.PickDateAsync(default!, default!, default).ReturnsForAnyArgs(DateTime.Today);
        host.Dialogs.PromptAsync(default!, default!, default!, default, default).ReturnsForAnyArgs("BATCH-1");
        await run.MarkPaidCommand.ExecuteAsync(null);
        run.ErrorMessage.Should().BeNull();
        run.Payslips.Single().Row.PaymentStatus.Should().Be(PaymentStatus.Paid);

        var pdf = Path.Combine(host.Root, "payslips.pdf");
        host.Files.SaveFile(default!, default!).ReturnsForAnyArgs(pdf);
        await run.ExportPdfCommand.ExecuteAsync("merged");
        File.Exists(pdf).Should().BeTrue();
        var sheet = Path.Combine(host.Root, "bank.xlsx");
        host.Files.SaveFile(default!, default!).ReturnsForAnyArgs(sheet);
        await run.ExportSheetCommand.ExecuteAsync("Bank");
        File.Exists(sheet).Should().BeTrue();

        var register = host.Get<PayrollRegisterViewModel>();
        await register.OnNavigatedToAsync(run.Run!.Id);
        register.Rows.Should().ContainSingle();
        register.TotalRows.Last().Net.Should().Be(run.Run.Net);
        register.Register!.Columns.Should().Contain(c => c.Name == "Festival bonus");

        await run.VoidCommand.ExecuteAsync(null);
        run.ErrorMessage.Should().NotBeNull("paid payslips must be marked unpaid before a void");
        await run.MarkUnpaidCommand.ExecuteAsync(null);
        await run.VoidCommand.ExecuteAsync(null);
        run.ErrorMessage.Should().BeNull();
        run.HasRun.Should().BeFalse();
        run.CanCompute.Should().BeTrue();

        await list.RefreshAsync();
        list.Periods.Should().ContainSingle().Which.Status.Should().Be(PayrollPeriodStatus.Open);
    }

    [Fact]
    public async Task Hr_can_compute_but_not_finalize()
    {
        await using var host = await DesktopTestHost.CreateAsync(completeSetup: true);
        await host.SignInAsync();
        var created = await host.Get<IDispatcher>().SendAsync(new CreateUserCommand("hr", "HR Person", "Hr12345678", Role.HR));
        created.IsSuccess.Should().BeTrue(created.ToString());
        host.Get<Application.Abstractions.IUserSession>().SignOut();
        await host.SignInAsync("hr", "Hr12345678");

        var run = host.Get<PayrollRunViewModel>();
        await run.OnNavigatedToAsync(new PayrollRunTarget(LastMonth.Year, LastMonth.Month));
        run.CanManage.Should().BeTrue();
        run.CanFinalizeRole.Should().BeFalse();
        run.CanFinalize.Should().BeFalse();
        run.CanVoid.Should().BeFalse();
        host.Get<SalaryComponentsSettingsViewModel>().CanEdit.Should().BeTrue();
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25);
        }

        condition().Should().BeTrue("the condition should become true within five seconds");
    }
}
