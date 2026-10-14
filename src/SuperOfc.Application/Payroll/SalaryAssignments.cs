using FluentValidation;
using Microsoft.EntityFrameworkCore;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Common;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Payroll;
using SuperOfc.Domain.Security;

namespace SuperOfc.Application.Payroll;

public sealed record SalaryAssignmentDto(
    Guid Id,
    Guid EmployeeId,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    Guid? StructureId,
    string? StructureName,
    string? Note,
    IReadOnlyList<SalaryLineDto> Lines,
    Money Gross,
    Money Net,
    bool IsLocked)
{
    public bool IsCurrent(DateOnly today) => EffectiveFrom <= today && (EffectiveTo is null || EffectiveTo >= today);
}

[RequiresPermission(Permission.ViewSalaries)]
public sealed record GetSalaryAssignmentsQuery(Guid EmployeeId) : IQuery<IReadOnlyList<SalaryAssignmentDto>>;

public sealed class GetSalaryAssignmentsHandler(IAppDbContext db, ICurrentUser user, PayrollLocks locks, IPolicyProvider policies, IClock clock, ICompanyContext company)
    : IRequestHandler<GetSalaryAssignmentsQuery, IReadOnlyList<SalaryAssignmentDto>>
{
    public async Task<Result<IReadOnlyList<SalaryAssignmentDto>>> HandleAsync(GetSalaryAssignmentsQuery request, CancellationToken cancellationToken)
    {
        var components = await db.SalaryComponents.AsNoTracking().Where(c => c.CompanyId == user.CompanyId).ToDictionaryAsync(c => c.Id, cancellationToken).ConfigureAwait(false);
        var structures = await db.SalaryStructures.AsNoTracking().Where(s => s.CompanyId == user.CompanyId).ToDictionaryAsync(s => s.Id, s => s.Name, cancellationToken).ConfigureAwait(false);
        var assignments = await db.SalaryAssignments.AsNoTracking().Include(a => a.Lines)
            .Where(a => a.EmployeeId == request.EmployeeId && a.CompanyId == user.CompanyId).OrderByDescending(a => a.EffectiveFrom)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var lockedUntil = await locks.LockedUntilAsync(cancellationToken).ConfigureAwait(false);
        var policy = await policies.GetSnapshotAsync(clock.TodayIn(company.TimeZone), cancellationToken).ConfigureAwait(false);
        var defs = components.Values.Where(c => c.DeletedAtUtc == null && c.IsActive).Select(ComponentDef.From).ToList();
        return assignments.Select(a =>
        {
            var preview = SalaryLines.Preview(a.Lines.Select(l => new SalaryLineInput(l.ComponentId, l.AmountMinor, l.Rate)).ToList(), defs, policy.Payroll.LineRounding);
            return new SalaryAssignmentDto(a.Id, a.EmployeeId, a.EffectiveFrom, a.EffectiveTo, a.StructureId, a.StructureId is { } s ? structures.GetValueOrDefault(s) : null, a.Note,
                SalaryLines.ToDto(a.Lines.Select(l => (l.ComponentId, l.AmountMinor, l.Rate)), components), preview.Gross, preview.Net, lockedUntil is { } until && a.EffectiveFrom <= until);
        }).ToList();
    }
}

/// <summary>
/// Adds a salary from a date, or edits one that no finalized payroll has used yet. An open earlier salary is closed the
/// day before; history is never rewritten (doc 03, P4-T2).
/// </summary>
[RequiresPermission(Permission.ManagePayroll)]
public sealed record SaveSalaryAssignmentCommand(Guid? Id, Guid EmployeeId, DateOnly EffectiveFrom, Guid? StructureId, string? Note, IReadOnlyList<SalaryLineInput> Lines) : ICommand<Guid>;

public sealed class SaveSalaryAssignmentValidator : AbstractValidator<SaveSalaryAssignmentCommand>
{
    public SaveSalaryAssignmentValidator()
    {
        RuleFor(x => x.Lines).NotEmpty().WithMessage("Add at least one component.");
        RuleFor(x => x.Note).MaximumLength(500);
    }
}

