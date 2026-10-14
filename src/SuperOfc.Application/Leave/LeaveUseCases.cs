using FluentValidation;
using Microsoft.EntityFrameworkCore;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Attendance;
using SuperOfc.Application.Common;
using SuperOfc.Application.Payroll;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Employees;
using SuperOfc.Domain.Leave;
using SuperOfc.Domain.Security;

namespace SuperOfc.Application.Leave;

// ---------- Leave types ----------

public sealed record LeaveTypeDto(
    Guid Id,
    string Name,
    string Code,
    bool IsPaid,
    decimal YearlyEntitlement,
    LeaveAccrual Accrual,
    decimal CarryForwardCap,
    bool AllowsHalfDay,
    bool RequiresAttachment,
    bool AllowNegativeBalance,
    int SortOrder);

public sealed record GetLeaveTypesQuery : IQuery<IReadOnlyList<LeaveTypeDto>>;

public sealed class GetLeaveTypesHandler(IAppDbContext db, ICurrentUser user) : IRequestHandler<GetLeaveTypesQuery, IReadOnlyList<LeaveTypeDto>>
{
    public async Task<Result<IReadOnlyList<LeaveTypeDto>>> HandleAsync(GetLeaveTypesQuery request, CancellationToken cancellationToken)
        => await db.LeaveTypes.AsNoTracking()
            .Where(t => t.CompanyId == user.CompanyId && t.DeletedAtUtc == null)
            .OrderBy(t => t.SortOrder).ThenBy(t => t.Name)
            .Select(t => new LeaveTypeDto(t.Id, t.Name, t.Code, t.IsPaid, t.YearlyEntitlement, t.Accrual, t.CarryForwardCap, t.AllowsHalfDay, t.RequiresAttachment, t.AllowNegativeBalance, t.SortOrder))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
}

[RequiresPermission(Permission.ManageLeave)]
public sealed record SaveLeaveTypeCommand(
    Guid? Id,
    string Name,
    string Code,
    bool IsPaid,
    decimal YearlyEntitlement,
    LeaveAccrual Accrual,
    decimal CarryForwardCap,
    bool AllowsHalfDay,
    bool RequiresAttachment,
    bool AllowNegativeBalance,
    int SortOrder) : ICommand<Guid>;

public sealed class SaveLeaveTypeValidator : AbstractValidator<SaveLeaveTypeCommand>
{
    public SaveLeaveTypeValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Enter a name.").MaximumLength(100);
        RuleFor(x => x.Code).NotEmpty().WithMessage("Enter a short code, for example CL.").MaximumLength(10);
        RuleFor(x => x.YearlyEntitlement).InclusiveBetween(0, 366);
        RuleFor(x => x.CarryForwardCap).InclusiveBetween(0, 366);
        RuleFor(x => x.Accrual).IsInEnum();
    }
}

public sealed class SaveLeaveTypeHandler(IAppDbContext db, ICurrentUser user, IIdGenerator ids, IAuditWriter audit) : IRequestHandler<SaveLeaveTypeCommand, Guid>
{
    public async Task<Result<Guid>> HandleAsync(SaveLeaveTypeCommand request, CancellationToken cancellationToken)
    {
        var code = request.Code.Trim().ToUpperInvariant();
        if (await db.LeaveTypes.AnyAsync(t => t.CompanyId == user.CompanyId && t.DeletedAtUtc == null && t.Code == code && t.Id != request.Id, cancellationToken).ConfigureAwait(false))
        {
            return Error.Validation(nameof(request.Code), "Another leave type uses this code.");
        }

        LeaveType type;
        if (request.Id is { } id)
        {
            var found = await db.LeaveTypes.FirstOrDefaultAsync(t => t.Id == id && t.CompanyId == user.CompanyId, cancellationToken).ConfigureAwait(false);
            if (found is null)
            {
                return Error.NotFound("LeaveType");
            }

            type = found;
        }
        else
        {
            type = new LeaveType { Id = ids.NewId(), CompanyId = user.CompanyId };
            db.LeaveTypes.Add(type);
        }

        type.Name = request.Name.Trim();
        type.Code = code;
        type.IsPaid = request.IsPaid;
        type.YearlyEntitlement = request.YearlyEntitlement;
        type.Accrual = request.Accrual;
        type.CarryForwardCap = request.CarryForwardCap;
        type.AllowsHalfDay = request.AllowsHalfDay;
        type.RequiresAttachment = request.RequiresAttachment;
        type.AllowNegativeBalance = request.AllowNegativeBalance;
        type.SortOrder = request.SortOrder;
        audit.Record(request.Id is null ? "LeaveType.Created" : "LeaveType.Updated", $"Leave type {type.Name}", nameof(LeaveType), type.Id, null, request);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return type.Id;
    }
}

