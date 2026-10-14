using FluentValidation;
using Microsoft.EntityFrameworkCore;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Common;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Payroll;
using SuperOfc.Domain.Security;

namespace SuperOfc.Application.Payroll;

public sealed record SalaryComponentDto(
    Guid Id,
    string Code,
    string Name,
    ComponentKind Kind,
    ComponentCalc Calc,
    decimal DefaultRate,
    Money DefaultAmount,
    string? Formula,
    bool IsBasic,
    bool IsTaxable,
    bool IsAttendanceLinked,
    int DisplayOrder,
    bool IsActive)
{
    public bool UsesRate => Calc is ComponentCalc.PercentOfBasic or ComponentCalc.PercentOfGross;

    public bool UsesAmount => Calc == ComponentCalc.Fixed;

    public static SalaryComponentDto From(SalaryComponent c)
    {
        ArgumentNullException.ThrowIfNull(c);
        return new(c.Id, c.Code, c.Name, c.Kind, c.Calc, c.DefaultRate, new Money(c.DefaultAmountMinor), c.Formula, c.IsBasic, c.IsTaxable, c.IsAttendanceLinked, c.DisplayOrder, c.IsActive);
    }
}

/// <summary>A value for one component in a structure or an assignment.</summary>
public sealed record SalaryLineInput(Guid ComponentId, long AmountMinor, decimal Rate);

[RequiresPermission(Permission.ViewSalaries)]
public sealed record GetSalaryComponentsQuery(bool ActiveOnly = false) : IQuery<IReadOnlyList<SalaryComponentDto>>;

public sealed class GetSalaryComponentsHandler(IAppDbContext db, ICurrentUser user) : IRequestHandler<GetSalaryComponentsQuery, IReadOnlyList<SalaryComponentDto>>
{
    public async Task<Result<IReadOnlyList<SalaryComponentDto>>> HandleAsync(GetSalaryComponentsQuery request, CancellationToken cancellationToken)
    {
        var query = db.SalaryComponents.AsNoTracking().Where(c => c.CompanyId == user.CompanyId && c.DeletedAtUtc == null);
        if (request.ActiveOnly)
        {
            query = query.Where(c => c.IsActive);
        }

        var list = await query.ToListAsync(cancellationToken).ConfigureAwait(false);
        return list.OrderBy(c => c.Kind).ThenBy(c => c.DisplayOrder).ThenBy(c => c.Code, StringComparer.Ordinal).Select(SalaryComponentDto.From).ToList();
    }
}

[RequiresPermission(Permission.ManagePayroll)]
public sealed record SaveSalaryComponentCommand(
    Guid? Id,
    string Code,
    string Name,
    ComponentKind Kind,
    ComponentCalc Calc,
    decimal DefaultRate,
    long DefaultAmountMinor,
    string? Formula,
    bool IsBasic,
    bool IsTaxable,
    bool IsAttendanceLinked,
    int DisplayOrder,
    bool IsActive) : ICommand<Guid>;

public sealed class SaveSalaryComponentValidator : AbstractValidator<SaveSalaryComponentCommand>
{
    public SaveSalaryComponentValidator()
    {
        RuleFor(x => x.Code).Must(c => SalaryComponent.IsValidCode(c?.Trim().ToUpperInvariant()))
            .WithMessage("Use 1-20 capital letters, digits or _ starting with a letter, for example HRA.");
        RuleFor(x => x.Name).NotEmpty().WithMessage("Enter a name.").MaximumLength(100);
        RuleFor(x => x.DefaultRate).InclusiveBetween(0, 1000).WithMessage("Enter a percentage from 0 to 1000.");
        RuleFor(x => x.DefaultAmountMinor).GreaterThanOrEqualTo(0).WithMessage("The amount cannot be negative.");
        RuleFor(x => x.Formula).NotEmpty().When(x => x.Calc == ComponentCalc.Formula).WithMessage("Enter a formula, for example BASIC * 0.1.");
        RuleFor(x => x.IsBasic).Must((x, basic) => !basic || (x.Kind == ComponentKind.Earning && x.Calc == ComponentCalc.Fixed))
            .WithMessage("The basic salary must be a fixed earning.");
        RuleFor(x => x.IsActive).Must((x, active) => active || !x.IsBasic).WithMessage("The basic salary cannot be switched off.");
    }
}

