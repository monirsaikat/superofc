using FluentValidation;
using Microsoft.EntityFrameworkCore;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Common;
using SuperOfc.Domain.Attendance;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Security;

namespace SuperOfc.Application.Attendance;

public sealed record ShiftDto(
    Guid Id,
    string Name,
    string? Code,
    TimeOnly StartTime,
    TimeOnly EndTime,
    int GraceInMinutes,
    int GraceOutMinutes,
    int BreakMinutes,
    int MinimumMinutesForHalfDay,
    int MinimumMinutesForFullDay,
    int OvertimeStartsAfterMinutes,
    bool IsDefault,
    int EmployeeCount)
{
    public bool CrossesMidnight => EndTime <= StartTime;

    public string TimeText => $"{StartTime:HH\\:mm} – {EndTime:HH\\:mm}";
}

public sealed record GetShiftsQuery : IQuery<IReadOnlyList<ShiftDto>>;

public sealed class GetShiftsHandler(IAppDbContext db, ICurrentUser user, IClock clock) : IRequestHandler<GetShiftsQuery, IReadOnlyList<ShiftDto>>
{
    public async Task<Result<IReadOnlyList<ShiftDto>>> HandleAsync(GetShiftsQuery request, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(clock.UtcNow);
        var counts = await db.ShiftAssignments.AsNoTracking()
            .Where(a => a.CompanyId == user.CompanyId && a.EffectiveFrom <= today && (a.EffectiveTo == null || a.EffectiveTo >= today))
            .GroupBy(a => a.ShiftId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken).ConfigureAwait(false);
        var shifts = await db.Shifts.AsNoTracking()
            .Where(s => s.CompanyId == user.CompanyId && s.DeletedAtUtc == null)
            .OrderByDescending(s => s.IsDefault).ThenBy(s => s.Name)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return shifts.Select(s => new ShiftDto(s.Id, s.Name, s.Code, s.StartTime, s.EndTime, s.GraceInMinutes, s.GraceOutMinutes, s.BreakMinutes,
            s.MinimumMinutesForHalfDay, s.MinimumMinutesForFullDay, s.OvertimeStartsAfterMinutes, s.IsDefault, counts.GetValueOrDefault(s.Id))).ToList();
    }
}

[RequiresPermission(Permission.ManageAttendance)]
public sealed record SaveShiftCommand(
    Guid? Id,
    string Name,
    string? Code,
    TimeOnly StartTime,
    TimeOnly EndTime,
    int GraceInMinutes,
    int GraceOutMinutes,
    int BreakMinutes,
    int MinimumMinutesForHalfDay,
    int MinimumMinutesForFullDay,
    int OvertimeStartsAfterMinutes,
    bool IsDefault) : ICommand<Guid>;

public sealed class SaveShiftValidator : AbstractValidator<SaveShiftCommand>
{
    public SaveShiftValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Enter a name.").MaximumLength(100);
        RuleFor(x => x.Code).MaximumLength(20);
        RuleFor(x => x.GraceInMinutes).InclusiveBetween(0, 240);
        RuleFor(x => x.GraceOutMinutes).InclusiveBetween(0, 240);
        RuleFor(x => x.BreakMinutes).InclusiveBetween(0, 600);
        RuleFor(x => x.OvertimeStartsAfterMinutes).InclusiveBetween(0, 600);
        RuleFor(x => x.EndTime).NotEqual(x => x.StartTime).WithMessage("End time must differ from start time.");
        RuleFor(x => x.MinimumMinutesForHalfDay).GreaterThanOrEqualTo(0)
            .LessThanOrEqualTo(x => x.MinimumMinutesForFullDay).WithMessage("Half-day minimum must not exceed the full-day minimum.");
        RuleFor(x => x.MinimumMinutesForFullDay).GreaterThan(0);
        RuleFor(x => x.BreakMinutes)
            .Must((x, b) => b < new Shift { StartTime = x.StartTime, EndTime = x.EndTime }.SpanMinutes)
            .WithMessage("The break must be shorter than the shift.");
    }
}

