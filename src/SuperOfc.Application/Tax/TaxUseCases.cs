using FluentValidation;
using Microsoft.EntityFrameworkCore;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Common;
using SuperOfc.Application.Payroll;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Payroll;
using SuperOfc.Domain.Security;
using SuperOfc.Domain.Tax;

namespace SuperOfc.Application.Tax;

public sealed record TaxRuleSetDto(Guid Id, string Name, string CountryCode, int FiscalYear, DateOnly EffectiveFrom, string? Note, TaxRules Rules, bool IsUsed, bool IsLatest)
{
    public string FiscalYearText => $"{FiscalYear}-{(FiscalYear + 1) % 100:00}";
}

[RequiresPermission(Permission.ViewSalaries)]
public sealed record GetTaxRuleSetsQuery : IQuery<IReadOnlyList<TaxRuleSetDto>>;

public sealed class GetTaxRuleSetsHandler(IAppDbContext db, ICurrentUser user) : IRequestHandler<GetTaxRuleSetsQuery, IReadOnlyList<TaxRuleSetDto>>
{
    public async Task<Result<IReadOnlyList<TaxRuleSetDto>>> HandleAsync(GetTaxRuleSetsQuery request, CancellationToken cancellationToken)
    {
        var sets = await db.TaxRuleSets.AsNoTracking().Where(r => r.CompanyId == user.CompanyId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var used = await db.TaxComputations.AsNoTracking().Where(c => c.CompanyId == user.CompanyId).Select(c => c.RuleSetId).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
        var latest = sets.GroupBy(s => s.FiscalYear).Select(g => g.MaxBy(s => s.EffectiveFrom)!.Id).ToHashSet();
        return sets.OrderByDescending(s => s.FiscalYear).ThenByDescending(s => s.EffectiveFrom)
            .Select(s => new TaxRuleSetDto(s.Id, s.Name, s.CountryCode, s.FiscalYear, s.EffectiveFrom, s.Note, TaxJson.Read(s.RulesJson), used.Contains(s.Id), latest.Contains(s.Id)))
            .ToList();
    }
}

/// <summary>
/// Creates or edits a fiscal year's rules. Rules already used by finalized payroll are never changed: the edit becomes a
/// new version effective from the first month after the finalized ones (P6-T2). Returns the id of the saved version.
/// </summary>
[RequiresPermission(Permission.ManageTax)]
public sealed record SaveTaxRuleSetCommand(Guid? Id, string Name, string CountryCode, int FiscalYear, TaxRules Rules, string? Note) : ICommand<Guid>;

public sealed class SaveTaxRuleSetValidator : AbstractValidator<SaveTaxRuleSetCommand>
{
    public SaveTaxRuleSetValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Enter a name.").MaximumLength(100);
        RuleFor(x => x.CountryCode).Matches("^[A-Z]{2}$").WithMessage("Use a two-letter country code, or XX for none.");
        RuleFor(x => x.FiscalYear).InclusiveBetween(2000, 2100);
        RuleFor(x => x.Rules).Must(r => r?.Validate() is null).WithMessage((_, r) => r?.Validate() ?? "Enter the rules.");
    }
}

