using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SuperOfc.Application.Attendance;
using SuperOfc.Application.Leave;
using SuperOfc.Desktop.Mvvm;
using SuperOfc.Desktop.Resources;
using SuperOfc.Domain.Security;

namespace SuperOfc.Desktop.ViewModels.Employees;

/// <summary>Employee › Attendance: current shift, shift history, change shift, this month's figures.</summary>
public sealed partial class EmployeeAttendanceTabViewModel(PageServices services) : EmployeeTabViewModel(services)
{
    private Guid _employeeId;

    public override string Header => Strings.EmpTab_Attendance;

    public override string Title => Header;

    public bool CanEdit => Can(Permission.ManageAttendance);

    public ObservableCollection<ShiftAssignmentDto> Assignments { get; } = [];

    public ObservableCollection<ShiftDto> Shifts { get; } = [];

    public ObservableCollection<ShiftDto> RotationShifts { get; } = [];

    [ObservableProperty]
    public partial string? CurrentShift { get; set; }

    [ObservableProperty]
    public partial string? MonthSummary { get; set; }

    [ObservableProperty]
    public partial bool IsAssigning { get; set; }

    [ObservableProperty]
    public partial Guid AssignShiftId { get; set; }

    [ObservableProperty]
    public partial Guid RotateWithId { get; set; }

    [ObservableProperty]
    public partial int RotationDays { get; set; } = 7;

    [ObservableProperty]
    public partial DateTime? AssignFrom { get; set; } = DateTime.Today;

    [ObservableProperty]
    public partial DateTime? AssignTo { get; set; }

    public override async Task LoadAsync(Guid employeeId)
    {
        _employeeId = employeeId;
        var shifts = await SendAsync(new GetShiftsQuery(), showErrors: false).ConfigureAwait(true);
        Shifts.Clear();
        RotationShifts.Clear();
        RotationShifts.Add(new ShiftDto(Guid.Empty, Strings.Employee_None, null, default, default, 0, 0, 0, 0, 0, 0, false, 0));
        if (shifts.IsSuccess)
        {
            foreach (var s in shifts.Value)
            {
                Shifts.Add(s);
                RotationShifts.Add(s);
            }
        }

        var history = await SendAsync(new GetShiftAssignmentsQuery(employeeId), showErrors: false).ConfigureAwait(true);
        Assignments.Clear();
        var today = DateOnly.FromDateTime(DateTime.Today);
        ShiftAssignmentDto? current = null;
        if (history.IsSuccess)
        {
            foreach (var a in history.Value)
            {
                Assignments.Add(a);
                if (current is null && a.EffectiveFrom <= today && (a.EffectiveTo is null || a.EffectiveTo >= today))
                {
                    current = a;
                }
            }
        }

        var fallback = Shifts.FirstOrDefault(s => s.IsDefault);
        CurrentShift = current is not null
            ? $"{current.Description}"
            : fallback is null ? Strings.Employee_None : $"{fallback.Name} {fallback.TimeText} {Strings.EmpAtt_Default}";

        var month = await SendAsync(new GetAttendanceMonthQuery(today.Year, today.Month, EmployeeId: employeeId), showErrors: false).ConfigureAwait(true);
        var row = month.IsSuccess ? month.Value.Rows.FirstOrDefault() : null;
        MonthSummary = row is null
            ? null
            : $"{Strings.Attendance_Present} {row.Present} · {Strings.Attendance_Late} {row.Late} · {Strings.Attendance_HalfDays} {row.HalfDays} · {Strings.Attendance_Absent} {row.Absent} · {Strings.Attendance_Leave} {row.Leave:0.#} · OT {row.OvertimeMinutes / 60m:0.#} h";
    }

    [RelayCommand]
    private void StartAssign()
    {
        AssignShiftId = Shifts.FirstOrDefault()?.Id ?? Guid.Empty;
        RotateWithId = Guid.Empty;
        RotationDays = 7;
        AssignFrom = DateTime.Today;
        AssignTo = null;
        ErrorMessage = null;
        IsAssigning = true;
    }

