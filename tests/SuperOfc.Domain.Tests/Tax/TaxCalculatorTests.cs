using SuperOfc.Domain.Common;
using SuperOfc.Domain.Tax;

namespace SuperOfc.Domain.Tests.Tax;

/// <summary>Synthetic rules (not a real country): 300k free, 100k at 5 %, 300k at 10 %, rest at 15 %; women 350k free;
/// one third of income exempt up to 450k; 15 % rebate on investment up to 20 % of income; minimum tax 3,000.</summary>
public class TaxCalculatorTests
{
    public static readonly TaxRules Rules = new()
    {
        Slabs = [new(300_000, 0), new(100_000, 5), new(300_000, 10), new(null, 15)],
        FirstSlabByCategory = new Dictionary<string, decimal> { ["Female"] = 350_000 },
        Exemptions = [new ExemptionRule("One third of income", ExemptionKind.PercentOfIncome, Percent: 100m / 3, Cap: 450_000)],
        Rebate = new RebateRule(15, 20),
        MinimumTax = 3_000,
    };

    private static readonly Dictionary<string, Money> None = [];

    private static TaxResult Month(decimal monthly, int remaining, decimal earned, decimal paid, string category = "General", decimal investment = 0)
        => TaxCalculator.Compute(new TaxInput(Rules, category, Money.FromMajor(earned), None, Money.FromMajor(monthly), None, remaining, Money.FromMajor(paid))
        {
            DeclaredInvestment = Money.FromMajor(investment),
        });

    [Theory]
    [InlineData(50_000, "General", 0, 5_000)]       // 600k - 200k exempt = 400k: 100k at 5 %
    [InlineData(100_000, "Female", 0, 42_500)]      // first 350k free: 5k + 30k + 7.5k
    [InlineData(100_000, "General", 0, 50_000)]     // 1.2M - 400k = 800k: 5k + 30k + 15k
    [InlineData(50_000, "General", 60_000, 3_000)]  // rebate 9k wipes 5k tax, minimum tax 3k applies
    [InlineData(20_000, "General", 0, 0)]           // 240k - 80k = 160k: below the free slab, no minimum
    public void Yearly_tax_follows_slabs_exemptions_rebate_and_minimum(decimal monthly, string category, decimal investment, decimal expected)
        => Month(monthly, 12, 0, 0, category, investment).AnnualTax.Should().Be(Money.FromMajor(expected));

    [Theory]
    [InlineData(50_000)]
    [InlineData(100_000)]
    [InlineData(37_777.77)]
    public void Twelve_identical_months_add_up_to_the_yearly_tax(decimal monthly)
    {
        decimal paid = 0;
        decimal earned = 0;
        for (var month = 0; month < 12; month++)
        {
            var result = Month(monthly, 12 - month, earned, paid);
            paid += result.ThisMonth.Major;
            earned += monthly;
        }

        paid.Should().Be(Month(monthly, 12, 0, 0).AnnualTax.Major);
    }

    [Fact]
    public void A_raise_mid_year_is_spread_over_the_remaining_months()
    {
        var before = Month(50_000, 12, 0, 0);
        var after = Month(100_000, 6, 300_000, before.ThisMonth.Major * 6);

        after.ProjectedIncome.Should().Be(Money.FromMajor(900_000));
        after.ThisMonth.Major.Should().BeGreaterThan(before.ThisMonth.Major);
        after.Explanation.Should().Contain(l => l.Contains("× 6 months", StringComparison.Ordinal));
    }

    [Fact]
    public void Component_exemptions_use_the_projected_component()
    {
        var rules = Rules with { Exemptions = [new ExemptionRule("House rent", ExemptionKind.PercentOfComponent, Percent: 50, ComponentCode: "HRA", Cap: 300_000)] };
        var result = TaxCalculator.Compute(new TaxInput(rules, "General", Money.Zero, None, Money.FromMajor(60_000),
            new Dictionary<string, Money> { ["HRA"] = Money.FromMajor(20_000) }, 12, Money.Zero));

        result.Exempt.Should().Be(Money.FromMajor(120_000));
        result.TaxableIncome.Should().Be(Money.FromMajor(600_000));
    }

    [Fact]
    public void Overpaid_tax_gives_zero_not_a_refund()
        => Month(20_000, 3, 180_000, 10_000).ThisMonth.Should().Be(Money.Zero);

    [Theory]
    [InlineData(new double[0], "slab")]
    public void Rules_without_slabs_are_invalid(double[] _, string expected)
        => new TaxRules().Validate().Should().Contain(expected);

    [Fact]
    public void The_last_slab_must_be_open_ended()
        => (Rules with { Slabs = [new(300_000, 0)] }).Validate().Should().NotBeNull();

    [Fact]
    public void The_sample_rules_are_valid()
        => Rules.Validate().Should().BeNull();

    [Fact]
    public void A_one_off_bonus_is_counted_once_not_for_every_remaining_month()
    {
        var input = new TaxInput(Rules, "General", Money.Zero, None, Money.FromMajor(150_000), None, 12, Money.Zero) { OneOffThisMonth = Money.FromMajor(100_000) };
        var result = TaxCalculator.Compute(input);
        result.ProjectedIncome.Should().Be(Money.FromMajor(700_000), "50,000 × 12 regular pay + 100,000 once");
        result.Explanation[0].Should().Contain("one-off 100,000");
        result.AnnualTax.Should().Be(Money.FromMajor(11_666.67m), "700,000 - a third exempt = 466,666.67: 5,000 + 6,666.67");
    }
}
