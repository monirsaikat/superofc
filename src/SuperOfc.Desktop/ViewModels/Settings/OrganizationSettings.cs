using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SuperOfc.Application.Audit;
using SuperOfc.Application.Common;
using SuperOfc.Application.Organization;
using SuperOfc.Desktop.Mvvm;
using SuperOfc.Desktop.Resources;
using SuperOfc.Domain.Organization;
using SuperOfc.Domain.Security;

namespace SuperOfc.Desktop.ViewModels.Settings;

/// <summary>Departments, designations, locations and grades on one page.</summary>
public sealed partial class OrganizationSettingsViewModel(PageServices services) : PageViewModel(services)
{
    public override string Title => Strings.Settings_Organization;

    public static IReadOnlyList<Choice<MasterDataKind>> Kinds { get; } =
    [
        new(MasterDataKind.Department, Strings.Org_Departments),
        new(MasterDataKind.Designation, Strings.Org_Designations),
        new(MasterDataKind.Location, Strings.Org_Locations),
        new(MasterDataKind.Grade, Strings.Org_Grades),
    ];

    public bool CanEdit => Can(Permission.ManageEmployees);

    public ObservableCollection<MasterDataDto> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDesignation))]
    public partial MasterDataKind Kind { get; set; } = MasterDataKind.Department;

    /// <summary>A designation's description lists its responsibilities, one per line, for <c>{Employee.Responsibilities}</c> in letters.</summary>
    public bool IsDesignation => Kind == MasterDataKind.Designation;

    [ObservableProperty]
    public partial MasterDataDto? Selected { get; set; }

    [ObservableProperty]
    public partial bool IsEditing { get; set; }

    [ObservableProperty]
    public partial string EditName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? EditCode { get; set; }

    [ObservableProperty]
    public partial string? EditDescription { get; set; }

    [ObservableProperty]
    public partial int EditSortOrder { get; set; }

    private Guid? _editId;

    public override Task OnNavigatedToAsync(object? parameter) => LoadAsync();

    async partial void OnKindChanged(MasterDataKind value)
    {
        IsEditing = false;
        await LoadAsync().ConfigureAwait(true);
    }

    partial void OnSelectedChanged(MasterDataDto? value)
    {
        if (value is null || !CanEdit)
        {
            return;
        }

        _editId = value.Id;
        EditName = value.Name;
        EditCode = value.Code;
        EditDescription = value.Description;
        EditSortOrder = value.SortOrder;
        IsEditing = true;
        ErrorMessage = null;
    }

    [RelayCommand]
    private void New()
    {
        Selected = null;
        _editId = null;
        EditName = string.Empty;
        EditCode = EditDescription = null;
        EditSortOrder = Items.Count;
        IsEditing = true;
        ErrorMessage = null;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var result = await SendAsync(new SaveMasterDataCommand(Kind, _editId, EditName, EditCode, EditDescription, EditSortOrder)).ConfigureAwait(true);
        if (result.IsSuccess)
        {
            IsEditing = false;
            Toast(Strings.App_Saved);
            await LoadAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (_editId is not { } id)
        {
            return;
        }

        var ok = await Services.Dialogs.ConfirmAsync(
            string.Format(CultureInfo.CurrentCulture, Strings.Org_DeleteTitle, EditName),
            Strings.Org_DeleteMessage,
            Strings.App_Delete,
            danger: true).ConfigureAwait(true);
        if (!ok)
        {
            return;
        }

        var result = await SendAsync(new DeleteMasterDataCommand(Kind, id)).ConfigureAwait(true);
        if (result.IsSuccess)
        {
            IsEditing = false;
            await LoadAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
        Selected = null;
    }

    private async Task LoadAsync()
    {
        var result = await SendAsync(new GetMasterDataQuery(Kind)).ConfigureAwait(true);
        Items.Clear();
        if (result.IsSuccess)
        {
            foreach (var item in result.Value)
            {
                Items.Add(item);
            }
        }
    }
}

public sealed partial class HolidaysSettingsViewModel(PageServices services) : PageViewModel(services)
{
    public override string Title => Strings.Settings_Holidays;

    public bool CanEdit => Can(Permission.ManageAttendance);

    public ObservableCollection<HolidayDto> Holidays { get; } = [];

    public ObservableCollection<LookupItem> Locations { get; } = [];

    public IReadOnlyList<int> Years { get; } = Enumerable.Range(DateTime.Today.Year - 3, 6).ToList();

    [ObservableProperty]
    public partial int Year { get; set; } = DateTime.Today.Year;

    [ObservableProperty]
    public partial HolidayDto? Selected { get; set; }

    [ObservableProperty]
    public partial bool IsEditing { get; set; }

    [ObservableProperty]
    public partial DateTime? EditDate { get; set; }

    [ObservableProperty]
    public partial string EditName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool EditPaid { get; set; } = true;

    [ObservableProperty]
    public partial Guid EditLocationId { get; set; }

    private Guid? _editId;

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        var lookups = await SendAsync(new GetOrganizationLookupsQuery()).ConfigureAwait(true);
        Locations.Clear();
        Locations.Add(new LookupItem(Guid.Empty, Strings.Holidays_AllLocations));
        if (lookups.IsSuccess)
        {
            foreach (var l in lookups.Value.Locations)
            {
                Locations.Add(l);
            }
        }

        await LoadAsync().ConfigureAwait(true);
    }

    async partial void OnYearChanged(int value) => await LoadAsync().ConfigureAwait(true);

    partial void OnSelectedChanged(HolidayDto? value)
    {
        if (value is null || !CanEdit)
        {
            return;
        }

        _editId = value.Id;
        EditDate = value.Date.ToDateTime(TimeOnly.MinValue);
        EditName = value.Name;
        EditPaid = value.IsPaid;
        EditLocationId = value.LocationId ?? Guid.Empty;
        IsEditing = true;
        ErrorMessage = null;
    }

    [RelayCommand]
    private void New()
    {
        Selected = null;
        _editId = null;
        EditDate = new DateTime(Year, DateTime.Today.Month, 1);
        EditName = string.Empty;
        EditPaid = true;
        EditLocationId = Guid.Empty;
        IsEditing = true;
        ErrorMessage = null;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (EditDate is null)
        {
            ErrorMessage = Strings.Holidays_Date;
            return;
        }

        var result = await SendAsync(new SaveHolidayCommand(_editId, DateOnly.FromDateTime(EditDate.Value), EditName, EditPaid, EditLocationId == Guid.Empty ? null : EditLocationId)).ConfigureAwait(true);
        if (result.IsSuccess)
        {
            IsEditing = false;
            Toast(Strings.App_Saved);
            await LoadAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (_editId is not { } id || !await Services.Dialogs.ConfirmAsync(Strings.Holidays_DeleteTitle, EditName, Strings.App_Delete, danger: true).ConfigureAwait(true))
        {
            return;
        }

        var result = await SendAsync(new DeleteHolidayCommand(id)).ConfigureAwait(true);
        if (result.IsSuccess)
        {
            IsEditing = false;
            await LoadAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task CopyPreviousAsync()
    {
        var result = await SendAsync(new CopyHolidaysFromPreviousYearCommand(Year)).ConfigureAwait(true);
        if (result.IsSuccess)
        {
            Toast(string.Format(CultureInfo.CurrentCulture, Strings.Holidays_Copied, result.Value));
            await LoadAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
        Selected = null;
    }

    private async Task LoadAsync()
    {
        var result = await SendAsync(new GetHolidaysQuery(Year)).ConfigureAwait(true);
        Holidays.Clear();
        if (result.IsSuccess)
        {
            foreach (var h in result.Value)
            {
                Holidays.Add(h);
            }
        }
    }
}

public sealed partial class AuditLogViewModel(PageServices services) : PageViewModel(services), IRefreshable
{
    private CancellationTokenSource? _debounce;

    public override string Title => Strings.Settings_AuditLog;

    public ObservableCollection<AuditEntryDto> Entries { get; } = [];

    public ObservableCollection<string> Users { get; } = [];

    [ObservableProperty]
    public partial string? Search { get; set; }

    [ObservableProperty]
    public partial string? UserName { get; set; }

    [ObservableProperty]
    public partial DateTime? From { get; set; }

    [ObservableProperty]
    public partial DateTime? To { get; set; }

    [ObservableProperty]
    public partial AuditEntryDto? Selected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageText))]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand), nameof(NextPageCommand))]
    public partial int PageIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageText))]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand), nameof(NextPageCommand))]
    public partial int PageCount { get; set; }

    [ObservableProperty]
    public partial int TotalCount { get; set; }

    public string PageText => string.Format(CultureInfo.CurrentCulture, Strings.Employees_Page, PageIndex + 1, Math.Max(1, PageCount));

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        var users = await SendAsync(new GetAuditUsersQuery()).ConfigureAwait(true);
        Users.Clear();
        Users.Add(string.Empty);
        if (users.IsSuccess)
        {
            foreach (var u in users.Value)
            {
                Users.Add(u);
            }
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    public Task RefreshAsync() => LoadAsync(0);

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
            // A newer keystroke replaced this search.
        }
    }

    async partial void OnUserNameChanged(string? value) => await LoadAsync(0).ConfigureAwait(true);

    async partial void OnFromChanged(DateTime? value) => await LoadAsync(0).ConfigureAwait(true);

    async partial void OnToChanged(DateTime? value) => await LoadAsync(0).ConfigureAwait(true);

    private bool CanPrevious() => PageIndex > 0;

    private bool CanNext() => PageIndex + 1 < PageCount;

    [RelayCommand(CanExecute = nameof(CanPrevious))]
    private Task PreviousPageAsync() => LoadAsync(PageIndex - 1);

    [RelayCommand(CanExecute = nameof(CanNext))]
    private Task NextPageAsync() => LoadAsync(PageIndex + 1);

    private async Task LoadAsync(int page)
    {
        var result = await SendAsync(new GetAuditLogQuery(
            Search,
            string.IsNullOrEmpty(UserName) ? null : UserName,
            From is null ? null : DateOnly.FromDateTime(From.Value),
            To is null ? null : DateOnly.FromDateTime(To.Value),
            null,
            page,
            200)).ConfigureAwait(true);
        if (result.IsFailure)
        {
            return;
        }

        Entries.Clear();
        foreach (var e in result.Value.Items)
        {
            Entries.Add(e);
        }

        PageIndex = page;
        PageCount = result.Value.PageCount;
        TotalCount = result.Value.TotalCount;
    }
}
