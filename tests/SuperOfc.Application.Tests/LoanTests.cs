using Microsoft.EntityFrameworkCore;
using SuperOfc.Application.Loans;
using SuperOfc.Application.Payroll;
using SuperOfc.Application.Policies;
using SuperOfc.Application.Tests.Support;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Loans;
using SuperOfc.Domain.Payroll;
using SuperOfc.Domain.Policies;
using SuperOfc.Domain.Security;

namespace SuperOfc.Application.Tests;

/// <summary>Loans and advances end to end, including payroll (P5-T1 to P5-T3). The test clock is 15 March 2026.</summary>
public class LoanTests
{
    private static readonly DateRange February = DateRange.Month(2026, 2);

    /// <summary>One employee on basic 30,000 (gross and net 47,500) with full attendance in February.</summary>
    private static async Task<(TestHost Host, Guid Employee)> SetUpAsync()
    {
        var host = await TestHost.CreateAsync();
        var components = await PayrollTestData.ComponentsAsync(host);
        var e1 = await EmployeeTestData.CreateAsync(host, "E1");
        await host.SendOkAsync(new SaveSalaryAssignmentCommand(null, e1, new DateOnly(2025, 1, 1), null, null, PayrollTestData.Lines(components, 30_000)));
        await PayrollTestData.PunchWorkingDaysAsync(host, [e1], February);
        return (host, e1);
    }

    private static async Task<Guid> ActiveLoanAsync(TestHost host, Guid employee, decimal amount, int count, InterestMethod method = InterestMethod.None, decimal rate = 0, int startMonth = 2)
    {
        var id = await host.SendOkAsync(new SaveLoanCommand(null, employee, LoanKind.Loan, Money.FromMajor(amount).Minor, method, rate, count, 2026, startMonth, new DateOnly(2026, 1, 20), "Medical"));
        await host.SendOkAsync(new ApproveLoanCommand(id));
        await host.SendOkAsync(new DisburseLoanCommand(id, new DateOnly(2026, 1, 25), "CHQ-1"));
        return id;
    }

    private static Task SetMinimumNetAsync(TestHost host, decimal minimum)
        => host.SendOkAsync(new SavePolicyCommand(PolicySnapshot.PayrollSection, new DateOnly(2025, 1, 1), new PayrollPolicy { MinimumNetPayMinor = Money.FromMajor(minimum).Minor }, null));

    [Fact]
    public async Task Request_approve_disburse_and_read_the_schedule_and_statement()
    {
        var (host, e1) = await SetUpAsync();
        await using var _ = host;

        var preview = await host.SendOkAsync(new PreviewLoanQuery(e1, null, Money.FromMajor(12_000).Minor, InterestMethod.FlatRate, 10, 12, 2026, 4));
        preview.Schedule!.Total.Should().Be(Money.FromMajor(13_200));
        preview.CurrentNet.Should().Be(Money.FromMajor(47_500));
        preview.ProjectedNet.Should().Be(Money.FromMajor(46_400));
        preview.Warnings.Should().BeEmpty();

        var id = await host.SendOkAsync(new SaveLoanCommand(null, e1, LoanKind.Loan, Money.FromMajor(12_000).Minor, InterestMethod.FlatRate, 10, 12, 2026, 4, new DateOnly(2026, 3, 10), "Wedding"));
        var detail = await host.SendOkAsync(new GetLoanQuery(id));
        detail.Summary.Number.Should().Be("L-0001");
        detail.Summary.Status.Should().Be(LoanStatus.Requested);
        detail.Installments.Should().BeEmpty("the schedule is made on approval");

        await host.SignInAsAsync(Role.HR);
        (await host.SendFailAsync(new ApproveLoanCommand(id))).Code.Should().Be("Auth.Forbidden", "HR manages loans but Accounts or Admin approve them");
        await host.SignInAsAsync(Role.Admin);
        await host.SendOkAsync(new ApproveLoanCommand(id));
        (await host.SendFailAsync(new SaveLoanCommand(id, e1, LoanKind.Loan, 100, InterestMethod.None, 0, 1, 2026, 4, new DateOnly(2026, 3, 10), null))).Message.Should().Contain("request");
        await host.SendOkAsync(new DisburseLoanCommand(id, new DateOnly(2026, 3, 12), "TRX-9"));

        detail = await host.SendOkAsync(new GetLoanQuery(id));
        detail.Summary.Status.Should().Be(LoanStatus.Active);
        detail.Installments.Should().HaveCount(12);
        new Money(detail.Installments.Sum(i => i.Amount.Minor)).Should().Be(Money.FromMajor(13_200));
        detail.Balance.Outstanding.Should().Be(Money.FromMajor(13_200));
        detail.Statement.Select(l => l.Balance.Major).Should().Equal(12_000m, 13_200m);
        detail.Settlement!.Payable.Should().Be(Money.FromMajor(12_000), "settling before the first installment costs no interest by default");

        var advance = await host.SendOkAsync(new SaveLoanCommand(null, e1, LoanKind.Advance, Money.FromMajor(5_000).Minor, InterestMethod.None, 0, 1, 2026, 4, new DateOnly(2026, 3, 10), null));
        (await host.SendOkAsync(new GetLoanQuery(advance))).Summary.Number.Should().Be("A-0001");
        (await host.SendOkAsync(new GetLoansQuery(OpenOnly: true))).Should().HaveCount(2);

        var path = Path.Combine(host.Root, "statement.pdf");
        await host.SendOkAsync(new ExportLoanStatementCommand(id, path));
        File.ReadAllBytes(path)[..5].Should().Equal("%PDF-"u8.ToArray());
    }

