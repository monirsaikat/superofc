using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SuperOfc.Application.Payroll;
using SuperOfc.Application.Reports;
using SuperOfc.Domain.Common;

namespace SuperOfc.Infrastructure.Reporting;

/// <summary>Any report table as a PDF (P7-T1). Wide reports print landscape; the header row repeats on every page.</summary>
public sealed class ReportPdfService : IReportPdfService
{
    public void Write(PayslipCompany company, ReportTable table, string path)
    {
        ArgumentNullException.ThrowIfNull(company);
        ArgumentNullException.ThrowIfNull(table);
        PdfStyles.EnsureLicense();
        var folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (folder is not null)
        {
            Directory.CreateDirectory(folder);
        }

        var landscape = table.Columns.Sum(c => c.Width) > 9 || table.Columns.Count > 8;
        Document.Create(container => container.Page(page =>
        {
            page.Size(landscape ? PageSizes.A4.Landscape() : PageSizes.A4);
            page.Margin(28);
            page.DefaultTextStyle(t => t.FontFamily(PdfStyles.Fonts).FontSize(8.5f));
            page.Header().Element(c => PdfStyles.Header(c, company, table.Title, table.Subtitle));
            page.Footer().Element(c => PdfStyles.Footer(c, company));
            page.Content().PaddingTop(10).Column(col =>
            {
                col.Spacing(8);
                col.Item().Table(t =>
                {
                    t.ColumnsDefinition(c =>
                    {
                        foreach (var column in table.Columns)
                        {
                            c.RelativeColumn(column.Width);
                        }
                    });
                    t.Header(h =>
                    {
                        foreach (var column in table.Columns)
                        {
                            var cell = h.Cell().Background(PdfStyles.HeaderFill).Padding(3);
                            (IsNumeric(column) ? cell.AlignRight() : cell).Text(column.Header).Bold();
                        }
                    });
                    foreach (var row in table.Rows)
                    {
                        for (var i = 0; i < table.Columns.Count; i++)
                        {
                            var cell = t.Cell().BorderBottom(0.5f).BorderColor(PdfStyles.Line).Padding(3);
                            (IsNumeric(table.Columns[i]) ? cell.AlignRight() : cell).Text(Format(company, table.Columns[i], row[i]));
                        }
                    }

                    if (table.Totals is { } totals)
                    {
                        for (var i = 0; i < table.Columns.Count; i++)
                        {
                            var cell = t.Cell().Background(PdfStyles.TotalFill).Padding(3);
                            (IsNumeric(table.Columns[i]) ? cell.AlignRight() : cell).Text(Format(company, table.Columns[i], totals[i])).Bold();
                        }
                    }
                });
                foreach (var note in table.Notes)
                {
                    col.Item().Text(note).FontColor(PdfStyles.Muted);
                }
            });
        }))
        .WithMetadata(new DocumentMetadata { Title = $"{table.Title} {table.Subtitle}", Author = company.Name, Creator = "SuperOfc" })
        .GeneratePdf(path);
    }

    private static bool IsNumeric(ReportColumn column) => column.Kind is ReportColumnKind.Integer or ReportColumnKind.Number or ReportColumnKind.Money;

    /// <summary>Cell text as shown in the app: money with company grouping, days and hours with up to two decimals.</summary>
    public static string Format(PayslipCompany company, ReportColumn column, object? value)
    {
        ArgumentNullException.ThrowIfNull(company);
        ArgumentNullException.ThrowIfNull(column);
        return value switch
        {
            null => string.Empty,
            decimal d when column.Kind == ReportColumnKind.Money => company.Money.Format(Money.FromMajor(d), withSymbol: false),
            decimal d => d.ToString("#,##0.##", CultureInfo.CurrentCulture),
            int i => i.ToString("#,##0", CultureInfo.CurrentCulture),
            DateOnly date => date.ToString(company.DateFormat, CultureInfo.CurrentCulture),
            _ => Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty,
        };
    }
}