public sealed class SaveShiftHandler(IAppDbContext db, ICurrentUser user, IIdGenerator ids, IAuditWriter audit) : IRequestHandler<SaveShiftCommand, Guid>
{
    public async Task<Result<Guid>> HandleAsync(SaveShiftCommand request, CancellationToken cancellationToken)
    {
        var duplicate = await db.Shifts.AnyAsync(
            s => s.CompanyId == user.CompanyId && s.DeletedAtUtc == null && s.Name == request.Name.Trim() && s.Id != request.Id, cancellationToken).ConfigureAwait(false);
        if (duplicate)
        {
            return Error.Validation(nameof(request.Name), "A shift with this name already exists.");
        }

        Shift shift;
        if (request.Id is { } id)
        {
            var found = await db.Shifts.FirstOrDefaultAsync(s => s.Id == id && s.CompanyId == user.CompanyId, cancellationToken).ConfigureAwait(false);
            if (found is null)
            {
                return Error.NotFound("Shift");
            }

            shift = found;
        }
        else
        {
            shift = new Shift { Id = ids.NewId(), CompanyId = user.CompanyId };
            db.Shifts.Add(shift);
        }

        var before = request.Id is null ? null : Snapshot(shift);
        shift.Name = request.Name.Trim();
        shift.Code = string.IsNullOrWhiteSpace(request.Code) ? null : request.Code.Trim();
        shift.StartTime = request.StartTime;
        shift.EndTime = request.EndTime;
        shift.GraceInMinutes = request.GraceInMinutes;
        shift.GraceOutMinutes = request.GraceOutMinutes;
        shift.BreakMinutes = request.BreakMinutes;
        shift.MinimumMinutesForHalfDay = request.MinimumMinutesForHalfDay;
        shift.MinimumMinutesForFullDay = request.MinimumMinutesForFullDay;
        shift.OvertimeStartsAfterMinutes = request.OvertimeStartsAfterMinutes;

        var anyDefault = await db.Shifts.AnyAsync(s => s.CompanyId == user.CompanyId && s.DeletedAtUtc == null && s.IsDefault && s.Id != shift.Id, cancellationToken).ConfigureAwait(false);
        if (request.IsDefault || !anyDefault)
        {
            await db.Shifts.Where(s => s.CompanyId == user.CompanyId && s.IsDefault && s.Id != shift.Id)
                .ForEachAsync(s => s.IsDefault = false, cancellationToken).ConfigureAwait(false);
            shift.IsDefault = true;
        }
        else
        {
            shift.IsDefault = false;
        }

        audit.Record(request.Id is null ? "Shift.Created" : "Shift.Updated", $"Shift {shift.Name}", nameof(Shift), shift.Id, before, Snapshot(shift));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return shift.Id;
    }

    private static object Snapshot(Shift s) => new { s.Name, s.Code, s.StartTime, s.EndTime, s.GraceInMinutes, s.GraceOutMinutes, s.BreakMinutes, s.MinimumMinutesForHalfDay, s.MinimumMinutesForFullDay, s.OvertimeStartsAfterMinutes, s.IsDefault };
}

[RequiresPermission(Permission.ManageAttendance)]
public sealed record DeleteShiftCommand(Guid Id) : ICommand<Unit>;

public sealed class DeleteShiftHandler(IAppDbContext db, ICurrentUser user, IClock clock, IAuditWriter audit) : IRequestHandler<DeleteShiftCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(DeleteShiftCommand request, CancellationToken cancellationToken)
    {
        var shift = await db.Shifts.FirstOrDefaultAsync(s => s.Id == request.Id && s.CompanyId == user.CompanyId && s.DeletedAtUtc == null, cancellationToken).ConfigureAwait(false);
        if (shift is null)
        {
            return Error.NotFound("Shift");
        }

        if (shift.IsDefault)
        {
            return Error.Validation("Make another shift the default before deleting this one.");
        }

        var today = DateOnly.FromDateTime(clock.UtcNow);
        var inUse = await db.ShiftAssignments.AnyAsync(a => a.ShiftId == shift.Id && (a.EffectiveTo == null || a.EffectiveTo >= today), cancellationToken).ConfigureAwait(false);
        if (inUse)
        {
            return Error.Validation("Employees are assigned to this shift. Move them to another shift first.");
        }

        shift.DeletedAtUtc = clock.UtcNow;
        audit.Record("Shift.Deleted", $"Shift {shift.Name}", nameof(Shift), shift.Id);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}