public sealed class SaveTaxRuleSetHandler(IAppDbContext db, ICurrentUser user, ICompanyContext company, IIdGenerator ids, IAuditWriter audit, PayrollLocks locks)
    : IRequestHandler<SaveTaxRuleSetCommand, Guid>
{
    public async Task<Result<Guid>> HandleAsync(SaveTaxRuleSetCommand request, CancellationToken cancellationToken)
    {
        var info = company.Company ?? throw new InvalidOperationException("Company is not loaded.");
        var fiscalStart = new DateOnly(request.FiscalYear, info.FiscalYearStartMonth, 1);
        var fiscalEnd = fiscalStart.AddYears(1).AddDays(-1);
        var json = TaxJson.Write(request.Rules);
        TaxRuleSet? existing = null;
        if (request.Id is { } id)
        {
            existing = await db.TaxRuleSets.FirstOrDefaultAsync(r => r.Id == id && r.CompanyId == user.CompanyId, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                return Error.NotFound("TaxRuleSet");
            }
        }
        else if (await db.TaxRuleSets.AnyAsync(r => r.CompanyId == user.CompanyId && r.FiscalYear == request.FiscalYear, cancellationToken).ConfigureAwait(false))
        {
            return Error.Validation("FiscalYear", "This fiscal year already has tax rules. Open them to edit.");
        }

        var used = existing is not null && await db.TaxComputations.AnyAsync(c => c.RuleSetId == existing.Id, cancellationToken).ConfigureAwait(false);
        TaxRuleSet target;
        if (existing is not null && !used)
        {
            target = existing;
        }
        else
        {
            // New fiscal year, or a new version after the finalized months.
            var from = fiscalStart;
            if (used)
            {
                var lockedUntil = await locks.LockedUntilAsync(cancellationToken).ConfigureAwait(false) ?? fiscalStart.AddDays(-1);
                from = new DateOnly(lockedUntil.Year, lockedUntil.Month, 1).AddMonths(1);
                if (from > fiscalEnd)
                {
                    return Error.Validation("FiscalYear", "Payroll for the whole fiscal year is finalized; its tax rules can no longer change.");
                }

                if (await db.TaxRuleSets.AnyAsync(r => r.CompanyId == user.CompanyId && r.FiscalYear == request.FiscalYear && r.EffectiveFrom >= from, cancellationToken).ConfigureAwait(false))
                {
                    return Error.Validation("A newer version of these rules exists. Edit that one instead.");
                }
            }

            target = new TaxRuleSet { Id = ids.NewId(), CompanyId = user.CompanyId, FiscalYear = request.FiscalYear, EffectiveFrom = from };
            db.TaxRuleSets.Add(target);
        }

        target.Name = request.Name.Trim();
        target.CountryCode = request.CountryCode;
        target.RulesJson = json;
        target.Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        audit.Record(target == existing ? "Tax.RulesEdited" : "Tax.RulesAdded", $"Tax rules {target.Name} {target.FiscalYear} from {target.EffectiveFrom:yyyy-MM-dd}",
            nameof(TaxRuleSet), target.Id, after: request.Rules);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return target.Id;
    }
}

[RequiresPermission(Permission.ManageTax)]
public sealed record DeleteTaxRuleSetCommand(Guid Id) : ICommand<Unit>;

public sealed class DeleteTaxRuleSetHandler(IAppDbContext db, ICurrentUser user, IAuditWriter audit) : IRequestHandler<DeleteTaxRuleSetCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(DeleteTaxRuleSetCommand request, CancellationToken cancellationToken)
    {
        var set = await db.TaxRuleSets.FirstOrDefaultAsync(r => r.Id == request.Id && r.CompanyId == user.CompanyId, cancellationToken).ConfigureAwait(false);
        if (set is null)
        {
            return Error.NotFound("TaxRuleSet");
        }

        if (await db.TaxComputations.AnyAsync(c => c.RuleSetId == set.Id, cancellationToken).ConfigureAwait(false))
        {
            return Error.Validation("These rules were used by finalized payroll and cannot be deleted.");
        }

        db.TaxRuleSets.Remove(set);
        audit.Record("Tax.RulesDeleted", $"Tax rules {set.Name} {set.FiscalYear} deleted", nameof(TaxRuleSet), set.Id);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}

[RequiresPermission(Permission.ViewSalaries)]
public sealed record GetTaxPacksQuery : IQuery<IReadOnlyList<TaxPack>>;

public sealed class GetTaxPacksHandler(ITaxPackSource packs) : IRequestHandler<GetTaxPacksQuery, IReadOnlyList<TaxPack>>
{
    public Task<Result<IReadOnlyList<TaxPack>>> HandleAsync(GetTaxPacksQuery request, CancellationToken cancellationToken)
        => Task.FromResult<Result<IReadOnlyList<TaxPack>>>(Result<IReadOnlyList<TaxPack>>.Success(packs.List()));
}

