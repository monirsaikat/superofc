using SuperOfc.Domain.Common;

namespace SuperOfc.Domain.Tax;

/// <summary>
/// Tax rules for one fiscal year, effective from a date (doc 03 Tax). Editing rules already used by finalized payroll
/// adds a new version from the next open month instead of changing history (P6-T2).
/// </summary>
public sealed class TaxRuleSet : CompanyEntity
{
    public string Name { get; set; } = string.Empty;

    /// <summary>ISO country code, or "XX" for the synthetic sample.</summary>
    public string CountryCode { get; set; } = "XX";

    /// <summary>The calendar year in which the fiscal year starts.</summary>
    public int FiscalYear { get; set; }

    public DateOnly EffectiveFrom { get; set; }

    /// <summary><see cref="TaxRules"/> as JSON.</summary>
    public string RulesJson { get; set; } = "{}";

    public string? Note { get; set; }
}

/// <summary>An employee's tax details for one fiscal year (declarations, category, previous employer).</summary>
public sealed class EmployeeTaxProfile : CompanyEntity
{
    public Guid EmployeeId { get; set; }

    public int FiscalYear { get; set; }

    public string Category { get; set; } = "General";

    public long DeclaredInvestmentMinor { get; set; }

    public long PriorEmployerIncomeMinor { get; set; }

    public long PriorEmployerTaxMinor { get; set; }

    /// <summary>No tax is deducted (for example a foreign national taxed elsewhere).</summary>
    public bool IsExempt { get; set; }

    public string? Note { get; set; }
}

/// <summary>The tax worked out for one finalized payslip, with the explanation shown on the tax page (doc 03 R5).</summary>
public sealed class TaxComputation : CompanyEntity
{
    public Guid PayslipId { get; set; }

    public Guid EmployeeId { get; set; }

    public Guid RuleSetId { get; set; }

    public int FiscalYear { get; set; }

    public int Year { get; set; }

    public int Month { get; set; }

    public long TaxableThisMonthMinor { get; set; }

    public long ProjectedIncomeMinor { get; set; }

    public long ExemptMinor { get; set; }

    public long TaxableIncomeMinor { get; set; }

    public long AnnualTaxMinor { get; set; }

    public long PaidBeforeMinor { get; set; }

    /// <summary>The tax actually deducted on the payslip.</summary>
    public long DeductedMinor { get; set; }

    /// <summary>Explanation lines as a JSON array of strings.</summary>
    public string ExplanationJson { get; set; } = "[]";
}
