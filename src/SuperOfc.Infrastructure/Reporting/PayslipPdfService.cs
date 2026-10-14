using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SuperOfc.Application.Payroll;
using SuperOfc.Domain.Payroll;

namespace SuperOfc.Infrastructure.Reporting;

/// <summary>
/// One A4 page per payslip (ADR-07): company header, employee block, days summary, earnings and deductions side by
/// side, net pay in figures and words. Text stays selectable and searchable. Amounts go through MoneyFormatter.
/// </summary>
public sealed class PayslipPdfService : IPayslipPdfService
{
    private const string Accent = "#1F4E79";
    private const string Muted = "#5F6B7A";
    private const string Line = "#D0D7DE";

    static PayslipPdfService()
    {
        // Community licence: free below the revenue threshold (ADR-07; re-check before commercial sale, doc 10).
        PdfStyles.EnsureLicense();
    }

    public void Write(PayslipCompany company, IReadOnlyList<PayslipDocument> payslips, string path)
    {
        ArgumentNullException.ThrowIfNull(company);
        ArgumentNullException.ThrowIfNull(payslips);
        var folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (folder is not null)
        {
            Directory.CreateDirectory(folder);
        }

        Build(company, payslips)
            .WithMetadata(new DocumentMetadata { Title = payslips.Count == 1 ? $"Payslip {payslips[0].Payslip.EmployeeCode}" : "Payslips", Author = company.Name, Creator = "SuperOfc" })
            .GeneratePdf(path);
    }

    public byte[] RenderPreview(PayslipCompany company, PayslipDocument payslip, int dpi = 110)
    {
        ArgumentNullException.ThrowIfNull(company);
        ArgumentNullException.ThrowIfNull(payslip);
        return Build(company, [payslip]).GenerateImages(new ImageGenerationSettings { ImageFormat = ImageFormat.Png, RasterDpi = dpi }).First();
    }

    private static Document Build(PayslipCompany company, IReadOnlyList<PayslipDocument> payslips)
    {
        var logo = company.LogoFullPath is not null ? File.ReadAllBytes(company.LogoFullPath) : null;
        return Document.Create(container =>
        {
            foreach (var document in payslips)
            {
                container.Page(page => Compose(page, company, logo, document));
            }
        });
    }

