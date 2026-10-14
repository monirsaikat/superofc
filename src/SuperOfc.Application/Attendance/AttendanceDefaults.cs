using SuperOfc.Domain.Attendance;
using SuperOfc.Domain.Leave;

namespace SuperOfc.Application.Attendance;

/// <summary>Reference data every company starts with: a default shift and common leave types (editable later).</summary>
public static class AttendanceDefaults
{
    public static Shift GeneralShift(Guid companyId, Guid id) => new()
    {
        Id = id,
        CompanyId = companyId,
        Name = "General",
        Code = "GEN",
        StartTime = new TimeOnly(9, 0),
        EndTime = new TimeOnly(18, 0),
        GraceInMinutes = 10,
        BreakMinutes = 60,
        MinimumMinutesForHalfDay = 240,
        MinimumMinutesForFullDay = 420,
        IsDefault = true,
    };

    public static IEnumerable<LeaveType> LeaveTypes(Guid companyId, Func<Guid> newId)
    {
        ArgumentNullException.ThrowIfNull(newId);
        yield return new LeaveType { Id = newId(), CompanyId = companyId, Name = "Casual leave", Code = "CL", YearlyEntitlement = 10, Accrual = LeaveAccrual.Yearly, SortOrder = 0 };
        yield return new LeaveType { Id = newId(), CompanyId = companyId, Name = "Sick leave", Code = "SL", YearlyEntitlement = 14, Accrual = LeaveAccrual.Yearly, SortOrder = 1 };
        yield return new LeaveType { Id = newId(), CompanyId = companyId, Name = "Earned leave", Code = "EL", YearlyEntitlement = 18, Accrual = LeaveAccrual.Monthly, CarryForwardCap = 60, AllowsHalfDay = false, SortOrder = 2 };
        yield return new LeaveType { Id = newId(), CompanyId = companyId, Name = "Unpaid leave", Code = "LWP", IsPaid = false, AllowNegativeBalance = true, SortOrder = 3 };
    }
}
