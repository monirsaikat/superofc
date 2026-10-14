using Microsoft.EntityFrameworkCore;
using SuperOfc.Application.Abstractions;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Employees;
using SuperOfc.Domain.Leave;
using SuperOfc.Domain.Organization;

namespace SuperOfc.Application.Leave;

/// <summary>
/// Leave balances (doc 03, R6). Accrual is calculated from the leave type and the date, so a balance is always
/// current when it is read or changed; there is no separate accrual job to forget. The leave year follows the
/// company fiscal year.
/// </summary>
public sealed class LeaveLedger(IAppDbContext db, ICurrentUser user, IIdGenerator ids, IClock clock, ICompanyContext companyContext)
{
    public DateOnly Today => clock.TodayIn(companyContext.TimeZone);

    public static int LeaveYear(Company company, DateOnly date) => company.FiscalYearStart(date).Year;

    public static DateOnly YearStart(Company company, int year) => new(year, company.FiscalYearStartMonth, 1);

    public Task<Company> CompanyAsync(CancellationToken cancellationToken)
        => db.Companies.AsNoTracking().FirstAsync(c => c.Id == user.CompanyId, cancellationToken);

    public async Task<WorkCalendar> CalendarAsync(Company company, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var holidays = await db.Holidays.AsNoTracking().Where(h => h.CompanyId == company.Id && h.Date >= from && h.Date <= to)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return new WorkCalendar(company.WorkingWeek, holidays);
    }

    /// <summary>
    /// Returns the tracked balance row for a year, creating it when missing (opening = capped carry-forward) and
    /// refreshing the accrued amount up to today.
    /// </summary>
    public async Task<LeaveBalance> GetForUpdateAsync(Company company, Employee employee, LeaveType type, int year, CancellationToken cancellationToken)
    {
        var balance = await db.LeaveBalances.FirstOrDefaultAsync(
            b => b.EmployeeId == employee.Id && b.LeaveTypeId == type.Id && b.Year == year, cancellationToken).ConfigureAwait(false);
        if (balance is null)
        {
            balance = new LeaveBalance
            {
                Id = ids.NewId(),
                CompanyId = user.CompanyId,
                EmployeeId = employee.Id,
                LeaveTypeId = type.Id,
                Year = year,
                Opening = await CarryForwardAsync(company, employee, type, year, cancellationToken).ConfigureAwait(false),
            };
            db.LeaveBalances.Add(balance);
        }

        balance.Accrued = type.AccruedBy(YearStart(company, year), Today, employee.JoinDate);
        return balance;
    }

    /// <summary>Balance values without saving anything (for display).</summary>
    public async Task<LeaveBalance> ProjectAsync(Company company, Employee employee, LeaveType type, int year, CancellationToken cancellationToken)
    {
        var stored = await db.LeaveBalances.AsNoTracking().FirstOrDefaultAsync(
            b => b.EmployeeId == employee.Id && b.LeaveTypeId == type.Id && b.Year == year, cancellationToken).ConfigureAwait(false);
        var balance = stored ?? new LeaveBalance
        {
            EmployeeId = employee.Id,
            LeaveTypeId = type.Id,
            Year = year,
            Opening = await CarryForwardAsync(company, employee, type, year, cancellationToken).ConfigureAwait(false),
        };
        balance.Accrued = type.AccruedBy(YearStart(company, year), Today, employee.JoinDate);
        return balance;
    }

    /// <summary>Working days of a request split by leave year (a request may cross the year boundary).</summary>
    public static Dictionary<int, decimal> DaysByYear(Company company, LeaveRequest request, WorkCalendar calendar, Guid? locationId)
    {
        var result = new Dictionary<int, decimal>();
        foreach (var date in request.Range.EachDay())
        {
            var days = request.DaysOn(date, calendar, locationId);
            if (days > 0)
            {
                var year = LeaveYear(company, date);
                result[year] = result.GetValueOrDefault(year) + days;
            }
        }

        return result;
    }

    private async Task<decimal> CarryForwardAsync(Company company, Employee employee, LeaveType type, int year, CancellationToken cancellationToken)
    {
        if (type.CarryForwardCap <= 0)
        {
            return 0m;
        }

        var previous = await db.LeaveBalances.AsNoTracking().FirstOrDefaultAsync(
            b => b.EmployeeId == employee.Id && b.LeaveTypeId == type.Id && b.Year == year - 1, cancellationToken).ConfigureAwait(false);
        if (previous is null)
        {
            return 0m;
        }

        // The previous year is over: its accrual is complete.
        var previousStart = YearStart(company, year - 1);
        previous.Accrued = type.AccruedBy(previousStart, previousStart.AddYears(1).AddDays(-1), employee.JoinDate);
        return Math.Clamp(previous.Closing, 0m, type.CarryForwardCap);
    }
}