public sealed record TaxProfileRow(Guid EmployeeId, string EmployeeCode, string EmployeeName, string Category, Money DeclaredInvestment, Money PriorEmployerIncome, Money PriorEmployerTax,
    bool IsExempt, string? Note, bool HasProfile);

[RequiresPermission(Permission.ViewSalaries)]
public sealed record GetTaxProfilesQuery(int FiscalYear) : IQuery<IReadOnlyList<TaxProfileRow>>;

public sealed class GetTaxProfilesHandler(IAppDbContext db, ICurrentUser user) : IRequestHandler<GetTaxProfilesQuery, IReadOnlyList<TaxProfileRow>>
{
    public async Task<Result<IReadOnlyList<TaxProfileRow>>> HandleAsync(GetTaxProfilesQuery request, CancellationToken cancellationToken)
    {
        var employees = await db.Employees.AsNoTracking().Where(e => e.CompanyId == user.CompanyId && e.DeletedAtUtc == null)
            .OrderBy(e => e.Code).Select(e => new { e.Id, e.Code, e.FullName, e.ExitDate }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var profiles = await db.EmployeeTaxProfiles.AsNoTracking().Where(p => p.CompanyId == user.CompanyId && p.FiscalYear == request.FiscalYear)
            .ToDictionaryAsync(p => p.EmployeeId, cancellationToken).ConfigureAwait(false);
        return employees.Where(e => e.ExitDate is null || e.ExitDate.Value.Year >= request.FiscalYear).Select(e =>
        {
            var p = profiles.GetValueOrDefault(e.Id);
            return new TaxProfileRow(e.Id, e.Code, e.FullName, p?.Category ?? "General", new Money(p?.DeclaredInvestmentMinor ?? 0), new Money(p?.PriorEmployerIncomeMinor ?? 0),
                new Money(p?.PriorEmployerTaxMinor ?? 0), p?.IsExempt ?? false, p?.Note, p is not null);
        }).ToList();
    }
}

/// <summary>Saves an employee's declarations for a fiscal year. Changes after finalized months affect only later months.</summary>
[RequiresPermission(Permission.ManageTax)]
public sealed record SaveTaxProfileCommand(Guid EmployeeId, int FiscalYear, string Category, long DeclaredInvestmentMinor, long PriorEmployerIncomeMinor, long PriorEmployerTaxMinor,
    bool IsExempt, string? Note) : ICommand<Unit>;

public sealed class SaveTaxProfileValidator : AbstractValidator<SaveTaxProfileCommand>
{
    public SaveTaxProfileValidator()
    {
        RuleFor(x => x.Category).NotEmpty().MaximumLength(50);
        RuleFor(x => x.DeclaredInvestmentMinor).GreaterThanOrEqualTo(0).WithMessage("Amounts cannot be negative.");
        RuleFor(x => x.PriorEmployerIncomeMinor).GreaterThanOrEqualTo(0).WithMessage("Amounts cannot be negative.");
        RuleFor(x => x.PriorEmployerTaxMinor).GreaterThanOrEqualTo(0).WithMessage("Amounts cannot be negative.");
        RuleFor(x => x.Note).MaximumLength(300);
    }
}

public sealed class SaveTaxProfileHandler(IAppDbContext db, ICurrentUser user, IIdGenerator ids, IAuditWriter audit) : IRequestHandler<SaveTaxProfileCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(SaveTaxProfileCommand request, CancellationToken cancellationToken)
    {
        var employee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == request.EmployeeId && e.CompanyId == user.CompanyId, cancellationToken).ConfigureAwait(false);
        if (employee is null)
        {
            return Error.NotFound("Employee");
        }

