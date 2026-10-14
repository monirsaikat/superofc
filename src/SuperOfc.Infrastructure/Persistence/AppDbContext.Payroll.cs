using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SuperOfc.Domain.Employees;
using SuperOfc.Domain.Payroll;

namespace SuperOfc.Infrastructure.Persistence;

public partial class AppDbContext
{
    public DbSet<SalaryComponent> SalaryComponents => Set<SalaryComponent>();

    public DbSet<SalaryStructure> SalaryStructures => Set<SalaryStructure>();

    public DbSet<SalaryStructureLine> SalaryStructureLines => Set<SalaryStructureLine>();

    public DbSet<SalaryAssignment> SalaryAssignments => Set<SalaryAssignment>();

    public DbSet<SalaryAssignmentLine> SalaryAssignmentLines => Set<SalaryAssignmentLine>();

    public DbSet<PayrollPeriod> PayrollPeriods => Set<PayrollPeriod>();

    public DbSet<PayrollRun> PayrollRuns => Set<PayrollRun>();

    public DbSet<Payslip> Payslips => Set<Payslip>();

    public DbSet<PayslipLine> PayslipLines => Set<PayslipLine>();

    public DbSet<PayrollAdjustment> PayrollAdjustments => Set<PayrollAdjustment>();
}

internal sealed class SalaryComponentConfiguration : IEntityTypeConfiguration<SalaryComponent>
{
    public void Configure(EntityTypeBuilder<SalaryComponent> builder)
    {
        builder.Property(c => c.Code).HasMaxLength(20).IsRequired();
        builder.Property(c => c.Name).HasMaxLength(100).IsRequired();
        builder.Property(c => c.Formula).HasMaxLength(300);
        builder.Property(c => c.DefaultRate).HasPrecision(9, 4);
        builder.HasIndex(c => new { c.CompanyId, c.Code });
    }
}