public sealed record ShiftAssignmentDto(Guid Id, Guid EmployeeId, Guid ShiftId, string ShiftName, DateOnly EffectiveFrom, DateOnly? EffectiveTo, IReadOnlyList<string> RotationNames, int RotationDays)
{
    public string Description => RotationNames.Count == 0 ? ShiftName : $"{string.Join(" → ", RotationNames)} (every {RotationDays} days)";
}

public sealed record GetShiftAssignmentsQuery(Guid EmployeeId) : IQuery<IReadOnlyList<ShiftAssignmentDto>>;

public sealed class GetShiftAssignmentsHandler(IAppDbContext db, ICurrentUser user) : IRequestHandler<GetShiftAssignmentsQuery, IReadOnlyList<ShiftAssignmentDto>>
{
    public async Task<Result<IReadOnlyList<ShiftAssignmentDto>>> HandleAsync(GetShiftAssignmentsQuery request, CancellationToken cancellationToken)
    {
        var names = await db.Shifts.AsNoTracking().Where(s => s.CompanyId == user.CompanyId).ToDictionaryAsync(s => s.Id, s => s.Name, cancellationToken).ConfigureAwait(false);
        var rows = await db.ShiftAssignments.AsNoTracking()
            .Where(a => a.CompanyId == user.CompanyId && a.EmployeeId == request.EmployeeId)
            .OrderByDescending(a => a.EffectiveFrom)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(a => new ShiftAssignmentDto(a.Id, a.EmployeeId, a.ShiftId, names.GetValueOrDefault(a.ShiftId, "?"), a.EffectiveFrom, a.EffectiveTo,
            a.Rotation.Select(r => names.GetValueOrDefault(r, "?")).ToList(), a.RotationDays)).ToList();
    }
}

/// <summary>
/// Assigns a shift (or rotation) to one or more employees from a date. An open-ended assignment that started
/// earlier is closed the day before; any other overlap is rejected.
/// </summary>
[RequiresPermission(Permission.ManageAttendance)]
public sealed record AssignShiftCommand(
    IReadOnlyList<Guid> EmployeeIds,
    Guid ShiftId,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    IReadOnlyList<Guid>? Rotation = null,
    int RotationDays = 7) : ICommand<int>;

public sealed class AssignShiftValidator : AbstractValidator<AssignShiftCommand>
{
    public AssignShiftValidator()
    {
        RuleFor(x => x.EmployeeIds).NotEmpty().WithMessage("Choose at least one employee.");
        RuleFor(x => x.EffectiveTo).GreaterThanOrEqualTo(x => x.EffectiveFrom).When(x => x.EffectiveTo is not null).WithMessage("The end date must be on or after the start date.");
        RuleFor(x => x.RotationDays).InclusiveBetween(1, 365);
        RuleFor(x => x.Rotation).Must(r => r is null || r.Count == 0 || r.Count >= 2).WithMessage("A rotation needs at least two shifts.");
    }
}

