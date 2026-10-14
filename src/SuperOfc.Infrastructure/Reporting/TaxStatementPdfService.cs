using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SuperOfc.Application.Payroll;
using SuperOfc.Application.Tax;

namespace SuperOfc.Infrastructure.Reporting;

/// <summary>Annual tax statement for one employee (P6-T3): month by month taxable pay and tax deducted, with totals.</summary>
public sealed class TaxStatementPdfService : ITaxStatementPdfService
{
    private const string Accent = PdfStyles.Accent;
    private const string Muted = PdfStyles.Muted;
    private const string Line = PdfStyles.Line;

    static TaxStatementPdfService()
    {
        PdfStyles.EnsureLicense();
    }

    public void Write(PayslipCompany company, TaxStatement statement, string path)
    {
        ArgumentNullException.ThrowIfNull(company);
        ArgumentNullException.ThrowIfNull(statement);
        var folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (folder is not null)
        {
            Directory.CreateDirectory(folder);
        }

        var money = company.Money;
        var year = $"{statement.FiscalYear}-{(statement.FiscalYear + 1) % 100:00}";
        Document.Create(container => container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(32);
            page.DefaultTextStyle(t => t.FontSize(9.5f));
            page.Header().Column(col =>
            {
                col.Item().Row(row =>
                {
                    row.RelativeItem().Column(c =>
                    {
                        c.Item().Text(company.Name).FontSize(15).Bold().FontColor(Accent);
                        if (!string.IsNullOrWhiteSpace(company.Address))
                        {
                            c.Item().Text(company.Address).FontColor(Muted);
                        }
                    });
                    row.ConstantItem(200).AlignRight().Column(c =>
                    {
                        c.Item().AlignRight().Text("TAX DEDUCTION STATEMENT").FontSize(12).Bold().FontColor(Accent);
                        c.Item().AlignRight().Text($"Fiscal year {year}").SemiBold();
                    });
                });
                col.Item().PaddingTop(8).LineHorizontal(1).LineColor(Accent);
            });

            page.Content().PaddingTop(10).Column(col =>
            {
                col.Spacing(12);
                col.Item().Text(t =>
                {
                    t.Span("Employee: ").FontColor(Muted);
                    t.Span($"{statement.EmployeeName} ({statement.EmployeeCode})").SemiBold();
                    t.Span("    Tax ID: ").FontColor(Muted);
                    t.Span(statement.TaxIdentifier ?? "-").SemiBold();
                    t.Span("    Category: ").FontColor(Muted);
                    t.Span(statement.Category).SemiBold();
                });

                col.Item().Table(table =>
                {
                    table.ColumnsDefinition(c =>
                    {
                        c.RelativeColumn(1.4f);
                        c.RelativeColumn(2);
                        c.RelativeColumn(2);
                        c.RelativeColumn(2);
                    });
                    table.Header(h =>
                    {
                        foreach (var title in new[] { "Month", "Taxable pay", "Projected yearly tax", "Tax deducted" })
                        {
                            h.Cell().Background("#EAF0F6").Padding(4).Text(title).Bold();
                        }
                    });
                    foreach (var row in statement.Rows)
                    {
                        table.Cell().BorderBottom(0.5f).BorderColor(Line).Padding(4).Text(row.MonthStart.ToString("MMMM yyyy", CultureInfo.InvariantCulture));
                        table.Cell().BorderBottom(0.5f).BorderColor(Line).Padding(4).AlignRight().Text(money.Format(row.Taxable, withSymbol: false));
                        table.Cell().BorderBottom(0.5f).BorderColor(Line).Padding(4).AlignRight().Text(money.Format(row.AnnualTax, withSymbol: false));
                        table.Cell().BorderBottom(0.5f).BorderColor(Line).Padding(4).AlignRight().Text(money.Format(row.Deducted, withSymbol: false));
                    }

                    table.Cell().Background("#F6F8FA").Padding(4).Text("Total").Bold();
                    table.Cell().Background("#F6F8FA").Padding(4).AlignRight().Text(money.Format(statement.TotalTaxable)).Bold();
                    table.Cell().Background("#F6F8FA").Padding(4);
                    table.Cell().Background("#F6F8FA").Padding(4).AlignRight().Text(money.Format(statement.TotalDeducted)).Bold();
                });

                if (!statement.PriorEmployerIncome.IsZero)
                {
                    col.Item().Text($"Previous employer this year: income {money.Format(statement.PriorEmployerIncome)}, tax {money.Format(statement.PriorEmployerTax)}.").FontColor(Muted);
                }
            });

            page.Footer().Column(f =>
            {
                f.Item().Text("Computer-generated statement of tax deducted from salary.").FontSize(8).FontColor(Muted);
                f.Item().Element(c => PdfStyles.Footer(c, company));
            });
        }))
        .WithMetadata(new DocumentMetadata { Title = $"Tax statement {statement.EmployeeCode} {year}", Author = company.Name, Creator = "SuperOfc" })
        .GeneratePdf(path);
    }
}
