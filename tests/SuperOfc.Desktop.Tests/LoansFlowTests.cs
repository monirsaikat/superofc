using System.IO;
using NSubstitute;
using SuperOfc.Application.Common;
using SuperOfc.Application.Employees;
using SuperOfc.Application.Payroll;
using SuperOfc.Desktop.Tests.Support;
using SuperOfc.Desktop.ViewModels.Loans;
using SuperOfc.Domain.Employees;
using SuperOfc.Domain.Loans;

namespace SuperOfc.Desktop.Tests;

/// <summary>Loan form with live schedule, approval, pay-out, repayment and statement through the view models (P5-T2).</summary>
public class LoansFlowTests
{
    [Fact]
    public async Task New_loan_with_preview_approve_pay_out_repay_and_export()
    {
        await using var host = await DesktopTestHost.CreateAsync(completeSetup: true);
        await host.SignInAsync();
        var dispatcher = host.Get<IDispatcher>();
        var input = new EmployeeInput("E1", "Person E1", Gender.Unspecified, null, null, null, null, null, null, new DateOnly(2025, 1, 1),
            null, null, null, null, PaymentMode.Cash, null, null, null, null, true, null);
        var employee = (await dispatcher.SendAsync(new CreateEmployeeCommand(input))).Value;
        var components = (await dispatcher.SendAsync(new GetSalaryComponentsQuery(ActiveOnly: true))).Value.ToDictionary(c => c.Code);
        (await dispatcher.SendAsync(new SaveSalaryAssignmentCommand(null, employee, new DateOnly(2025, 1, 1), null, null,
            [new SalaryLineInput(components["BASIC"].Id, 3_000_000, 0)]))).IsSuccess.Should().BeTrue();

        var form = host.Get<LoanFormViewModel>();
        form.PreviewDelay = TimeSpan.Zero;
        await form.OnNavigatedToAsync(new LoanFormTarget(null, employee));
        form.EmployeeId.Should().Be(employee);
        form.Amount = 12_000;
        form.Count = 6;
        await form.RefreshPreviewAsync();
        form.Schedule.Should().HaveCount(6);
        form.TotalText.Should().Contain("2,000.00");
        form.NetText.Should().NotBeNull();
        form.IsBelowMinimum.Should().BeFalse();

        await form.SaveAndApproveCommand.ExecuteAsync(null);
        form.ErrorMessage.Should().BeNull();
        form.HasErrors.Should().BeFalse();

        var list = host.Get<LoansViewModel>();
        await list.OnNavigatedToAsync(null);
        var item = list.Loans.Should().ContainSingle().Subject;
        item.Status.Should().Be(LoanStatus.Approved);

        var detail = host.Get<LoanDetailViewModel>();
        await detail.OnNavigatedToAsync(item.Id);
        detail.CanDisburse.Should().BeTrue();
        detail.CanApprove.Should().BeFalse();
        host.Dialogs.PickDateAsync(default!, default!, default).ReturnsForAnyArgs(DateTime.Today);
        host.Dialogs.PromptAsync(default!, default!, default!, default, default).ReturnsForAnyArgs("TRX-1");
        await detail.DisburseCommand.ExecuteAsync(null);
        detail.Loan!.Summary.Status.Should().Be(LoanStatus.Active);
        detail.CanRepay.Should().BeTrue();

        host.Dialogs.PromptAsync(default!, default!, default!, default, default).ReturnsForAnyArgs("2,500");
        await detail.RepayCommand.ExecuteAsync(null);
        detail.ErrorMessage.Should().BeNull();
        detail.Loan!.Balance.Outstanding.Major.Should().Be(9_500);
        detail.Loan.Statement.Should().HaveCount(2);

        var pdf = Path.Combine(host.Root, "statement.pdf");
        host.Files.SaveFile(default!, default!).ReturnsForAnyArgs(pdf);
        await detail.ExportCommand.ExecuteAsync(null);
        File.Exists(pdf).Should().BeTrue();

        var tab = host.Get<EmployeeLoansTabViewModel>();
        await tab.LoadAsync(employee);
        tab.Loans.Should().ContainSingle();
        tab.Summary.Should().Contain("9,500.00");
    }
}
