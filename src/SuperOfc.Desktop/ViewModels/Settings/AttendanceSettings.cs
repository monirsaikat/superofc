using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SuperOfc.Application.Attendance;
using SuperOfc.Application.Common;
using SuperOfc.Application.Employees;
using SuperOfc.Application.Leave;
using SuperOfc.Application.Organization;
using SuperOfc.Desktop.Mvvm;
using SuperOfc.Desktop.Resources;
using SuperOfc.Domain.Leave;
using SuperOfc.Domain.Policies;
using SuperOfc.Domain.Security;

namespace SuperOfc.Desktop.ViewModels.Settings;

public sealed partial class ShiftsSettingsViewModel(PageServices services) : PageViewModel(services)
{
    private Guid? _editId;

    public override string Title => Strings.Settings_Shifts;

    public bool CanEdit => Can(Permission.ManageAttendance);

    public ObservableCollection<ShiftDto> Shifts { get; } = [];

    public ObservableCollection<LookupItem> Departments { get; } = [];

    [ObservableProperty]
    public partial ShiftDto? Selected { get; set; }

    [ObservableProperty]
    public partial bool IsEditing { get; set; }

    [ObservableProperty]
    public partial string EditName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? EditCode { get; set; }

    [ObservableProperty]
    public partial string EditStart { get; set; } = "09:00";

    [ObservableProperty]
    public partial string EditEnd { get; set; } = "18:00";

    [ObservableProperty]
    public partial int EditGraceIn { get; set; } = 10;

    [ObservableProperty]
    public partial int EditGraceOut { get; set; }

    [ObservableProperty]
    public partial int EditBreak { get; set; } = 60;

    [ObservableProperty]
    public partial int EditHalfDay { get; set; } = 240;

    [ObservableProperty]
    public partial int EditFullDay { get; set; } = 420;

    [ObservableProperty]
    public partial int EditOvertimeAfter { get; set; }

    [ObservableProperty]
    public partial bool EditIsDefault { get; set; }

    [ObservableProperty]
    public partial Guid AssignDepartmentId { get; set; }

    [ObservableProperty]
    public partial Guid AssignShiftId { get; set; }

    [ObservableProperty]
    public partial DateTime? AssignFrom { get; set; } = DateTime.Today;

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        var lookups = await SendAsync(new GetOrganizationLookupsQuery()).ConfigureAwait(true);
        Departments.Clear();
        if (lookups.IsSuccess)
        {
            foreach (var d in lookups.Value.Departments)
            {
                Departments.Add(d);
            }
        }