    [Fact]
    public async Task Approval_is_blocked_when_net_pay_would_fall_below_the_minimum()
    {
        var (host, e1) = await SetUpAsync();
        await using var _ = host;
        await SetMinimumNetAsync(host, 40_000);

        var id = await host.SendOkAsync(new SaveLoanCommand(null, e1, LoanKind.Loan, Money.FromMajor(30_000).Minor, InterestMethod.None, 0, 3, 2026, 4, new DateOnly(2026, 3, 10), null));
        var preview = await host.SendOkAsync(new PreviewLoanQuery(e1, id, Money.FromMajor(30_000).Minor, InterestMethod.None, 0, 3, 2026, 4));
        preview.ProjectedNet.Should().Be(Money.FromMajor(37_500));
        preview.Warnings.Should().Contain(w => w.Contains("below the minimum", StringComparison.Ordinal));

        (await host.SendFailAsync(new ApproveLoanCommand(id))).Message.Should().Contain("37,500.00");
        await host.SendOkAsync(new SaveLoanCommand(id, e1, LoanKind.Loan, Money.FromMajor(30_000).Minor, InterestMethod.None, 0, 6, 2026, 4, new DateOnly(2026, 3, 10), null));
        await host.SendOkAsync(new ApproveLoanCommand(id));
    }