public sealed class SaveSalaryAssignmentHandler(IAppDbContext db, ICurrentUser user, IIdGenerator ids, IAuditWriter audit, PayrollLocks locks)
    : IRequestHandler<SaveSalaryAssignmentCommand, Guid>
{
    public async Task<Result<Guid>> HandleAsync(SaveSalaryAssignmentCommand request, CancellationToken cancellationToken)
    {
        var employee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == request.EmployeeId && e.CompanyId == user.CompanyId && e.DeletedAtUtc == null, cancellationToken).ConfigureAwait(false);
        if (employee is null)
        {
            return Error.NotFound("Employee");
        }

        var check = await SalaryLines.ValidateAsync(db, user, request.Lines, cancellationToken).ConfigureAwait(false);
        if (check.IsFailure)
        {
            return check.Errors[0];
        }

        var result = await SalaryAssignmentWriter.WriteAsync(db, user, ids, locks, request.Id, employee.Id, request.EffectiveFrom, request.StructureId, request.Note, request.Lines, cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            return result;
        }

        audit.Record(request.Id is null ? "Salary.Assigned" : "Salary.Updated", $"Salary for {employee.Code} {employee.FullName} from {request.EffectiveFrom:yyyy-MM-dd}", "Employee", employee.Id,
            after: request.Lines.Select(l => new { l.ComponentId, l.AmountMinor, l.Rate }));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return result.Value;
    }
}

[RequiresPermission(Permission.ManagePayroll)]
public sealed record DeleteSalaryAssignmentCommand(Guid Id) : ICommand<Unit>;

public sealed class DeleteSalaryAssignmentHandler(IAppDbContext db, ICurrentUser user, IAuditWriter audit, PayrollLocks locks) : IRequestHandler<DeleteSalaryAssignmentCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(DeleteSalaryAssignmentCommand request, CancellationToken cancellationToken)
    {
        var assignment = await db.SalaryAssignments.Include(a => a.Lines).FirstOrDefaultAsync(a => a.Id == request.Id && a.CompanyId == user.CompanyId, cancellationToken).ConfigureAwait(false);
        if (assignment is null)
        {
            return Error.NotFound("SalaryAssignment");
        }

        if (await locks.LockedUntilAsync(cancellationToken).ConfigureAwait(false) is { } until && assignment.EffectiveFrom <= until)
        {
            return Error.Validation("Finalized payroll used this salary, so it cannot be deleted. Add a new salary from a later date instead.");
        }

        // The salary before it continues again.
        var previous = await db.SalaryAssignments.Where(a => a.EmployeeId == assignment.EmployeeId && a.EffectiveTo == assignment.EffectiveFrom.AddDays(-1))
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (previous is not null)
        {
            previous.EffectiveTo = assignment.EffectiveTo;
        }

        db.SalaryAssignments.Remove(assignment);
        audit.Record("Salary.Deleted", $"Salary from {assignment.EffectiveFrom:yyyy-MM-dd} deleted", "Employee", assignment.EmployeeId);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}

public sealed record SalaryRevisionRow(Guid EmployeeId, string Code, string Name, Money CurrentBasic, Money NewBasic, Money CurrentGross, Money NewGross, string? Problem);

/// <summary>Percent increase for many employees from one date (P4-T2 bulk revision). Preview first, then apply.</summary>
[RequiresPermission(Permission.ManagePayroll)]
public sealed record PreviewSalaryRevisionQuery(IReadOnlyList<Guid> EmployeeIds, DateOnly EffectiveFrom, decimal Percent, bool AllFixedEarnings) : IQuery<IReadOnlyList<SalaryRevisionRow>>;

[RequiresPermission(Permission.ManagePayroll)]
public sealed record ApplySalaryRevisionCommand(IReadOnlyList<Guid> EmployeeIds, DateOnly EffectiveFrom, decimal Percent, bool AllFixedEarnings, string? Note) : ICommand<int>;

public sealed class SalaryRevisionValidator : AbstractValidator<ApplySalaryRevisionCommand>
{
    public SalaryRevisionValidator()
    {
        RuleFor(x => x.Percent).InclusiveBetween(-50, 500).NotEqual(0).WithMessage("Enter a percentage between -50 and 500, not 0.");
        RuleFor(x => x.EmployeeIds).NotEmpty().WithMessage("Choose at least one employee.");
    }
}

public sealed class PreviewSalaryRevisionHandler(SalaryRevision revision) : IRequestHandler<PreviewSalaryRevisionQuery, IReadOnlyList<SalaryRevisionRow>>
{
    public async Task<Result<IReadOnlyList<SalaryRevisionRow>>> HandleAsync(PreviewSalaryRevisionQuery request, CancellationToken cancellationToken)
        => (await revision.PlanAsync(request.EmployeeIds, request.EffectiveFrom, request.Percent, request.AllFixedEarnings, cancellationToken).ConfigureAwait(false))
            .Select(p => p.Row).ToList();
}

