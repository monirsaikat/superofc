using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SuperOfc.Domain.Employees;
using SuperOfc.Domain.Letters;

namespace SuperOfc.Infrastructure.Persistence;

public partial class AppDbContext
{
    public DbSet<LetterTemplate> LetterTemplates => Set<LetterTemplate>();

    public DbSet<IssuedLetter> IssuedLetters => Set<IssuedLetter>();

    public DbSet<IssuedLetterDocument> IssuedLetterDocuments => Set<IssuedLetterDocument>();
}

internal sealed class LetterTemplateConfiguration : IEntityTypeConfiguration<LetterTemplate>
{
    public void Configure(EntityTypeBuilder<LetterTemplate> builder)
    {
        builder.Property(t => t.Name).HasMaxLength(100).IsRequired();
        builder.Property(t => t.Subject).HasMaxLength(300).IsRequired();
        builder.Property(t => t.Body).HasMaxLength(20_000).IsRequired();
        builder.Property(t => t.NumberPrefix).HasMaxLength(20).IsRequired();
        builder.Property(t => t.DocumentFileName).HasMaxLength(255);
        builder.Property(t => t.DocumentSha256).HasMaxLength(64);
        builder.HasIndex(t => new { t.CompanyId, t.Name });
    }
}

internal sealed class IssuedLetterConfiguration : IEntityTypeConfiguration<IssuedLetter>
{
    public void Configure(EntityTypeBuilder<IssuedLetter> builder)
    {
        builder.Property(l => l.TemplateName).HasMaxLength(100).IsRequired();
        builder.Property(l => l.Number).HasMaxLength(40).IsRequired();
        builder.Property(l => l.Subject).HasMaxLength(300).IsRequired();
        builder.Property(l => l.Body).HasMaxLength(20_000).IsRequired();
        builder.Property(l => l.IssuedBy).HasMaxLength(100).IsRequired();
        builder.HasOne<Employee>().WithMany().HasForeignKey(l => l.EmployeeId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<LetterTemplate>().WithMany().HasForeignKey(l => l.TemplateId).OnDelete(DeleteBehavior.SetNull);
        builder.HasIndex(l => new { l.CompanyId, l.Number }).IsUnique();
        builder.HasIndex(l => new { l.EmployeeId, l.IssuedOn });
    }
}

internal sealed class IssuedLetterDocumentConfiguration : IEntityTypeConfiguration<IssuedLetterDocument>
{
    public void Configure(EntityTypeBuilder<IssuedLetterDocument> builder)
    {
        builder.Property(d => d.Content).IsRequired();
        builder.HasOne<IssuedLetter>().WithOne().HasForeignKey<IssuedLetterDocument>(d => d.IssuedLetterId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(d => d.IssuedLetterId).IsUnique();
    }
}
