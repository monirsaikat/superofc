using SuperOfc.Domain.Attendance;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Loans;
using SuperOfc.Domain.Payroll;
using SuperOfc.Domain.Policies;

namespace SuperOfc.Domain.Tests.Loans;

public class LoanScheduleTests
{
    private static LoanSchedule Generate(decimal principal, int count, InterestMethod method = InterestMethod.None, decimal rate = 0, long step = 100)
        => LoanScheduleGenerator.Generate(new ScheduleRequest(Money.FromMajor(principal), method, rate, count, 2026, 11, new RoundingPolicy(RoundingMode.HalfUp, step))).Value;

    [Theory]
    [InlineData(10_000, 3, 1)]
    [InlineData(10_000, 3, 100)]
    [InlineData(10_000, 7, 1000)]
    [InlineData(99_999.99, 12, 100)]
    [InlineData(1_000, 1, 100)]
    [InlineData(50, 120, 100)]
    public void Interest_free_schedule_sums_exactly_and_the_last_installment_absorbs_rounding(decimal principal, int count, long step)
    {
        var schedule = Generate(principal, count, step: step);

        schedule.Installments.Should().HaveCount(count);
        new Money(schedule.Installments.Sum(i => i.Amount.Minor)).Should().Be(Money.FromMajor(principal));
        schedule.Installments.Take(count - 1).Should().OnlyContain(i => i.Amount == schedule.Installment);
        schedule.Installments[^1].Amount.Should().BeLessThanOrEqualTo(schedule.Installment);
        schedule.Installments[^1].Amount.Minor.Should().BePositive();
        schedule.Interest.Should().Be(Money.Zero);
    }

    [Fact]
    public void Installments_are_whole_steps_when_the_count_allows_it()
    {
        var schedule = Generate(10_000, 3);

        schedule.Installment.Should().Be(Money.FromMajor(3_334));
        schedule.Installments[^1].Amount.Should().Be(Money.FromMajor(3_332));
    }

    [Fact]
    public void Flat_interest_is_principal_times_rate_times_months()
    {
        // 60,000 at 10 % a year over 12 months = 6,000 interest; 66,000 / 12 = 5,500.
        var schedule = Generate(60_000, 12, InterestMethod.FlatRate, 10);

        schedule.Interest.Should().Be(Money.FromMajor(6_000));
        schedule.Total.Should().Be(Money.FromMajor(66_000));
        schedule.Installments.Should().OnlyContain(i => i.Amount == Money.FromMajor(5_500) && i.Interest == Money.FromMajor(500));
        new Money(schedule.Installments.Sum(i => i.Principal.Minor)).Should().Be(Money.FromMajor(60_000));
    }

    [Fact]
    public void Interest_parts_sum_to_the_total_interest_with_uneven_amounts()
    {
        var schedule = Generate(25_000, 7, InterestMethod.FlatRate, 12.5m);

        new Money(schedule.Installments.Sum(i => i.Interest.Minor)).Should().Be(schedule.Interest);
        new Money(schedule.Installments.Sum(i => i.Amount.Minor)).Should().Be(schedule.Total);
        schedule.Installments.Should().OnlyContain(i => i.Principal.Minor >= 0 && i.Interest.Minor >= 0);
    }

    [Fact]
    public void Due_months_follow_the_start_month_across_the_year_end()
        => Generate(3_000, 3).Installments.Select(i => (i.Year, i.Month)).Should().Equal((2026, 11), (2026, 12), (2027, 1));

    [Theory]
    [InlineData(0, 3)]
    [InlineData(1_000, 0)]
    [InlineData(1_000, 121)]
    [InlineData(0.05, 10)]
    public void Invalid_requests_are_rejected(decimal principal, int count)
        => LoanScheduleGenerator.Generate(new ScheduleRequest(Money.FromMajor(principal), InterestMethod.None, 0, count, 2026, 1, RoundingPolicy.WholeUnit)).IsFailure.Should().BeTrue();
}

public class LoanBalanceTests
{
    /// <summary>An active 12,000 loan at 10 % over 12 months (13,200 total, 1,100 a month, 100 of it interest) from January 2026.</summary>
    private static Loan ActiveLoan()
    {
        var schedule = LoanScheduleGenerator.Generate(new ScheduleRequest(Money.FromMajor(12_000), InterestMethod.FlatRate, 10, 12, 2026, 1, RoundingPolicy.WholeUnit)).Value;
        var loan = new Loan
        {
            Id = Guid.NewGuid(), PrincipalMinor = schedule.Principal.Minor, InterestMinor = schedule.Interest.Minor, InstallmentCount = 12, InterestMethod = InterestMethod.FlatRate,
            AnnualRate = 10, StartYear = 2026, StartMonth = 1, Status = LoanStatus.Active,
        };
        loan.Installments = schedule.Installments.Select(i => new LoanInstallment
        {
            Id = Guid.NewGuid(), LoanId = loan.Id, Sequence = i.Sequence, Year = i.Year, Month = i.Month, AmountMinor = i.Amount.Minor, PrincipalMinor = i.Principal.Minor, InterestMinor = i.Interest.Minor,
        }).ToList();
        loan.Transactions.Add(new LoanTransaction { Kind = LoanTransactionKind.Disbursement, AmountMinor = loan.PrincipalMinor });
        return loan;
    }

