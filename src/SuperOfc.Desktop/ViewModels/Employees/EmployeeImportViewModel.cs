using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SuperOfc.Application.Employees;
using SuperOfc.Desktop.Mvvm;
using SuperOfc.Desktop.Resources;
using SuperOfc.Desktop.Services;
using SuperOfc.Domain.Security;

namespace SuperOfc.Desktop.ViewModels.Employees;

/// <summary>Three-step import: choose file, check (preview with problems), done.</summary>
public sealed partial class EmployeeImportViewModel(PageServices services, IFileDialogService files) : PageViewModel(services)
{
    public const int StepFile = 0;
    public const int StepPreview = 1;
    public const int StepDone = 2;

    public override string Title => Strings.Import_Title;

    public bool CanEdit => Can(Permission.ManageEmployees);

    public ObservableCollection<ImportRowResult> Rows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFileStep), nameof(IsPreviewStep), nameof(IsDoneStep))]
    public partial int Step { get; set; }

    [ObservableProperty]
    public partial string? FilePath { get; set; }

    [ObservableProperty]
    public partial bool CreateLookups { get; set; } = true;

    [ObservableProperty]
    public partial string? SummaryText { get; set; }

    [ObservableProperty]
    public partial string? NewLookupsText { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    [NotifyPropertyChangedFor(nameof(RunText), nameof(HasProblems))]
    public partial int ValidCount { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    [NotifyPropertyChangedFor(nameof(HasProblems))]
    public partial int ErrorCount { get; set; }

    [ObservableProperty]
    public partial string? DoneText { get; set; }

    public bool IsFileStep => Step == StepFile;

    public bool IsPreviewStep => Step == StepPreview;

    public bool IsDoneStep => Step == StepDone;

    public bool HasProblems => ErrorCount > 0;

    public string RunText => string.Format(CultureInfo.CurrentCulture, Strings.Import_Run, ValidCount);

    [RelayCommand]
    private async Task DownloadTemplateAsync()
    {
        var path = files.SaveFile("Excel workbook (*.xlsx)|*.xlsx", "Employee-import-template.xlsx");
        if (path is null)
        {
            return;
        }

        var result = await SendAsync(new WriteEmployeeImportTemplateCommand(path)).ConfigureAwait(true);
        if (result.IsSuccess)
        {
            files.ShellOpen(path);
        }
    }

    [RelayCommand]
    private async Task ChooseFileAsync()
    {
        var path = files.OpenFile("Excel or CSV (*.xlsx;*.csv)|*.xlsx;*.csv");
        if (path is null)
        {
            return;
        }

        FilePath = path;
        await CheckAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task CheckAsync()
    {
        if (FilePath is null)
        {
            return;
        }

        var result = await SendAsync(new PreviewEmployeeImportQuery(FilePath, CreateLookups)).ConfigureAwait(true);
        if (result.IsFailure)
        {
            return;
        }

        var preview = result.Value;
        Rows.Clear();

        // Problems first so they are visible without scrolling.
        foreach (var row in preview.Rows.OrderBy(r => r.IsValid).ThenBy(r => r.RowNumber))
        {
            Rows.Add(row);
        }

        ValidCount = preview.ValidCount;
        ErrorCount = preview.ErrorCount;
        SummaryText = string.Format(CultureInfo.CurrentCulture, Strings.Import_Summary, preview.ValidCount, preview.ErrorCount);
        NewLookupsText = preview.NewLookups.Count == 0
            ? null
            : string.Format(CultureInfo.CurrentCulture, Strings.Import_NewLookups, string.Join(", ", preview.NewLookups));
        Step = StepPreview;
    }

    private bool CanRun() => ValidCount > 0 && ErrorCount == 0;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        if (FilePath is null)
        {
            return;
        }

        var result = await SendAsync(new CommitEmployeeImportCommand(FilePath, CreateLookups)).ConfigureAwait(true);
        if (result.IsSuccess)
        {
            DoneText = string.Format(CultureInfo.CurrentCulture, Strings.Import_Done, result.Value);
            Step = StepDone;
        }
    }

    [RelayCommand]
    private void Restart()
    {
        Rows.Clear();
        FilePath = null;
        ValidCount = ErrorCount = 0;
        Step = StepFile;
    }

    [RelayCommand]
    private void ExportErrors()
    {
        var path = files.SaveFile("CSV (*.csv)|*.csv", "Import-problems.csv");
        if (path is null)
        {
            return;
        }

        var csv = new StringBuilder();
        csv.AppendLine("Row,Code,Name,Problems");
        foreach (var row in Rows.Where(r => !r.IsValid))
        {
            csv.Append(row.RowNumber.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(Quote(row.Code)).Append(',')
                .Append(Quote(row.FullName)).Append(',')
                .AppendLine(Quote(row.ErrorText));
        }

        File.WriteAllText(path, csv.ToString(), new UTF8Encoding(true));
        files.ShellOpen(path);
    }

    [RelayCommand]
    private Task OpenEmployeesAsync() => Services.Navigation.NavigateToAsync<EmployeesViewModel>();

    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
