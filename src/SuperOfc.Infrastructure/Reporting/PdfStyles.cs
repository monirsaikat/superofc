using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;
using SuperOfc.Application.Payroll;

namespace SuperOfc.Infrastructure.Reporting;

/// <summary>Shared look for every PDF the app makes (P7-T1): colours, the company header and the page footer.</summary>
internal static class PdfStyles
{
    public const string Accent = "#1F4E79";
    public const string Muted = "#5F6B7A";
    public const string Line = "#D0D7DE";
    public const string HeaderFill = "#EAF0F6";
    public const string TotalFill = "#F6F8FA";

    /// <summary>Sets the QuestPDF licence; call before building a document.</summary>
    public static void EnsureLicense() => QuestPDF.Settings.License = LicenseType.Community;

    /// <summary>Company name and address on the left, the document title and subtitle on the right, then a rule.</summary>
    public static void Header(IContainer container, PayslipCompany company, string title, string subtitle)
        => container.Column(col =>
        {
            col.Item().Row(row =>
            {
                if (company.LogoFullPath is { } logo && File.Exists(logo))
                {
                    row.ConstantItem(44).Height(44).Image(logo).FitArea();
                    row.ConstantItem(10);
                }

                row.RelativeItem().Column(c =>
                {
                    c.Item().Text(company.Name).FontSize(14).Bold().FontColor(Accent);
                    if (!string.IsNullOrWhiteSpace(company.Address))
                    {
                        c.Item().Text(company.Address).FontColor(Muted);
                    }
                });
                row.RelativeItem().AlignRight().Column(c =>
                {
                    c.Item().AlignRight().Text(title).FontSize(12).Bold().FontColor(Accent);
                    c.Item().AlignRight().Text(subtitle).FontColor(Muted);
                });
            });
            col.Item().PaddingTop(6).LineHorizontal(1).LineColor(Accent);
        });

    /// <summary>Print time on the left and "Page x of y" on the right.</summary>
    public static void Footer(IContainer container, PayslipCompany company)
        => container.Row(row =>
        {
            row.RelativeItem().Text("Printed " + DateTime.Now.ToString(company.DateFormat + " HH:mm", CultureInfo.CurrentCulture) + " by SuperOfc").FontSize(8).FontColor(Muted);
            row.RelativeItem().AlignRight().Text(t =>
            {
                t.DefaultTextStyle(s => s.FontSize(8).FontColor(Muted));
                t.Span("Page ");
                t.CurrentPageNumber();
                t.Span(" of ");
                t.TotalPages();
            });
        });
}
