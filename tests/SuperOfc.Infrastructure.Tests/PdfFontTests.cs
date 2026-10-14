using SuperOfc.Application.Common;
using SuperOfc.Application.Letters;
using SuperOfc.Application.Payroll;
using SuperOfc.Infrastructure.Reporting;

namespace SuperOfc.Infrastructure.Tests;

/// <summary>PDFs carry their own fonts: the taka sign and Bengali names print without any font installed on the PC.</summary>
public class PdfFontTests
{
    [Fact]
    public void Letter_with_taka_sign_and_Bengali_name_renders_with_bundled_fonts()
    {
        var path = Path.Combine(Path.GetTempPath(), $"superofc-font-{Guid.NewGuid():N}.pdf");
        var company = new PayslipCompany("Saikat", "Dhaka", null, null, null, new MoneyFormatter("৳", Domain.Organization.NumberGrouping.Western), "dd MMM yyyy");
        var letter = new LetterDocument("HR/2026/0001", new DateOnly(2026, 10, 14), "Salary certificate",
            "His current monthly gross salary is **৳ 8,000.00** (Taka Eight Thousand Only).\n\nনুসরাত হোসেন", "E0001", "নুসরাত হোসেন");

        PdfStyles.EnsureLicense();
        QuestPDF.Settings.ThrowOnMissingTextGlyphs = true;
        try
        {
            new LetterPdfService().Write(company, [letter], path);
            new FileInfo(path).Length.Should().BeGreaterThan(1000);
        }
        finally
        {
            QuestPDF.Settings.ThrowOnMissingTextGlyphs = false;
            File.Delete(path);
        }
    }
}
