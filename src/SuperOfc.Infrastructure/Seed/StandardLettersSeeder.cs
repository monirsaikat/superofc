using Microsoft.EntityFrameworkCore;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Letters;
using SuperOfc.Infrastructure.Persistence;

namespace SuperOfc.Infrastructure.Seed;

/// <summary>
/// Brings existing companies' letter templates up to the standard set (ADR-24): untouched built-in text versions of the
/// salary and experience certificates become the company's Word letters, and missing Word letters are added. Idempotent:
/// a Word letter provided once is recognised by its file name, even after it is edited or deleted.
/// </summary>
internal sealed class StandardLettersSeeder(IIdGenerator ids, IWordLetterService word) : IDataSeeder
{
    public int Order => 40;

    public async Task SeedAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var companies = await db.Companies.AsNoTracking().Where(c => c.IsSetupComplete).Select(c => c.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var companyId in companies)
        {
            var existing = await db.LetterTemplates.IgnoreQueryFilters().Where(t => t.CompanyId == companyId).ToListAsync(cancellationToken).ConfigureAwait(false);
            db.LetterTemplates.AddRange(LetterDefaults.Upgrade(existing, companyId, ids.NewId, word));
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
