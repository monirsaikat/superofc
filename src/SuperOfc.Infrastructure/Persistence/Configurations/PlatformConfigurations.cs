using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SuperOfc.Domain.Organization;
using SuperOfc.Domain.Platform;
using SuperOfc.Domain.Security;

namespace SuperOfc.Infrastructure.Persistence.Configurations;

internal sealed class CompanyConfiguration : IEntityTypeConfiguration<Company>
{
    public void Configure(EntityTypeBuilder<Company> builder)
    {
        builder.Property(c => c.Name).HasMaxLength(200).IsRequired();
        builder.Property(c => c.CurrencyCode).HasMaxLength(3);
        builder.Property(c => c.CurrencySymbol).HasMaxLength(5);
        builder.Property(c => c.TimeZoneId).HasMaxLength(100);
        builder.Property(c => c.DateFormat).HasMaxLength(40);
        builder.Property(c => c.SignatoryName).HasMaxLength(150);
        builder.Property(c => c.SignatoryTitle).HasMaxLength(100);
        builder.ComplexProperty(c => c.WorkingWeek);
    }
}

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.Property(u => u.Username).HasMaxLength(50).IsRequired().UseCollation("NOCASE");
        builder.HasIndex(u => u.Username).IsUnique();
        builder.Property(u => u.DisplayName).HasMaxLength(100);
        builder.Property(u => u.PasswordHash).HasMaxLength(500);
    }
}

internal sealed class SignInTokenConfiguration : IEntityTypeConfiguration<SignInToken>
{
    public void Configure(EntityTypeBuilder<SignInToken> builder)
    {
        builder.Property(t => t.TokenHash).HasMaxLength(64).IsRequired();
        builder.HasIndex(t => t.TokenHash).IsUnique();
        builder.HasIndex(t => t.UserId);
        builder.HasOne<User>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public void Configure(EntityTypeBuilder<AuditEntry> builder)
    {
        builder.Property(a => a.Action).HasMaxLength(100);
        builder.Property(a => a.EntityType).HasMaxLength(100);
        builder.Property(a => a.UserName).HasMaxLength(100);
        builder.Property(a => a.Summary).HasMaxLength(1000);
        builder.Property(a => a.BeforeJson).HasMaxLength(int.MaxValue);
        builder.Property(a => a.AfterJson).HasMaxLength(int.MaxValue);
        builder.HasIndex(a => a.AtUtc);
        builder.HasIndex(a => a.EntityId);
        builder.HasIndex(a => a.Action);
    }
}

internal sealed class SettingConfiguration : IEntityTypeConfiguration<Setting>
{
    public void Configure(EntityTypeBuilder<Setting> builder)
    {
        builder.Property(s => s.Key).HasMaxLength(200);
        builder.Property(s => s.ValueJson).HasMaxLength(int.MaxValue);
        builder.HasIndex(s => new { s.Key, s.CompanyId }).IsUnique();
    }
}

internal sealed class PolicyVersionConfiguration : IEntityTypeConfiguration<PolicyVersion>
{
    public void Configure(EntityTypeBuilder<PolicyVersion> builder)
    {
        builder.Property(p => p.Section).HasMaxLength(50);
        builder.Property(p => p.Json).HasMaxLength(int.MaxValue);
        builder.HasIndex(p => new { p.CompanyId, p.Section, p.EffectiveFrom }).IsUnique();
    }
}

internal sealed class BackupRecordConfiguration : IEntityTypeConfiguration<BackupRecord>
{
    public void Configure(EntityTypeBuilder<BackupRecord> builder)
    {
        builder.Property(b => b.FilePath).HasMaxLength(1000);
        builder.Property(b => b.AppVersion).HasMaxLength(50);
        builder.HasIndex(b => b.CreatedAtUtc);
    }
}

internal sealed class SchemaInfoConfiguration : IEntityTypeConfiguration<SchemaInfo>
{
    public void Configure(EntityTypeBuilder<SchemaInfo> builder)
    {
        builder.Property(s => s.AppVersion).HasMaxLength(50);
        builder.Property(s => s.LastMigration).HasMaxLength(200);
    }
}
