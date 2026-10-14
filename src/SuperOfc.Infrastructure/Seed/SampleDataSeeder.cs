using Microsoft.Extensions.Logging;
using SuperOfc.Application.Abstractions;
using SuperOfc.Infrastructure.Persistence;

namespace SuperOfc.Infrastructure.Seed;

/// <summary>
/// Loads demo data for evaluation. Each module adds its part in a partial file. Idempotent: modules check
/// for existing rows before inserting.
/// </summary>
public sealed partial class SampleDataSeeder(AppDbContext db, IIdGenerator ids, IClock clock, ILogger<SampleDataSeeder> logger) : ISampleDataSeeder
{
    public async Task SeedAsync(Guid companyId, CancellationToken cancellationToken = default)
    {
        foreach (var step in Steps())
        {
            await step(companyId, cancellationToken).ConfigureAwait(false);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        logger.LogInformation("Sample data loaded for company {CompanyId}", companyId);
    }

    /// <summary>Seeding steps in dependency order; each module adds its own.</summary>
    private IEnumerable<Func<Guid, CancellationToken, Task>> Steps()
    {
        yield return SeedOrganizationAsync;
        yield return SeedAttendanceAsync;
    }

    private DateTime UtcNow => clock.UtcNow;

    private Guid NewId() => ids.NewId();
}
