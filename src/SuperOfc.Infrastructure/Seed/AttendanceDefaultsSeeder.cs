using Microsoft.EntityFrameworkCore;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Attendance;
using SuperOfc.Infrastructure.Persistence;

namespace SuperOfc.Infrastructure.Seed;

/// <summary>
/// Gives companies created before the attendance module a default shift and leave types.
/// Runs at every start; does nothing once a company has any shift or leave type (even deleted ones).
/// </summary>
internal sealed class AttendanceDefaultsSeeder(IIdGenerator ids) : IDataSeeder
{
    public int Order => 20;

    public async Task SeedAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var companies = await db.Companies.AsNoTracking().Where(c => c.IsSetupComplete).Select(c => c.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var companyId in companies)
        {
            if (!await db.Shifts.IgnoreQueryFilters().AnyAsync(s => s.CompanyId == companyId, cancellationToken).ConfigureAwait(false))
            {
                db.Shifts.Add(AttendanceDefaults.GeneralShift(companyId, ids.NewId()));
            }

            if (!await db.LeaveTypes.IgnoreQueryFilters().AnyAsync(t => t.CompanyId == companyId, cancellationToken).ConfigureAwait(false))
            {
                db.LeaveTypes.AddRange(AttendanceDefaults.LeaveTypes(companyId, ids.NewId));
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