public sealed class ApplySalaryRevisionHandler(IAppDbContext db, ICurrentUser user, IIdGenerator ids, IAuditWriter audit, PayrollLocks locks, SalaryRevision revision)
    : IRequestHandler<ApplySalaryRevisionCommand, int>
{
    public async Task<Result<int>> HandleAsync(ApplySalaryRevisionCommand request, CancellationToken cancellationToken)
    {
        var plan = await revision.PlanAsync(request.EmployeeIds, request.EffectiveFrom, request.Percent, request.AllFixedEarnings, cancellationToken).ConfigureAwait(false);
        var ready = plan.Where(p => p.Row.Problem is null).ToList();
        if (ready.Count == 0)
        {
            return Error.Validation(plan.FirstOrDefault()?.Row.Problem ?? "None of the chosen employees has a salary to revise.");
        }

        await using var tx = await db.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var p in ready)
        {
            var written = await SalaryAssignmentWriter.WriteAsync(db, user, ids, locks, null, p.Row.EmployeeId, request.EffectiveFrom, p.StructureId,
                request.Note ?? $"Revision {request.Percent:+0.##;-0.##} %", p.Lines, cancellationToken).ConfigureAwait(false);
            if (written.IsFailure)
            {
                return Result<int>.Failure(written.Errors);
            }
        }

        audit.Record("Salary.BulkRevision", $"{ready.Count} salaries revised by {request.Percent} % from {request.EffectiveFrom:yyyy-MM-dd}", after: new { request.Percent, request.AllFixedEarnings, Employees = ready.Count });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ready.Count;
    }
}

/// <summary>Works out the new lines for a bulk revision.</summary>
public sealed class SalaryRevision(IAppDbContext db, ICurrentUser user, PayrollLocks locks, IPolicyProvider policies)
{
    public sealed record Planned(SalaryRevisionRow Row, Guid? StructureId, IReadOnlyList<SalaryLineInput> Lines);

