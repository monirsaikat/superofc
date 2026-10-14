using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SuperOfc.Domain.Attendance;
using SuperOfc.Domain.Employees;
using SuperOfc.Domain.Leave;
using SuperOfc.Infrastructure.Persistence.Converters;

namespace SuperOfc.Infrastructure.Persistence;

public partial class AppDbContext
{
    public DbSet<Shift> Shifts => Set<Shift>();

    public DbSet<ShiftAssignment> ShiftAssignments => Set<ShiftAssignment>();

    public DbSet<AttendancePunch> AttendancePunches => Set<AttendancePunch>();

    public DbSet<AttendanceDay> AttendanceDays => Set<AttendanceDay>();

    public DbSet<AttendanceCorrection> AttendanceCorrections => Set<AttendanceCorrection>();

    public DbSet<LeaveType> LeaveTypes => Set<LeaveType>();

    public DbSet<LeaveBalance> LeaveBalances => Set<LeaveBalance>();

    public DbSet<LeaveRequest> LeaveRequests => Set<LeaveRequest>();
}

internal sealed class ShiftConfiguration : IEntityTypeConfiguration<Shift>
{
    public void Configure(EntityTypeBuilder<Shift> builder)
    {
        builder.Property(s => s.Name).HasMaxLength(100).IsRequired();
        builder.Property(s => s.Code).HasMaxLength(20);
        builder.HasIndex(s => new { s.CompanyId, s.Name });
    }
}

internal sealed class ShiftAssignmentConfiguration : IEntityTypeConfiguration<ShiftAssignment>
{
    public void Configure(EntityTypeBuilder<ShiftAssignment> builder)
    {
        builder.Property(a => a.RotationShiftIds).HasMaxLength(1000);
        builder.HasOne<Employee>().WithMany().HasForeignKey(a => a.EmployeeId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Shift>().WithMany().HasForeignKey(a => a.ShiftId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(a => new { a.EmployeeId, a.EffectiveFrom });
    }
}

internal sealed class AttendancePunchConfiguration : IEntityTypeConfiguration<AttendancePunch>
{
    public void Configure(EntityTypeBuilder<AttendancePunch> builder)
    {
        builder.Property(p => p.LocalTime).HasConversion<LocalDateTimeConverter>();
        builder.Property(p => p.DedupKey).HasMaxLength(200).IsRequired();
        builder.HasIndex(p => p.DedupKey).IsUnique();
        builder.Property(p => p.DeviceUserId).HasMaxLength(50);
        builder.Property(p => p.Reason).HasMaxLength(300);
        builder.HasOne<Employee>().WithMany().HasForeignKey(p => p.EmployeeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(p => new { p.EmployeeId, p.LocalTime });
        builder.HasIndex(p => new { p.CompanyId, p.LocalTime });
    }
}

internal sealed class AttendanceDayConfiguration : IEntityTypeConfiguration<AttendanceDay>
{
    public void Configure(EntityTypeBuilder<AttendanceDay> builder)
    {
        builder.Property(d => d.FirstIn).HasConversion<NullableLocalDateTimeConverter>();
        builder.Property(d => d.LastOut).HasConversion<NullableLocalDateTimeConverter>();
        builder.Property(d => d.CorrectionNote).HasMaxLength(300);
        builder.Property(d => d.LeaveDays).HasPrecision(4, 1);
        builder.HasOne<Employee>().WithMany().HasForeignKey(d => d.EmployeeId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(d => new { d.EmployeeId, d.Date }).IsUnique();
        builder.HasIndex(d => new { d.CompanyId, d.Date });
    }
}

internal sealed class AttendanceCorrectionConfiguration : IEntityTypeConfiguration<AttendanceCorrection>
{
    public void Configure(EntityTypeBuilder<AttendanceCorrection> builder)
    {
        builder.Property(c => c.Reason).HasMaxLength(300);
        builder.HasOne<Employee>().WithMany().HasForeignKey(c => c.EmployeeId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(c => new { c.EmployeeId, c.Date });
    }
}

internal sealed class LeaveTypeConfiguration : IEntityTypeConfiguration<LeaveType>
{
    public void Configure(EntityTypeBuilder<LeaveType> builder)
    {
        builder.Property(t => t.Name).HasMaxLength(100).IsRequired();
        builder.Property(t => t.Code).HasMaxLength(10).IsRequired();
        builder.Property(t => t.YearlyEntitlement).HasPrecision(6, 1);
        builder.Property(t => t.CarryForwardCap).HasPrecision(6, 1);
        builder.HasIndex(t => new { t.CompanyId, t.Code });
    }
}

internal sealed class LeaveBalanceConfiguration : IEntityTypeConfiguration<LeaveBalance>
{
    public void Configure(EntityTypeBuilder<LeaveBalance> builder)
    {
        builder.Property(b => b.Opening).HasPrecision(6, 1);
        builder.Property(b => b.Accrued).HasPrecision(6, 1);
        builder.Property(b => b.Used).HasPrecision(6, 1);
        builder.Property(b => b.Adjusted).HasPrecision(6, 1);
        builder.Ignore(b => b.Closing);
        builder.HasOne<Employee>().WithMany().HasForeignKey(b => b.EmployeeId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<LeaveType>().WithMany().HasForeignKey(b => b.LeaveTypeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(b => new { b.EmployeeId, b.LeaveTypeId, b.Year }).IsUnique();
    }
}

internal sealed class LeaveRequestConfiguration : IEntityTypeConfiguration<LeaveRequest>
{
    public void Configure(EntityTypeBuilder<LeaveRequest> builder)
    {
        builder.Property(r => r.Days).HasPrecision(6, 1);
        builder.Property(r => r.Reason).HasMaxLength(500);
        builder.Property(r => r.DecisionNote).HasMaxLength(500);
        builder.Property(r => r.DecidedBy).HasMaxLength(100);
        builder.Property(r => r.AttachmentPath).HasMaxLength(500);
        builder.Ignore(r => r.Range);
        builder.HasOne<Employee>().WithMany().HasForeignKey(r => r.EmployeeId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<LeaveType>().WithMany().HasForeignKey(r => r.LeaveTypeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(r => new { r.EmployeeId, r.FromDate });
        builder.HasIndex(r => new { r.CompanyId, r.Status });
    }
}