[RequiresPermission(Permission.ManageLeave)]
public sealed record DeleteLeaveTypeCommand(Guid Id) : ICommand<Unit>;

public sealed class DeleteLeaveTypeHandler(IAppDbContext db, ICurrentUser user, IClock clock, IAuditWriter audit) : IRequestHandler<DeleteLeaveTypeCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(DeleteLeaveTypeCommand request, CancellationToken cancellationToken)
    {
        var type = await db.LeaveTypes.FirstOrDefaultAsync(t => t.Id == request.Id && t.CompanyId == user.CompanyId && t.DeletedAtUtc == null, cancellationToken).ConfigureAwait(false);
        if (type is null)
        {
            return Error.NotFound("LeaveType");
        }

        if (await db.LeaveRequests.AnyAsync(r => r.LeaveTypeId == type.Id && r.Status == LeaveRequestStatus.Pending, cancellationToken).ConfigureAwait(false))
        {
            return Error.Validation("Decide the pending requests for this leave type first.");
        }

        type.DeletedAtUtc = clock.UtcNow;
        audit.Record("LeaveType.Deleted", $"Leave type {type.Name}", nameof(LeaveType), type.Id);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}

// ---------- Balances ----------

public sealed record LeaveBalanceDto(Guid LeaveTypeId, string LeaveType, string Code, bool IsPaid, int Year, decimal Opening, decimal Accrued, decimal Used, decimal Adjusted, decimal Closing);

public sealed record GetLeaveBalancesQuery(Guid EmployeeId, int? Year = null) : IQuery<IReadOnlyList<LeaveBalanceDto>>;

public sealed class GetLeaveBalancesHandler(IAppDbContext db, ICurrentUser user, LeaveLedger ledger) : IRequestHandler<GetLeaveBalancesQuery, IReadOnlyList<LeaveBalanceDto>>
{
    public async Task<Result<IReadOnlyList<LeaveBalanceDto>>> HandleAsync(GetLeaveBalancesQuery request, CancellationToken cancellationToken)
    {
        var employee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == request.EmployeeId && e.CompanyId == user.CompanyId, cancellationToken).ConfigureAwait(false);
        if (employee is null)
        {
            return Error.NotFound("Employee");
        }

        var company = await ledger.CompanyAsync(cancellationToken).ConfigureAwait(false);
        var year = request.Year ?? LeaveLedger.LeaveYear(company, ledger.Today);
        var types = await db.LeaveTypes.AsNoTracking().Where(t => t.CompanyId == user.CompanyId && t.DeletedAtUtc == null)
            .OrderBy(t => t.SortOrder).ThenBy(t => t.Name).ToListAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<LeaveBalanceDto>();
        foreach (var type in types)
        {
            var b = await ledger.ProjectAsync(company, employee, type, year, cancellationToken).ConfigureAwait(false);
            result.Add(new LeaveBalanceDto(type.Id, type.Name, type.Code, type.IsPaid, year, b.Opening, b.Accrued, b.Used, b.Adjusted, b.Closing));
        }

        return result;
    }
}

[RequiresPermission(Permission.ManageLeave)]
public sealed record AdjustLeaveBalanceCommand(Guid EmployeeId, Guid LeaveTypeId, int Year, decimal Days, string Reason) : ICommand<Unit>;

public sealed class AdjustLeaveBalanceValidator : AbstractValidator<AdjustLeaveBalanceCommand>
{
    public AdjustLeaveBalanceValidator()
    {
        RuleFor(x => x.Days).NotEqual(0).WithMessage("Enter the number of days to add or remove.").InclusiveBetween(-366, 366);
        RuleFor(x => x.Days).Must(d => d * 2 == Math.Floor(d * 2)).WithMessage("Use whole or half days.");
        RuleFor(x => x.Reason).NotEmpty().WithMessage("Enter a reason.").MaximumLength(300);
    }
}

