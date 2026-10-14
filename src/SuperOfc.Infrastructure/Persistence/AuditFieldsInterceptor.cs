using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SuperOfc.Application.Abstractions;
using SuperOfc.Domain.Common;

namespace SuperOfc.Infrastructure.Persistence;

/// <summary>Fills audit fields and bumps the row version on every save.</summary>
public sealed class AuditFieldsInterceptor(IClock clock, ICurrentUser currentUser) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Apply(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Apply(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Apply(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var now = clock.UtcNow;
        var user = currentUser.IsAuthenticated ? currentUser.UserName : "system";
        foreach (var entry in context.ChangeTracker.Entries<Entity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.CreatedAtUtc = now;
                    entry.Entity.CreatedBy = user;
                    entry.Entity.RowVersion = 1;
                    if (entry.Entity is CompanyEntity { CompanyId: var companyId } companyEntity && companyId == Guid.Empty)
                    {
                        companyEntity.CompanyId = currentUser.CompanyId;
                    }

                    break;
                case EntityState.Modified:
                    entry.Entity.ModifiedAtUtc = now;
                    entry.Entity.ModifiedBy = user;
                    entry.Entity.RowVersion++;
                    break;
            }
        }
    }
}