public sealed class SaveSalaryComponentHandler(IAppDbContext db, ICurrentUser user, IIdGenerator ids, IAuditWriter audit) : IRequestHandler<SaveSalaryComponentCommand, Guid>
{
    public async Task<Result<Guid>> HandleAsync(SaveSalaryComponentCommand request, CancellationToken cancellationToken)
    {
        var code = request.Code.Trim().ToUpperInvariant();
        var all = await db.SalaryComponents.Where(c => c.CompanyId == user.CompanyId && c.DeletedAtUtc == null).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (all.Any(c => c.Code == code && c.Id != request.Id))
        {
            return Error.Validation(nameof(request.Code), "Another component already uses this code.");
        }

        SalaryComponent component;
        if (request.Id is { } id)
        {
            var found = all.FirstOrDefault(c => c.Id == id);
            if (found is null)
            {
                return Error.NotFound("SalaryComponent");
            }

            component = found;
            if (component.Code != code && all.Any(c => c.Id != id && PayslipCalculator.CodesIn(c.Formula).Contains(component.Code)))
            {
                return Error.Validation(nameof(request.Code), $"Formulas use {component.Code}; change them before renaming the code.");
            }

            if (component.IsBasic && !request.IsBasic)
            {
                return Error.Validation(nameof(request.IsBasic), "Mark another component as the basic salary first.");
            }

            if (component.Kind != request.Kind && await db.SalaryAssignmentLines.AnyAsync(l => l.ComponentId == id, cancellationToken).ConfigureAwait(false))
            {
                return Error.Validation(nameof(request.Kind), "Salaries use this component, so it cannot change between earning and deduction. Add a new component instead.");
            }
        }
        else
        {
            component = new SalaryComponent { Id = ids.NewId(), CompanyId = user.CompanyId };
            db.SalaryComponents.Add(component);
            all.Add(component);
        }

        if (request.Calc == ComponentCalc.Formula)
        {
            var known = all.Where(c => c.Id != component.Id).Select(c => c.Code).Append(code).ToHashSet(StringComparer.Ordinal);
            var parsed = FormulaEvaluator.Parse(request.Formula, known);
            if (parsed.IsFailure)
            {
                return parsed.Errors[0];
            }

            if (parsed.Value.Contains(code) || HasCycle(all, component.Id, code, parsed.Value))
            {
                return Error.Validation(nameof(request.Formula), "The formula refers back to this component through other formulas.");
            }
        }

        if (request.Calc == ComponentCalc.PercentOfBasic && !request.IsBasic && !all.Any(c => c.IsBasic && c.Id != component.Id))
        {
            return Error.Validation(nameof(request.Calc), "Mark a component as the basic salary first.");
        }

        var before = request.Id is null ? null : Snapshot(component);
        if (request.IsBasic)
        {
            foreach (var other in all.Where(c => c.IsBasic && c.Id != component.Id))
            {
                other.IsBasic = false;
            }
        }

        component.Code = code;
        component.Name = request.Name.Trim();
        component.Kind = request.Kind;
        component.Calc = request.Calc;
        component.DefaultRate = request.DefaultRate;
        component.DefaultAmountMinor = request.DefaultAmountMinor;
        component.Formula = request.Calc == ComponentCalc.Formula ? request.Formula!.Trim() : null;
        component.IsBasic = request.IsBasic;
        component.IsTaxable = request.IsTaxable;
        component.IsAttendanceLinked = request.IsAttendanceLinked;
        component.DisplayOrder = request.DisplayOrder;
        component.IsActive = request.IsActive;

        audit.Record(request.Id is null ? "SalaryComponent.Created" : "SalaryComponent.Updated", $"Salary component {component.Code} {component.Name}", nameof(SalaryComponent), component.Id, before, Snapshot(component));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return component.Id;
    }

