using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SuperOfc.Domain.Employees;
using SuperOfc.Domain.Payroll;
using SuperOfc.Domain.Tax;

namespace SuperOfc.Infrastructure.Persistence;

public partial class AppDbContext
{
    public DbSet<TaxRuleSet> TaxRuleSets => Set<TaxRuleSet>();

    public DbSet<EmployeeTaxProfile> EmployeeTaxProfiles => Set<EmployeeTaxProfile>();

    public DbSet<TaxComputation> TaxComputations => Set<TaxComputation>();
}

internal sealed class TaxRuleSetConfiguration : IEntityTypeConfiguration<TaxRuleSet>
{
    public void Configure(EntityTypeBuilder<TaxRuleSet> builder)
    {
        builder.Property(r => r.Name).HasMaxLength(100).IsRequired();
        builder.Property(r => r.CountryCode).HasMaxLength(2).IsRequired();
        builder.Property(r => r.Note).HasMaxLength(300);
        builder.HasIndex(r => new { r.CompanyId, r.FiscalYear, r.EffectiveFrom });
    }
}

internal sealed class EmployeeTaxProfileConfiguration : IEntityTypeConfiguration<EmployeeTaxProfile>
{
    public void Configure(EntityTypeBuilder<EmployeeTaxProfile> builder)
    {
        builder.Property(p => p.Category).HasMaxLength(50).IsRequired();
        builder.Property(p => p.Note).HasMaxLength(300);
        builder.HasOne<Employee>().WithMany().HasForeignKey(p => p.EmployeeId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(p => new { p.EmployeeId, p.FiscalYear }).IsUnique();
    }
}

internal sealed class TaxComputationConfiguration : IEntityTypeConfiguration<TaxComputation>
{
    public void Configure(EntityTypeBuilder<TaxComputation> builder)
    {
        builder.HasOne<Payslip>().WithMany().HasForeignKey(c => c.PayslipId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<TaxRuleSet>().WithMany().HasForeignKey(c => c.RuleSetId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(c => c.PayslipId).IsUnique();
        builder.HasIndex(c => new { c.EmployeeId, c.FiscalYear });
    }
}
