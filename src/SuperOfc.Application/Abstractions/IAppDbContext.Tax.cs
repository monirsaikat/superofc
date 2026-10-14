using Microsoft.EntityFrameworkCore;
using SuperOfc.Domain.Tax;

namespace SuperOfc.Application.Abstractions;

public partial interface IAppDbContext
{
    DbSet<TaxRuleSet> TaxRuleSets { get; }

    DbSet<EmployeeTaxProfile> EmployeeTaxProfiles { get; }

    DbSet<TaxComputation> TaxComputations { get; }
}
