using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SuperOfc.Application.Attendance;
using SuperOfc.Application.Common;
using SuperOfc.Application.Leave;
using SuperOfc.Desktop.Mvvm;
using SuperOfc.Desktop.Resources;
using SuperOfc.Desktop.Services;
using SuperOfc.Domain.Leave;
using SuperOfc.Domain.Security;

namespace SuperOfc.Desktop.ViewModels.Leave;

/// <summary>Leave requests: approval queue, all requests and a new-request panel with the employee's balances.</summary>
public sealed partial class LeaveViewModel(PageServices services, IFileDialogService files) : PageViewModel(services), IRefreshable, ICreatesNew
{
    private CancellationTokenSource? _debounce;
    private bool _ready;

    public override string Title => Strings.Leave_Title;

    public bool CanManage => Can(Permission.ManageLeave);

    public bool CanApprove => Can(Permission.ApproveLeave);

    public BulkObservableCollection<LeaveRequestDto> Requests { get; } = [];

    public ObservableCollection<LookupItem> Employees { get; } = [];

    public ObservableCollection<LeaveTypeDto> Types { get; } = [];

    public ObservableCollection<LeaveBalanceDto> Balances { get; } = [];

    public static IReadOnlyList<Choice<LeaveRequestStatus?>> Statuses { get; } =
    [
        new(LeaveRequestStatus.Pending, Strings.Leave_Pending),
        new(null, Strings.Leave_All),
        new(LeaveRequestStatus.Approved, Strings.LeaveStatus_Approved),
        new(LeaveRequestStatus.Rejected, Strings.LeaveStatus_Rejected),
        new(LeaveRequestStatus.Cancelled, Strings.LeaveStatus_Cancelled),
    ];

    [ObservableProperty]
    public partial LeaveRequestStatus? Status { get; set; } = LeaveRequestStatus.Pending;

