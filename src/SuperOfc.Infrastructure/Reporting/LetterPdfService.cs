using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SuperOfc.Application.Letters;
using SuperOfc.Application.Payroll;
using SuperOfc.Domain.Letters;

namespace SuperOfc.Infrastructure.Reporting;

/// <summary>HR letters on the company letterhead: reference and date, subject, paragraphs with bold runs. One letter per page.</summary>
public sealed class LetterPdfService : ILetterPdfService
{
    public void Write(PayslipCompany company, IReadOnlyList<LetterDocument> letters, string path)
    {
        ArgumentNullException.ThrowIfNull(company);
        ArgumentNullException.ThrowIfNull(letters);
        PdfStyles.EnsureLicense();
        var folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (folder is not null)
        {
            Directory.CreateDirectory(folder);
        }

        Document.Create(container =>
        {
            foreach (var letter in letters)
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.MarginHorizontal(56);
                    page.MarginVertical(40);
                    page.DefaultTextStyle(t => t.FontSize(11).LineHeight(1.35f));
                    page.Header().Column(col =>
                    {
                        col.Item().Row(row =>
                        {
                            if (company.LogoFullPath is { } logo && File.Exists(logo))
                            {
                                row.ConstantItem(48).Height(48).Image(logo).FitArea();
                                row.ConstantItem(12);
                            }

                            row.RelativeItem().Column(c =>
                            {
                                c.Item().Text(company.Name).FontSize(16).Bold().FontColor(PdfStyles.Accent);
                                var contact = string.Join("  ·  ", new[] { company.Address, company.Phone, company.Email }.Where(x => !string.IsNullOrWhiteSpace(x)));
                                if (contact.Length > 0)
                                {
                                    c.Item().Text(contact).FontSize(9).FontColor(PdfStyles.Muted);
                                }
                            });
                        });
                        col.Item().PaddingTop(6).LineHorizontal(1).LineColor(PdfStyles.Accent);
                    });

                    page.Content().PaddingTop(18).Column(col =>
                    {
                        col.Spacing(10);
                        col.Item().Row(row =>
                        {
                            row.RelativeItem().Text(t =>
                            {
                                t.Span("Ref: ").FontColor(PdfStyles.Muted);
                                t.Span(letter.Number);
                            });
                            row.RelativeItem().AlignRight().Text(letter.Date.ToString(company.DateFormat, CultureInfo.CurrentCulture));
                        });
                        col.Item().PaddingTop(6).Text(letter.Subject).Bold().FontSize(12.5f).Underline();
                        foreach (var paragraph in LetterText.Paragraphs(letter.Body))
                        {
                            col.Item().Text(t =>
                            {
                                foreach (var (text, bold) in paragraph)
                                {
                                    var span = t.Span(text);
                                    if (bold)
                                    {
                                        span.Bold();
                                    }
                                }
                            });
                        }
                    });

                    page.Footer().AlignCenter().Text(letter.Number).FontSize(8).FontColor(PdfStyles.Muted);
                });
            }
        })
        .WithMetadata(new DocumentMetadata { Title = letters.Count == 1 ? $"{letters[0].Subject} {letters[0].Number}" : "Letters", Author = company.Name, Creator = "SuperOfc" })
        .GeneratePdf(path);
    }
}