    [Fact]
    public async Task Payroll_deducts_installments_and_void_returns_them_to_due()
    {
        var (host, e1) = await SetUpAsync();
        await using var _ = host;
        var loan = await ActiveLoanAsync(host, e1, 10_000, 4);

        var summary = await host.SendOkAsync(new ComputePayrollCommand(2026, 2));
        var run = await host.SendOkAsync(new GetPayrollRunQuery(summary.RunId));
        var slip = await host.SendOkAsync(new GetPayslipQuery(run.Payslips.Single().Id));
        slip.DeductionLines.Should().ContainSingle(l => l.Source == PayslipLineSource.Loan && l.Amount == Money.FromMajor(2_500) && l.Name == "Loan L-0001 installment 1 of 4");
        slip.Net.Should().Be(Money.FromMajor(45_000));

        // A repayment after computing makes the draft stale.
        host.Clock.Advance(TimeSpan.FromMinutes(5));
        await host.SendOkAsync(new RecordLoanRepaymentCommand(loan, new DateOnly(2026, 3, 15), Money.FromMajor(1_000).Minor, "Cash", null));
        (await host.SendFailAsync(new FinalizePayrollRunCommand(run.Id))).Code.Should().Be("Concurrency.Conflict");
        host.Clock.Advance(TimeSpan.FromMinutes(5));
        summary = await host.SendOkAsync(new ComputePayrollCommand(2026, 2));
        slip = await host.SendOkAsync(new GetPayslipQuery((await host.SendOkAsync(new GetPayrollRunQuery(summary.RunId))).Payslips.Single().Id));
        slip.DeductionLines.Single(l => l.Source == PayslipLineSource.Loan).Amount.Should().Be(Money.FromMajor(1_500), "1,000 of the first installment was repaid in cash");
        await host.SendOkAsync(new FinalizePayrollRunCommand(summary.RunId));

        var detail = await host.SendOkAsync(new GetLoanQuery(loan));
        detail.Installments[0].Status.Should().Be(InstallmentStatus.Deducted);
        detail.Balance.Outstanding.Should().Be(Money.FromMajor(7_500));
        detail.Statement.Where(l => l.Kind == LoanTransactionKind.PayrollRepayment).Select(l => l.Credit).Should().Equal(slip.DeductionLines.Where(l => l.Source == PayslipLineSource.Loan).Select(l => l.Amount));

        await host.SendOkAsync(new VoidPayrollRunCommand(summary.RunId, "Wrong month"));
        detail = await host.SendOkAsync(new GetLoanQuery(loan));
        detail.Installments[0].Status.Should().Be(InstallmentStatus.Due);
        detail.Installments[0].Paid.Should().Be(Money.FromMajor(1_000), "the cash repayment stays");
        detail.Balance.Outstanding.Should().Be(Money.FromMajor(9_000));
        detail.Statement.Should().NotContain(l => l.Kind == LoanTransactionKind.PayrollRepayment);
    }

    [Fact]
    public async Task An_installment_that_breaks_the_minimum_net_moves_to_the_end_and_void_undoes_it()
    {
        var (host, e1) = await SetUpAsync();
        await using var _ = host;
        var loan = await ActiveLoanAsync(host, e1, 20_000, 2);
        await SetMinimumNetAsync(host, 40_000);

        var summary = await host.SendOkAsync(new ComputePayrollCommand(2026, 2));
        var row = (await host.SendOkAsync(new GetPayrollRunQuery(summary.RunId))).Payslips.Single();
        row.Net.Should().Be(Money.FromMajor(47_500));
        row.Warnings.Should().Contain("Skipped");
        await host.SendOkAsync(new FinalizePayrollRunCommand(summary.RunId));

        var detail = await host.SendOkAsync(new GetLoanQuery(loan));
        detail.Installments.Select(i => (i.Sequence, i.Status, i.DueMonth.Month)).Should().Equal(
            (1, InstallmentStatus.Skipped, 2), (2, InstallmentStatus.Due, 3), (3, InstallmentStatus.Due, 4));
        detail.Installments[2].IsCarried.Should().BeTrue();
        detail.Installments[2].Amount.Should().Be(Money.FromMajor(10_000));
        detail.Balance.Outstanding.Should().Be(Money.FromMajor(20_000));

        await host.SendOkAsync(new VoidPayrollRunCommand(summary.RunId, "Policy mistake"));
        detail = await host.SendOkAsync(new GetLoanQuery(loan));
        detail.Installments.Select(i => i.Status).Should().Equal(InstallmentStatus.Due, InstallmentStatus.Due);
    }

