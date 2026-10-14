using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SuperOfc.Application.Backups;
using SuperOfc.Desktop.Mvvm;
using SuperOfc.Desktop.Resources;
using SuperOfc.Domain.Security;
using Wpf.Ui.Controls;

namespace SuperOfc.Desktop.ViewModels;

/// <summary>A clickable dashboard tile.</summary>
public sealed record DashboardTile(string Title, string Value, string? Detail, SymbolRegular Icon, Type? Target, object? Parameter = null);

public sealed partial class DashboardViewModel(PageServices services) : PageViewModel(services), IRefreshable
{
    public override string Title => Strings.Dashboard_Title;

    public string Welcome => string.Format(CultureInfo.CurrentCulture, Strings.Dashboard_Welcome, Services.User.DisplayName);

    public ObservableCollection<DashboardTile> Tiles { get; } = [];

    [ObservableProperty]
    public partial string LastBackupText { get; set; } = string.Empty;

    public override Task OnNavigatedToAsync(object? parameter) => RefreshAsync();

    /// <summary>Loads after the shell is shown; the page shows a progress ring meanwhile (P7-T3).</summary>
    public async Task RefreshAsync()
    {
        IsBusy = true;
        try
        {
            await LoadAsync().ConfigureAwait(true);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadAsync()
    {
        var tiles = new List<DashboardTile>();
        await DashboardTiles.AddModuleTilesAsync(this, tiles).ConfigureAwait(true);

        if (Can(Permission.ManageBackups))
        {
            var backups = await SendAsync(new GetBackupsQuery(), showErrors: false).ConfigureAwait(true);
            var last = backups.IsSuccess ? backups.Value.FirstOrDefault() : null;
            LastBackupText = last is null
                ? Strings.Dashboard_NoBackup
                : last.CreatedAtUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
            tiles.Add(new DashboardTile(Strings.Dashboard_LastBackup, LastBackupText, null, SymbolRegular.CloudArrowUp24, typeof(Settings.SettingsViewModel), "Backup"));
        }

        Tiles.Clear();
        foreach (var tile in tiles)
        {
            Tiles.Add(tile);
        }
    }

    [RelayCommand]
    private async Task OpenTileAsync(DashboardTile? tile)
    {
        if (tile?.Target is not null)
        {
            await Services.Navigation.NavigateToAsync(tile.Target, tile.Parameter).ConfigureAwait(true);
        }
    }

    internal Task<SuperOfc.Domain.Common.Result<T>> QueryAsync<T>(SuperOfc.Application.Common.IRequest<T> request) => SendAsync(request, showErrors: false);
}

/// <summary>Module tiles; each module adds its own figures here as it ships.</summary>
public static partial class DashboardTiles
{
    public static async Task AddModuleTilesAsync(DashboardViewModel page, List<DashboardTile> tiles)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(tiles);
        await AddEmployeeTilesAsync(page, tiles).ConfigureAwait(true);
        await AddAttendanceTilesAsync(page, tiles).ConfigureAwait(true);
        await AddFigureTilesAsync(page, tiles).ConfigureAwait(true);
    }

    /// <summary>Payroll, loans, tax, devices and documents from one combined query.</summary>
    private static async Task AddFigureTilesAsync(DashboardViewModel page, List<DashboardTile> tiles)
    {
        var result = await page.QueryAsync(new SuperOfc.Application.Reports.GetDashboardQuery()).ConfigureAwait(true);
        if (result.IsFailure)
        {
            return;
        }

        var f = result.Value;
        if (f.Payroll is { } p)
        {
            var month = new DateTime(p.Year, p.Month, 1).ToString("MMMM yyyy", CultureInfo.CurrentCulture);
            var status = p.Status switch
            {
                SuperOfc.Domain.Payroll.PayrollPeriodStatus.Computed => Strings.Dashboard_PayrollDraft,
                SuperOfc.Domain.Payroll.PayrollPeriodStatus.Locked => Strings.Dashboard_PayrollFinalized,
                SuperOfc.Domain.Payroll.PayrollPeriodStatus.Paid => Strings.Dashboard_PayrollPaid,
                _ => Strings.Dashboard_PayrollNotStarted,
            };
            tiles.Add(new DashboardTile(string.Format(CultureInfo.CurrentCulture, Strings.Dashboard_Payroll, month), status,
                p.Employees > 0 ? string.Format(CultureInfo.CurrentCulture, Strings.Dashboard_PayrollDetail, page.Money.Format(p.Net), p.Employees) : null,
                SymbolRegular.Money24, typeof(Payroll.PayrollViewModel)));
        }

        if (f.Loans is { } l && (l.Open > 0 || l.Requested > 0))
        {
            tiles.Add(new DashboardTile(Strings.Dashboard_Loans, page.Money.Format(l.Outstanding),
                string.Format(CultureInfo.CurrentCulture, Strings.Dashboard_LoansDetail, l.Open, l.Requested), SymbolRegular.Wallet24, typeof(Loans.LoansViewModel)));
        }

        if (f.TaxRulesMissing)
        {
            tiles.Add(new DashboardTile(Strings.Dashboard_Tax, Strings.Dashboard_TaxMissing, Strings.Dashboard_TaxMissingDetail, SymbolRegular.ReceiptMoney24, typeof(Tax.TaxViewModel)));
        }

        if (f.Devices is { } d && (d.Devices > 0 || d.UnlinkedPunches > 0))
        {
            tiles.Add(new DashboardTile(Strings.Dashboard_Devices,
                string.Format(CultureInfo.CurrentCulture, Strings.Dashboard_DevicesOk, d.Devices - d.NeedAttention, d.Devices),
                d.UnlinkedPunches > 0 ? string.Format(CultureInfo.CurrentCulture, Strings.Dashboard_UnlinkedPunches, d.UnlinkedPunches) : null,
                SymbolRegular.Fingerprint24, typeof(Devices.DevicesViewModel)));
        }

        if (f.DocumentsExpiring > 0)
        {
            tiles.Add(new DashboardTile(Strings.Dashboard_Documents, f.DocumentsExpiring.ToString("N0", CultureInfo.CurrentCulture), Strings.Dashboard_DocumentsDetail,
                SymbolRegular.DocumentError24, typeof(Employees.EmployeesViewModel)));
        }
    }

    private static async Task AddAttendanceTilesAsync(DashboardViewModel page, List<DashboardTile> tiles)
    {
        var today = await page.QueryAsync(new SuperOfc.Application.Attendance.GetTodayAttendanceQuery()).ConfigureAwait(true);
        if (today.IsFailure)
        {
            return;
        }

        var t = today.Value;
        if (t.Expected > 0)
        {
            tiles.Insert(Math.Min(1, tiles.Count), new DashboardTile(
                Strings.Dashboard_Today,
                string.Format(CultureInfo.CurrentCulture, Strings.Dashboard_PresentOf, t.In, t.Expected),
                string.Format(CultureInfo.CurrentCulture, Strings.Dashboard_TodayDetail, t.Late, t.Absent, t.OnLeave),
                SymbolRegular.CalendarClock24,
                typeof(Attendance.AttendanceViewModel)));
        }

        if (t.PendingLeaveRequests > 0)
        {
            tiles.Add(new DashboardTile(
                Strings.Dashboard_PendingLeave,
                t.PendingLeaveRequests.ToString(CultureInfo.CurrentCulture),
                Strings.Dashboard_PendingLeaveDetail,
                SymbolRegular.CalendarPerson20,
                typeof(Leave.LeaveViewModel)));
        }
    }

    private static async Task AddEmployeeTilesAsync(DashboardViewModel page, List<DashboardTile> tiles)
    {
        var headcount = await page.QueryAsync(new SuperOfc.Application.Employees.GetHeadcountQuery(DateOnly.FromDateTime(DateTime.Today))).ConfigureAwait(true);
        if (headcount.IsFailure)
        {
            return;
        }

        var h = headcount.Value;
        var total = h.Active + h.Probation + h.OnLeave;
        tiles.Add(new DashboardTile(
            Strings.Dashboard_Headcount,
            total.ToString("N0", CultureInfo.CurrentCulture),
            string.Format(CultureInfo.CurrentCulture, Strings.Dashboard_HeadcountDetail, h.Probation, h.OnLeave),
            SymbolRegular.People24,
            typeof(Employees.EmployeesViewModel)));
        tiles.Add(new DashboardTile(
            Strings.Dashboard_ThisMonth,
            string.Format(CultureInfo.CurrentCulture, Strings.Dashboard_JoinedLeft, h.JoinedThisMonth, h.LeftThisMonth),
            null,
            SymbolRegular.PersonArrowRight24,
            typeof(Employees.EmployeesViewModel)));
    }
}
