using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Leave;
using SuperOfc.Application.Organization;
using SuperOfc.Application.Reports;
using SuperOfc.Desktop.Mvvm;
using SuperOfc.Desktop.Resources;
using SuperOfc.Desktop.Services;
using SuperOfc.Desktop.ViewModels.Tax;

namespace SuperOfc.Desktop.ViewModels.Reports;

/// <summary>One parameter on the report form. Only the editor for its kind is shown.</summary>
public sealed partial class ReportParameterItem : ObservableObject
{
    public ReportParameterItem(ReportParameter parameter, DateOnly today, int fiscalStartMonth, IReadOnlyList<Choice<string>> options)
    {
        Parameter = parameter;
        var thisMonth = new DateTime(today.Year, today.Month, 1);
        var lastMonth = thisMonth.AddMonths(-1);
        Months = [.. Enumerable.Range(0, 36).Select(i => thisMonth.AddMonths(-i))];
        Month = lastMonth;
        From = lastMonth;
        To = lastMonth.AddMonths(1).AddDays(-1);
        var fiscal = TaxYears.Containing(today, fiscalStartMonth);
        Years = TaxYears.Around(fiscal + 1, fiscalStartMonth);
        Year = fiscal;
        Options = options;
        Selected = options.FirstOrDefault()?.Value;
    }

    public ReportParameter Parameter { get; }

    public string Label => Parameter.Label;

    public bool IsMonth => Parameter.Kind == ReportParameterKind.Month;

    public bool IsRange => Parameter.Kind == ReportParameterKind.DateRange;

    public bool IsYear => Parameter.Kind == ReportParameterKind.FiscalYear;

    public bool IsOptions => Parameter.Kind is ReportParameterKind.Department or ReportParameterKind.LeaveType or ReportParameterKind.Choice;

    public IReadOnlyList<Choice<int>> Years { get; }

    public IReadOnlyList<DateTime> Months { get; }

    public IReadOnlyList<Choice<string>> Options { get; }

    [ObservableProperty]
    public partial DateTime? Month { get; set; }

    [ObservableProperty]
    public partial DateTime? From { get; set; }

    [ObservableProperty]
    public partial DateTime? To { get; set; }

    [ObservableProperty]
    public partial int Year { get; set; }

    [ObservableProperty]
    public partial string? Selected { get; set; }

    /// <summary>The invariant value sent to the report, or null when nothing is chosen.</summary>
    public string? Value() => Parameter.Kind switch
    {
        ReportParameterKind.Month => Month is { } m ? ReportArgs.MonthValue(m.Year, m.Month) : null,
        ReportParameterKind.DateRange => From is { } f && To is { } t ? ReportArgs.RangeValue(DateOnly.FromDateTime(f), DateOnly.FromDateTime(t)) : null,
        ReportParameterKind.FiscalYear => Year.ToString(CultureInfo.InvariantCulture),
        _ => string.IsNullOrEmpty(Selected) ? null : Selected,
    };
}

/// <summary>A row in the preview grid; the totals row is shown in bold.</summary>
public sealed record ReportRowView(IReadOnlyList<object?> Cells, bool IsTotal);

/// <summary>Reports (P7-T1): pick a report, fill in its choices, preview it, then save it as PDF or Excel.</summary>
public sealed partial class ReportsViewModel(PageServices services, IFileDialogService files, IClock clock, ICompanyContext company) : PageViewModel(services)
{
    private List<Choice<string>> _departments = [];
    private List<Choice<string>> _leaveTypes = [];

    public override string Title => Strings.Reports_Title;

    public ObservableCollection<ReportInfo> Reports { get; } = [];

    public ObservableCollection<ReportParameterItem> Parameters { get; } = [];

    public ObservableCollection<ReportRowView> Rows { get; } = [];