    private static bool HasCycle(List<SalaryComponent> all, Guid id, string code, IReadOnlySet<string> uses)
    {
        var formulas = all.Where(c => c.Id != id && c.Calc == ComponentCalc.Formula).ToDictionary(c => c.Code, c => PayslipCalculator.CodesIn(c.Formula).ToList(), StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>(uses);
        while (stack.Count > 0)
        {
            var next = stack.Pop();
            if (next == code)
            {
                return true;
            }

            if (seen.Add(next) && formulas.TryGetValue(next, out var deps))
            {
                foreach (var d in deps)
                {
                    stack.Push(d);
                }
            }
        }

        return false;
    }

    private static object Snapshot(SalaryComponent c) => new { c.Code, c.Name, c.Kind, c.Calc, c.DefaultRate, c.DefaultAmountMinor, c.Formula, c.IsBasic, c.IsTaxable, c.IsAttendanceLinked, c.DisplayOrder, c.IsActive };
}

[RequiresPermission(Permission.ManagePayroll)]
public sealed record DeleteSalaryComponentCommand(Guid Id) : ICommand<Unit>;

public sealed class DeleteSalaryComponentHandler(IAppDbContext db, ICurrentUser user, IClock clock, IAuditWriter audit) : IRequestHandler<DeleteSalaryComponentCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(DeleteSalaryComponentCommand request, CancellationToken cancellationToken)
    {
        var component = await db.SalaryComponents.FirstOrDefaultAsync(c => c.Id == request.Id && c.CompanyId == user.CompanyId && c.DeletedAtUtc == null, cancellationToken).ConfigureAwait(false);
        if (component is null)
        {
            return Error.NotFound("SalaryComponent");
        }

        if (component.IsBasic)
        {
            return Error.Validation("The basic salary cannot be deleted.");
        }

        var formulas = await db.SalaryComponents.AsNoTracking().Where(c => c.CompanyId == user.CompanyId && c.DeletedAtUtc == null && c.Formula != null).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (formulas.FirstOrDefault(c => PayslipCalculator.CodesIn(c.Formula).Contains(component.Code)) is { } usedBy)
        {
            return Error.Validation($"The formula of {usedBy.Code} uses this component.");
        }

        if (await db.SalaryStructureLines.AnyAsync(l => l.ComponentId == component.Id, cancellationToken).ConfigureAwait(false))
        {
            return Error.Validation("A salary structure uses this component. Remove it from the structure first.");
        }

        // Past salaries keep their lines; a deleted component is no longer paid.
        component.DeletedAtUtc = clock.UtcNow;
        component.IsActive = false;
        audit.Record("SalaryComponent.Deleted", $"Salary component {component.Code}", nameof(SalaryComponent), component.Id);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}

public sealed record SalaryLineDto(Guid ComponentId, string Code, string Name, ComponentKind Kind, ComponentCalc Calc, Money Amount, decimal Rate);

public sealed record SalaryStructureDto(Guid Id, string Name, string? Description, IReadOnlyList<SalaryLineDto> Lines);

[RequiresPermission(Permission.ViewSalaries)]
public sealed record GetSalaryStructuresQuery : IQuery<IReadOnlyList<SalaryStructureDto>>;

public sealed class GetSalaryStructuresHandler(IAppDbContext db, ICurrentUser user) : IRequestHandler<GetSalaryStructuresQuery, IReadOnlyList<SalaryStructureDto>>
{
    public async Task<Result<IReadOnlyList<SalaryStructureDto>>> HandleAsync(GetSalaryStructuresQuery request, CancellationToken cancellationToken)
    {
        var components = await db.SalaryComponents.AsNoTracking().Where(c => c.CompanyId == user.CompanyId).ToDictionaryAsync(c => c.Id, cancellationToken).ConfigureAwait(false);
        var structures = await db.SalaryStructures.AsNoTracking().Include(s => s.Lines)
            .Where(s => s.CompanyId == user.CompanyId && s.DeletedAtUtc == null).OrderBy(s => s.Name).ToListAsync(cancellationToken).ConfigureAwait(false);
        return structures.Select(s => new SalaryStructureDto(s.Id, s.Name, s.Description, SalaryLines.ToDto(s.Lines.Select(l => (l.ComponentId, l.AmountMinor, l.Rate)), components))).ToList();
    }
}

[RequiresPermission(Permission.ManagePayroll)]
public sealed record SaveSalaryStructureCommand(Guid? Id, string Name, string? Description, IReadOnlyList<SalaryLineInput> Lines) : ICommand<Guid>;

public sealed class SaveSalaryStructureValidator : AbstractValidator<SaveSalaryStructureCommand>
{
    public SaveSalaryStructureValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Enter a name.").MaximumLength(100);
        RuleFor(x => x.Description).MaximumLength(500);
        RuleFor(x => x.Lines).NotEmpty().WithMessage("Add at least one component.");
    }
}

