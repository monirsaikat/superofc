using System.Globalization;
using SuperOfc.Domain.Common;

namespace SuperOfc.Domain.Tax;

/// <summary>
/// Monthly income tax by yearly projection (doc 03 R5). Pure and country-agnostic.
/// 1. Projected income = earned so far + prior employer + regular pay this month × remaining months + one-off pay this month.
/// 2. Minus exemptions, then slabs, rebate on declared investment, minimum tax.
/// 3. This month = (yearly tax - paid so far) / remaining months, rounded up; the last month trues up exactly.
/// </summary>
public static class TaxCalculator
{
    public static TaxResult Compute(TaxInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var rules = input.Rules;
        var remaining = Math.Max(1, input.RemainingMonthsIncludingThis);
        var lines = new List<string>();

        decimal Project(Money soFar, Money now) => soFar.Major + (now.Major * remaining);
        var oneOff = Math.Clamp(input.OneOffThisMonth.Major, 0, Math.Max(0, input.TaxableThisMonth.Major));
        var regular = input.TaxableThisMonth.Major - oneOff;
        var income = input.TaxableSoFar.Major + (regular * remaining) + oneOff + input.PriorEmployerIncome.Major;
        lines.Add(oneOff > 0
            ? $"Projected yearly income {N(income)} = earned {N(input.TaxableSoFar.Major + input.PriorEmployerIncome.Major)} + {N(regular)} × {remaining} months + one-off {N(oneOff)}"
            : $"Projected yearly income {N(income)} = earned {N(input.TaxableSoFar.Major + input.PriorEmployerIncome.Major)} + {N(regular)} × {remaining} months");

        decimal exempt = 0;
        foreach (var rule in rules.Exemptions)
        {
            var amount = rule.Kind switch
            {
                ExemptionKind.Fixed => rule.Amount,
                ExemptionKind.PercentOfComponent => Project(input.ComponentsSoFar.GetValueOrDefault(rule.ComponentCode!), input.ComponentsThisMonth.GetValueOrDefault(rule.ComponentCode!))
                                                    * rule.Percent / 100m,
                _ => income * rule.Percent / 100m,
            };
            amount = Math.Min(Math.Max(0, amount), rule.Cap ?? decimal.MaxValue);
            amount = Math.Min(amount, income - exempt);
            if (amount > 0)
            {
                exempt += amount;
                lines.Add($"Exempt: {rule.Name} {N(amount)}");
            }
        }

        var taxable = Math.Max(0, income - exempt);
        lines.Add($"Taxable income {N(taxable)}");

        var slabTax = SlabTax(rules, input.Category, taxable, lines, out var freeWidth);
        decimal rebate = 0;
        if (rules.Rebate is { } r && input.DeclaredInvestment.Major > 0 && slabTax > 0)
        {
            var eligible = Math.Min(input.DeclaredInvestment.Major, taxable * r.MaxInvestmentPercentOfIncome / 100m);
            rebate = Math.Min(eligible * r.Percent / 100m, r.MaxRebate ?? decimal.MaxValue);
            rebate = Math.Min(rebate, slabTax);
            lines.Add($"Investment rebate {N(rebate)} ({r.Percent:0.##} % of {N(eligible)})");
        }

        var annual = slabTax - rebate;
        if (rules.MinimumTax > 0 && taxable > freeWidth && annual < rules.MinimumTax)
        {
            annual = rules.MinimumTax;
            lines.Add($"Minimum tax applies: {N(annual)}");
        }

        annual = Math.Round(annual, 2, MidpointRounding.AwayFromZero);
        lines.Add($"Yearly tax {N(annual)}");

        var paid = input.TaxPaidSoFar.Major + input.PriorEmployerTax.Major;
        var due = Math.Max(0, annual - paid);
        var month = remaining == 1 ? due : Math.Min(due, RoundUp(due / remaining, rules.RoundingStep));
        lines.Add(remaining == 1
            ? $"Last month of the year: {N(annual)} - paid {N(paid)} = {N(month)}"
            : $"This month ({N(annual)} - paid {N(paid)}) / {remaining} = {N(month)}");

        return new TaxResult(Money.FromMajor(income), Money.FromMajor(exempt), Money.FromMajor(taxable), Money.FromMajor(slabTax), Money.FromMajor(rebate),
            Money.FromMajor(annual), Money.FromMajor(paid), Money.FromMajor(month), lines);
    }

    /// <summary>Progressive slabs; the category may widen the first (tax-free) slab.</summary>
    public static decimal SlabTax(TaxRules rules, string category, decimal taxable, List<string>? lines, out decimal freeWidth)
    {
        ArgumentNullException.ThrowIfNull(rules);
        freeWidth = 0;
        decimal tax = 0;
        var left = taxable;
        for (var i = 0; i < rules.Slabs.Count && left > 0; i++)
        {
            var slab = rules.Slabs[i];
            var width = slab.Width;
            if (i == 0 && rules.FirstSlabByCategory.TryGetValue(category, out var special))
            {
                width = special;
            }

            if (i == 0)
            {
                freeWidth = width ?? decimal.MaxValue;
            }

            var part = width is { } w ? Math.Min(left, w) : left;
            var slabTax = part * slab.RatePercent / 100m;
            tax += slabTax;
            left -= part;
            lines?.Add($"{N(part)} at {slab.RatePercent:0.##} % = {N(slabTax)}");
        }

        return tax;
    }

    private static decimal RoundUp(decimal value, decimal step)
        => step <= 0 ? value : Math.Ceiling(value / step) * step;

    private static string N(decimal value) => value.ToString("#,##0.##", CultureInfo.InvariantCulture);
}
