using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SuperOfc.Domain.Employees;
using SuperOfc.Domain.Organization;

namespace SuperOfc.Infrastructure.Persistence;

public partial class AppDbContext
{
    public DbSet<Department> Departments => Set<Department>();

    public DbSet<Designation> Designations => Set<Designation>();

    public DbSet<Location> Locations => Set<Location>();

    public DbSet<Grade> Grades => Set<Grade>();

    public DbSet<Holiday> Holidays => Set<Holiday>();

    public DbSet<Employee> Employees => Set<Employee>();

    public DbSet<EmploymentEvent> EmploymentEvents => Set<EmploymentEvent>();

    public DbSet<EmployeeDocument> EmployeeDocuments => Set<EmployeeDocument>();
}

internal abstract class MasterDataConfiguration<T> : IEntityTypeConfiguration<T>
    where T : MasterDataEntity
{
    public void Configure(EntityTypeBuilder<T> builder)
    {
        builder.Property(m => m.Name).HasMaxLength(100).IsRequired();
        builder.Property(m => m.Code).HasMaxLength(20);
        builder.Property(m => m.Description).HasMaxLength(500);
        builder.Ignore(m => m.Kind);
        builder.HasIndex(m => new { m.CompanyId, m.Name });
    }
}

internal sealed class DepartmentConfiguration : MasterDataConfiguration<Department>;

internal sealed class DesignationConfiguration : MasterDataConfiguration<Designation>;

internal sealed class LocationConfiguration : MasterDataConfiguration<Location>;

internal sealed class GradeConfiguration : MasterDataConfiguration<Grade>;

internal sealed class HolidayConfiguration : IEntityTypeConfiguration<Holiday>
{
    public void Configure(EntityTypeBuilder<Holiday> builder)
    {
        builder.Property(h => h.Name).HasMaxLength(100).IsRequired();
        builder.HasIndex(h => new { h.CompanyId, h.Date });
        builder.HasOne<Location>().WithMany().HasForeignKey(h => h.LocationId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class EmployeeConfiguration : IEntityTypeConfiguration<Employee>
{
    public void Configure(EntityTypeBuilder<Employee> builder)
    {
        builder.Property(e => e.Code).HasMaxLength(20).IsRequired().UseCollation("NOCASE");
        builder.HasIndex(e => new { e.CompanyId, e.Code }).IsUnique();
        builder.Property(e => e.FullName).HasMaxLength(150).IsRequired().UseCollation("NOCASE");
        builder.HasIndex(e => e.FullName);
        builder.HasIndex(e => e.Status);
        builder.Property(e => e.Phone).HasMaxLength(30);
        builder.Property(e => e.Email).HasMaxLength(150);
        builder.Property(e => e.NationalId).HasMaxLength(50);
        builder.Property(e => e.FatherName).HasMaxLength(200);
        builder.Property(e => e.BankAccount).HasMaxLength(50);
        builder.Property(e => e.TaxIdentifier).HasMaxLength(50);
        builder.Property(e => e.CustomFieldsJson).HasMaxLength(int.MaxValue);
        builder.HasOne<Department>().WithMany().HasForeignKey(e => e.DepartmentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Designation>().WithMany().HasForeignKey(e => e.DesignationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Location>().WithMany().HasForeignKey(e => e.LocationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Grade>().WithMany().HasForeignKey(e => e.GradeId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class EmploymentEventConfiguration : IEntityTypeConfiguration<EmploymentEvent>
{
    public void Configure(EntityTypeBuilder<EmploymentEvent> builder)
    {
        builder.Property(e => e.Description).HasMaxLength(1000);
        builder.HasOne<Employee>().WithMany().HasForeignKey(e => e.EmployeeId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(e => new { e.EmployeeId, e.Date });
    }
}

internal sealed class EmployeeDocumentConfiguration : IEntityTypeConfiguration<EmployeeDocument>
{
    public void Configure(EntityTypeBuilder<EmployeeDocument> builder)
    {
        builder.Property(d => d.Title).HasMaxLength(150);
        builder.Property(d => d.FilePath).HasMaxLength(500);
        builder.Property(d => d.FileName).HasMaxLength(255);
        builder.HasOne<Employee>().WithMany().HasForeignKey(d => d.EmployeeId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(d => d.ExpiryDate);
    }
}