        var profile = await db.EmployeeTaxProfiles.FirstOrDefaultAsync(p => p.EmployeeId == request.EmployeeId && p.FiscalYear == request.FiscalYear, cancellationToken).ConfigureAwait(false);
        if (profile is null)
        {
            profile = new EmployeeTaxProfile { Id = ids.NewId(), CompanyId = user.CompanyId, EmployeeId = request.EmployeeId, FiscalYear = request.FiscalYear };
            db.EmployeeTaxProfiles.Add(profile);
        }

        profile.Category = request.Category.Trim();
        profile.DeclaredInvestmentMinor = request.DeclaredInvestmentMinor;
        profile.PriorEmployerIncomeMinor = request.PriorEmployerIncomeMinor;
        profile.PriorEmployerTaxMinor = request.PriorEmployerTaxMinor;
        profile.IsExempt = request.IsExempt;
        profile.Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        audit.Record("Tax.ProfileSaved", $"Tax profile {employee.Code} {request.FiscalYear}: {profile.Category}, investment {request.DeclaredInvestmentMinor / 100m:N2}", "Employee", employee.Id);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}

public sealed record TaxProjection(string RuleSetName, int FiscalYear, string Category, int RemainingMonths, Money MonthlyTaxable, TaxResult Result, bool IsExempt);

/// <summary>How this month's tax for one employee is worked out, from finalized months plus the current salary (P6-T2).</summary>
[RequiresPermission(Permission.ViewSalaries)]
public sealed record GetTaxProjectionQuery(Guid EmployeeId, int Year, int Month) : IQuery<TaxProjection>;

public sealed class GetTaxProjectionHandler(IAppDbContext db, ICurrentUser user, ICompanyContext company, TaxData data, IPolicyProvider policies)
    : IRequestHandler<GetTaxProjectionQuery, TaxProjection>
{
    public async Task<Result<TaxProjection>> HandleAsync(GetTaxProjectionQuery request, CancellationToken cancellationToken)
    {
        var info = company.Company ?? throw new InvalidOperationException("Company is not loaded.");
        var contexts = await data.ContextsAsync(info, request.Year, request.Month, [request.EmployeeId], null, cancellationToken).ConfigureAwait(false);
        if (!contexts.TryGetValue(request.EmployeeId, out var context))
        {
            return Error.Validation("There are no tax rules for this fiscal year. Add them on the Tax page.");
        }

        // This month's taxable pay: the salary in force on the first of the month, full month.
        var first = new DateOnly(request.Year, request.Month, 1);
        var assignment = await db.SalaryAssignments.AsNoTracking().Include(a => a.Lines)
            .Where(a => a.EmployeeId == request.EmployeeId && a.CompanyId == user.CompanyId && a.EffectiveFrom <= first.AddMonths(1).AddDays(-1))
            .OrderByDescending(a => a.EffectiveFrom).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var byCode = new Dictionary<string, Money>(StringComparer.Ordinal);
        if (assignment is not null)
        {
            var components = await db.SalaryComponents.AsNoTracking().Where(c => c.CompanyId == user.CompanyId && c.DeletedAtUtc == null && c.IsActive)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var policy = await policies.GetSnapshotAsync(first, cancellationToken).ConfigureAwait(false);
            var preview = SalaryLines.Preview(assignment.Lines.Select(l => new SalaryLineInput(l.ComponentId, l.AmountMinor, l.Rate)).ToList(),
                components.Select(ComponentDef.From).ToList(), policy.Payroll.LineRounding);
            var taxable = components.Where(c => c.IsTaxable && c.Kind == ComponentKind.Earning).Select(c => c.Code).ToHashSet(StringComparer.Ordinal);
            foreach (var line in preview.Lines.Where(l => taxable.Contains(l.Code)))
            {
                byCode[line.Code] = line.Amount;
            }
        }

        var monthly = Money.Sum(byCode.Values);
        var result = TaxData.Compute(context, new TaxBase(monthly, byCode));
        return new TaxProjection(context.RuleSet.Name, context.FiscalYear, context.Category, context.RemainingMonths, monthly, result, context.IsExempt);
    }
}
