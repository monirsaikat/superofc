using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SuperOfc.Domain.Employees;
using SuperOfc.Domain.Loans;

namespace SuperOfc.Infrastructure.Persistence;

public partial class AppDbContext
{
    public DbSet<Loan> Loans => Set<Loan>();

    public DbSet<LoanInstallment> LoanInstallments => Set<LoanInstallment>();

    public DbSet<LoanTransaction> LoanTransactions => Set<LoanTransaction>();

    public DbSet<LoanAllocation> LoanAllocations => Set<LoanAllocation>();
}

internal sealed class LoanConfiguration : IEntityTypeConfiguration<Loan>
{
    public void Configure(EntityTypeBuilder<Loan> builder)
    {
        builder.Property(l => l.Number).HasMaxLength(20).IsRequired();
        builder.Property(l => l.AnnualRate).HasPrecision(9, 4);
        builder.Property(l => l.Purpose).HasMaxLength(300);
        builder.Property(l => l.ApprovedBy).HasMaxLength(100);
        builder.Property(l => l.DisbursementReference).HasMaxLength(100);
        builder.Property(l => l.Note).HasMaxLength(500);
        builder.Ignore(l => l.Principal);
        builder.Ignore(l => l.Interest);
        builder.Ignore(l => l.Total);
        builder.HasOne<Employee>().WithMany().HasForeignKey(l => l.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(l => l.Installments).WithOne().HasForeignKey(i => i.LoanId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(l => l.Transactions).WithOne().HasForeignKey(t => t.LoanId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(l => new { l.CompanyId, l.Number }).IsUnique();
        builder.HasIndex(l => new { l.CompanyId, l.Status });
        builder.HasIndex(l => l.EmployeeId);
    }
}

internal sealed class LoanInstallmentConfiguration : IEntityTypeConfiguration<LoanInstallment>
{
    public void Configure(EntityTypeBuilder<LoanInstallment> builder)
    {
        builder.Property(i => i.Note).HasMaxLength(300);
        builder.HasIndex(i => new { i.LoanId, i.Sequence }).IsUnique();
        builder.HasIndex(i => new { i.Year, i.Month, i.Status });
        builder.HasIndex(i => i.PayslipId);
    }
}

internal sealed class LoanTransactionConfiguration : IEntityTypeConfiguration<LoanTransaction>
{
    public void Configure(EntityTypeBuilder<LoanTransaction> builder)
    {
        builder.Property(t => t.Reference).HasMaxLength(100);
        builder.Property(t => t.Note).HasMaxLength(300);
        builder.Ignore(t => t.Amount);
        builder.HasMany(t => t.Allocations).WithOne().HasForeignKey(a => a.TransactionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(t => new { t.LoanId, t.Date });
        builder.HasIndex(t => t.PayslipId);
    }
}

internal sealed class LoanAllocationConfiguration : IEntityTypeConfiguration<LoanAllocation>
{
    public void Configure(EntityTypeBuilder<LoanAllocation> builder)
    {
        builder.HasOne<LoanInstallment>().WithMany().HasForeignKey(a => a.InstallmentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(a => a.InstallmentId);
    }
}
