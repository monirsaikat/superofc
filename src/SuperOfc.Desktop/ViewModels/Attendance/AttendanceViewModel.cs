using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Attendance;
using SuperOfc.Application.Common;
using SuperOfc.Application.Organization;
using SuperOfc.Desktop.Mvvm;
using SuperOfc.Desktop.Resources;
using SuperOfc.Desktop.Services;
using SuperOfc.Domain.Attendance;
using SuperOfc.Domain.Organization;
using SuperOfc.Domain.Security;

namespace SuperOfc.Desktop.ViewModels.Attendance;

/// <summary>One cell of the monthly sheet.</summary>
public sealed record SheetCell(int DayIndex, AttendanceStatus? Status, bool IsCorrected, bool MissingPunch, string Code, string ToolTip);

public sealed record SheetRow(Guid EmployeeId, string Code, string Name, string? Department, IReadOnlyList<SheetCell> Cells, int Present, int Late, int HalfDays, int Absent, decimal Leave, decimal OvertimeHours);

public sealed record SheetDay(int Index, DateOnly Date, DayKind Kind)
{
    public string Header => $"{Date.Day}\n{Date:ddd}";

    public bool IsOff => Kind is DayKind.WeeklyOff or DayKind.Holiday;
}

/// <summary>Monthly attendance sheet (docs/04 grid-editing page) with a side panel for one day.</summary>
public sealed partial class AttendanceViewModel(PageServices services, IFileDialogService files, IClock clock, ICompanyContext company) : PageViewModel(services), IRefreshable
{
    private CancellationTokenSource? _debounce;
    private bool _ready;
    private bool _autoRecomputed;

    public override string Title => Strings.Attendance_Title;

    public bool CanEdit => Can(Permission.ManageAttendance);

    public BulkObservableCollection<SheetRow> Rows { get; } = [];

    public ObservableCollection<SheetDay> Days { get; } = [];

    public ObservableCollection<LookupItem> Departments { get; } = [];

    public IReadOnlyList<DateTime> Months { get; } = Enumerable.Range(0, 24).Select(i => new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(-i)).ToList();

    public static IReadOnlyList<Choice<AttendanceStatus>> CorrectionStatuses { get; } =
        new[] { AttendanceStatus.Present, AttendanceStatus.Late, AttendanceStatus.HalfDay, AttendanceStatus.Absent, AttendanceStatus.Leave, AttendanceStatus.Holiday, AttendanceStatus.WeeklyOff }
            .Select(s => new Choice<AttendanceStatus>(s, Strings.Get($"AttStatus_{s}"))).ToList();

    /// <summary>Raised after rows are reloaded so the view can rebuild day columns when the month changes.</summary>
    public event EventHandler? SheetReloaded;

    [ObservableProperty]
    public partial DateTime Month { get; set; } = new(DateTime.Today.Year, DateTime.Today.Month, 1);

    [ObservableProperty]
    public partial Guid DepartmentId { get; set; }

    [ObservableProperty]
    public partial string? Search { get; set; }

    [ObservableProperty]
    public partial bool HasMissingDays { get; set; }