internal sealed class SalaryStructureConfiguration : IEntityTypeConfiguration<SalaryStructure>
{
    public void Configure(EntityTypeBuilder<SalaryStructure> builder)
    {
        builder.Property(s => s.Name).HasMaxLength(100).IsRequired();
        builder.Property(s => s.Description).HasMaxLength(500);
        builder.HasMany(s => s.Lines).WithOne().HasForeignKey(l => l.StructureId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class SalaryStructureLineConfiguration : IEntityTypeConfiguration<SalaryStructureLine>
{
    public void Configure(EntityTypeBuilder<SalaryStructureLine> builder)
    {
        builder.ToTable(nameof(SalaryStructureLine));
        builder.Property(l => l.Id).ValueGeneratedNever();
        builder.Property(l => l.Rate).HasPrecision(9, 4);
        builder.HasOne<SalaryComponent>().WithMany().HasForeignKey(l => l.ComponentId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class SalaryAssignmentConfiguration : IEntityTypeConfiguration<SalaryAssignment>
{
    public void Configure(EntityTypeBuilder<SalaryAssignment> builder)
    {
        builder.Property(a => a.Note).HasMaxLength(500);
        builder.Ignore(a => a.Range);
        builder.HasOne<Employee>().WithMany().HasForeignKey(a => a.EmployeeId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(a => a.Lines).WithOne().HasForeignKey(l => l.AssignmentId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(a => new { a.EmployeeId, a.EffectiveFrom }).IsUnique();
    }
}

internal sealed class SalaryAssignmentLineConfiguration : IEntityTypeConfiguration<SalaryAssignmentLine>
{
    public void Configure(EntityTypeBuilder<SalaryAssignmentLine> builder)
    {
        builder.ToTable(nameof(SalaryAssignmentLine));
        builder.Property(l => l.Id).ValueGeneratedNever();
        builder.Property(l => l.Rate).HasPrecision(9, 4);
        builder.HasOne<SalaryComponent>().WithMany().HasForeignKey(l => l.ComponentId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PayrollPeriodConfiguration : IEntityTypeConfiguration<PayrollPeriod>
{
    public void Configure(EntityTypeBuilder<PayrollPeriod> builder)
    {
        builder.Ignore(p => p.Range);
        builder.Ignore(p => p.IsLocked);
        builder.HasIndex(p => new { p.CompanyId, p.Year, p.Month }).IsUnique();
    }
}

internal sealed class PayrollRunConfiguration : IEntityTypeConfiguration<PayrollRun>
{
    public void Configure(EntityTypeBuilder<PayrollRun> builder)
    {
        builder.Property(r => r.ComputedBy).HasMaxLength(100);
        builder.Property(r => r.FinalizedBy).HasMaxLength(100);
        builder.Property(r => r.VoidReason).HasMaxLength(500);
        builder.Property(r => r.PolicyJson).HasMaxLength(20000);
        builder.HasOne<PayrollPeriod>().WithMany().HasForeignKey(r => r.PeriodId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(r => new { r.PeriodId, r.RunNumber }).IsUnique();
    }
}

internal sealed class PayslipConfiguration : IEntityTypeConfiguration<Payslip>
{
    public void Configure(EntityTypeBuilder<Payslip> builder)
    {
        builder.Property(p => p.EmployeeCode).HasMaxLength(50);
        builder.Property(p => p.EmployeeName).HasMaxLength(200);
        builder.Property(p => p.Department).HasMaxLength(100);
        builder.Property(p => p.Designation).HasMaxLength(100);
        builder.Property(p => p.BankName).HasMaxLength(100);
        builder.Property(p => p.BankAccount).HasMaxLength(50);
        builder.Property(p => p.PaymentReference).HasMaxLength(100);
        builder.Property(p => p.Warnings).HasMaxLength(2000);
        foreach (var name in new[] { nameof(Payslip.PayableDays), nameof(Payslip.PresentDays), nameof(Payslip.HalfDays), nameof(Payslip.PaidLeaveDays),
                     nameof(Payslip.UnpaidLeaveDays), nameof(Payslip.AbsentDays), nameof(Payslip.LatePenaltyDays), nameof(Payslip.OvertimeHours) })
        {
            builder.Property(name).HasPrecision(9, 2);
        }

        builder.Ignore(p => p.Gross);
        builder.Ignore(p => p.Deductions);
        builder.Ignore(p => p.Net);
        builder.HasOne<PayrollRun>().WithMany().HasForeignKey(p => p.RunId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Employee>().WithMany().HasForeignKey(p => p.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(p => p.Lines).WithOne().HasForeignKey(l => l.PayslipId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(p => new { p.RunId, p.EmployeeId }).IsUnique();
        builder.HasIndex(p => p.EmployeeId);
    }
}

internal sealed class PayslipLineConfiguration : IEntityTypeConfiguration<PayslipLine>
{
    public void Configure(EntityTypeBuilder<PayslipLine> builder)
    {
        builder.ToTable(nameof(PayslipLine));
        builder.Property(l => l.Id).ValueGeneratedNever();
        builder.Property(l => l.Code).HasMaxLength(20);
        builder.Property(l => l.Name).HasMaxLength(100);
        builder.Property(l => l.Basis).HasMaxLength(300);
        builder.Property(l => l.Quantity).HasPrecision(9, 2);
        builder.Ignore(l => l.Amount);
        builder.HasIndex(l => l.ReferenceId);
    }
}

internal sealed class PayrollAdjustmentConfiguration : IEntityTypeConfiguration<PayrollAdjustment>
{
    public void Configure(EntityTypeBuilder<PayrollAdjustment> builder)
    {
        builder.Property(a => a.Name).HasMaxLength(100).IsRequired();
        builder.Property(a => a.Reason).HasMaxLength(300);
        builder.HasOne<Employee>().WithMany().HasForeignKey(a => a.EmployeeId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(a => new { a.CompanyId, a.Year, a.Month });
    }
}