    public ObservableCollection<string> Notes { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReport))]
    public partial ReportInfo? SelectedReport { get; set; }

    /// <summary>The last result; the page builds its grid columns from it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTable))]
    public partial ReportTable? Table { get; set; }

    [ObservableProperty]
    public partial string? StatusText { get; set; }

    public bool HasReport => SelectedReport is not null;

    public bool HasTable => Table is not null;

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        var reports = await SendAsync(new GetReportsQuery()).ConfigureAwait(true);
        Reports.Clear();
        foreach (var report in reports.IsSuccess ? reports.Value : [])
        {
            Reports.Add(report);
        }

        var lookups = await SendAsync(new GetOrganizationLookupsQuery(), showErrors: false).ConfigureAwait(true);
        _departments = [new(string.Empty, Strings.Reports_AllDepartments), .. (lookups.IsSuccess ? lookups.Value.Departments : []).Select(d => new Choice<string>(d.Id.ToString(), d.Name))];
        var types = await SendAsync(new GetLeaveTypesQuery(), showErrors: false).ConfigureAwait(true);
        _leaveTypes = [new(string.Empty, Strings.Reports_AllLeaveTypes), .. (types.IsSuccess ? types.Value : []).Select(t => new Choice<string>(t.Id.ToString(), t.Name))];

        SelectedReport = parameter is string id ? Reports.FirstOrDefault(r => r.Id == id) : SelectedReport ?? Reports.FirstOrDefault();
    }

    partial void OnSelectedReportChanged(ReportInfo? value)
    {
        Parameters.Clear();
        Rows.Clear();
        Notes.Clear();
        Table = null;
        StatusText = null;
        ErrorMessage = null;
        if (value is null)
        {
            return;
        }

        var today = clock.TodayIn(company.TimeZone);
        var startMonth = company.Company?.FiscalYearStartMonth ?? 7;
        foreach (var p in value.Parameters)
        {
            IReadOnlyList<Choice<string>> options = p.Kind switch
            {
                ReportParameterKind.Department => _departments,
                ReportParameterKind.LeaveType => _leaveTypes,
                ReportParameterKind.Choice => [.. (p.Choices ?? []).Select(c => new Choice<string>(c.Value, c.Text))],
                _ => [],
            };
            Parameters.Add(new ReportParameterItem(p, today, startMonth, options));
        }
    }

    private ReportArgs Args() => ReportArgs.Of([.. Parameters.Select(p => (p.Parameter.Name, p.Value()))]);

    [RelayCommand]
    private async Task RunAsync()
    {
        if (SelectedReport is not { } report)
        {
            return;
        }

        ErrorMessage = null;
        var watch = Stopwatch.StartNew();
        var result = await SendAsync(new RunReportQuery(report.Id, Args())).ConfigureAwait(true);
        watch.Stop();
        Rows.Clear();
        Notes.Clear();
        if (result.IsFailure)
        {
            Table = null;
            StatusText = null;
            return;
        }

        var table = result.Value;
        foreach (var row in table.Rows)
        {
            Rows.Add(new ReportRowView(row, false));
        }

        if (table.Totals is { } totals)
        {
            Rows.Add(new ReportRowView(totals, true));
        }

        foreach (var note in table.Notes)
        {
            Notes.Add(note);
        }

        Table = table;
        StatusText = string.Format(CultureInfo.CurrentCulture, Strings.Reports_Status, table.Subtitle, table.Rows.Count, watch.Elapsed.TotalSeconds);
    }

    [RelayCommand]
    private Task ExportPdfAsync() => ExportAsync(ReportFormat.Pdf, "PDF (*.pdf)|*.pdf", ".pdf");

    [RelayCommand]
    private Task ExportExcelAsync() => ExportAsync(ReportFormat.Excel, "Excel (*.xlsx)|*.xlsx", ".xlsx");

    private async Task ExportAsync(ReportFormat format, string filter, string extension)
    {
        if (SelectedReport is not { } report)
        {
            return;
        }

        var name = Table is { } t ? $"{t.Title} {t.Subtitle}" : report.Name;
        var safe = new string([.. name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c)]);
        var path = files.SaveFile(filter, safe + extension);
        if (path is not null && (await SendAsync(new ExportReportCommand(report.Id, Args(), path, format)).ConfigureAwait(true)).IsSuccess)
        {
            files.ShellOpen(path);
        }
    }
}