public sealed class SaveSalaryStructureHandler(IAppDbContext db, ICurrentUser user, IIdGenerator ids, IAuditWriter audit) : IRequestHandler<SaveSalaryStructureCommand, Guid>
{
    public async Task<Result<Guid>> HandleAsync(SaveSalaryStructureCommand request, CancellationToken cancellationToken)
    {
        var name = request.Name.Trim();
        if (await db.SalaryStructures.AnyAsync(s => s.CompanyId == user.CompanyId && s.DeletedAtUtc == null && s.Name == name && s.Id != request.Id, cancellationToken).ConfigureAwait(false))
        {
            return Error.Validation(nameof(request.Name), "A structure with this name already exists.");
        }

        var check = await SalaryLines.ValidateAsync(db, user, request.Lines, cancellationToken).ConfigureAwait(false);
        if (check.IsFailure)
        {
            return check.Errors[0];
        }

        SalaryStructure structure;
        if (request.Id is { } id)
        {
            var found = await db.SalaryStructures.Include(s => s.Lines).FirstOrDefaultAsync(s => s.Id == id && s.CompanyId == user.CompanyId && s.DeletedAtUtc == null, cancellationToken).ConfigureAwait(false);
            if (found is null)
            {
                return Error.NotFound("SalaryStructure");
            }

            structure = found;
            db.SalaryStructureLines.RemoveRange(structure.Lines);
            structure.Lines.Clear();
        }
        else
        {
            structure = new SalaryStructure { Id = ids.NewId(), CompanyId = user.CompanyId };
            db.SalaryStructures.Add(structure);
        }

        structure.Name = name;
        structure.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        foreach (var line in request.Lines.DistinctBy(l => l.ComponentId))
        {
            var row = new SalaryStructureLine { Id = ids.NewId(), StructureId = structure.Id, ComponentId = line.ComponentId, AmountMinor = line.AmountMinor, Rate = line.Rate };
            structure.Lines.Add(row);
            db.SalaryStructureLines.Add(row);
        }

        audit.Record(request.Id is null ? "SalaryStructure.Created" : "SalaryStructure.Updated", $"Salary structure {structure.Name}", nameof(SalaryStructure), structure.Id);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return structure.Id;
    }
}

[RequiresPermission(Permission.ManagePayroll)]
public sealed record DeleteSalaryStructureCommand(Guid Id) : ICommand<Unit>;

public sealed class DeleteSalaryStructureHandler(IAppDbContext db, ICurrentUser user, IClock clock, IAuditWriter audit) : IRequestHandler<DeleteSalaryStructureCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(DeleteSalaryStructureCommand request, CancellationToken cancellationToken)
    {
        var structure = await db.SalaryStructures.FirstOrDefaultAsync(s => s.Id == request.Id && s.CompanyId == user.CompanyId && s.DeletedAtUtc == null, cancellationToken).ConfigureAwait(false);
        if (structure is null)
        {
            return Error.NotFound("SalaryStructure");
        }

        // Assignments copied their values, so they are not affected.
        structure.DeletedAtUtc = clock.UtcNow;
        audit.Record("SalaryStructure.Deleted", $"Salary structure {structure.Name}", nameof(SalaryStructure), structure.Id);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}

public sealed record SalaryPreviewLine(string Code, string Name, ComponentKind Kind, Money Amount);

public sealed record SalaryPreview(IReadOnlyList<SalaryPreviewLine> Lines, Money Gross, Money Deductions, Money Net, Money EmployerContributions, IReadOnlyList<string> Warnings);

/// <summary>Monthly amounts for a set of component values with no attendance effect (structure editor and salary tab).</summary>
[RequiresPermission(Permission.ViewSalaries)]
public sealed record PreviewSalaryQuery(IReadOnlyList<SalaryLineInput> Lines) : IQuery<SalaryPreview>;