    [Fact]
    public async Task Settlement_follows_the_interest_policy_and_closes_the_loan()
    {
        var (host, e1) = await SetUpAsync();
        await using var _ = host;
        await host.SendOkAsync(new SavePolicyCommand(PolicySnapshot.LoanSection, new DateOnly(2025, 1, 1), new LoanPolicy { SettlementInterest = SettlementInterest.ProRata }, null));
        var loan = await ActiveLoanAsync(host, e1, 12_000, 12, InterestMethod.FlatRate, 10, startMonth: 1);
        await host.SendOkAsync(new RecordLoanRepaymentCommand(loan, new DateOnly(2026, 2, 1), Money.FromMajor(1_100).Minor, null, "January in cash"));
        (await host.SendFailAsync(new RecordLoanRepaymentCommand(loan, new DateOnly(2026, 2, 1), Money.FromMajor(50_000).Minor, null, null))).Code.Should().Be("Validation.AmountMinor");

        // March: three months of interest earned (300), one paid, so 200 charged and 900 waived.
        var quote = await host.SendOkAsync(new SettleLoanCommand(loan, new DateOnly(2026, 3, 15), "CASH-7"));

        quote.Payable.Should().Be(Money.FromMajor(11_000 + 200));
        quote.InterestWaived.Should().Be(Money.FromMajor(900));
        var detail = await host.SendOkAsync(new GetLoanQuery(loan));
        detail.Summary.Status.Should().Be(LoanStatus.Closed);
        detail.Balance.Outstanding.Should().Be(Money.Zero);
        detail.Statement[^1].Balance.Should().Be(Money.Zero);
        detail.Installments.Should().OnlyContain(i => i.Status == InstallmentStatus.Paid);

        var summary = await host.SendOkAsync(new ComputePayrollCommand(2026, 2));
        (await host.SendOkAsync(new GetPayrollRunQuery(summary.RunId))).Payslips.Single().Deductions.Should().Be(Money.Zero, "a closed loan is not deducted");
    }

    [Fact]
    public async Task A_leaver_repays_as_much_as_the_minimum_net_allows_and_the_rest_stays_owed()
    {
        var (host, e1) = await SetUpAsync();
        await using var _ = host;
        var loan = await ActiveLoanAsync(host, e1, 60_000, 10);
        await SetMinimumNetAsync(host, 5_000);
        await host.WithDbAsync(async db =>
        {
            var employee = await db.Employees.SingleAsync(e => e.Id == e1);
            employee.ExitDate = new DateOnly(2026, 2, 28);
            return await db.SaveChangesAsync();
        });

        var summary = await host.SendOkAsync(new ComputePayrollCommand(2026, 2));
        var slip = await host.SendOkAsync(new GetPayslipQuery((await host.SendOkAsync(new GetPayrollRunQuery(summary.RunId))).Payslips.Single().Id));
        slip.DeductionLines.Single(l => l.Source == PayslipLineSource.Loan).Amount.Should().Be(Money.FromMajor(42_500));
        slip.Net.Should().Be(Money.FromMajor(5_000));
        await host.SendOkAsync(new FinalizePayrollRunCommand(summary.RunId));

        var item = (await host.SendOkAsync(new GetLoansQuery(EmployeeId: e1))).Single();
        item.Outstanding.Should().Be(Money.FromMajor(17_500));
        item.IsReceivable.Should().BeTrue();
        item.Status.Should().Be(LoanStatus.Active);

        await host.SendOkAsync(new WriteOffLoanCommand(loan, new DateOnly(2026, 3, 15), "Left the country"));
        var detail = await host.SendOkAsync(new GetLoanQuery(loan));
        detail.Summary.Status.Should().Be(LoanStatus.WrittenOff);
        detail.Balance.Outstanding.Should().Be(Money.Zero);
    }

    [Fact]
    public async Task Requests_can_be_rejected_and_deleted_but_active_loans_cannot()
    {
        var (host, e1) = await SetUpAsync();
        await using var _ = host;
        var id = await host.SendOkAsync(new SaveLoanCommand(null, e1, LoanKind.Loan, 100_000, InterestMethod.None, 0, 2, 2026, 4, new DateOnly(2026, 3, 1), null));
        await host.SendOkAsync(new RejectLoanCommand(id, "Too soon after the last one"));
        (await host.SendFailAsync(new ApproveLoanCommand(id))).Code.Should().Be("Validation");
        await host.SendOkAsync(new DeleteLoanCommand(id));

        var active = await ActiveLoanAsync(host, e1, 1_000, 1, startMonth: 4);
        (await host.SendFailAsync(new DeleteLoanCommand(active))).Code.Should().Be("Validation");
        (await host.SendFailAsync(new SaveLoanCommand(null, e1, LoanKind.Loan, 100_000, InterestMethod.None, 0, 2, 2026, 13, new DateOnly(2026, 3, 1), null))).Code.Should().Be("Validation.StartMonth");
    }
}
