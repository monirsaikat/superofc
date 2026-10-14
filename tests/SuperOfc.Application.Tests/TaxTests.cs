using Microsoft.EntityFrameworkCore;
using SuperOfc.Application.Payroll;
using SuperOfc.Application.Tax;
using SuperOfc.Application.Tests.Support;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Payroll;
using SuperOfc.Domain.Security;
using SuperOfc.Domain.Tax;

namespace SuperOfc.Application.Tests;

/// <summary>
/// Tax in payroll (P6). Fiscal year July 2025 to June 2026, synthetic rules from the domain tests: basic 100,000
/// gives 152,500 a month, all taxable. In February (5 months left) 762,500 projected, a third exempt, 508,333.33
/// taxable: 5,000 + 10,833.33 = 15,833.33 a year, 3,167 a month.
/// </summary>
public class TaxTests
{
    /// <summary>The same synthetic rules as the domain tests (not a real country).</summary>
    private static readonly TaxRules Rules = new()
    {
        Slabs = [new(300_000, 0), new(100_000, 5), new(300_000, 10), new(null, 15)],
        FirstSlabByCategory = new Dictionary<string, decimal> { ["Female"] = 350_000 },
        Exemptions = [new ExemptionRule("One third of income", ExemptionKind.PercentOfIncome, Percent: 100m / 3, Cap: 450_000)],
        Rebate = new RebateRule(15, 20),
        MinimumTax = 3_000,
    };

    private static long TaxOf(PayslipDto slip) => slip.DeductionLines.Where(l => l.Source == PayslipLineSource.Tax).Sum(l => l.Amount.Minor);

    private static async Task<(TestHost Host, Guid Employee, Guid RuleSet)> SetUpAsync()
    {
        var host = await TestHost.CreateAsync();
        var components = await PayrollTestData.ComponentsAsync(host);
        var e1 = await EmployeeTestData.CreateAsync(host, "E1");
        await host.SendOkAsync(new SaveSalaryAssignmentCommand(null, e1, new DateOnly(2025, 1, 1), null, null, PayrollTestData.Lines(components, 100_000)));
        var ruleSet = await host.SendOkAsync(new SaveTaxRuleSetCommand(null, "Sample 2025-26", "XX", 2025, Rules, null));
        return (host, e1, ruleSet);
    }

    private static async Task<PayrollRunSummary> RunMonthAsync(TestHost host, Guid employee, int year, int month, bool finalize = true, bool punch = true)
    {
        if (punch)
        {
            await PayrollTestData.PunchWorkingDaysAsync(host, [employee], DateRange.Month(year, month));
        }

        var summary = await host.SendOkAsync(new ComputePayrollCommand(year, month));
        if (finalize)
        {
            await host.SendOkAsync(new FinalizePayrollRunCommand(summary.RunId));
        }

        return summary;
    }

    private static async Task<PayslipDto> SlipAsync(TestHost host, Guid runId)
        => await host.SendOkAsync(new GetPayslipQuery((await host.SendOkAsync(new GetPayrollRunQuery(runId))).Payslips.Single().Id));

    [Fact]
    public async Task Payslips_carry_this_months_share_and_the_statement_adds_up()
    {
        var (host, e1, _) = await SetUpAsync();
        await using var _ = host;

        var projection = await host.SendOkAsync(new GetTaxProjectionQuery(e1, 2026, 2));
        projection.RemainingMonths.Should().Be(5);
        projection.Result.AnnualTax.Should().Be(new Money(1_583_333));
        projection.Result.ThisMonth.Should().Be(Money.FromMajor(3_167));

        var feb = await RunMonthAsync(host, e1, 2026, 2);
        var slip = await SlipAsync(host, feb.RunId);
        slip.DeductionLines.Should().ContainSingle(l => l.Source == PayslipLineSource.Tax).Which.Amount.Should().Be(Money.FromMajor(3_167));

        // Move the clock past the fiscal year end and run March to June.
        host.Clock.UtcNow = new DateTime(2026, 7, 10, 4, 0, 0, DateTimeKind.Utc);
        var deducted = TaxOf(slip);
        for (var month = 3; month <= 6; month++)
        {
            deducted += TaxOf(await SlipAsync(host, (await RunMonthAsync(host, e1, 2026, month)).RunId));
        }

        var statement = await host.SendOkAsync(new GetTaxStatementQuery(e1, 2025));
        statement.Rows.Should().HaveCount(5);
        statement.TotalDeducted.Should().Be(new Money(deducted));
        statement.TotalDeducted.Major.Should().BeApproximately(15_833.33m, 1m, "June trues up the year");
        statement.Rows[^1].Explanation.Should().Contain(l => l.StartsWith("Last month of the year", StringComparison.Ordinal));

        var summary = await host.SendOkAsync(new GetTaxSummaryQuery(2025));
        summary.TotalDeducted.Should().Be(statement.TotalDeducted);

        var pdf = Path.Combine(host.Root, "tax.pdf");
        await host.SendOkAsync(new ExportTaxStatementCommand(e1, 2025, pdf));
        File.ReadAllBytes(pdf)[..5].Should().Equal("%PDF-"u8.ToArray());
        var xlsx = Path.Combine(host.Root, "tax.xlsx");
        (await host.SendOkAsync(new ExportTaxSummaryCommand(2025, xlsx))).Should().Be(1);
    }

