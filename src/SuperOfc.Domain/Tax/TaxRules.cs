using SuperOfc.Domain.Common;

namespace SuperOfc.Domain.Tax;

/// <summary>One band of income and its rate. <see cref="Width"/> null means "the rest of the income".</summary>
public sealed record TaxSlab(decimal? Width, decimal RatePercent);

public enum ExemptionKind
{
    /// <summary>A fixed yearly amount.</summary>
    Fixed = 0,

    /// <summary>A percentage of one component's yearly amount, optionally capped.</summary>
    PercentOfComponent = 1,

    /// <summary>A percentage of total yearly taxable income, optionally capped.</summary>
    PercentOfIncome = 2,
}

/// <summary>Income that is not taxed (doc 03 R5). Amounts are yearly, in major currency units.</summary>
public sealed record ExemptionRule(string Name, ExemptionKind Kind, decimal Amount = 0, decimal Percent = 0, string? ComponentCode = null, decimal? Cap = null);

/// <summary>Tax credit for declared investment: Percent of the eligible investment, capped by a share of income and an amount.</summary>
public sealed record RebateRule(decimal Percent, decimal MaxInvestmentPercentOfIncome = 100, decimal? MaxRebate = null);

/// <summary>
/// A country's yearly tax rules as data (ADR-08). Slabs are applied in order; a category (for example "Female")
/// can have its own tax-free first slab. All amounts are yearly, in major currency units.
/// </summary>
public sealed record TaxRules
{
    public IReadOnlyList<TaxSlab> Slabs { get; init; } = [];

    /// <summary>Width of the first (usually 0 %) slab per taxpayer category; categories not listed use the first slab.</summary>
    public IReadOnlyDictionary<string, decimal> FirstSlabByCategory { get; init; } = new Dictionary<string, decimal>();

    public IReadOnlyList<ExemptionRule> Exemptions { get; init; } = [];

    public RebateRule? Rebate { get; init; }

    /// <summary>Yearly minimum tax once taxable income is above the tax-free slab; 0 for none.</summary>
    public decimal MinimumTax { get; init; }

    /// <summary>Monthly tax is rounded up to this step in major units (1 = whole units).</summary>
    public decimal RoundingStep { get; init; } = 1;

    public IReadOnlyList<string> Categories => ["General", .. FirstSlabByCategory.Keys.Where(k => k != "General").Order(StringComparer.Ordinal)];

    /// <summary>Checks the rules make sense; returns the first problem or null.</summary>
    public string? Validate()
    {
        if (Slabs.Count == 0)
        {
            return "Add at least one slab.";
        }

        if (Slabs.Take(Slabs.Count - 1).Any(s => s.Width is null or <= 0))
        {
            return "Every slab except the last needs a width above zero.";
        }

        if (Slabs[^1].Width is not null)
        {
            return "The last slab covers the rest of the income; leave its width empty.";
        }

        if (Slabs.Any(s => s.RatePercent is < 0 or > 100))
        {
            return "Rates must be between 0 and 100 %.";
        }

        if (FirstSlabByCategory.Values.Any(v => v < 0) || MinimumTax < 0 || RoundingStep <= 0)
        {
            return "Amounts cannot be negative and the rounding step must be above zero.";
        }

        if (Exemptions.Any(e => string.IsNullOrWhiteSpace(e.Name) || e.Percent is < 0 or > 100 || e.Amount < 0 || e.Cap < 0
                                || (e.Kind == ExemptionKind.PercentOfComponent && string.IsNullOrWhiteSpace(e.ComponentCode))))
        {
            return "Each exemption needs a name, a percentage from 0 to 100, and a component code when it is a share of a component.";
        }

        return Rebate is { Percent: < 0 or > 100 } ? "The rebate percentage must be between 0 and 100." : null;
    }
}

/// <summary>What the calculator needs about one employee for one payroll month (doc 03 R5).</summary>
public sealed record TaxInput(
    TaxRules Rules,
    string Category,
    Money TaxableSoFar,
    IReadOnlyDictionary<string, Money> ComponentsSoFar,
    Money TaxableThisMonth,
    IReadOnlyDictionary<string, Money> ComponentsThisMonth,
    int RemainingMonthsIncludingThis,
    Money TaxPaidSoFar)
{
    /// <summary>Part of <see cref="TaxableThisMonth"/> paid once (bonus, arrears): counted once, not for every remaining month.</summary>
    public Money OneOffThisMonth { get; init; } = Money.Zero;

    public Money DeclaredInvestment { get; init; } = Money.Zero;

    public Money PriorEmployerIncome { get; init; } = Money.Zero;

    public Money PriorEmployerTax { get; init; } = Money.Zero;
}

public sealed record TaxResult(
    Money ProjectedIncome,
    Money Exempt,
    Money TaxableIncome,
    Money SlabTax,
    Money Rebate,
    Money AnnualTax,
    Money PaidSoFar,
    Money ThisMonth,
    IReadOnlyList<string> Explanation);