    [RelayCommand]
    private void CancelAssign() => IsAssigning = false;

    [RelayCommand]
    private async Task SaveAssignAsync()
    {
        if (AssignFrom is null || AssignShiftId == Guid.Empty)
        {
            return;
        }

        IReadOnlyList<Guid>? rotation = RotateWithId != Guid.Empty && RotateWithId != AssignShiftId ? [AssignShiftId, RotateWithId] : null;
        var result = await SendAsync(new AssignShiftCommand([_employeeId], AssignShiftId, DateOnly.FromDateTime(AssignFrom.Value),
            AssignTo is null ? null : DateOnly.FromDateTime(AssignTo.Value), rotation, RotationDays)).ConfigureAwait(true);
        if (result.IsSuccess)
        {
            IsAssigning = false;
            Toast(Strings.App_Saved);
            await LoadAsync(_employeeId).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task DeleteAssignmentAsync(ShiftAssignmentDto? assignment)
    {
        if (assignment is null || !await Services.Dialogs.ConfirmAsync(Strings.App_Delete, assignment.Description, Strings.App_Delete, danger: true).ConfigureAwait(true))
        {
            return;
        }

        if ((await SendAsync(new DeleteShiftAssignmentCommand(assignment.Id)).ConfigureAwait(true)).IsSuccess)
        {
            await LoadAsync(_employeeId).ConfigureAwait(true);
        }
    }
}

/// <summary>Employee › Leave: balances with adjustments and the employee's requests.</summary>
public sealed partial class EmployeeLeaveTabViewModel(PageServices services) : EmployeeTabViewModel(services)
{
    private Guid _employeeId;

    public override string Header => Strings.EmpTab_Leave;

    public override string Title => Header;

    public bool CanEdit => Can(Permission.ManageLeave);

    public ObservableCollection<LeaveBalanceDto> Balances { get; } = [];

    public ObservableCollection<LeaveRequestDto> Requests { get; } = [];

    public override async Task LoadAsync(Guid employeeId)
    {
        _employeeId = employeeId;
        var balances = await SendAsync(new GetLeaveBalancesQuery(employeeId), showErrors: false).ConfigureAwait(true);
        Balances.Clear();
        if (balances.IsSuccess)
        {
            foreach (var b in balances.Value)
            {
                Balances.Add(b);
            }
        }

        var requests = await SendAsync(new GetLeaveRequestsQuery(EmployeeId: employeeId), showErrors: false).ConfigureAwait(true);
        Requests.Clear();
        if (requests.IsSuccess)
        {
            foreach (var r in requests.Value)
            {
                Requests.Add(r);
            }
        }
    }

    [RelayCommand]
    private async Task AdjustAsync(LeaveBalanceDto? balance)
    {
        if (balance is null)
        {
            return;
        }

        var title = string.Format(CultureInfo.CurrentCulture, Strings.Leave_AdjustTitle, balance.LeaveType);
        var daysText = await Services.Dialogs.PromptAsync(title, $"{Strings.Leave_Available}: {balance.Closing:0.#}", Strings.Leave_AdjustDays).ConfigureAwait(true);
        if (daysText is null)
        {
            return;
        }

        if (!decimal.TryParse(daysText, NumberStyles.Number, CultureInfo.CurrentCulture, out var days) && !decimal.TryParse(daysText, NumberStyles.Number, CultureInfo.InvariantCulture, out days))
        {
            ErrorMessage = Strings.Leave_AdjustDays;
            return;
        }

        var reason = await Services.Dialogs.PromptAsync(title, $"{days:+0.#;-0.#} {Strings.Leave_Days}", Strings.Leave_Reason).ConfigureAwait(true);
        if (reason is null)
        {
            return;
        }

        if ((await SendAsync(new AdjustLeaveBalanceCommand(_employeeId, balance.LeaveTypeId, balance.Year, days, reason)).ConfigureAwait(true)).IsSuccess)
        {
            Toast(Strings.App_Saved);
            await LoadAsync(_employeeId).ConfigureAwait(true);
        }
    }
}