    public async Task<IReadOnlyList<Planned>> PlanAsync(IReadOnlyList<Guid> employeeIds, DateOnly from, decimal percent, bool allFixedEarnings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(employeeIds);
        var ids = employeeIds.ToHashSet();
        var employees = await db.Employees.AsNoTracking().Where(e => ids.Contains(e.Id) && e.CompanyId == user.CompanyId && e.DeletedAtUtc == null)
            .OrderBy(e => e.Code).Select(e => new { e.Id, e.Code, e.FullName }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var assignments = await db.SalaryAssignments.AsNoTracking().Include(a => a.Lines)
            .Where(a => ids.Contains(a.EmployeeId) && a.EffectiveFrom < from && (a.EffectiveTo == null || a.EffectiveTo >= from.AddDays(-1)))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var later = (await db.SalaryAssignments.AsNoTracking().Where(a => ids.Contains(a.EmployeeId) && a.EffectiveFrom >= from).Select(a => a.EmployeeId)
            .ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
        var components = await db.SalaryComponents.AsNoTracking().Where(c => c.CompanyId == user.CompanyId && c.DeletedAtUtc == null && c.IsActive)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var defs = components.Select(ComponentDef.From).ToList();
        var scaled = components.Where(c => c.Kind == ComponentKind.Earning && c.Calc == ComponentCalc.Fixed && (allFixedEarnings || c.IsBasic)).Select(c => c.Id).ToHashSet();
        var basicId = components.FirstOrDefault(c => c.IsBasic)?.Id;
        var policy = await policies.GetSnapshotAsync(from, cancellationToken).ConfigureAwait(false);
        var lockedUntil = await locks.LockedUntilAsync(cancellationToken).ConfigureAwait(false);
        var factor = 1 + (percent / 100m);
        var wholeUnits = RoundingPolicy.WholeUnit;

        var plan = new List<Planned>();
        foreach (var e in employees)
        {
            var current = assignments.Where(a => a.EmployeeId == e.Id).OrderByDescending(a => a.EffectiveFrom).FirstOrDefault();
            string? problem = current is null ? "No salary before this date."
                : later.Contains(e.Id) ? "A salary from this date or later already exists."
                : lockedUntil is { } until && from <= until ? $"Payroll up to {until:yyyy-MM-dd} is finalized."
                : null;
            var currentLines = current?.Lines.Select(l => new SalaryLineInput(l.ComponentId, l.AmountMinor, l.Rate)).ToList() ?? [];
            var newLines = currentLines.Select(l => scaled.Contains(l.ComponentId) ? l with { AmountMinor = wholeUnits.Apply(l.AmountMinor * factor) } : l).ToList();
            var before = SalaryLines.Preview(currentLines, defs, policy.Payroll.LineRounding);
            var after = SalaryLines.Preview(newLines, defs, policy.Payroll.LineRounding);
            var basicBefore = new Money(currentLines.FirstOrDefault(l => l.ComponentId == basicId)?.AmountMinor ?? 0);
            var basicAfter = new Money(newLines.FirstOrDefault(l => l.ComponentId == basicId)?.AmountMinor ?? 0);
            plan.Add(new Planned(new SalaryRevisionRow(e.Id, e.Code, e.FullName, basicBefore, basicAfter, before.Gross, after.Gross, problem), current?.StructureId, newLines));
        }

        return plan;
    }
}

/// <summary>The single place that writes assignments, so every path keeps the no-overlap and no-history-change rules.</summary>
internal static class SalaryAssignmentWriter
{
    public static async Task<Result<Guid>> WriteAsync(
        IAppDbContext db, ICurrentUser user, IIdGenerator ids, PayrollLocks locks, Guid? id, Guid employeeId, DateOnly from, Guid? structureId, string? note,
        IReadOnlyList<SalaryLineInput> lines, CancellationToken cancellationToken)
    {
        var lockedUntil = await locks.LockedUntilAsync(cancellationToken).ConfigureAwait(false);
        if (lockedUntil is { } until && from <= until)
        {
            return Error.Validation("EffectiveFrom", $"Payroll up to {until:dd MMM yyyy} is finalized. Choose a date after it.");
        }

        var others = await db.SalaryAssignments.Where(a => a.EmployeeId == employeeId && a.Id != id).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (others.Any(a => a.EffectiveFrom == from))
        {
            return Error.Validation("EffectiveFrom", "A salary already starts on this date. Edit that one instead.");
        }

        if (others.Any(a => a.EffectiveFrom > from))
        {
            return Error.Validation("EffectiveFrom", "A later salary already exists. Delete it first or choose a later date.");
        }

        SalaryAssignment assignment;
        if (id is { } existingId)
        {
            var found = await db.SalaryAssignments.Include(a => a.Lines).FirstOrDefaultAsync(a => a.Id == existingId && a.CompanyId == user.CompanyId, cancellationToken).ConfigureAwait(false);
            if (found is null || found.EmployeeId != employeeId)
            {
                return Error.NotFound("SalaryAssignment");
            }

            if (lockedUntil is { } lu && found.EffectiveFrom <= lu)
            {
                return Error.Validation("EffectiveFrom", "Finalized payroll used this salary. Add a new salary from a later date instead.");
            }

            assignment = found;

            // Moving the start date: let the previous salary run up to the new date again.
            foreach (var previous in others.Where(a => a.EffectiveTo == found.EffectiveFrom.AddDays(-1)))
            {
                previous.EffectiveTo = found.EffectiveTo;
            }

            db.SalaryAssignmentLines.RemoveRange(assignment.Lines);
            assignment.Lines.Clear();
        }
        else
        {
            assignment = new SalaryAssignment { Id = ids.NewId(), CompanyId = user.CompanyId, EmployeeId = employeeId };
            db.SalaryAssignments.Add(assignment);
        }

        // Close the salary that was running on the new start date.
        foreach (var open in others.Where(a => a.EffectiveFrom < from && (a.EffectiveTo is null || a.EffectiveTo >= from)))
        {
            open.EffectiveTo = from.AddDays(-1);
        }

        assignment.EffectiveFrom = from;
        assignment.EffectiveTo = null;
        assignment.StructureId = structureId;
        assignment.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        foreach (var line in lines.DistinctBy(l => l.ComponentId))
        {
            var row = new SalaryAssignmentLine { Id = ids.NewId(), AssignmentId = assignment.Id, ComponentId = line.ComponentId, AmountMinor = line.AmountMinor, Rate = line.Rate };
            assignment.Lines.Add(row);
            db.SalaryAssignmentLines.Add(row);
        }

        return assignment.Id;
    }
}
