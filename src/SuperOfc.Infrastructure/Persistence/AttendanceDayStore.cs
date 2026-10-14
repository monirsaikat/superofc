using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SuperOfc.Application.Abstractions;
using SuperOfc.Domain.Attendance;

namespace SuperOfc.Infrastructure.Persistence;

/// <summary>
/// Upserts attendance days with one prepared statement instead of EF change tracking (about ten times faster).
/// Values are written in the same text formats EF Core uses for SQLite so EF reads them back unchanged.
/// </summary>
internal sealed class AttendanceDayStore(AppDbContext db, ICurrentUser user, IClock clock) : IAttendanceDayStore
{
    private const string Sql = """
        INSERT INTO AttendanceDay (Id, CompanyId, EmployeeId, Date, ShiftId, FirstIn, LastOut, PunchCount, WorkedMinutes, LateMinutes,
            EarlyLeaveMinutes, OvertimeMinutes, Status, MissingPunch, LeaveDays, LeaveTypeId, LeaveIsPaid, IsManuallyCorrected, CorrectionNote,
            ComputedAtUtc, CreatedAtUtc, CreatedBy, ModifiedAtUtc, ModifiedBy, RowVersion)
        VALUES ($id, $company, $employee, $date, $shift, $firstIn, $lastOut, $punches, $worked, $late,
            $early, $overtime, $status, $missing, $leaveDays, $leaveType, $leavePaid, 0, NULL,
            $now, $now, $user, NULL, NULL, 1)
        ON CONFLICT (EmployeeId, Date) DO UPDATE SET
            ShiftId = excluded.ShiftId, FirstIn = excluded.FirstIn, LastOut = excluded.LastOut, PunchCount = excluded.PunchCount,
            WorkedMinutes = excluded.WorkedMinutes, LateMinutes = excluded.LateMinutes, EarlyLeaveMinutes = excluded.EarlyLeaveMinutes,
            OvertimeMinutes = excluded.OvertimeMinutes, Status = excluded.Status, MissingPunch = excluded.MissingPunch,
            LeaveDays = excluded.LeaveDays, LeaveTypeId = excluded.LeaveTypeId, LeaveIsPaid = excluded.LeaveIsPaid,
            ComputedAtUtc = excluded.ComputedAtUtc, ModifiedAtUtc = excluded.ComputedAtUtc, ModifiedBy = excluded.CreatedBy,
            RowVersion = AttendanceDay.RowVersion + 1
        WHERE AttendanceDay.IsManuallyCorrected = 0;
        """;

    public async Task UpsertAsync(IReadOnlyCollection<AttendanceDay> days, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(days);
        if (days.Count == 0)
        {
            return;
        }

        var now = Text(clock.UtcNow);
        var userName = user.IsAuthenticated ? user.UserName : "system";
        await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = db.Database.GetDbConnection();
            var outer = db.Database.CurrentTransaction?.GetDbTransaction();
            var transaction = outer ?? await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var command = connection.CreateCommand();
                await using (command.ConfigureAwait(false))
                {
                    command.Transaction = transaction;
                    command.CommandText = Sql;
                    var p = new Dictionary<string, DbParameter>(StringComparer.Ordinal);
                    foreach (var name in new[] { "$id", "$company", "$employee", "$date", "$shift", "$firstIn", "$lastOut", "$punches", "$worked", "$late", "$early", "$overtime", "$status", "$missing", "$leaveDays", "$leaveType", "$leavePaid", "$now", "$user" })
                    {
                        var parameter = command.CreateParameter();
                        parameter.ParameterName = name;
                        command.Parameters.Add(parameter);
                        p[name] = parameter;
                    }

                    p["$now"].Value = now;
                    p["$user"].Value = userName;
                    await command.PrepareAsync(cancellationToken).ConfigureAwait(false);
                    foreach (var d in days)
                    {
                        p["$id"].Value = Text(d.Id);
                        p["$company"].Value = Text(d.CompanyId);
                        p["$employee"].Value = Text(d.EmployeeId);
                        p["$date"].Value = d.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                        p["$shift"].Value = d.ShiftId is { } shift ? Text(shift) : DBNull.Value;
                        p["$firstIn"].Value = d.FirstIn is { } firstIn ? Text(firstIn) : DBNull.Value;
                        p["$lastOut"].Value = d.LastOut is { } lastOut ? Text(lastOut) : DBNull.Value;
                        p["$punches"].Value = d.PunchCount;
                        p["$worked"].Value = d.WorkedMinutes;
                        p["$late"].Value = d.LateMinutes;
                        p["$early"].Value = d.EarlyLeaveMinutes;
                        p["$overtime"].Value = d.OvertimeMinutes;
                        p["$status"].Value = d.Status.ToString();
                        p["$missing"].Value = d.MissingPunch ? 1 : 0;
                        p["$leaveDays"].Value = d.LeaveDays.ToString("0.0###########################", CultureInfo.InvariantCulture);
                        p["$leaveType"].Value = d.LeaveTypeId is { } leaveType ? Text(leaveType) : DBNull.Value;
                        p["$leavePaid"].Value = d.LeaveIsPaid ? 1 : 0;
                        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    }
                }

                if (outer is null)
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                if (outer is null)
                {
                    await transaction.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    // EF Core's SQLite formats: GUIDs as upper-case text, dates as ISO text.
    private static string Text(Guid value) => value.ToString().ToUpperInvariant();

    private static string Text(DateTime value) => value.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture);
}
