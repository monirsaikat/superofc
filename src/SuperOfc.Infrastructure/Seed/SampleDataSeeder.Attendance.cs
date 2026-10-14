using Microsoft.EntityFrameworkCore;
using SuperOfc.Domain.Attendance;
using SuperOfc.Domain.Leave;
using SuperOfc.Domain.Organization;

namespace SuperOfc.Infrastructure.Seed;

public sealed partial class SampleDataSeeder
{
    /// <summary>A night shift, six weeks of punches with realistic variation, and a few leave requests.</summary>
    private async Task SeedAttendanceAsync(Guid companyId, CancellationToken ct)
    {
        if (await db.AttendancePunches.AnyAsync(p => p.CompanyId == companyId, ct).ConfigureAwait(false))
        {
            return;
        }

        var company = await db.Companies.AsNoTracking().FirstAsync(c => c.Id == companyId, ct).ConfigureAwait(false);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(company.TimeZoneId);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(UtcNow, zone));

        if (!await db.Shifts.AnyAsync(s => s.CompanyId == companyId && s.Name == "Night", ct).ConfigureAwait(false))
        {
            db.Shifts.Add(new Shift
            {
                Id = NewId(), CompanyId = companyId, Name = "Night", Code = "NGT", StartTime = new TimeOnly(22, 0), EndTime = new TimeOnly(6, 0),
                GraceInMinutes = 10, BreakMinutes = 30, MinimumMinutesForHalfDay = 210, MinimumMinutesForFullDay = 390,
            });
        }

        var employees = await db.Employees.AsNoTracking().Where(e => e.CompanyId == companyId && e.ExitDate == null)
            .Select(e => new { e.Id, e.Code, e.JoinDate, e.LocationId }).ToListAsync(ct).ConfigureAwait(false);
        var holidays = await db.Holidays.AsNoTracking().Where(h => h.CompanyId == companyId).ToListAsync(ct).ConfigureAwait(false);
        var calendar = new WorkCalendar(company.WorkingWeek, holidays);
        var random = new Random(7);
        var start = today.AddDays(-42);

        foreach (var e in employees)
        {
            // Each person has a habit: some are always early, a few are often late.
            var habit = random.Next(-15, 20);
            for (var date = start; date < today; date = date.AddDays(1))
            {
                if (date < e.JoinDate || calendar.GetDayKind(date, e.LocationId) is DayKind.WeeklyOff or DayKind.Holiday)
                {
                    continue;
                }

                var roll = random.NextDouble();
                if (roll < 0.05)
                {
                    continue; // absent
                }

                var inAt = date.ToDateTime(new TimeOnly(9, 0)).AddMinutes(habit + random.Next(-10, 15));
                AddPunch(companyId, e.Id, e.Code, inAt, zone);
                if (roll < 0.08)
                {
                    continue; // forgot to punch out
                }

                var outAt = date.ToDateTime(new TimeOnly(18, 0)).AddMinutes(random.Next(-20, 110));
                AddPunch(companyId, e.Id, e.Code, outAt, zone);
            }
        }

        // Leave: two approved in the past and two waiting for approval.
        var types = await db.LeaveTypes.AsNoTracking().Where(t => t.CompanyId == companyId && t.DeletedAtUtc == null).ToListAsync(ct).ConfigureAwait(false);
        var casual = types.FirstOrDefault(t => t.Code == "CL");
        var sick = types.FirstOrDefault(t => t.Code == "SL");
        if (casual is not null && sick is not null && employees.Count >= 4)
        {
            AddLeave(companyId, employees[0].Id, sick, NextWorkingDay(calendar, today.AddDays(-20)), 1, LeaveRequestStatus.Approved, "Fever", company, calendar);
            AddLeave(companyId, employees[1].Id, casual, NextWorkingDay(calendar, today.AddDays(-10)), 2, LeaveRequestStatus.Approved, "Family event", company, calendar);
            AddLeave(companyId, employees[2].Id, casual, NextWorkingDay(calendar, today.AddDays(5)), 1, LeaveRequestStatus.Pending, "Personal work", company, calendar);
            AddLeave(companyId, employees[3].Id, sick, NextWorkingDay(calendar, today.AddDays(2)), 1, LeaveRequestStatus.Pending, "Doctor appointment", company, calendar);
        }
    }

    private void AddPunch(Guid companyId, Guid employeeId, string code, DateTime local, TimeZoneInfo zone)
        => db.AttendancePunches.Add(new AttendancePunch
        {
            Id = NewId(),
            CompanyId = companyId,
            EmployeeId = employeeId,
            LocalTime = local,
            PunchAtUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone),
            Source = PunchSource.Device,
            DeviceUserId = code,
            DedupKey = AttendancePunch.DeviceKey("SAMPLE", code, local),
        });

    private void AddLeave(Guid companyId, Guid employeeId, LeaveType type, DateOnly from, int workingDays, LeaveRequestStatus status, string reason, Company company, WorkCalendar calendar)
    {
        var to = from;
        for (var counted = 1; counted < workingDays;)
        {
            to = to.AddDays(1);
            if (calendar.GetDayKind(to) is DayKind.Working)
            {
                counted++;
            }
        }

        var days = LeaveRequest.CountDays(from, to, false, calendar, null);
        db.LeaveRequests.Add(new LeaveRequest
        {
            Id = NewId(), CompanyId = companyId, EmployeeId = employeeId, LeaveTypeId = type.Id, FromDate = from, ToDate = to, Days = days,
            Status = status, Reason = reason, DecidedBy = status == LeaveRequestStatus.Approved ? "sample" : null,
            DecidedAtUtc = status == LeaveRequestStatus.Approved ? UtcNow : null,
        });

        if (status == LeaveRequestStatus.Approved)
        {
            db.LeaveBalances.Add(new LeaveBalance
            {
                Id = NewId(), CompanyId = companyId, EmployeeId = employeeId, LeaveTypeId = type.Id,
                Year = company.FiscalYearStart(from).Year, Used = days,
            });
        }
    }

    private static DateOnly NextWorkingDay(WorkCalendar calendar, DateOnly date)
    {
        while (calendar.GetDayKind(date) is not DayKind.Working)
        {
            date = date.AddDays(1);
        }

        return date;
    }
}
