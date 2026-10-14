using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Payroll;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Organization;
using SuperOfc.Domain.Payroll;
using SuperOfc.Domain.Tax;

namespace SuperOfc.Application.Tax;

/// <summary>A country pack shipped with the app (Infrastructure/Seed/Tax).</summary>
public sealed record TaxPack(string Id, string Name, string CountryCode, string? Note, TaxRules Rules);

public interface ITaxPackSource
{
    IReadOnlyList<TaxPack> List();
}

public static class TaxJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false,
    };

    public static string Write(TaxRules rules) => JsonSerializer.Serialize(rules, Options);

    public static TaxRules Read(string json) => JsonSerializer.Deserialize<TaxRules>(json, Options) ?? new TaxRules();
}

/// <summary>Everything needed to work out one employee's tax for one month.</summary>
public sealed record TaxContext(TaxRuleSet RuleSet, TaxRules Rules, int FiscalYear, string Category, bool IsExempt, TaxInput Template, int RemainingMonths);

/// <summary>Loads rule sets, profiles and year-to-date figures from finalized payroll (doc 03 R5).</summary>
public sealed class TaxData(IAppDbContext db, ICurrentUser user)
{
    /// <summary>The rule set version in force for a month, or null when the company has none for that fiscal year.</summary>
    public async Task<TaxRuleSet?> RuleSetForAsync(Company company, DateOnly monthStart, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(company);
        var fiscalYear = company.FiscalYearStart(monthStart).Year;
        return await db.TaxRuleSets.AsNoTracking().Where(r => r.CompanyId == user.CompanyId && r.FiscalYear == fiscalYear && r.EffectiveFrom <= monthStart)
            .OrderByDescending(r => r.EffectiveFrom).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Per employee: the tax context for the month, built from finalized payslips earlier in the fiscal year.</summary>
    public async Task<Dictionary<Guid, TaxContext>> ContextsAsync(Company company, int year, int month, IReadOnlyCollection<Guid> employeeIds, Guid? excludeRunId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(company);
        var monthStart = new DateOnly(year, month, 1);
        var result = new Dictionary<Guid, TaxContext>();
        var ruleSet = await RuleSetForAsync(company, monthStart, cancellationToken).ConfigureAwait(false);
        if (ruleSet is null || employeeIds.Count == 0)
        {
            return result;
        }

        var rules = TaxJson.Read(ruleSet.RulesJson);
        var fiscal = company.FiscalYear(monthStart);
        var fiscalYear = fiscal.From.Year;
        var profiles = await db.EmployeeTaxProfiles.AsNoTracking().Where(p => p.FiscalYear == fiscalYear && employeeIds.Contains(p.EmployeeId))
            .ToDictionaryAsync(p => p.EmployeeId, cancellationToken).ConfigureAwait(false);
        var exits = await db.Employees.AsNoTracking().Where(e => employeeIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.ExitDate, cancellationToken).ConfigureAwait(false);

        // Finalized payslips earlier in this fiscal year.
        var fromIndex = (fiscal.From.Year * 12) + fiscal.From.Month - 1;
        var thisIndex = (year * 12) + month - 1;
        var runIds = await db.PayrollRuns.AsNoTracking().Where(r => r.CompanyId == user.CompanyId && r.Status == PayrollRunStatus.Finalized && r.Id != excludeRunId)
            .Join(db.PayrollPeriods, r => r.PeriodId, pp => pp.Id, (r, pp) => new { r.Id, pp.Year, pp.Month })
            .Where(x => (x.Year * 12) + x.Month - 1 >= fromIndex && (x.Year * 12) + x.Month - 1 < thisIndex)
            .Select(x => x.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var earlier = await db.Payslips.AsNoTracking().Include(p => p.Lines).Where(p => runIds.Contains(p.RunId) && employeeIds.Contains(p.EmployeeId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var byEmployee = earlier.GroupBy(p => p.EmployeeId).ToDictionary(g => g.Key, g => g.ToList());

        var fiscalEnd = fiscal.To!.Value;
        foreach (var id in employeeIds)
        {
            var profile = profiles.GetValueOrDefault(id);
            var slips = byEmployee.GetValueOrDefault(id) ?? [];
            var lastMonth = exits.GetValueOrDefault(id) is { } exit && exit < fiscalEnd ? exit : fiscalEnd;
            var remaining = Math.Max(1, ((lastMonth.Year * 12) + lastMonth.Month - 1) - thisIndex + 1);
            var components = slips.SelectMany(s => s.Lines.Where(l => l.Kind == ComponentKind.Earning && l.IsTaxable))
                .GroupBy(l => l.Code, StringComparer.Ordinal).ToDictionary(g => g.Key, g => new Money(g.Sum(l => l.AmountMinor)), StringComparer.Ordinal);
            var template = new TaxInput(rules, profile?.Category ?? "General", new Money(slips.Sum(s => s.TaxableGrossMinor)), components, Money.Zero,
                new Dictionary<string, Money>(), remaining, new Money(slips.Sum(s => s.TaxMinor)))
            {
                DeclaredInvestment = new Money(profile?.DeclaredInvestmentMinor ?? 0),
                PriorEmployerIncome = new Money(profile?.PriorEmployerIncomeMinor ?? 0),
                PriorEmployerTax = new Money(profile?.PriorEmployerTaxMinor ?? 0),
            };
            result[id] = new TaxContext(ruleSet, rules, fiscalYear, template.Category, profile?.IsExempt ?? false, template, remaining);
        }

        return result;
    }

    public static TaxResult Compute(TaxContext context, TaxBase month)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(month);
        return TaxCalculator.Compute(context.Template with { TaxableThisMonth = month.Taxable, ComponentsThisMonth = month.ByCode, OneOffThisMonth = month.OneOff });
    }
}

/// <summary>
/// Income tax in payroll (P6-T3): each payslip gets this month's share of the projected yearly tax; the last month of
/// the fiscal year (or the month an employee leaves) trues up. Finalize stores a <see cref="TaxComputation"/> per payslip.
/// </summary>
public sealed class TaxPayrollParticipant(IAppDbContext db, ICurrentUser user, ICompanyContext company, TaxData data, IIdGenerator ids) : IPayrollParticipant
{
    public async Task<PayrollContribution> PrepareAsync(PayrollContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var contribution = new PayrollContribution();
        var contexts = await data.ContextsAsync(Company(), context.Period.Year, context.Period.Month, context.EmployeeIds, null, cancellationToken).ConfigureAwait(false);
        foreach (var (employeeId, tax) in contexts)
        {
            if (!tax.IsExempt)
            {
                contribution.Tax[employeeId] = month => TaxData.Compute(tax, month).ThisMonth;
            }
        }

        return contribution;
    }

    public async Task<DateTime?> LastInputChangeAsync(PayrollPeriod period, CancellationToken cancellationToken)
    {
        var rules = await db.TaxRuleSets.Where(r => r.CompanyId == user.CompanyId).MaxAsync(r => (DateTime?)(r.ModifiedAtUtc ?? r.CreatedAtUtc), cancellationToken).ConfigureAwait(false);
        var profiles = await db.EmployeeTaxProfiles.Where(p => p.CompanyId == user.CompanyId).MaxAsync(p => (DateTime?)(p.ModifiedAtUtc ?? p.CreatedAtUtc), cancellationToken).ConfigureAwait(false);
        return new[] { rules, profiles }.Max();
    }

    public async Task<Result> OnFinalizedAsync(PayrollRun run, PayrollPeriod period, IReadOnlyList<Payslip> payslips, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(period);
        ArgumentNullException.ThrowIfNull(payslips);
        var contexts = await data.ContextsAsync(Company(), period.Year, period.Month, payslips.Select(p => p.EmployeeId).ToList(), run.Id, cancellationToken).ConfigureAwait(false);
        foreach (var slip in payslips)
        {
            if (!contexts.TryGetValue(slip.EmployeeId, out var context))
            {
                continue;
            }

            var byCode = slip.Lines.Where(l => l.Kind == ComponentKind.Earning && l.IsTaxable).GroupBy(l => l.Code, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => new Money(g.Sum(l => l.AmountMinor)), StringComparer.Ordinal);
            var oneOff = new Money(slip.Lines.Where(l => l.Kind == ComponentKind.Earning && l.IsTaxable && l.Source == PayslipLineSource.Adjustment).Sum(l => l.AmountMinor));
            var result = TaxData.Compute(context, new TaxBase(new Money(slip.TaxableGrossMinor), byCode) { OneOff = oneOff });
            db.TaxComputations.Add(new TaxComputation
            {
                Id = ids.NewId(), CompanyId = user.CompanyId, PayslipId = slip.Id, EmployeeId = slip.EmployeeId, RuleSetId = context.RuleSet.Id, FiscalYear = context.FiscalYear,
                Year = period.Year, Month = period.Month, TaxableThisMonthMinor = slip.TaxableGrossMinor, ProjectedIncomeMinor = result.ProjectedIncome.Minor,
                ExemptMinor = result.Exempt.Minor, TaxableIncomeMinor = result.TaxableIncome.Minor, AnnualTaxMinor = result.AnnualTax.Minor,
                PaidBeforeMinor = result.PaidSoFar.Minor, DeductedMinor = slip.TaxMinor,
                ExplanationJson = JsonSerializer.Serialize(context.IsExempt ? ["Exempt from tax deduction (employee tax profile)."] : result.Explanation),
            });
        }

        return Result.Success();
    }

    public async Task<Result> OnVoidedAsync(PayrollRun run, PayrollPeriod period, IReadOnlyList<Payslip> payslips, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payslips);
        var ids = payslips.Select(p => p.Id).ToList();
        var rows = await db.TaxComputations.Where(c => ids.Contains(c.PayslipId)).ToListAsync(cancellationToken).ConfigureAwait(false);
        db.TaxComputations.RemoveRange(rows);
        return Result.Success();
    }

    private Company Company() => company.Company ?? throw new InvalidOperationException("Company is not loaded.");
}
