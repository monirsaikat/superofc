using System.IO;
using NSubstitute;
using SuperOfc.Application.Common;
using SuperOfc.Application.Employees;
using SuperOfc.Desktop.Tests.Support;
using SuperOfc.Desktop.ViewModels;
using SuperOfc.Desktop.ViewModels.Reports;
using SuperOfc.Domain.Employees;

namespace SuperOfc.Desktop.Tests;

/// <summary>Reports page and dashboard through the view models (P7).</summary>
public class ReportsFlowTests
{
    [Fact]
    public async Task Pick_a_report_fill_choices_preview_and_export()
    {
        await using var host = await DesktopTestHost.CreateAsync(completeSetup: true);
        await host.SignInAsync();
        var dispatcher = host.Get<IDispatcher>();
        foreach (var code in new[] { "E1", "E2" })
        {
            var input = new EmployeeInput(code, "Person " + code, Gender.Unspecified, null, null, null, null, null, null, new DateOnly(2024, 1, 1),
                null, null, null, null, PaymentMode.Cash, null, null, null, null, true, null);
            (await dispatcher.SendAsync(new CreateEmployeeCommand(input))).IsSuccess.Should().BeTrue();
        }

        var page = host.Get<ReportsViewModel>();
        await page.OnNavigatedToAsync("headcount");
        page.Reports.Should().HaveCount(7);
        page.SelectedReport!.Id.Should().Be("headcount");
        var period = page.Parameters.Should().ContainSingle().Subject;
        period.IsRange.Should().BeTrue();
        period.From = new DateTime(2024, 1, 1);
        period.To = new DateTime(2024, 12, 31);

        await page.RunCommand.ExecuteAsync(null);
        page.ErrorMessage.Should().BeNull();
        page.Table!.Title.Should().Be("Headcount, joiners and leavers");
        page.Rows.Should().HaveCount(2, "one department row and the totals row");
        page.Rows[^1].IsTotal.Should().BeTrue();
        page.Rows[^1].Cells[2].Should().Be(2, "both joined in 2024");
        page.StatusText.Should().Contain("1 rows");

        var pdf = Path.Combine(host.Root, "headcount.pdf");
        host.Files.SaveFile(default!, default!).ReturnsForAnyArgs(pdf);
        await page.ExportPdfCommand.ExecuteAsync(null);
        File.Exists(pdf).Should().BeTrue();

        page.SelectedReport = page.Reports.Single(r => r.Id == "attendance-summary");
        page.Table.Should().BeNull("changing the report clears the preview");
        page.Parameters.Select(p => p.Parameter.Name).Should().Equal("Month", "Department", "GroupBy");
        page.Parameters[1].Selected.Should().BeEmpty("all departments is the default");
        page.Parameters[2].Selected.Should().Be("employee");
        page.Parameters[0].Month = null;
        await page.RunCommand.ExecuteAsync(null);
        page.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Dashboard_shows_payroll_and_tax_tiles_for_admins()
    {
        await using var host = await DesktopTestHost.CreateAsync(completeSetup: true);
        await host.SignInAsync();
        var dashboard = host.Get<DashboardViewModel>();
        await dashboard.RefreshAsync();
        dashboard.IsBusy.Should().BeFalse();
        dashboard.Tiles.Should().Contain(t => t.Target == typeof(ViewModels.Payroll.PayrollViewModel));
        dashboard.Tiles.Should().Contain(t => t.Target == typeof(ViewModels.Tax.TaxViewModel));
    }
}
