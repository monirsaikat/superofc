using FluentValidation;
using Microsoft.EntityFrameworkCore;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Common;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Organization;
using SuperOfc.Domain.Security;

namespace SuperOfc.Application.Organization;

public sealed record MasterDataDto(Guid Id, MasterDataKind Kind, string Name, string? Code, string? Description, int SortOrder, int EmployeeCount);

internal static class MasterDataSets
{
    public static IQueryable<MasterDataEntity> Query(IAppDbContext db, MasterDataKind kind) => kind switch
    {
        MasterDataKind.Department => db.Departments,
        MasterDataKind.Designation => db.Designations,
        MasterDataKind.Location => db.Locations,
        MasterDataKind.Grade => db.Grades,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static MasterDataEntity Create(MasterDataKind kind) => kind switch
    {
        MasterDataKind.Department => new Department(),
        MasterDataKind.Designation => new Designation(),
        MasterDataKind.Location => new Location(),
        MasterDataKind.Grade => new Grade(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

/// <summary>Active items of one kind with the number of current employees using each.</summary>
public sealed record GetMasterDataQuery(MasterDataKind Kind) : IQuery<IReadOnlyList<MasterDataDto>>;

public sealed class GetMasterDataHandler(IAppDbContext db, ICurrentUser user) : IRequestHandler<GetMasterDataQuery, IReadOnlyList<MasterDataDto>>
{
    public async Task<Result<IReadOnlyList<MasterDataDto>>> HandleAsync(GetMasterDataQuery request, CancellationToken cancellationToken)
    {
        var items = await MasterDataSets.Query(db, request.Kind).AsNoTracking()
            .Where(m => m.CompanyId == user.CompanyId && m.DeletedAtUtc == null)
            .OrderBy(m => m.SortOrder).ThenBy(m => m.Name)
            .Select(m => new { m.Id, m.Name, m.Code, m.Description, m.SortOrder })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var employees = db.Employees.AsNoTracking().Where(e => e.CompanyId == user.CompanyId && e.DeletedAtUtc == null && e.ExitDate == null);
        var counts = request.Kind switch
        {
            MasterDataKind.Department => await employees.Where(e => e.DepartmentId != null).GroupBy(e => e.DepartmentId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken).ConfigureAwait(false),
            MasterDataKind.Designation => await employees.Where(e => e.DesignationId != null).GroupBy(e => e.DesignationId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken).ConfigureAwait(false),
            MasterDataKind.Location => await employees.Where(e => e.LocationId != null).GroupBy(e => e.LocationId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken).ConfigureAwait(false),
            _ => await employees.Where(e => e.GradeId != null).GroupBy(e => e.GradeId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken).ConfigureAwait(false),
        };

        return items.Select(m => new MasterDataDto(m.Id, request.Kind, m.Name, m.Code, m.Description, m.SortOrder, counts.GetValueOrDefault(m.Id))).ToList();
    }
}

/// <summary>All lookups needed by employee forms, in one round trip.</summary>
public sealed record OrganizationLookups(
    IReadOnlyList<LookupItem> Departments,
    IReadOnlyList<LookupItem> Designations,
    IReadOnlyList<LookupItem> Locations,
    IReadOnlyList<LookupItem> Grades);

public sealed record GetOrganizationLookupsQuery : IQuery<OrganizationLookups>;

public sealed class GetOrganizationLookupsHandler(IAppDbContext db, ICurrentUser user) : IRequestHandler<GetOrganizationLookupsQuery, OrganizationLookups>
{
    public async Task<Result<OrganizationLookups>> HandleAsync(GetOrganizationLookupsQuery request, CancellationToken cancellationToken)
    {
        async Task<IReadOnlyList<LookupItem>> Load(MasterDataKind kind)
            => await MasterDataSets.Query(db, kind).AsNoTracking()
                .Where(m => m.CompanyId == user.CompanyId && m.DeletedAtUtc == null)
                .OrderBy(m => m.SortOrder).ThenBy(m => m.Name)
                .Select(m => new LookupItem(m.Id, m.Name))
                .ToListAsync(cancellationToken).ConfigureAwait(false);

        return new OrganizationLookups(
            await Load(MasterDataKind.Department).ConfigureAwait(false),
            await Load(MasterDataKind.Designation).ConfigureAwait(false),
            await Load(MasterDataKind.Location).ConfigureAwait(false),
            await Load(MasterDataKind.Grade).ConfigureAwait(false));
    }
}

[RequiresPermission(Permission.ManageEmployees)]
public sealed record SaveMasterDataCommand(MasterDataKind Kind, Guid? Id, string Name, string? Code, string? Description, int SortOrder) : ICommand<Guid>;

public sealed class SaveMasterDataValidator : AbstractValidator<SaveMasterDataCommand>
{
    public SaveMasterDataValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Enter a name.").MaximumLength(100);
        RuleFor(x => x.Code).MaximumLength(20);
        RuleFor(x => x.Kind).IsInEnum();
    }
}

public sealed class SaveMasterDataHandler(IAppDbContext db, ICurrentUser user, IIdGenerator ids, IAuditWriter audit) : IRequestHandler<SaveMasterDataCommand, Guid>
{
    public async Task<Result<Guid>> HandleAsync(SaveMasterDataCommand request, CancellationToken cancellationToken)
    {
        var name = request.Name.Trim();
        var set = MasterDataSets.Query(db, request.Kind);
        var duplicate = await set.AnyAsync(
            m => m.CompanyId == user.CompanyId && m.DeletedAtUtc == null && m.Id != request.Id && m.Name.ToLower() == name.ToLower(),
            cancellationToken).ConfigureAwait(false);
        if (duplicate)
        {
            return new Error("Validation.Name", $"A {request.Kind.ToString().ToLowerInvariant()} named '{name}' already exists.");
        }

        MasterDataEntity entity;
        object? before = null;
        if (request.Id is { } id)
        {
            var existing = await set.FirstOrDefaultAsync(m => m.Id == id && m.DeletedAtUtc == null, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                return Error.NotFound(request.Kind.ToString());
            }

            entity = existing;
            before = new { entity.Name, entity.Code, entity.Description, entity.SortOrder };
        }
        else
        {
            entity = MasterDataSets.Create(request.Kind);
            entity.Id = ids.NewId();
            entity.CompanyId = user.CompanyId;
            db.Add(entity);
        }

        entity.Name = name;
        entity.Code = string.IsNullOrWhiteSpace(request.Code) ? null : request.Code.Trim();
        entity.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        entity.SortOrder = request.SortOrder;
        audit.Record(
            $"{request.Kind}.{(before is null ? "Created" : "Updated")}",
            $"{request.Kind} '{entity.Name}' {(before is null ? "created" : "updated")}.",
            request.Kind.ToString(),
            entity.Id,
            before,
            new { entity.Name, entity.Code, entity.Description, entity.SortOrder });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return entity.Id;
    }
}

/// <summary>Soft-deletes a lookup. Allowed even when used; existing records keep showing the old name.</summary>
[RequiresPermission(Permission.ManageEmployees)]
public sealed record DeleteMasterDataCommand(MasterDataKind Kind, Guid Id) : ICommand<Unit>;

public sealed class DeleteMasterDataHandler(IAppDbContext db, IClock clock, IAuditWriter audit) : IRequestHandler<DeleteMasterDataCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(DeleteMasterDataCommand request, CancellationToken cancellationToken)
    {
        var entity = await MasterDataSets.Query(db, request.Kind).FirstOrDefaultAsync(m => m.Id == request.Id && m.DeletedAtUtc == null, cancellationToken).ConfigureAwait(false);
        if (entity is null)
        {
            return Error.NotFound(request.Kind.ToString());
        }

        entity.DeletedAtUtc = clock.UtcNow;
        audit.Record($"{request.Kind}.Deleted", $"{request.Kind} '{entity.Name}' deleted.", request.Kind.ToString(), entity.Id);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}

public sealed record HolidayDto(Guid Id, DateOnly Date, string Name, bool IsPaid, Guid? LocationId, string? LocationName);

public sealed record GetHolidaysQuery(int Year) : IQuery<IReadOnlyList<HolidayDto>>;

public sealed class GetHolidaysHandler(IAppDbContext db, ICurrentUser user) : IRequestHandler<GetHolidaysQuery, IReadOnlyList<HolidayDto>>
{
    public async Task<Result<IReadOnlyList<HolidayDto>>> HandleAsync(GetHolidaysQuery request, CancellationToken cancellationToken)
    {
        var yearStart = new DateOnly(request.Year, 1, 1);
        var yearEnd = new DateOnly(request.Year, 12, 31);
        var rows = await (
            from h in db.Holidays.AsNoTracking()
            where h.CompanyId == user.CompanyId && h.Date >= yearStart && h.Date <= yearEnd
            join l in db.Locations on h.LocationId equals l.Id into locations
            from l in locations.DefaultIfEmpty()
            orderby h.Date
            select new HolidayDto(h.Id, h.Date, h.Name, h.IsPaid, h.LocationId, l == null ? null : l.Name))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows;
    }
}

[RequiresPermission(Permission.ManageAttendance)]
public sealed record SaveHolidayCommand(Guid? Id, DateOnly Date, string Name, bool IsPaid, Guid? LocationId) : ICommand<Guid>;

public sealed class SaveHolidayValidator : AbstractValidator<SaveHolidayCommand>
{
    public SaveHolidayValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Enter the holiday name.").MaximumLength(100);
    }
}

public sealed class SaveHolidayHandler(IAppDbContext db, ICurrentUser user, IIdGenerator ids, IAuditWriter audit) : IRequestHandler<SaveHolidayCommand, Guid>
{
    public async Task<Result<Guid>> HandleAsync(SaveHolidayCommand request, CancellationToken cancellationToken)
    {
        var clash = await db.Holidays.AnyAsync(
            h => h.CompanyId == user.CompanyId && h.Id != request.Id && h.Date == request.Date && h.LocationId == request.LocationId,
            cancellationToken).ConfigureAwait(false);
        if (clash)
        {
            return new Error("Validation.Date", "There is already a holiday on this date.");
        }

        Holiday holiday;
        if (request.Id is { } id)
        {
            var existing = await db.Holidays.FirstOrDefaultAsync(h => h.Id == id, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                return Error.NotFound("Holiday");
            }

            holiday = existing;
        }
        else
        {
            holiday = new Holiday { Id = ids.NewId(), CompanyId = user.CompanyId };
            db.Holidays.Add(holiday);
        }

        holiday.Date = request.Date;
        holiday.Name = request.Name.Trim();
        holiday.IsPaid = request.IsPaid;
        holiday.LocationId = request.LocationId;
        audit.Record("Holiday.Saved", $"Holiday '{holiday.Name}' on {holiday.Date:yyyy-MM-dd} saved.", nameof(Holiday), holiday.Id, after: new { holiday.Date, holiday.Name, holiday.IsPaid });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return holiday.Id;
    }
}

[RequiresPermission(Permission.ManageAttendance)]
public sealed record DeleteHolidayCommand(Guid Id) : ICommand<Unit>;

public sealed class DeleteHolidayHandler(IAppDbContext db, IAuditWriter audit) : IRequestHandler<DeleteHolidayCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(DeleteHolidayCommand request, CancellationToken cancellationToken)
    {
        var holiday = await db.Holidays.FirstOrDefaultAsync(h => h.Id == request.Id, cancellationToken).ConfigureAwait(false);
        if (holiday is null)
        {
            return Error.NotFound("Holiday");
        }

        db.Holidays.Remove(holiday);
        audit.Record("Holiday.Deleted", $"Holiday '{holiday.Name}' on {holiday.Date:yyyy-MM-dd} deleted.", nameof(Holiday), holiday.Id);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}

/// <summary>Copies fixed-date holidays from the previous year (moving dates by one year). Returns how many were added.</summary>
[RequiresPermission(Permission.ManageAttendance)]
public sealed record CopyHolidaysFromPreviousYearCommand(int Year) : ICommand<int>;

public sealed class CopyHolidaysHandler(IAppDbContext db, ICurrentUser user, IIdGenerator ids, IAuditWriter audit) : IRequestHandler<CopyHolidaysFromPreviousYearCommand, int>
{
    public async Task<Result<int>> HandleAsync(CopyHolidaysFromPreviousYearCommand request, CancellationToken cancellationToken)
    {
        var from = new DateOnly(request.Year - 1, 1, 1);
        var to = new DateOnly(request.Year - 1, 12, 31);
        var previous = await db.Holidays.AsNoTracking().Where(h => h.CompanyId == user.CompanyId && h.Date >= from && h.Date <= to).ToListAsync(cancellationToken).ConfigureAwait(false);
        var existing = (await db.Holidays.AsNoTracking()
                .Where(h => h.CompanyId == user.CompanyId && h.Date >= from.AddYears(1) && h.Date <= to.AddYears(1))
                .Select(h => new { h.Date, h.LocationId })
                .ToListAsync(cancellationToken).ConfigureAwait(false))
            .Select(h => (h.Date, h.LocationId)).ToHashSet();
        var added = 0;
        foreach (var h in previous)
        {
            var date = h.Date.AddYears(1);
            if (existing.Contains((date, h.LocationId)))
            {
                continue;
            }

            db.Holidays.Add(new Holiday { Id = ids.NewId(), CompanyId = user.CompanyId, Date = date, Name = h.Name, IsPaid = h.IsPaid, LocationId = h.LocationId });
            added++;
        }

        audit.Record("Holiday.Copied", $"{added} holidays copied into {request.Year}.");
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return added;
    }
}