    /// <summary>Shown when finalized payroll covers this month (P4-T4).</summary>
    [ObservableProperty]
    public partial string? LockText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDayOpen))]
    public partial AttendanceDayDetail? Day { get; set; }

    public bool IsDayOpen => Day is not null;

    public ObservableCollection<PunchDto> DayPunches { get; } = [];

    [ObservableProperty]
    public partial AttendanceStatus CorrectStatus { get; set; }

    [ObservableProperty]
    public partial int CorrectLate { get; set; }

    [ObservableProperty]
    public partial int CorrectOvertime { get; set; }

    [ObservableProperty]
    public partial string? CorrectReason { get; set; }

    [ObservableProperty]
    public partial string? NewPunchTime { get; set; }

    [ObservableProperty]
    public partial string? NewPunchReason { get; set; }

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
        await LoadAsync().ConfigureAwait(true);
    }

    public Task RefreshAsync() => LoadAsync();

    async partial void OnMonthChanged(DateTime value)
    {
        _autoRecomputed = false;
        Day = null;
        await ReloadIfReadyAsync().ConfigureAwait(true);
    }

    async partial void OnDepartmentIdChanged(Guid value) => await ReloadIfReadyAsync().ConfigureAwait(true);

    async partial void OnSearchChanged(string? value)
    {
        _debounce?.Cancel();
        _debounce = new CancellationTokenSource();
        try
        {
            await Task.Delay(300, _debounce.Token).ConfigureAwait(true);
            await ReloadIfReadyAsync().ConfigureAwait(true);
        }
        catch (TaskCanceledException)
        {
            // Superseded by a newer keystroke.
        }
    }

    [RelayCommand]
    private async Task RecomputeAsync()
    {
        var (from, to) = MonthRange();
        var result = await SendAsync(new RecomputeAttendanceCommand(from, to)).ConfigureAwait(true);
        if (result.IsSuccess)
        {
            Toast(string.Format(CultureInfo.CurrentCulture, Strings.Attendance_Recomputed, result.Value));
            await LoadAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        var path = files.SaveFile("Excel workbook (*.xlsx)|*.xlsx|CSV (*.csv)|*.csv", $"Attendance-{Month:yyyy-MM}.xlsx");
        if (path is null)
        {
            return;
        }

        var result = await SendAsync(new ExportAttendanceMonthQuery(path, Month.Year, Month.Month, DepartmentId == Guid.Empty ? null : DepartmentId)).ConfigureAwait(true);
        if (result.IsSuccess)
        {
            files.ShellOpen(path);
        }
    }

    [RelayCommand]
    private Task OpenPunchLogAsync() => Services.Navigation.NavigateToAsync<PunchLogViewModel>();

    /// <summary>Opens the side panel for one employee and day (called by the grid).</summary>
    public async Task OpenDayAsync(SheetRow row, int dayIndex)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (dayIndex < 0 || dayIndex >= Days.Count)
        {
            return;
        }

        await LoadDayAsync(row.EmployeeId, Days[dayIndex].Date).ConfigureAwait(true);
    }

    [RelayCommand]
    private void CloseDay() => Day = null;

    [RelayCommand]
    private async Task SaveCorrectionAsync()
    {
        if (Day is not { } day)
        {
            return;
        }

        var result = await SendAsync(new CorrectAttendanceDayCommand(day.EmployeeId, day.Date, CorrectStatus, CorrectLate, CorrectOvertime, CorrectReason ?? string.Empty)).ConfigureAwait(true);
        if (result.IsSuccess)
        {
            Toast(Strings.App_Saved);
            await AfterDayChangeAsync(day).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task ResetCorrectionAsync()
    {
        if (Day is not { } day)
        {
            return;
        }

        if ((await SendAsync(new ResetAttendanceCorrectionCommand(day.EmployeeId, day.Date)).ConfigureAwait(true)).IsSuccess)
        {
            await AfterDayChangeAsync(day).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task AddPunchAsync()
    {
        if (Day is not { } day)
        {
            return;
        }

        if (!Settings.ShiftsSettingsViewModel.TryParseTime(NewPunchTime, out var time))
        {
            ErrorMessage = Strings.Shifts_InvalidTime;
            return;
        }

        // Times before 06:00 typed on a day panel usually belong to the night after.
        var date = time.Hour < 6 && day.ShiftName is not null && day.FirstIn is { Hour: >= 18 } ? day.Date.AddDays(1) : day.Date;
        var result = await SendAsync(new AddManualPunchCommand(day.EmployeeId, date.ToDateTime(time), PunchType.Unknown, NewPunchReason ?? string.Empty)).ConfigureAwait(true);
        if (result.IsSuccess)
        {
            NewPunchTime = NewPunchReason = null;
            await AfterDayChangeAsync(day).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task DeletePunchAsync(PunchDto? punch)
    {
        if (punch is not { CanDelete: true } || Day is not { } day)
        {
            return;
        }

        var reason = await Services.Dialogs.PromptAsync(Strings.Attendance_DeletePunchTitle, punch.LocalTime.ToString("g", CultureInfo.CurrentCulture), Strings.Attendance_DeleteReason).ConfigureAwait(true);
        if (reason is not null && (await SendAsync(new DeleteManualPunchCommand(punch.Id, reason)).ConfigureAwait(true)).IsSuccess)
        {
            await AfterDayChangeAsync(day).ConfigureAwait(true);
        }
    }

    private async Task AfterDayChangeAsync(AttendanceDayDetail day)
    {
        await LoadAsync().ConfigureAwait(true);
        await LoadDayAsync(day.EmployeeId, day.Date).ConfigureAwait(true);
    }

    private async Task LoadDayAsync(Guid employeeId, DateOnly date)
    {
        var result = await SendAsync(new GetAttendanceDayQuery(employeeId, date)).ConfigureAwait(true);
        if (result.IsFailure)
        {
            return;
        }

        var d = result.Value;
        DayPunches.Clear();
        foreach (var p in d.Punches)
        {
            DayPunches.Add(p);
        }

        CorrectStatus = d.Status is null or AttendanceStatus.NotJoined or AttendanceStatus.Exited ? AttendanceStatus.Present : d.Status.Value;
        CorrectLate = d.LateMinutes;
        CorrectOvertime = d.OvertimeMinutes;
        CorrectReason = d.CorrectionNote;
        ErrorMessage = null;
        Day = d;
    }

    private (DateOnly From, DateOnly To) MonthRange()
    {
        var from = new DateOnly(Month.Year, Month.Month, 1);
        var to = from.AddMonths(1).AddDays(-1);
        var today = clock.TodayIn(company.TimeZone);
        return (from, to > today ? today : to);
    }

    private Task ReloadIfReadyAsync() => _ready ? LoadAsync() : Task.CompletedTask;

    private async Task LoadAsync()
    {
        var result = await SendAsync(new GetAttendanceMonthQuery(Month.Year, Month.Month, DepartmentId == Guid.Empty ? null : DepartmentId, Search)).ConfigureAwait(true);
        if (result.IsFailure)
        {
            return;
        }

        var month = result.Value;
        var lockedUntil = await PayrollLockedUntilAsync().ConfigureAwait(true);
        LockText = lockedUntil is { } until && until >= DateOnly.FromDateTime(Month)
            ? string.Format(CultureInfo.CurrentCulture, Strings.Lock_Attendance, Format(until))
            : null;
        var (_, lastPast) = MonthRange();
        var pastIndexes = month.Dates.Select((d, i) => (d, i)).Where(x => x.d <= lastPast).Select(x => x.i).ToHashSet();
        var missingPast = month.Rows.Any(r => pastIndexes.Any(i => r.Cells[i].Status is null));

        // The first time a month is opened, fill uncalculated past days automatically (it never touches corrections).
        if (missingPast && CanEdit && !_autoRecomputed && pastIndexes.Count > 0)
        {
            _autoRecomputed = true;
            var (from, to) = MonthRange();
            if ((await SendAsync(new RecomputeAttendanceCommand(from, to), showErrors: false).ConfigureAwait(true)).IsSuccess)
            {
                await LoadAsync().ConfigureAwait(true);
                return;
            }
        }

        HasMissingDays = missingPast;
        if (Days.Count != month.Dates.Count || (Days.Count > 0 && Days[0].Date != month.Dates[0]))
        {
            Days.Clear();
            for (var i = 0; i < month.Dates.Count; i++)
            {
                Days.Add(new SheetDay(i, month.Dates[i], month.DayKinds[i]));
            }
        }

        Rows.ReplaceAll(month.Rows.Select(r => new SheetRow(
            r.EmployeeId, r.Code, r.Name, r.Department,
            r.Cells.Select((c, i) => new SheetCell(i, c.Status, c.IsCorrected, c.MissingPunch, AttendanceCodes.Short(c.Status), CellTip(c))).ToList(),
            r.Present, r.Late, r.HalfDays, r.Absent, r.Leave, Math.Round(r.OvertimeMinutes / 60m, 1))));
        SheetReloaded?.Invoke(this, EventArgs.Empty);
    }

    private static string CellTip(AttendanceCell c)
    {
        if (c.Status is null)
        {
            return string.Empty;
        }

        var parts = new List<string> { Strings.Get($"AttStatus_{c.Status}") };
        if (c.LateMinutes > 0)
        {
            parts.Add($"{Strings.Attendance_Late} {c.LateMinutes}m");
        }

        if (c.OvertimeMinutes > 0)
        {
            parts.Add($"OT {c.OvertimeMinutes}m");
        }

        if (c.MissingPunch)
        {
            parts.Add(Strings.Attendance_MissingPunch);
        }

        if (c.IsCorrected)
        {
            parts.Add(Strings.Attendance_Corrected);
        }

        return string.Join(" · ", parts);
    }
}

public sealed partial class PunchLogViewModel(PageServices services) : PageViewModel(services), IRefreshable
{
    private CancellationTokenSource? _debounce;
    private bool _ready;

    public override string Title => Strings.Punches_Title;

    public bool CanEdit => Can(Permission.ManageAttendance);

    public BulkObservableCollection<PunchDto> Punches { get; } = [];

    public ObservableCollection<LookupItem> Employees { get; } = [];

    public static IReadOnlyList<Choice<PunchSource?>> Sources { get; } =
    [
        new(null, Strings.Punches_AllSources),
        new(PunchSource.Device, Strings.Source_Device),
        new(PunchSource.Manual, Strings.Source_Manual),
        new(PunchSource.Import, Strings.Source_Import),
    ];

    [ObservableProperty]
    public partial DateTime? From { get; set; } = DateTime.Today.AddDays(-7);

    [ObservableProperty]
    public partial DateTime? To { get; set; } = DateTime.Today;

    [ObservableProperty]
    public partial string? Search { get; set; }

    [ObservableProperty]
    public partial PunchSource? Source { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageText))]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand), nameof(NextPageCommand))]
    public partial int PageIndex { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageText))]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand), nameof(NextPageCommand))]
    public partial int PageCount { get; set; }

    [ObservableProperty]
    public partial bool IsAdding { get; set; }

    [ObservableProperty]
    public partial Guid NewEmployeeId { get; set; }

    [ObservableProperty]
    public partial DateTime? NewDate { get; set; } = DateTime.Today;

    [ObservableProperty]
    public partial string? NewTime { get; set; }

    [ObservableProperty]
    public partial string? NewReason { get; set; }

    public string PageText => string.Format(CultureInfo.CurrentCulture, Strings.Employees_Page, PageIndex + 1, Math.Max(1, PageCount));

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

        _ready = true;
        await LoadAsync(0).ConfigureAwait(true);
    }

    public Task RefreshAsync() => LoadAsync(PageIndex);

    async partial void OnFromChanged(DateTime? value) => await ReloadAsync().ConfigureAwait(true);

    async partial void OnToChanged(DateTime? value) => await ReloadAsync().ConfigureAwait(true);

    async partial void OnSourceChanged(PunchSource? value) => await ReloadAsync().ConfigureAwait(true);

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

    private bool CanPrevious() => PageIndex > 0;

    private bool CanNext() => PageIndex + 1 < PageCount;

    [RelayCommand(CanExecute = nameof(CanPrevious))]
    private Task PreviousPageAsync() => LoadAsync(PageIndex - 1);

    [RelayCommand(CanExecute = nameof(CanNext))]
    private Task NextPageAsync() => LoadAsync(PageIndex + 1);

    [RelayCommand]
    private void StartAdd()
    {
        IsAdding = true;
        NewEmployeeId = Employees.FirstOrDefault()?.Id ?? Guid.Empty;
        NewDate = DateTime.Today;
        NewTime = NewReason = null;
        ErrorMessage = null;
    }

    [RelayCommand]
    private void CancelAdd() => IsAdding = false;

    [RelayCommand]
    private async Task SaveAddAsync()
    {
        if (NewDate is null || !Settings.ShiftsSettingsViewModel.TryParseTime(NewTime, out var time))
        {
            ErrorMessage = Strings.Shifts_InvalidTime;
            return;
        }

        var result = await SendAsync(new AddManualPunchCommand(NewEmployeeId, DateOnly.FromDateTime(NewDate.Value).ToDateTime(time), PunchType.Unknown, NewReason ?? string.Empty)).ConfigureAwait(true);
        if (result.IsSuccess)
        {
            IsAdding = false;
            Toast(Strings.App_Saved);
            await LoadAsync(0).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(PunchDto? punch)
    {
        if (punch is not { CanDelete: true })
        {
            return;
        }

        var reason = await Services.Dialogs.PromptAsync(Strings.Attendance_DeletePunchTitle, $"{punch.EmployeeCode} · {punch.LocalTime:g}", Strings.Attendance_DeleteReason).ConfigureAwait(true);
        if (reason is not null && (await SendAsync(new DeleteManualPunchCommand(punch.Id, reason)).ConfigureAwait(true)).IsSuccess)
        {
            await LoadAsync(PageIndex).ConfigureAwait(true);
        }
    }

    private Task ReloadAsync() => _ready ? LoadAsync(0) : Task.CompletedTask;

    private async Task LoadAsync(int page)
    {
        var from = DateOnly.FromDateTime(From ?? DateTime.Today.AddDays(-7));
        var to = DateOnly.FromDateTime(To ?? DateTime.Today);
        var result = await SendAsync(new GetPunchesQuery(from, to < from ? from : to, null, Search, Source, page, 200)).ConfigureAwait(true);
        if (result.IsFailure)
        {
            return;
        }

        Punches.ReplaceAll(result.Value.Items);
        PageIndex = page;
        PageCount = result.Value.PageCount;
    }
}