public sealed class AdjustLeaveBalanceHandler(IAppDbContext db, ICurrentUser user, LeaveLedger ledger, IAuditWriter audit) : IRequestHandler<AdjustLeaveBalanceCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(AdjustLeaveBalanceCommand request, CancellationToken cancellationToken)
    {
        var employee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == request.EmployeeId && e.CompanyId == user.CompanyId, cancellationToken).ConfigureAwait(false);
        var type = await db.LeaveTypes.AsNoTracking().FirstOrDefaultAsync(t => t.Id == request.LeaveTypeId && t.CompanyId == user.CompanyId, cancellationToken).ConfigureAwait(false);
        if (employee is null || type is null)
        {
            return Error.NotFound("LeaveBalance");
        }

        var company = await ledger.CompanyAsync(cancellationToken).ConfigureAwait(false);
        var balance = await ledger.GetForUpdateAsync(company, employee, type, request.Year, cancellationToken).ConfigureAwait(false);
        balance.Adjusted += request.Days;
        if (balance.Closing < 0 && !type.AllowNegativeBalance)
        {
            return Error.Validation(nameof(request.Days), "The balance cannot go below zero for this leave type.");
        }

        audit.Record("Leave.BalanceAdjusted", $"{employee.Code} {type.Code} {request.Year}: {request.Days:+0.#;-0.#} days ({request.Reason})", nameof(LeaveBalance), balance.Id, null,
            new { request.Days, request.Reason });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}

// ---------- Requests ----------

public sealed record LeaveRequestDto(
    Guid Id,
    Guid EmployeeId,
    string EmployeeCode,
    string EmployeeName,
    Guid LeaveTypeId,
    string LeaveType,
    DateOnly FromDate,
    DateOnly ToDate,
    bool IsHalfDay,
    decimal Days,
    LeaveRequestStatus Status,
    string? Reason,
    string? DecisionNote,
    string? DecidedBy,
    DateTime CreatedAtUtc,
    string? AttachmentPath);

public sealed record GetLeaveRequestsQuery(LeaveRequestStatus? Status = null, Guid? EmployeeId = null, DateOnly? From = null, DateOnly? To = null, string? Search = null)
    : IQuery<IReadOnlyList<LeaveRequestDto>>;

