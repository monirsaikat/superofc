using Microsoft.EntityFrameworkCore;
using SuperOfc.Domain.Attendance;
using SuperOfc.Domain.Leave;

namespace SuperOfc.Application.Abstractions;

public partial interface IAppDbContext
{
    DbSet<Shift> Shifts { get; }

    DbSet<ShiftAssignment> ShiftAssignments { get; }

    DbSet<AttendancePunch> AttendancePunches { get; }

    DbSet<AttendanceDay> AttendanceDays { get; }

    DbSet<AttendanceCorrection> AttendanceCorrections { get; }

    DbSet<LeaveType> LeaveTypes { get; }

    DbSet<LeaveBalance> LeaveBalances { get; }

    DbSet<LeaveRequest> LeaveRequests { get; }
}

/// <summary>Fast write path for computed attendance days (thousands of rows per recompute).</summary>
public interface IAttendanceDayStore
{
    /// <summary>
    /// Inserts new days and updates existing ones (matched by employee and date) in one transaction.
    /// Rows that are manually corrected are never changed, even if they appear in <paramref name="days"/>.
    /// </summary>
    Task UpsertAsync(IReadOnlyCollection<AttendanceDay> days, CancellationToken cancellationToken);
}
