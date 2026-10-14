using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SuperOfc.Application.Common;
using SuperOfc.Application.Employees;
using SuperOfc.Application.Organization;
using SuperOfc.Desktop.Mvvm;
using SuperOfc.Desktop.Resources;
using SuperOfc.Desktop.Services;
using SuperOfc.Domain.Employees;
using SuperOfc.Domain.Security;

namespace SuperOfc.Desktop.ViewModels.Employees;

/// <summary>Employee list (docs/04 list page): search, filters, sort, paging, export.</summary>
public sealed partial class EmployeesViewModel(PageServices services, IFileDialogService files) : PageViewModel(services), IRefreshable, ICreatesNew
{
    private const int PageSize = 200;
    private CancellationTokenSource? _debounce;
    private bool _ready;

    public override string Title => Strings.Employees_Title;

    public bool CanEdit => Can(Permission.ManageEmployees);

    public BulkObservableCollection<EmployeeListItem> Employees { get; } = [];

    public ObservableCollection<LookupItem> Departments { get; } = [];

    public static IReadOnlyList<Choice<EmployeeStatus?>> Statuses { get; } =
    [
        new(null, Strings.Employees_AllStatuses),
        new(EmployeeStatus.Active, Strings.Status_Active),
        new(EmployeeStatus.Probation, Strings.Status_Probation),
        new(EmployeeStatus.OnLeave, Strings.Status_OnLeave),
        new(EmployeeStatus.Left, Strings.Status_Left),
    ];

    [ObservableProperty]
    public partial string? Search { get; set; }

    [ObservableProperty]
    public partial Guid DepartmentId { get; set; }

    [ObservableProperty]
    public partial EmployeeStatus? Status { get; set; }

    [ObservableProperty]
    public partial bool IncludeLeft { get; set; }

    [ObservableProperty]
    public partial EmployeeSort Sort { get; set; } = EmployeeSort.Code;

    [ObservableProperty]
    public partial bool Descending { get; set; }

    [ObservableProperty]
    public partial EmployeeListItem? Selected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageText))]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand), nameof(NextPageCommand))]
    public partial int PageIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageText), nameof(HasPages))]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand), nameof(NextPageCommand))]
    public partial int PageCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText), nameof(IsEmpty))]
    public partial int TotalCount { get; set; }

    public string CountText => string.Format(CultureInfo.CurrentCulture, Strings.Employees_Count, TotalCount);

    public string PageText => string.Format(CultureInfo.CurrentCulture, Strings.Employees_Page, PageIndex + 1, Math.Max(1, PageCount));

    public bool HasPages => PageCount > 1;

    public bool IsEmpty => _ready && TotalCount == 0 && string.IsNullOrEmpty(Search) && DepartmentId == Guid.Empty && Status is null;

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        var lookups = await SendAsync(new GetOrganizationLookupsQuery()).ConfigureAwait(true);
        Departments.Clear();
        Departments.Add(new LookupItem(Guid.Empty, Strings.Employees_AllDepartments));
        if (lookups.IsSuccess)
        {
            foreach (var d in lookups.Value.Departments)
            {
                Departments.Add(d);
            }
        }

        _ready = true;
        await LoadAsync(0).ConfigureAwait(true);
    }

    public Task RefreshAsync() => LoadAsync(PageIndex);

    public Task CreateNewAsync() => NewEmployeeAsync();

    async partial void OnSearchChanged(string? value)
    {
        _debounce?.Cancel();
        _debounce = new CancellationTokenSource();
        try
        {
            await Task.Delay(300, _debounce.Token).ConfigureAwait(true);
            await LoadAsync(0).ConfigureAwait(true);
        }
        catch (TaskCanceledException)
        {
            // Superseded by a newer keystroke.
        }
    }

    async partial void OnDepartmentIdChanged(Guid value) => await ReloadIfReadyAsync().ConfigureAwait(true);

    async partial void OnStatusChanged(EmployeeStatus? value) => await ReloadIfReadyAsync().ConfigureAwait(true);

    async partial void OnIncludeLeftChanged(bool value) => await ReloadIfReadyAsync().ConfigureAwait(true);

    /// <summary>Called by the grid when a column header is clicked.</summary>
    public async Task SortByAsync(string column)
    {
        var sort = column switch
        {
            nameof(EmployeeListItem.FullName) => EmployeeSort.Name,
            nameof(EmployeeListItem.JoinDate) => EmployeeSort.JoinDate,
            nameof(EmployeeListItem.Department) => EmployeeSort.Department,
            _ => EmployeeSort.Code,
        };
        Descending = sort == Sort && !Descending;
        Sort = sort;
        await LoadAsync(0).ConfigureAwait(true);
    }

    [RelayCommand]
    private Task NewEmployeeAsync() => Services.Navigation.NavigateToAsync<EmployeeDetailViewModel>();

    [RelayCommand]
    private async Task OpenAsync(EmployeeListItem? item)
    {
        var target = item ?? Selected;
        if (target is not null)
        {
            await Services.Navigation.NavigateToAsync<EmployeeDetailViewModel>(target.Id).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private Task ImportAsync() => Services.Navigation.NavigateToAsync<EmployeeImportViewModel>();

    [RelayCommand]
    private async Task ExportAsync()
    {
        var path = files.SaveFile("Excel workbook (*.xlsx)|*.xlsx|CSV (*.csv)|*.csv", $"Employees-{DateTime.Today:yyyy-MM-dd}.xlsx");
        if (path is null)
        {
            return;
        }

        var result = await SendAsync(new ExportEmployeesQuery(path, Filter(0))).ConfigureAwait(true);
        if (result.IsSuccess)
        {
            Toast(string.Format(CultureInfo.CurrentCulture, Strings.Employees_Exported, result.Value));
            files.ShellOpen(path);
        }
    }

    private bool CanPrevious() => PageIndex > 0;

    private bool CanNext() => PageIndex + 1 < PageCount;

    [RelayCommand(CanExecute = nameof(CanPrevious))]
    private Task PreviousPageAsync() => LoadAsync(PageIndex - 1);

    [RelayCommand(CanExecute = nameof(CanNext))]
    private Task NextPageAsync() => LoadAsync(PageIndex + 1);

    private Task ReloadIfReadyAsync() => _ready ? LoadAsync(0) : Task.CompletedTask;

    private GetEmployeeListQuery Filter(int page) => new(
        Search,
        DepartmentId == Guid.Empty ? null : DepartmentId,
        null,
        Status,
        IncludeLeft,
        Sort,
        Descending,
        page,
        PageSize);

    private async Task LoadAsync(int page)
    {
        var result = await SendAsync(Filter(page)).ConfigureAwait(true);
        if (result.IsFailure)
        {
            return;
        }

        Employees.ReplaceAll(result.Value.Items);
        PageIndex = page;
        PageCount = result.Value.PageCount;
        TotalCount = result.Value.TotalCount;
    }
}