public sealed class GetLeaveRequestsHandler(IAppDbContext db, ICurrentUser user) : IRequestHandler<GetLeaveRequestsQuery, IReadOnlyList<LeaveRequestDto>>
{
    public async Task<Result<IReadOnlyList<LeaveRequestDto>>> HandleAsync(GetLeaveRequestsQuery request, CancellationToken cancellationToken)
    {
        var query = from r in db.LeaveRequests.AsNoTracking()
                    join e in db.Employees.AsNoTracking() on r.EmployeeId equals e.Id
                    join t in db.LeaveTypes.AsNoTracking() on r.LeaveTypeId equals t.Id
                    where r.CompanyId == user.CompanyId
                    select new { r, e.Code, e.FullName, TypeName = t.Name };
        if (request.Status is { } status)
        {
            query = query.Where(x => x.r.Status == status);
        }

        if (request.EmployeeId is { } employeeId)
        {
            query = query.Where(x => x.r.EmployeeId == employeeId);
        }

        if (request.From is { } rangeStart)
        {
            query = query.Where(x => x.r.ToDate >= rangeStart);
        }

        if (request.To is { } rangeEnd)
        {
            query = query.Where(x => x.r.FromDate <= rangeEnd);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim();
            query = query.Where(x => x.Code.Contains(term) || x.FullName.Contains(term));
        }

        return await query.OrderByDescending(x => x.r.FromDate).ThenBy(x => x.Code).Take(1000)
            .Select(x => new LeaveRequestDto(x.r.Id, x.r.EmployeeId, x.Code, x.FullName, x.r.LeaveTypeId, x.TypeName, x.r.FromDate, x.r.ToDate, x.r.IsHalfDay, x.r.Days,
                x.r.Status, x.r.Reason, x.r.DecisionNote, x.r.DecidedBy, x.r.CreatedAtUtc, x.r.AttachmentPath))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}

[RequiresPermission(Permission.ManageLeave)]
public sealed record CreateLeaveRequestCommand(
    Guid EmployeeId,
    Guid LeaveTypeId,
    DateOnly FromDate,
    DateOnly ToDate,
    bool IsHalfDay,
    string? Reason,
    string? AttachmentSourcePath = null,
    bool ApproveNow = false) : ICommand<Guid>;

public sealed class CreateLeaveRequestValidator : AbstractValidator<CreateLeaveRequestCommand>
{
    public CreateLeaveRequestValidator()
    {
        RuleFor(x => x.EmployeeId).NotEmpty().WithMessage("Choose an employee.");
        RuleFor(x => x.LeaveTypeId).NotEmpty().WithMessage("Choose a leave type.");
        RuleFor(x => x.ToDate).GreaterThanOrEqualTo(x => x.FromDate).WithMessage("The last day must be on or after the first day.");
        RuleFor(x => x.ToDate).Equal(x => x.FromDate).When(x => x.IsHalfDay).WithMessage("A half day must be a single date.");
        RuleFor(x => x).Must(x => x.ToDate.DayNumber - x.FromDate.DayNumber < 366).WithMessage("A request can cover at most one year.");
        RuleFor(x => x.Reason).MaximumLength(500);
    }
}

public sealed class CreateLeaveRequestHandler(
    IAppDbContext db,
    ICurrentUser user,
    IIdGenerator ids,
    IAuditWriter audit,
    IFileStorage files,
    LeaveLedger ledger,
    AttendanceEngine engine,
    PayrollLocks locks) : IRequestHandler<CreateLeaveRequestCommand, Guid>
{
    public async Task<Result<Guid>> HandleAsync(CreateLeaveRequestCommand request, CancellationToken cancellationToken)
    {
        var employee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == request.EmployeeId && e.CompanyId == user.CompanyId && e.DeletedAtUtc == null, cancellationToken).ConfigureAwait(false);
        if (employee is null)
        {
            return Error.NotFound("Employee");
        }

        var type = await db.LeaveTypes.AsNoTracking().FirstOrDefaultAsync(t => t.Id == request.LeaveTypeId && t.CompanyId == user.CompanyId && t.DeletedAtUtc == null, cancellationToken).ConfigureAwait(false);
        if (type is null)
        {
            return Error.NotFound("LeaveType");
        }

        if (request.IsHalfDay && !type.AllowsHalfDay)
        {
            return Error.Validation(nameof(request.IsHalfDay), $"{type.Name} cannot be taken as a half day.");
        }

        if (type.RequiresAttachment && string.IsNullOrWhiteSpace(request.AttachmentSourcePath))
        {
            return Error.Validation(nameof(request.AttachmentSourcePath), $"{type.Name} needs a supporting document.");
        }

        if (!employee.IsEmployedOn(request.FromDate) || !employee.IsEmployedOn(request.ToDate))
        {
            return Error.Validation(nameof(request.FromDate), "The dates must be within the employment period.");
        }

        if (request.ToDate >= request.FromDate && await locks.FirstLockedInAsync(new DateRange(request.FromDate, request.ToDate), cancellationToken).ConfigureAwait(false) is { } locked)
        {
            return PayrollLocks.LockedError(locked, nameof(request.FromDate));
        }

        var overlapping = await db.LeaveRequests.AnyAsync(
            r => r.EmployeeId == employee.Id && (r.Status == LeaveRequestStatus.Pending || r.Status == LeaveRequestStatus.Approved)
                 && r.FromDate <= request.ToDate && r.ToDate >= request.FromDate, cancellationToken).ConfigureAwait(false);
        if (overlapping)
        {
            return Error.Validation(nameof(request.FromDate), "There is already a leave request on some of these dates.");
        }

        var company = await ledger.CompanyAsync(cancellationToken).ConfigureAwait(false);
        var calendar = await ledger.CalendarAsync(company, request.FromDate, request.ToDate, cancellationToken).ConfigureAwait(false);
        var days = LeaveRequest.CountDays(request.FromDate, request.ToDate, request.IsHalfDay, calendar, employee.LocationId);
        if (days <= 0)
        {
            return Error.Validation(nameof(request.FromDate), "These dates are all weekly offs or holidays.");
        }

        var leave = new LeaveRequest
        {
            Id = ids.NewId(),
            CompanyId = user.CompanyId,
            EmployeeId = employee.Id,
            LeaveTypeId = type.Id,
            FromDate = request.FromDate,
            ToDate = request.ToDate,
            IsHalfDay = request.IsHalfDay,
            Days = days,
            Status = LeaveRequestStatus.Pending,
            Reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim(),
        };

        // Check the balance up front so the user hears about it now, not at approval.
        var problem = await LeaveApproval.CheckBalanceAsync(ledger, company, employee, type, leave, calendar, reserve: false, cancellationToken).ConfigureAwait(false);
        if (problem is not null)
        {
            return problem;
        }

        if (!string.IsNullOrWhiteSpace(request.AttachmentSourcePath))
        {
            var stream = File.OpenRead(request.AttachmentSourcePath);
            await using (stream.ConfigureAwait(false))
            {
                leave.AttachmentPath = await files.SaveAsync("leave", employee.Id, Path.GetFileName(request.AttachmentSourcePath), stream, cancellationToken).ConfigureAwait(false);
            }
        }

        db.LeaveRequests.Add(leave);
        audit.Record("Leave.Requested", $"{employee.Code} {type.Code} {leave.FromDate:yyyy-MM-dd}–{leave.ToDate:yyyy-MM-dd} ({days} days)", nameof(LeaveRequest), leave.Id, null,
            new { type.Code, leave.FromDate, leave.ToDate, leave.Days });

        if (request.ApproveNow)
        {
            if (!PermissionMatrix.Has(user.Role, Permission.ApproveLeave))
            {
                return Error.Forbidden;
            }

            var approval = await LeaveApproval.ApproveAsync(ledger, company, employee, type, leave, calendar, user.UserName, null, cancellationToken).ConfigureAwait(false);
            if (approval is not null)
            {
                return approval;
            }

            audit.Record("Leave.Approved", $"{employee.Code} {type.Code} {leave.FromDate:yyyy-MM-dd}", nameof(LeaveRequest), leave.Id);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (request.ApproveNow)
        {
            await LeaveApproval.RecomputeAsync(engine, ledger, leave, cancellationToken).ConfigureAwait(false);
        }

        return leave.Id;
    }
}

public enum LeaveDecision
{
    Approve,
    Reject,
    Cancel,
}

/// <summary>Approve or reject a pending request, or cancel a pending or approved one (cancelling restores the balance).</summary>
[RequiresPermission(Permission.ManageLeave)]
public sealed record DecideLeaveRequestCommand(Guid Id, LeaveDecision Decision, string? Note) : ICommand<Unit>;

public sealed class DecideLeaveRequestValidator : AbstractValidator<DecideLeaveRequestCommand>
{
    public DecideLeaveRequestValidator()
    {
        RuleFor(x => x.Decision).IsInEnum();
        RuleFor(x => x.Note).NotEmpty().When(x => x.Decision != LeaveDecision.Approve).WithMessage("Enter a reason.");
        RuleFor(x => x.Note).MaximumLength(500);
    }
}

public sealed class DecideLeaveRequestHandler(IAppDbContext db, ICurrentUser user, IClock clock, IAuditWriter audit, LeaveLedger ledger, AttendanceEngine engine, PayrollLocks locks)
    : IRequestHandler<DecideLeaveRequestCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(DecideLeaveRequestCommand request, CancellationToken cancellationToken)
    {
        var needed = request.Decision == LeaveDecision.Cancel ? Permission.ManageLeave : Permission.ApproveLeave;
        if (!PermissionMatrix.Has(user.Role, needed))
        {
            return Error.Forbidden;
        }

        var leave = await db.LeaveRequests.FirstOrDefaultAsync(r => r.Id == request.Id && r.CompanyId == user.CompanyId, cancellationToken).ConfigureAwait(false);
        if (leave is null)
        {
            return Error.NotFound("LeaveRequest");
        }

        var allowed = request.Decision == LeaveDecision.Cancel
            ? leave.Status is LeaveRequestStatus.Pending or LeaveRequestStatus.Approved
            : leave.Status == LeaveRequestStatus.Pending;
        if (!allowed)
        {
            return Error.Validation($"This request is already {leave.Status.ToString().ToLowerInvariant()}.");
        }

        if (await locks.FirstLockedInAsync(new DateRange(leave.FromDate, leave.ToDate), cancellationToken).ConfigureAwait(false) is { } locked)
        {
            return PayrollLocks.LockedError(locked);
        }

        var employee = await db.Employees.AsNoTracking().FirstAsync(e => e.Id == leave.EmployeeId, cancellationToken).ConfigureAwait(false);
        var type = await db.LeaveTypes.AsNoTracking().FirstAsync(t => t.Id == leave.LeaveTypeId, cancellationToken).ConfigureAwait(false);
        var company = await ledger.CompanyAsync(cancellationToken).ConfigureAwait(false);
        var calendar = await ledger.CalendarAsync(company, leave.FromDate, leave.ToDate, cancellationToken).ConfigureAwait(false);
        var wasApproved = leave.Status == LeaveRequestStatus.Approved;

        switch (request.Decision)
        {
            case LeaveDecision.Approve:
                var problem = await LeaveApproval.ApproveAsync(ledger, company, employee, type, leave, calendar, user.UserName, request.Note, cancellationToken).ConfigureAwait(false);
                if (problem is not null)
                {
                    return problem;
                }

                break;
            case LeaveDecision.Reject:
                leave.Status = LeaveRequestStatus.Rejected;
                break;
            default:
                if (wasApproved)
                {
                    foreach (var (year, days) in LeaveLedger.DaysByYear(company, leave, calendar, employee.LocationId))
                    {
                        var balance = await ledger.GetForUpdateAsync(company, employee, type, year, cancellationToken).ConfigureAwait(false);
                        balance.Used -= days;
                    }
                }

                leave.Status = LeaveRequestStatus.Cancelled;
                break;
        }

        leave.DecidedBy = user.UserName;
        leave.DecidedAtUtc = clock.UtcNow;
        leave.DecisionNote = string.IsNullOrWhiteSpace(request.Note) ? leave.DecisionNote : request.Note.Trim();
        audit.Record($"Leave.{leave.Status}", $"{employee.Code} {type.Code} {leave.FromDate:yyyy-MM-dd}–{leave.ToDate:yyyy-MM-dd}: {leave.Status}", nameof(LeaveRequest), leave.Id, null,
            new { leave.Status, request.Note });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (request.Decision == LeaveDecision.Approve || wasApproved)
        {
            await LeaveApproval.RecomputeAsync(engine, ledger, leave, cancellationToken).ConfigureAwait(false);
        }

        return Unit.Value;
    }
}

