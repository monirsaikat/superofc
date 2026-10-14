using Microsoft.EntityFrameworkCore;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Common;
using SuperOfc.Domain.Attendance;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Leave;

namespace SuperOfc.Application.Attendance;

/// <summary>Employees for pickers, shown as "code · name". Includes people who left only when asked.</summary>
public sealed record GetEmployeePickListQuery(bool IncludeLeft = false) : IQuery<IReadOnlyList<LookupItem>>;

public sealed class GetEmployeePickListHandler(IAppDbContext db, ICurrentUser user) : IRequestHandler<GetEmployeePickListQuery, IReadOnlyList<LookupItem>>
{
    public async Task<Result<IReadOnlyList<LookupItem>>> HandleAsync(GetEmployeePickListQuery request, CancellationToken cancellationToken)
    {
        var query = db.Employees.AsNoTracking().Where(e => e.CompanyId == user.CompanyId && e.DeletedAtUtc == null);
        if (!request.IncludeLeft)
        {
            query = query.Where(e => e.ExitDate == null);
        }

        return await query.OrderBy(e => e.Code)
            .Select(e => new LookupItem(e.Id, e.Code + " · " + e.FullName))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed record TodayAttendance(DateOnly Date, int Expected, int In, int Late, int Absent, int OnLeave, int PendingLeaveRequests);

/// <summary>Live figures for today, computed from punches so far (nothing is saved).</summary>
public sealed record GetTodayAttendanceQuery : IQuery<TodayAttendance>;

public sealed class GetTodayAttendanceHandler(IAppDbContext db, ICurrentUser user, AttendanceEngine engine, IClock clock, ICompanyContext company)
    : IRequestHandler<GetTodayAttendanceQuery, TodayAttendance>
{
    public async Task<Result<TodayAttendance>> HandleAsync(GetTodayAttendanceQuery request, CancellationToken cancellationToken)
    {
        var today = clock.TodayIn(company.TimeZone);
        var results = await engine.PreviewAsync(null, new DateRange(today, today), cancellationToken).ConfigureAwait(false);
        var working = results.Select(r => r.Result).Where(r => r.Status is not (AttendanceStatus.Holiday or AttendanceStatus.WeeklyOff or AttendanceStatus.NotJoined or AttendanceStatus.Exited)).ToList();
        var pending = await db.LeaveRequests.CountAsync(r => r.CompanyId == user.CompanyId && r.Status == LeaveRequestStatus.Pending, cancellationToken).ConfigureAwait(false);
        return new TodayAttendance(
            today,
            working.Count,
            working.Count(r => r.PunchCount > 0),
            working.Count(r => r.LateMinutes > 0),
            working.Count(r => r.PunchCount == 0 && r.Status == AttendanceStatus.Absent),
            working.Count(r => r.Status == AttendanceStatus.Leave),
            pending);
    }
}