    private static void Pay(Loan loan, long amount, LoanTransactionKind kind = LoanTransactionKind.PayrollRepayment)
    {
        foreach (var (installment, part) in LoanBalanceCalculator.Allocate(loan.Installments, amount))
        {
            installment.PaidMinor += part;
            if (installment.RemainingMinor == 0)
            {
                installment.Status = InstallmentStatus.Deducted;
            }
        }

        loan.Transactions.Add(new LoanTransaction { Kind = kind, AmountMinor = amount });
    }

    [Fact]
    public void Balance_is_total_minus_repayments_and_matches_the_open_installments()
    {
        var loan = ActiveLoan();
        Pay(loan, 330_000);   // three installments
        Pay(loan, 50_000);    // half of the fourth: interest first

        var balance = LoanBalanceCalculator.Balance(loan);

        balance.Outstanding.Should().Be(Money.FromMajor(13_200 - 3_800));
        (balance.OutstandingPrincipal + balance.OutstandingInterest).Should().Be(balance.Outstanding);
        balance.OutstandingInterest.Should().Be(Money.FromMajor(800), "the half-paid installment's interest is already covered");
        loan.Installments.Count(i => i.Status == InstallmentStatus.Deducted).Should().Be(3);
    }

    [Fact]
    public void Allocation_fills_installments_in_due_order_and_stops_at_the_amount()
    {
        var loan = ActiveLoan();
        var parts = LoanBalanceCalculator.Allocate(loan.Installments, 250_000);

        parts.Select(p => (p.Installment.Sequence, p.Amount)).Should().Equal((1, 110_000L), (2, 110_000L), (3, 30_000L));
    }

    [Theory]
    [InlineData(SettlementInterest.Full, 8_800, 0)]
    [InlineData(SettlementInterest.None, 8_000, 800)]
    [InlineData(SettlementInterest.ProRata, 8_200, 600)]
    public void Settlement_after_four_months_charges_interest_per_policy(SettlementInterest policy, decimal payable, decimal waived)
    {
        var loan = ActiveLoan();
        Pay(loan, 440_000);   // January to April paid

        // Settling in June: pro rata earns six months of interest (600), four already paid, so 200 more.
        var quote = LoanBalanceCalculator.Settlement(loan, policy, 2026, 6);

        quote.Outstanding.Should().Be(Money.FromMajor(8_800));
        quote.PrincipalDue.Should().Be(Money.FromMajor(8_000));
        quote.Payable.Should().Be(Money.FromMajor(payable));
        quote.InterestWaived.Should().Be(Money.FromMajor(waived));
        (quote.Payable + quote.InterestWaived).Should().Be(quote.Outstanding);
    }

    [Fact]
    public void Balance_is_zero_before_the_loan_is_paid_out()
    {
        var loan = ActiveLoan();
        loan.Status = LoanStatus.Approved;
        LoanBalanceCalculator.Balance(loan).Outstanding.Should().Be(Money.Zero);
    }
}

public class LoanDeductionRuleTests
{
    private static readonly DateRange March = DateRange.Month(2026, 3);

    private static PayslipResult Compute(long minimumNet, params DueDeduction[] loans)
    {
        var basic = new ComponentDef(Guid.NewGuid(), "BASIC", "Basic", ComponentKind.Earning, ComponentCalc.Fixed, null, true, true, true, 0);
        var input = new PayslipInput(March, new DateRange(new DateOnly(2025, 1, 1), null), true, [.. March.EachDay().Select(d => new PayDay(d, AttendanceStatus.Present))], 31,
            [basic], [new PaySegment(new DateRange(new DateOnly(2025, 1, 1), null), [new PayLine(basic.Id, 1_000_000)])],
            new PayrollPolicy { MinimumNetPayMinor = minimumNet }, new AttendancePolicy())
        {
            Loans = loans,
        };
        return PayslipCalculator.Compute(input);
    }

    [Fact]
    public void Installments_are_deducted_exactly_without_further_rounding()
    {
        var result = Compute(0, new DueDeduction(Guid.NewGuid(), "LOAN", "Loan", new Money(333_334)));
        result.Loan.Should().Be(new Money(333_334));
        result.Net.Should().Be(new Money(666_666));
    }

    [Fact]
    public void An_installment_that_breaks_the_minimum_is_skipped_whole()
    {
        var result = Compute(700_000, new DueDeduction(Guid.NewGuid(), "LOAN", "Loan", new Money(400_000)));
        result.Loan.Should().Be(Money.Zero);
        result.Skipped.Should().ContainSingle();
    }

    [Fact]
    public void A_final_recovery_takes_what_the_minimum_allows()
    {
        var result = Compute(700_000, new DueDeduction(Guid.NewGuid(), "LOAN", "Final recovery", new Money(400_000), AllowPartial: true));
        result.Loan.Should().Be(new Money(300_000));
        result.Net.Should().Be(new Money(700_000));
        result.Warnings.Should().ContainSingle(w => w.Contains("could be recovered", StringComparison.Ordinal));
    }
}