    [ObservableProperty]
    public partial string? Search { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanDecide), nameof(CanCancelSelected))]
    public partial LeaveRequestDto? Selected { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    public partial bool IsCreating { get; set; }

    [ObservableProperty]
    public partial Guid NewEmployeeId { get; set; }

    [ObservableProperty]
    public partial Guid NewTypeId { get; set; }

    [ObservableProperty]
    public partial DateTime? NewFrom { get; set; } = DateTime.Today;

    [ObservableProperty]
    public partial DateTime? NewTo { get; set; } = DateTime.Today;

    [ObservableProperty]
    public partial bool NewHalfDay { get; set; }

    [ObservableProperty]
    public partial string? NewReason { get; set; }

    [ObservableProperty]
    public partial string? NewAttachment { get; set; }

    [ObservableProperty]
    public partial bool NewApproveNow { get; set; }

    public bool CanDecide => CanApprove && Selected is { Status: LeaveRequestStatus.Pending };

    public bool CanCancelSelected => CanManage && Selected is { Status: LeaveRequestStatus.Pending or LeaveRequestStatus.Approved };

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        var employees = await SendAsync(new GetEmployeePickListQuery()).ConfigureAwait(true);
        Employees.Clear();
        if (employees.IsSuccess)
        {
            foreach (var e in employees.Value)
            {
                Employees.Add(e);
            }
        }

        var types = await SendAsync(new GetLeaveTypesQuery()).ConfigureAwait(true);
        Types.Clear();
        if (types.IsSuccess)
        {
            foreach (var t in types.Value)
            {
                Types.Add(t);
            }
        }

        _ready = true;
        await LoadAsync().ConfigureAwait(true);
    }

    public Task RefreshAsync() => LoadAsync();

    public Task CreateNewAsync()
    {
        StartNew();
        return Task.CompletedTask;
    }

    async partial void OnStatusChanged(LeaveRequestStatus? value) => await ReloadAsync().ConfigureAwait(true);

    async partial void OnSearchChanged(string? value)
    {
        _debounce?.Cancel();
        _debounce = new CancellationTokenSource();
        try
        {
            await Task.Delay(300, _debounce.Token).ConfigureAwait(true);
            await ReloadAsync().ConfigureAwait(true);
        }
        catch (TaskCanceledException)
        {
            // Superseded by a newer keystroke.
        }
    }

    async partial void OnSelectedChanged(LeaveRequestDto? value)
    {
        if (value is not null && !IsCreating)
        {
            await LoadBalancesAsync(value.EmployeeId).ConfigureAwait(true);
        }
    }

    async partial void OnNewEmployeeIdChanged(Guid value)
    {
        if (IsCreating && value != Guid.Empty)
        {
            await LoadBalancesAsync(value).ConfigureAwait(true);
        }
    }

    partial void OnNewFromChanged(DateTime? value)
    {
        if (value is { } from && (NewTo is null || NewTo < from))
        {
            NewTo = from;
        }
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private void StartNew()
    {
        Selected = null;
        NewEmployeeId = Employees.FirstOrDefault()?.Id ?? Guid.Empty;
        NewTypeId = Types.FirstOrDefault()?.Id ?? Guid.Empty;
        NewFrom = NewTo = DateTime.Today;
        NewHalfDay = false;
        NewReason = NewAttachment = null;
        NewApproveNow = CanApprove;
        ErrorMessage = null;
        IsCreating = true;
        _ = LoadBalancesAsync(NewEmployeeId);
    }

    [RelayCommand]
    private void CancelNew() => IsCreating = false;

    [RelayCommand]
    private void ChooseAttachment()
    {
        var path = files.OpenFile("Documents and images (*.pdf;*.jpg;*.jpeg;*.png)|*.pdf;*.jpg;*.jpeg;*.png|All files (*.*)|*.*");
        if (path is not null)
        {
            NewAttachment = path;
        }
    }

    [RelayCommand]
    private async Task SubmitAsync()
    {
        if (NewFrom is null || NewTo is null)
        {
            return;
        }

        var result = await SendAsync(new CreateLeaveRequestCommand(
            NewEmployeeId, NewTypeId, DateOnly.FromDateTime(NewFrom.Value), DateOnly.FromDateTime(NewHalfDay ? NewFrom.Value : NewTo.Value),
            NewHalfDay, NewReason, NewAttachment, NewApproveNow && CanApprove)).ConfigureAwait(true);
        if (result.IsSuccess)
        {
            IsCreating = false;
            Toast(NewApproveNow ? Strings.Leave_Approved : Strings.Leave_Created);
            await LoadAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private Task ApproveAsync(LeaveRequestDto? request) => DecideAsync(request ?? Selected, LeaveDecision.Approve);

    [RelayCommand]
    private Task RejectAsync(LeaveRequestDto? request) => DecideAsync(request ?? Selected, LeaveDecision.Reject);

    [RelayCommand]
    private Task CancelLeaveAsync(LeaveRequestDto? request) => DecideAsync(request ?? Selected, LeaveDecision.Cancel);

    private async Task DecideAsync(LeaveRequestDto? request, LeaveDecision decision)
    {
        if (request is null)
        {
            return;
        }

        string? note = null;
        if (decision != LeaveDecision.Approve)
        {
            var title = decision == LeaveDecision.Reject ? Strings.Leave_RejectTitle : Strings.Leave_CancelTitle;
            note = await Services.Dialogs.PromptAsync(title, $"{request.EmployeeName} · {request.LeaveType} · {request.FromDate:d}", Strings.Leave_NoteLabel).ConfigureAwait(true);
            if (note is null)
            {
                return;
            }
        }

        var result = await SendAsync(new DecideLeaveRequestCommand(request.Id, decision, note)).ConfigureAwait(true);
        if (result.IsSuccess)
        {
            if (decision == LeaveDecision.Approve)
            {
                Toast(Strings.Leave_Approved);
            }

            await LoadAsync().ConfigureAwait(true);
            await LoadBalancesAsync(request.EmployeeId).ConfigureAwait(true);
        }
    }

    private Task ReloadAsync() => _ready ? LoadAsync() : Task.CompletedTask;

    /// <summary>Shown when any payroll is finalized: leave on those dates cannot change (P4-T4).</summary>
    [ObservableProperty]
    public partial string? LockText { get; set; }

    private async Task LoadAsync()
    {
        var lockedUntil = await PayrollLockedUntilAsync().ConfigureAwait(true);
        LockText = lockedUntil is { } until ? string.Format(CultureInfo.CurrentCulture, Strings.Lock_Leave, Format(until)) : null;
        var result = await SendAsync(new GetLeaveRequestsQuery(Status, Search: Search)).ConfigureAwait(true);
        if (result.IsFailure)
        {
            return;
        }

        var selectedId = Selected?.Id;
        Requests.ReplaceAll(result.Value);
        Selected = Requests.FirstOrDefault(r => r.Id == selectedId);
        IsEmpty = Requests.Count == 0;
    }

    private async Task LoadBalancesAsync(Guid employeeId)
    {
        var result = await SendAsync(new GetLeaveBalancesQuery(employeeId), showErrors: false).ConfigureAwait(true);
        Balances.Clear();
        if (result.IsSuccess)
        {
            foreach (var b in result.Value)
            {
                Balances.Add(b);
            }
        }
    }
}