public sealed class PreviewSalaryHandler(IAppDbContext db, ICurrentUser user, IPolicyProvider policies, IClock clock, ICompanyContext company) : IRequestHandler<PreviewSalaryQuery, SalaryPreview>
{
    public async Task<Result<SalaryPreview>> HandleAsync(PreviewSalaryQuery request, CancellationToken cancellationToken)
    {
        var components = await db.SalaryComponents.AsNoTracking().Where(c => c.CompanyId == user.CompanyId && c.DeletedAtUtc == null && c.IsActive)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var policy = await policies.GetSnapshotAsync(clock.TodayIn(company.TimeZone), cancellationToken).ConfigureAwait(false);
        return SalaryLines.Preview(request.Lines, components.Select(ComponentDef.From).ToList(), policy.Payroll.LineRounding);
    }
}

/// <summary>Validation and preview shared by structures and assignments.</summary>
public static class SalaryLines
{
    public static async Task<Result> ValidateAsync(IAppDbContext db, ICurrentUser user, IReadOnlyList<SalaryLineInput> lines, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(lines);
        var ids = lines.Select(l => l.ComponentId).ToHashSet();
        var components = await db.SalaryComponents.AsNoTracking().Where(c => ids.Contains(c.Id) && c.CompanyId == user.CompanyId && c.DeletedAtUtc == null)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (components.Count != ids.Count)
        {
            return Error.Validation("Lines", "A component in the list no longer exists.");
        }

        if (lines.Any(l => l.AmountMinor < 0 || l.Rate < 0 || l.Rate > 1000))
        {
            return Error.Validation("Lines", "Amounts cannot be negative and percentages must be between 0 and 1000.");
        }

        var basic = components.FirstOrDefault(c => c.IsBasic);
        if (basic is null || lines.First(l => l.ComponentId == basic.Id).AmountMinor <= 0)
        {
            return Error.Validation("Lines", "Enter the basic salary.");
        }

        return Result.Success();
    }

    public static IReadOnlyList<SalaryLineDto> ToDto(IEnumerable<(Guid ComponentId, long AmountMinor, decimal Rate)> lines, IReadOnlyDictionary<Guid, SalaryComponent> components)
    {
        ArgumentNullException.ThrowIfNull(components);
        return lines.Where(l => components.ContainsKey(l.ComponentId)).Select(l => (Line: l, C: components[l.ComponentId]))
            .OrderBy(x => x.C.Kind).ThenBy(x => x.C.DisplayOrder).ThenBy(x => x.C.Code, StringComparer.Ordinal)
            .Select(x => new SalaryLineDto(x.C.Id, x.C.Code, x.C.Name, x.C.Kind, x.C.Calc, new Money(x.Line.AmountMinor), x.Line.Rate)).ToList();
    }

    public static SalaryPreview Preview(IReadOnlyList<SalaryLineInput> lines, IReadOnlyList<ComponentDef> components, RoundingPolicy rounding)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(components);
        var byId = components.ToDictionary(c => c.Id);
        var warnings = new List<string>();
        var segment = new PaySegment(new DateRange(DateOnly.MinValue, null), lines.Select(l => new PayLine(l.ComponentId, l.AmountMinor, l.Rate)).ToList());
        var amounts = PayslipCalculator.MonthlyAmounts(segment, byId, warnings);
        var rows = amounts.Select(a => byId[a.Key]).OrderBy(c => c.Kind).ThenBy(c => c.DisplayOrder).ThenBy(c => c.Code, StringComparer.Ordinal)
            .Select(c => new SalaryPreviewLine(c.Code, c.Name, c.Kind, new Money(rounding.Apply(amounts[c.Id])))).ToList();
        var gross = Money.Sum(rows.Where(r => r.Kind == ComponentKind.Earning).Select(r => r.Amount));
        var deductions = Money.Sum(rows.Where(r => r.Kind == ComponentKind.Deduction).Select(r => r.Amount));
        return new SalaryPreview(rows, gross, deductions, gross - deductions, Money.Sum(rows.Where(r => r.Kind == ComponentKind.EmployerContribution).Select(r => r.Amount)), warnings);
    }
}