public sealed class AssignShiftHandler(IAppDbContext db, ICurrentUser user, IIdGenerator ids, IAuditWriter audit, AttendanceEngine engine, IClock clock)
    : IRequestHandler<AssignShiftCommand, int>
{
    public async Task<Result<int>> HandleAsync(AssignShiftCommand request, CancellationToken cancellationToken)
    {
        var shiftIds = new HashSet<Guid>(request.Rotation ?? []) { request.ShiftId };
        var known = await db.Shifts.CountAsync(s => s.CompanyId == user.CompanyId && s.DeletedAtUtc == null && shiftIds.Contains(s.Id), cancellationToken).ConfigureAwait(false);
        if (known != shiftIds.Count)
        {
            return Error.NotFound("Shift");
        }

        var newRange = new DateRange(request.EffectiveFrom, request.EffectiveTo);
        var existing = await db.ShiftAssignments
            .Where(a => a.CompanyId == user.CompanyId && request.EmployeeIds.Contains(a.EmployeeId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var employeeId in request.EmployeeIds.Distinct())
        {
            foreach (var current in existing.Where(a => a.EmployeeId == employeeId && a.Range.Overlaps(newRange)))
            {
                if (current.EffectiveFrom < request.EffectiveFrom)
                {
                    // Close the earlier assignment the day before the new one starts.
                    current.EffectiveTo = request.EffectiveFrom.AddDays(-1);
                }
                else if (current.EffectiveFrom == request.EffectiveFrom)
                {
                    // Same start date: the new assignment replaces it.
                    db.ShiftAssignments.Remove(current);
                }
                else
                {
                    return Error.Validation(nameof(request.EffectiveFrom), $"There is already a later shift assignment from {current.EffectiveFrom:yyyy-MM-dd}. Delete it first.");
                }
            }

            db.ShiftAssignments.Add(new ShiftAssignment
            {
                Id = ids.NewId(),
                CompanyId = user.CompanyId,
                EmployeeId = employeeId,
                ShiftId = request.ShiftId,
                EffectiveFrom = request.EffectiveFrom,
                EffectiveTo = request.EffectiveTo,
                RotationShiftIds = request.Rotation is { Count: > 0 } r ? string.Join(',', r) : null,
                RotationDays = request.RotationDays,
            });
        }

        audit.Record("Shift.Assigned", $"Shift assigned to {request.EmployeeIds.Count} employee(s) from {request.EffectiveFrom:yyyy-MM-dd}", nameof(ShiftAssignment), null, null,
            new { request.ShiftId, request.EffectiveFrom, request.EffectiveTo, request.EmployeeIds.Count });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await RecomputePastAsync(engine, clock, request.EmployeeIds, request.EffectiveFrom, request.EffectiveTo, cancellationToken).ConfigureAwait(false);
        return request.EmployeeIds.Count;
    }

    /// <summary>Recomputes already-passed days affected by a change (at most the last 62 days).</summary>
    internal static async Task RecomputePastAsync(AttendanceEngine engine, IClock clock, IReadOnlyCollection<Guid> employees, DateOnly from, DateOnly? to, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(clock.UtcNow);
        var start = from > today.AddDays(-62) ? from : today.AddDays(-62);
        var end = to is { } t && t < today ? t : today;
        if (start <= end)
        {
            await engine.RecomputeAsync(employees, new DateRange(start, end), cancellationToken).ConfigureAwait(false);
        }
    }
}

[RequiresPermission(Permission.ManageAttendance)]
public sealed record DeleteShiftAssignmentCommand(Guid Id) : ICommand<Unit>;

public sealed class DeleteShiftAssignmentHandler(IAppDbContext db, ICurrentUser user, IAuditWriter audit, AttendanceEngine engine, IClock clock)
    : IRequestHandler<DeleteShiftAssignmentCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(DeleteShiftAssignmentCommand request, CancellationToken cancellationToken)
    {
        var assignment = await db.ShiftAssignments.FirstOrDefaultAsync(a => a.Id == request.Id && a.CompanyId == user.CompanyId, cancellationToken).ConfigureAwait(false);
        if (assignment is null)
        {
            return Error.NotFound("ShiftAssignment");
        }

        db.ShiftAssignments.Remove(assignment);
        audit.Record("Shift.AssignmentDeleted", $"Shift assignment from {assignment.EffectiveFrom:yyyy-MM-dd} removed", nameof(ShiftAssignment), assignment.Id);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await AssignShiftHandler.RecomputePastAsync(engine, clock, [assignment.EmployeeId], assignment.EffectiveFrom, assignment.EffectiveTo, cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
