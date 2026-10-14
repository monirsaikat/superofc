using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SuperOfc.Application.Loans;
using SuperOfc.Application.Payroll;
using SuperOfc.Domain.Loans;

namespace SuperOfc.Infrastructure.Reporting;

/// <summary>Loan statement (P5-T2): loan terms, the repayment schedule and every transaction with a running balance.</summary>
public sealed class LoanStatementPdfService : ILoanStatementPdfService
{
    private const string Accent = PdfStyles.Accent;
    private const string Muted = PdfStyles.Muted;
    private const string Line = PdfStyles.Line;

    static LoanStatementPdfService()
    {
        PdfStyles.EnsureLicense();
    }

    public void Write(PayslipCompany company, LoanDetail loan, string path)
    {
        ArgumentNullException.ThrowIfNull(company);
        ArgumentNullException.ThrowIfNull(loan);
        var folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (folder is not null)
        {
            Directory.CreateDirectory(folder);
        }

        var logo = company.LogoFullPath is not null ? File.ReadAllBytes(company.LogoFullPath) : null;
        var money = company.Money;
        var s = loan.Summary;
        string Date(DateOnly d) => d.ToString(company.DateFormat, CultureInfo.InvariantCulture);

        Document.Create(container => container.Page(page =>
        {
            page.Size(PageSizes.A4);
            page.Margin(32);
            page.DefaultTextStyle(t => t.FontSize(9.5f));
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
                    });
                    row.ConstantItem(180).AlignRight().Column(c =>
                    {
                        c.Item().AlignRight().Text(s.Kind == LoanKind.Advance ? "ADVANCE STATEMENT" : "LOAN STATEMENT").FontSize(13).Bold().FontColor(Accent);
                        c.Item().AlignRight().Text(s.Number).FontSize(11).SemiBold();
                    });
                });
                col.Item().PaddingTop(8).LineHorizontal(1).LineColor(Accent);
            });

            page.Content().PaddingTop(10).Column(col =>
            {
                col.Spacing(12);
                col.Item().Table(table =>
                {
                    table.ColumnsDefinition(c =>
                    {
                        c.RelativeColumn(1.2f);
                        c.RelativeColumn(2);
                        c.RelativeColumn(1.2f);
                        c.RelativeColumn(2);
                    });
                    void Pair(string label, string value)
                    {
                        table.Cell().PaddingVertical(2).Text(label).FontColor(Muted);
                        table.Cell().PaddingVertical(2).Text(value).SemiBold();
                    }

                    Pair("Employee", $"{s.EmployeeName} ({s.EmployeeCode})");
                    Pair("Status", s.Status.ToString());
                    Pair("Amount", money.Format(s.Principal));
                    Pair("Interest", loan.InterestMethod == Domain.Policies.InterestMethod.FlatRate ? $"{money.Format(loan.Interest)} ({loan.AnnualRate:0.##} % a year, flat)" : "None");
                    Pair("Installments", $"{s.InstallmentCount} × {money.Format(s.Installment)} from {s.FirstMonth.ToString("MMM yyyy", CultureInfo.InvariantCulture)}");
                    Pair("Paid out", loan.DisbursedOn is { } d ? Date(d) : "-");
                    Pair("Outstanding", money.Format(loan.Balance.Outstanding));
                    Pair("Statement date", Date(DateOnly.FromDateTime(DateTime.Today)));
                });

                col.Item().Text("Transactions").Bold().FontColor(Accent);
                col.Item().Table(table =>
                {
                    table.ColumnsDefinition(c =>
                    {
                        c.ConstantColumn(80);
                        c.RelativeColumn(3);
                        c.RelativeColumn(1.4f);
                        c.RelativeColumn(1.4f);
                        c.RelativeColumn(1.4f);
                    });
                    table.Header(h =>
                    {
                        foreach (var title in new[] { "Date", "Description", "Lent", "Repaid", "Balance" })
                        {
                            h.Cell().Background("#EAF0F6").Padding(4).Text(title).Bold();
                        }
                    });
                    foreach (var line in loan.Statement)
                    {
                        table.Cell().BorderBottom(0.5f).BorderColor(Line).Padding(4).Text(Date(line.Date));
                        table.Cell().BorderBottom(0.5f).BorderColor(Line).Padding(4).Text(line.Reference is null ? line.Description : $"{line.Description} · {line.Reference}");
                        table.Cell().BorderBottom(0.5f).BorderColor(Line).Padding(4).AlignRight().Text(line.Debit.IsZero ? string.Empty : money.Format(line.Debit, withSymbol: false));
                        table.Cell().BorderBottom(0.5f).BorderColor(Line).Padding(4).AlignRight().Text(line.Credit.IsZero ? string.Empty : money.Format(line.Credit, withSymbol: false));
                        table.Cell().BorderBottom(0.5f).BorderColor(Line).Padding(4).AlignRight().Text(money.Format(line.Balance, withSymbol: false));
                    }
                });

                col.Item().Text("Repayment schedule").Bold().FontColor(Accent);
                col.Item().Table(table =>
                {
                    table.ColumnsDefinition(c =>
                    {
                        c.ConstantColumn(30);
                        c.ConstantColumn(70);
                        c.RelativeColumn();
                        c.RelativeColumn();
                        c.RelativeColumn();
                        c.RelativeColumn();
                        c.RelativeColumn(1.4f);
                    });
                    table.Header(h =>
                    {
                        foreach (var title in new[] { "#", "Month", "Principal", "Interest", "Amount", "Paid", "Status" })
                        {
                            h.Cell().Background("#EAF0F6").Padding(4).Text(title).Bold();
                        }
                    });
                    foreach (var i in loan.Installments)
                    {
                        table.Cell().BorderBottom(0.5f).BorderColor(Line).Padding(3).Text(i.Sequence.ToString(CultureInfo.InvariantCulture));
                        table.Cell().BorderBottom(0.5f).BorderColor(Line).Padding(3).Text(i.DueMonth.ToString("MMM yyyy", CultureInfo.InvariantCulture));
                        table.Cell().BorderBottom(0.5f).BorderColor(Line).Padding(3).AlignRight().Text(money.Format(i.Principal, withSymbol: false));
                        table.Cell().BorderBottom(0.5f).BorderColor(Line).Padding(3).AlignRight().Text(money.Format(i.Interest, withSymbol: false));
                        table.Cell().BorderBottom(0.5f).BorderColor(Line).Padding(3).AlignRight().Text(money.Format(i.Amount, withSymbol: false));
                        table.Cell().BorderBottom(0.5f).BorderColor(Line).Padding(3).AlignRight().Text(money.Format(i.Paid, withSymbol: false));
                        table.Cell().BorderBottom(0.5f).BorderColor(Line).Padding(3).Text(i.Status.ToString());
                    }
                });
            });

            page.Footer().Row(row =>
            {
                row.RelativeItem().Text("Computer-generated statement.").FontSize(8).FontColor(Muted);
                row.ConstantItem(120).AlignRight().Text(t =>
                {
                    t.DefaultTextStyle(x => x.FontSize(8).FontColor(Muted));
                    t.Span("Page ");
                    t.CurrentPageNumber();
                    t.Span(" of ");
                    t.TotalPages();
                });
            });
        }))
        .WithMetadata(new DocumentMetadata { Title = $"Statement {s.Number}", Author = company.Name, Creator = "SuperOfc" })
        .GeneratePdf(path);
    }
}