internal static class LeaveApproval
{
    /// <summary>Returns an error when the balance is not enough; when <paramref name="reserve"/> is true the days are booked.</summary>
    public static async Task<Error?> CheckBalanceAsync(
        LeaveLedger ledger,
        Domain.Organization.Company company,
        Employee employee,
        LeaveType type,
        LeaveRequest leave,
        Domain.Organization.WorkCalendar calendar,
        bool reserve,
        CancellationToken cancellationToken)
    {
        foreach (var (year, days) in LeaveLedger.DaysByYear(company, leave, calendar, employee.LocationId))
        {
            var balance = reserve
                ? await ledger.GetForUpdateAsync(company, employee, type, year, cancellationToken).ConfigureAwait(false)
                : await ledger.ProjectAsync(company, employee, type, year, cancellationToken).ConfigureAwait(false);
            if (!type.AllowNegativeBalance && balance.Closing - days < 0)
            {
                return Error.Validation("Days", $"Not enough {type.Name} balance: {balance.Closing:0.#} days left, {days:0.#} requested.");
            }

            if (reserve)
            {
                balance.Used += days;
            }
        }

        return null;
    }

    public static async Task<Error?> ApproveAsync(
        LeaveLedger ledger,
        Domain.Organization.Company company,
        Employee employee,
        LeaveType type,
        LeaveRequest leave,
        Domain.Organization.WorkCalendar calendar,
        string approver,
        string? note,
        CancellationToken cancellationToken)
    {
        var problem = await CheckBalanceAsync(ledger, company, employee, type, leave, calendar, reserve: true, cancellationToken).ConfigureAwait(false);
        if (problem is not null)
        {
            return problem;
        }

        leave.Status = LeaveRequestStatus.Approved;
        leave.DecidedBy = approver;
        leave.DecisionNote = note;
        return null;
    }

    /// <summary>Leave changes attendance days that have already passed (future days are computed when they come).</summary>
    public static async Task RecomputeAsync(AttendanceEngine engine, LeaveLedger ledger, LeaveRequest leave, CancellationToken cancellationToken)
    {
        var end = leave.ToDate < ledger.Today ? leave.ToDate : ledger.Today;
        if (leave.FromDate <= end)
        {
            await engine.RecomputeAsync([leave.EmployeeId], new DateRange(leave.FromDate, end), cancellationToken).ConfigureAwait(false);
        }
    }
}