        AssignDepartmentId = Departments.FirstOrDefault()?.Id ?? Guid.Empty;
        await LoadAsync().ConfigureAwait(true);
    }

    partial void OnSelectedChanged(ShiftDto? value)
    {
        if (value is null || !CanEdit)
        {
            return;
        }

        _editId = value.Id;
        EditName = value.Name;
        EditCode = value.Code;
        EditStart = value.StartTime.ToString("HH:mm", CultureInfo.InvariantCulture);
        EditEnd = value.EndTime.ToString("HH:mm", CultureInfo.InvariantCulture);
        EditGraceIn = value.GraceInMinutes;
        EditGraceOut = value.GraceOutMinutes;
        EditBreak = value.BreakMinutes;
        EditHalfDay = value.MinimumMinutesForHalfDay;
        EditFullDay = value.MinimumMinutesForFullDay;
        EditOvertimeAfter = value.OvertimeStartsAfterMinutes;
        EditIsDefault = value.IsDefault;
        IsEditing = true;
        ErrorMessage = null;
    }

    [RelayCommand]
    private void New()
    {
        Selected = null;
        _editId = null;
        EditName = string.Empty;
        EditCode = null;
        EditStart = "09:00";
        EditEnd = "18:00";
        EditGraceIn = 10;
        EditGraceOut = 0;
        EditBreak = 60;
        EditHalfDay = 240;
        EditFullDay = 420;
        EditOvertimeAfter = 0;
        EditIsDefault = false;
        IsEditing = true;
        ErrorMessage = null;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!TryParseTime(EditStart, out var start) || !TryParseTime(EditEnd, out var end))
        {
            ErrorMessage = Strings.Shifts_InvalidTime;
            return;
        }

        var result = await SendAsync(new SaveShiftCommand(_editId, EditName, EditCode, start, end, EditGraceIn, EditGraceOut, EditBreak, EditHalfDay, EditFullDay, EditOvertimeAfter, EditIsDefault))
            .ConfigureAwait(true);
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
        if (_editId is not { } id || !await Services.Dialogs.ConfirmAsync(string.Format(CultureInfo.CurrentCulture, Strings.Shifts_DeleteTitle, EditName), EditName, Strings.App_Delete, danger: true).ConfigureAwait(true))
        {
            return;
        }

        if ((await SendAsync(new DeleteShiftCommand(id)).ConfigureAwait(true)).IsSuccess)
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

    [RelayCommand]
    private async Task AssignToDepartmentAsync()
    {
        if (AssignDepartmentId == Guid.Empty || AssignShiftId == Guid.Empty || AssignFrom is null)
        {
            return;
        }

        var employees = await SendAsync(new GetEmployeeListQuery(DepartmentId: AssignDepartmentId, PageSize: 1000)).ConfigureAwait(true);
        if (employees.IsFailure || employees.Value.Items.Count == 0)
        {
            return;
        }

        var ids = employees.Value.Items.Select(e => e.Id).ToList();
        var result = await SendAsync(new AssignShiftCommand(ids, AssignShiftId, DateOnly.FromDateTime(AssignFrom.Value), null)).ConfigureAwait(true);
        if (result.IsSuccess)
        {
            Toast(string.Format(CultureInfo.CurrentCulture, Strings.Shifts_Assigned, result.Value));
            await LoadAsync().ConfigureAwait(true);
        }
    }

    internal static bool TryParseTime(string? text, out TimeOnly time)
        => TimeOnly.TryParseExact(text?.Trim(), ["HH:mm", "H:mm", "HHmm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    private async Task LoadAsync()
    {
        var result = await SendAsync(new GetShiftsQuery()).ConfigureAwait(true);
        Shifts.Clear();
        if (result.IsSuccess)
        {
            foreach (var s in result.Value)
            {
                Shifts.Add(s);
            }
        }

        if (AssignShiftId == Guid.Empty || Shifts.All(s => s.Id != AssignShiftId))
        {
            AssignShiftId = Shifts.FirstOrDefault()?.Id ?? Guid.Empty;
        }
    }
}

public sealed partial class LeaveTypesSettingsViewModel(PageServices services) : PageViewModel(services)
{
    private Guid? _editId;

    public override string Title => Strings.Settings_LeaveTypes;

    public bool CanEdit => Can(Permission.ManageLeave);

    public ObservableCollection<LeaveTypeDto> Types { get; } = [];

    public static IReadOnlyList<Choice<LeaveAccrual>> Accruals { get; } =
    [
        new(LeaveAccrual.Yearly, Strings.Accrual_Yearly),
        new(LeaveAccrual.Monthly, Strings.Accrual_Monthly),
    ];

    [ObservableProperty]
    public partial LeaveTypeDto? Selected { get; set; }

    [ObservableProperty]
    public partial bool IsEditing { get; set; }

    [ObservableProperty]
    public partial string EditName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EditCode { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool EditPaid { get; set; } = true;

    [ObservableProperty]
    public partial double EditEntitlement { get; set; }

    [ObservableProperty]
    public partial LeaveAccrual EditAccrual { get; set; }

    [ObservableProperty]
    public partial double EditCarryCap { get; set; }

    [ObservableProperty]
    public partial bool EditHalfDay { get; set; } = true;

    [ObservableProperty]
    public partial bool EditAttachment { get; set; }

    [ObservableProperty]
    public partial bool EditNegative { get; set; }

    public override Task OnNavigatedToAsync(object? parameter) => LoadAsync();

    partial void OnSelectedChanged(LeaveTypeDto? value)
    {
        if (value is null || !CanEdit)
        {
            return;
        }

        _editId = value.Id;
        EditName = value.Name;
        EditCode = value.Code;
        EditPaid = value.IsPaid;
        EditEntitlement = (double)value.YearlyEntitlement;
        EditAccrual = value.Accrual;
        EditCarryCap = (double)value.CarryForwardCap;
        EditHalfDay = value.AllowsHalfDay;
        EditAttachment = value.RequiresAttachment;
        EditNegative = value.AllowNegativeBalance;
        IsEditing = true;
        ErrorMessage = null;
    }

    [RelayCommand]
    private void New()
    {
        Selected = null;
        _editId = null;
        EditName = EditCode = string.Empty;
        EditPaid = EditHalfDay = true;
        EditEntitlement = EditCarryCap = 0;
        EditAccrual = LeaveAccrual.Yearly;
        EditAttachment = EditNegative = false;
        IsEditing = true;
        ErrorMessage = null;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var result = await SendAsync(new SaveLeaveTypeCommand(_editId, EditName, EditCode, EditPaid, (decimal)EditEntitlement, EditAccrual, (decimal)EditCarryCap,
            EditHalfDay, EditAttachment, EditNegative, _editId is null ? Types.Count : Selected?.SortOrder ?? 0)).ConfigureAwait(true);
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
        if (_editId is not { } id || !await Services.Dialogs.ConfirmAsync(string.Format(CultureInfo.CurrentCulture, Strings.LeaveTypes_DeleteTitle, EditName), EditName, Strings.App_Delete, danger: true).ConfigureAwait(true))
        {
            return;
        }

        if ((await SendAsync(new DeleteLeaveTypeCommand(id)).ConfigureAwait(true)).IsSuccess)
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
        var result = await SendAsync(new GetLeaveTypesQuery()).ConfigureAwait(true);
        Types.Clear();
        if (result.IsSuccess)
        {
            foreach (var t in result.Value)
            {
                Types.Add(t);
            }
        }
    }
}

/// <summary>Attendance section on Settings › Policies.</summary>
public sealed partial class AttendancePolicyEditor : PolicyEditorBase
{
    public AttendancePolicyEditor(AttendancePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        PunchPairing = policy.PunchPairing;
        MissingPunch = policy.MissingPunch;
        OvertimeEnabled = policy.OvertimeEnabled;
        OvertimeRoundingMinutes = policy.OvertimeRoundingMinutes;
        OvertimeOnHolidays = policy.OvertimeOnHolidays;
        WeeklyOffsPaid = policy.WeeklyOffsPaid;
        HolidaysPaid = policy.HolidaysPaid;
        LateInstances = policy.LatePenalty.Instances;
        LateDeductDays = (double)policy.LatePenalty.DeductDays;
        DuplicatePunchMinutes = policy.DuplicatePunchMinutes;
        _windowBefore = policy.WindowBeforeShiftHours;
        _windowAfter = policy.WindowAfterShiftHours;
    }

    private readonly int _windowBefore;
    private readonly int _windowAfter;

    public override string Section => PolicySnapshot.AttendanceSection;

    public override string Header => Strings.PolicyAtt_Header;

    public static IReadOnlyList<Choice<PunchPairing>> Pairings { get; } =
    [
        new(PunchPairing.FirstLast, Strings.PolicyAtt_FirstLast),
        new(PunchPairing.Alternating, Strings.PolicyAtt_Alternating),
    ];

    public static IReadOnlyList<Choice<MissingPunchRule>> MissingRules { get; } =
    [
        new(MissingPunchRule.TreatAsHalfDay, Strings.PolicyAtt_MissingHalf),
        new(MissingPunchRule.TreatAsAbsent, Strings.PolicyAtt_MissingAbsent),
        new(MissingPunchRule.TreatAsPresent, Strings.PolicyAtt_MissingPresent),
    ];

    [ObservableProperty]
    public partial PunchPairing PunchPairing { get; set; }

    [ObservableProperty]
    public partial MissingPunchRule MissingPunch { get; set; }

    [ObservableProperty]
    public partial bool OvertimeEnabled { get; set; }

    [ObservableProperty]
    public partial int OvertimeRoundingMinutes { get; set; }

    [ObservableProperty]
    public partial bool OvertimeOnHolidays { get; set; }

    [ObservableProperty]
    public partial bool WeeklyOffsPaid { get; set; }

    [ObservableProperty]
    public partial bool HolidaysPaid { get; set; }

    [ObservableProperty]
    public partial int LateInstances { get; set; }

    [ObservableProperty]
    public partial double LateDeductDays { get; set; }

    [ObservableProperty]
    public partial int DuplicatePunchMinutes { get; set; }

    /// <summary>Try-out punches for the preview line (general 09:00-18:00 shift, 60 minute break, 10 minute grace).</summary>
    [ObservableProperty]
    public partial string SampleIn { get; set; } = "09:20";

    [ObservableProperty]
    public partial string? SampleOut { get; set; } = "17:30";

    public string SampleResult
    {
        get
        {
            if (!ShiftsSettingsViewModel.TryParseTime(SampleIn, out var inTime))
            {
                return Strings.Shifts_InvalidTime;
            }

            var date = new DateOnly(2026, 1, 5);
            var punches = new List<DateTime> { date.ToDateTime(inTime) };
            if (ShiftsSettingsViewModel.TryParseTime(SampleOut, out var outTime))
            {
                punches.Add(date.ToDateTime(outTime));
            }

            var shift = new Domain.Attendance.ShiftSpec(Guid.Empty, "General", new TimeOnly(9, 0), 540, 10, 0, 60, 240, 420, 0);
            var result = Domain.Attendance.AttendanceCalculator.Compute(
                new Domain.Attendance.DayInput(date, date.AddYears(-1), null, true, shift, Domain.Organization.DayKind.Working, null, punches),
                (AttendancePolicy)Build());
            var text = $"{Strings.Get($"AttStatus_{result.Status}")} · {Strings.Attendance_Worked} {result.WorkedMinutes / 60}:{result.WorkedMinutes % 60:00}";
            if (result.LateMinutes > 0)
            {
                text += $" · {Strings.Attendance_Late} {result.LateMinutes}m";
            }

            if (result.OvertimeMinutes > 0)
            {
                text += $" · OT {result.OvertimeMinutes}m";
            }

            return text;
        }
    }

    protected override bool IsPolicyProperty(string? propertyName)
        => propertyName is not (nameof(SampleIn) or nameof(SampleOut) or nameof(SampleResult));

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e?.PropertyName is not nameof(SampleResult))
        {
            base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(SampleResult)));
        }
    }

    public override object Build() => new AttendancePolicy
    {
        PunchPairing = PunchPairing,
        MissingPunch = MissingPunch,
        OvertimeEnabled = OvertimeEnabled,
        OvertimeRoundingMinutes = Math.Clamp(OvertimeRoundingMinutes, 1, 120),
        OvertimeOnHolidays = OvertimeOnHolidays,
        WeeklyOffsPaid = WeeklyOffsPaid,
        HolidaysPaid = HolidaysPaid,
        LatePenalty = new LatePenalty(Math.Clamp(LateInstances, 0, 31), (decimal)Math.Clamp(LateDeductDays, 0, 5)),
        DuplicatePunchMinutes = Math.Clamp(DuplicatePunchMinutes, 0, 30),
        WindowBeforeShiftHours = _windowBefore,
        WindowAfterShiftHours = _windowAfter,
    };
}