    private static void Compose(PageDescriptor page, PayslipCompany company, byte[]? logo, PayslipDocument document)
    {
        var p = document.Payslip;
        var money = company.Money;
        var month = new DateTime(p.Year, p.Month, 1).ToString("MMMM yyyy", CultureInfo.InvariantCulture);
        page.Size(PageSizes.A4);
        page.Margin(32);
        page.DefaultTextStyle(t => t.FontFamily(PdfStyles.Fonts).FontSize(9.5f).FontColor(Colors.Black));

        page.Header().Column(col =>
        {
            col.Item().Row(row =>
            {
                if (logo is not null)
                {
                    row.ConstantItem(64).Height(48).Image(logo).FitArea();
                    row.ConstantItem(10);
                }

                row.RelativeItem().Column(c =>
                {
                    c.Item().Text(company.Name).FontSize(15).Bold().FontColor(Accent);
                    if (!string.IsNullOrWhiteSpace(company.Address))
                    {
                        c.Item().Text(company.Address).FontColor(Muted);
                    }

                    var contact = string.Join("  ·  ", new[] { company.Phone, company.Email }.Where(s => !string.IsNullOrWhiteSpace(s)));
                    if (contact.Length > 0)
                    {
                        c.Item().Text(contact).FontColor(Muted);
                    }
                });
                row.ConstantItem(150).AlignRight().Column(c =>
                {
                    c.Item().AlignRight().Text("PAYSLIP").FontSize(15).Bold().FontColor(Accent);
                    c.Item().AlignRight().Text(month).FontSize(11).SemiBold();
                    if (p.RunStatus != PayrollRunStatus.Finalized)
                    {
                        c.Item().AlignRight().Text(p.RunStatus == PayrollRunStatus.Voided ? "VOIDED" : "DRAFT").Bold().FontColor(Colors.Red.Medium);
                    }
                });
            });
            col.Item().PaddingTop(8).LineHorizontal(1).LineColor(Accent);
        });

        page.Content().PaddingTop(10).Column(col =>
        {
            col.Spacing(10);

            // Employee block.
            col.Item().Table(table =>
            {
                table.ColumnsDefinition(c =>
                {
                    c.RelativeColumn(1.1f);
                    c.RelativeColumn(2);
                    c.RelativeColumn(1.1f);
                    c.RelativeColumn(2);
                });
                void Pair(string label, string? value)
                {
                    table.Cell().PaddingVertical(2).Text(label).FontColor(Muted);
                    table.Cell().PaddingVertical(2).Text(string.IsNullOrWhiteSpace(value) ? "-" : value).SemiBold();
                }

                Pair("Employee", p.EmployeeName);
                Pair("Code", p.EmployeeCode);
                Pair("Department", p.Department);
                Pair("Designation", p.Designation);
                Pair("Payment", PaymentText(p));
                Pair("Pay period", month);
            });

            // Days summary.
            col.Item().Border(0.5f).BorderColor(Line).Background("#F6F8FA").Padding(6).Table(table =>
            {
                var cells = new List<(string Label, string Value)>
                {
                    ("Days", p.CalendarDays.ToString(CultureInfo.InvariantCulture)),
                    ("Payable", Days(p.PayableDays)),
                    ("Present", Days(p.PresentDays + (p.HalfDays / 2))),
                    ("Paid leave", Days(p.PaidLeaveDays)),
                    ("Unpaid leave", Days(p.UnpaidLeaveDays)),
                    ("Absent", Days(p.AbsentDays)),
                    ("Holidays", p.Holidays.ToString(CultureInfo.InvariantCulture)),
                    ("Weekly off", p.WeeklyOffs.ToString(CultureInfo.InvariantCulture)),
                    ("Late", p.LateDays.ToString(CultureInfo.InvariantCulture)),
                    ("Overtime h", Days(p.OvertimeHours)),
                };
                table.ColumnsDefinition(c =>
                {
                    foreach (var _ in cells)
                    {
                        c.RelativeColumn();
                    }
                });
                foreach (var (label, _) in cells)
                {
                    table.Cell().AlignCenter().Text(label).FontSize(8).FontColor(Muted);
                }

                foreach (var (_, value) in cells)
                {
                    table.Cell().AlignCenter().Text(value).SemiBold();
                }
            });

            // Earnings and deductions side by side.
            col.Item().Row(row =>
            {
                row.RelativeItem().Element(e => Lines(e, "Earnings", p.EarningLines, "Gross pay", p.Gross, money));
                row.ConstantItem(14);
                row.RelativeItem().Element(e => Lines(e, "Deductions", p.DeductionLines, "Total deductions", p.Deductions, money));
            });

            // Net pay.
            col.Item().Background(Accent).Padding(10).Row(row =>
            {
                row.RelativeItem().Column(c =>
                {
                    c.Item().Text("NET PAY").FontColor(Colors.White).Bold();
                    c.Item().Text(document.NetInWords).FontColor(Colors.White).FontSize(9);
                });
                row.ConstantItem(170).AlignRight().AlignMiddle().Text(money.Format(p.Net)).FontSize(16).Bold().FontColor(Colors.White);
            });

            if (p.EmployerLines.Count > 0)
            {
                col.Item().Text(t =>
                {
                    t.Span("Paid by the employer in addition: ").FontColor(Muted);
                    t.Span(string.Join(", ", p.EmployerLines.Select(l => $"{l.Name} {money.Format(l.Amount)}")));
                });
            }

            if (p.PaymentStatus == PaymentStatus.Paid && p.PaidOn is { } paidOn)
            {
                col.Item().Text($"Paid on {paidOn.ToString(company.DateFormat, CultureInfo.InvariantCulture)}{(p.PaymentReference is null ? string.Empty : " · Ref " + p.PaymentReference)}").FontColor(Muted);
            }
        });

        page.Footer().Column(col =>
        {
            col.Item().LineHorizontal(0.5f).LineColor(Line);
            col.Item().PaddingTop(4).Row(row =>
            {
                row.RelativeItem().Text("This is a computer-generated payslip and needs no signature.").FontSize(8).FontColor(Muted);
                row.ConstantItem(120).AlignRight().Text(t =>
                {
                    t.DefaultTextStyle(s => s.FontSize(8).FontColor(Muted));
                    t.Span("Page ");
                    t.CurrentPageNumber();
                    t.Span(" of ");
                    t.TotalPages();
                });
            });
        });
    }

    private static void Lines(IContainer container, string title, IReadOnlyList<PayslipLineDto> lines, string totalLabel, Domain.Common.Money total, Application.Common.MoneyFormatter money)
    {
        container.Border(0.5f).BorderColor(Line).Table(table =>
        {
            table.ColumnsDefinition(c =>
            {
                c.RelativeColumn(3);
                c.RelativeColumn(2);
            });
            table.Header(h =>
            {
                h.Cell().ColumnSpan(2).Background("#EAF0F6").Padding(5).Text(title).Bold().FontColor(Accent);
            });
            foreach (var line in lines)
            {
                table.Cell().BorderBottom(0.5f).BorderColor(Line).Padding(5).Column(c =>
                {
                    c.Item().Text(line.Name);
                    if (!string.IsNullOrWhiteSpace(line.Basis))
                    {
                        c.Item().Text(line.Basis).FontSize(7.5f).FontColor(Muted);
                    }
                });
                table.Cell().BorderBottom(0.5f).BorderColor(Line).Padding(5).AlignRight().Text(money.Format(line.Amount, withSymbol: false));
            }

            if (lines.Count == 0)
            {
                table.Cell().ColumnSpan(2).Padding(5).Text("None").FontColor(Muted);
            }

            table.Cell().Background("#F6F8FA").Padding(5).Text(totalLabel).Bold();
            table.Cell().Background("#F6F8FA").Padding(5).AlignRight().Text(money.Format(total)).Bold();
        });
    }

    private static string PaymentText(PayslipDto p) => p.PaymentMode switch
    {
        Domain.Employees.PaymentMode.Bank => string.Join(" · ", new[] { "Bank", p.BankName, p.BankAccount }.Where(s => !string.IsNullOrWhiteSpace(s))),
        Domain.Employees.PaymentMode.MobileBanking => string.Join(" · ", new[] { "Mobile banking", p.BankAccount }.Where(s => !string.IsNullOrWhiteSpace(s))),
        Domain.Employees.PaymentMode.Cheque => "Cheque",
        _ => "Cash",
    };

    private static string Days(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