    [Fact]
    public async Task Editing_rules_used_by_finalized_payroll_adds_a_version_from_the_next_month()
    {
        var (host, e1, ruleSet) = await SetUpAsync();
        await using var _ = host;
        await RunMonthAsync(host, e1, 2026, 2);

        var stricter = Rules with { Exemptions = [] };
        var version = await host.SendOkAsync(new SaveTaxRuleSetCommand(ruleSet, "Sample 2025-26 (budget change)", "XX", 2025, stricter, "Exemption removed"));

        version.Should().NotBe(ruleSet);
        var sets = await host.SendOkAsync(new GetTaxRuleSetsQuery());
        sets.Should().HaveCount(2);
        sets.Single(s => s.Id == ruleSet).Rules.Exemptions.Should().ContainSingle("history is not edited");
        sets.Single(s => s.Id == version).EffectiveFrom.Should().Be(new DateOnly(2026, 3, 1));
        (await host.SendFailAsync(new DeleteTaxRuleSetCommand(ruleSet))).Code.Should().Be("Validation");

        // An unused version is edited in place.
        (await host.SendOkAsync(new SaveTaxRuleSetCommand(version, "Renamed", "XX", 2025, stricter, null))).Should().Be(version);
        (await host.SendOkAsync(new GetTaxProjectionQuery(e1, 2026, 3))).RuleSetName.Should().Be("Renamed");
    }

    [Fact]
    public async Task Exempt_profiles_pay_no_tax_and_void_removes_the_computations()
    {
        var (host, e1, _) = await SetUpAsync();
        await using var _ = host;
        await host.SendOkAsync(new SaveTaxProfileCommand(e1, 2025, "Female", 0, 0, 0, IsExempt: true, "Taxed abroad"));
        (await host.SendOkAsync(new GetTaxProfilesQuery(2025))).Single().IsExempt.Should().BeTrue();

        var feb = await RunMonthAsync(host, e1, 2026, 2);
        (await SlipAsync(host, feb.RunId)).DeductionLines.Should().NotContain(l => l.Source == PayslipLineSource.Tax);
        (await host.WithDbAsync(db => db.TaxComputations.CountAsync())).Should().Be(1);

        await host.SendOkAsync(new VoidPayrollRunCommand(feb.RunId, "Profile was wrong"));
        (await host.WithDbAsync(db => db.TaxComputations.CountAsync())).Should().Be(0);

        await host.SendOkAsync(new SaveTaxProfileCommand(e1, 2025, "Female", Money.FromMajor(50_000).Minor, 0, 0, IsExempt: false, null));
        var again = await RunMonthAsync(host, e1, 2026, 2, finalize: false, punch: false);
        (await SlipAsync(host, again.RunId)).DeductionLines.Should().Contain(l => l.Source == PayslipLineSource.Tax);
    }

    [Fact]
    public async Task A_profile_change_after_compute_makes_the_draft_stale_and_hr_cannot_edit_tax()
    {
        var (host, e1, _) = await SetUpAsync();
        await using var _ = host;
        var feb = await RunMonthAsync(host, e1, 2026, 2, finalize: false);
        host.Clock.Advance(TimeSpan.FromMinutes(5));
        await host.SendOkAsync(new SaveTaxProfileCommand(e1, 2025, "General", Money.FromMajor(10_000).Minor, 0, 0, false, null));
        (await host.SendFailAsync(new FinalizePayrollRunCommand(feb.RunId))).Code.Should().Be("Concurrency.Conflict");

        await host.SignInAsAsync(Role.HR);
        (await host.SendFailAsync(new SaveTaxProfileCommand(e1, 2025, "General", 0, 0, 0, false, null))).Code.Should().Be("Auth.Forbidden");
    }

    [Fact]
    public async Task The_sample_country_pack_is_valid_and_can_be_used()
    {
        await using var host = await TestHost.CreateAsync();
        var pack = (await host.SendOkAsync(new GetTaxPacksQuery())).Should().ContainSingle(p => p.Id == "xx-sample").Subject;
        pack.Rules.Validate().Should().BeNull();
        await host.SendOkAsync(new SaveTaxRuleSetCommand(null, pack.Name, pack.CountryCode, 2025, pack.Rules, pack.Note));
        (await host.SendFailAsync(new SaveTaxRuleSetCommand(null, pack.Name, pack.CountryCode, 2025, pack.Rules, null))).Code.Should().Be("Validation.FiscalYear");
        (await host.SendFailAsync(new SaveTaxRuleSetCommand(null, "Bad", "XX", 2026, new TaxRules(), null))).Code.Should().Be("Validation.Rules");
    }
}
