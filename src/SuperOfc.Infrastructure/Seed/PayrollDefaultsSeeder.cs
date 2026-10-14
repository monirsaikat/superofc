using Microsoft.EntityFrameworkCore;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Payroll;
using SuperOfc.Infrastructure.Persistence;

namespace SuperOfc.Infrastructure.Seed;

/// <summary>Gives companies created before the payroll module the default salary components. Does nothing once any exist.</summary>
internal sealed class PayrollDefaultsSeeder(IIdGenerator ids) : IDataSeeder
{
    public int Order => 30;

    public async Task SeedAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var companies = await db.Companies.AsNoTracking().Where(c => c.IsSetupComplete).Select(c => c.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var companyId in companies)
        {
            if (await db.SalaryComponents.AnyAsync(c => c.CompanyId == companyId, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            var (components, structure) = PayrollDefaults.Create(companyId, ids.NewId);
            db.SalaryComponents.AddRange(components);
            db.SalaryStructures.Add(structure);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
