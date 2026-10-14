using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SuperOfc.Application.Payroll;
using SuperOfc.Desktop.Mvvm;
using SuperOfc.Desktop.Resources;
using SuperOfc.Desktop.Services;
using SuperOfc.Domain.Payroll;
using SuperOfc.Domain.Security;

namespace SuperOfc.Desktop.ViewModels.Payroll;

/// <summary>Which month (and optionally which run) the payroll run page opens.</summary>
public sealed record PayrollRunTarget(int Year, int Month, Guid? RunId = null);

/// <summary>Payroll: the list of months with their run status and totals (P4-T4).</summary>
public sealed partial class PayrollViewModel(PageServices services) : PageViewModel(services), IRefreshable, ICreatesNew
{
    public override string Title => Strings.Payroll_Title;

    public bool CanRun => Can(Permission.ManagePayroll);

    public ObservableCollection<PayrollPeriodDto> Periods { get; } = [];

    [ObservableProperty]
    public partial PayrollPeriodDto? Selected { get; set; }

    [ObservableProperty]
    public partial string? NextText { get; set; }

    [ObservableProperty]
    public partial string? LockText { get; set; }

    private (int Year, int Month) _next;

    public bool IsEmpty => Periods.Count == 0;

    public override Task OnNavigatedToAsync(object? parameter) => RefreshAsync();

    public async Task RefreshAsync()
    {
        var result = await SendAsync(new GetPayrollPeriodsQuery()).ConfigureAwait(true);
        Periods.Clear();
        if (result.IsFailure)
        {
            return;
        }

        foreach (var p in result.Value.Periods)
        {
            Periods.Add(p);
        }

        _next = (result.Value.NextYear, result.Value.NextMonth);
        NextText = string.Format(CultureInfo.CurrentCulture, Strings.Payroll_RunFor, MonthName(_next.Year, _next.Month));
        var locked = Periods.FirstOrDefault(p => p.Status is PayrollPeriodStatus.Locked or PayrollPeriodStatus.Paid);
        LockText = locked is null ? null : string.Format(CultureInfo.CurrentCulture, Strings.Payroll_LockedUntil, Format(locked.FirstDay.AddMonths(1).AddDays(-1)));
        OnPropertyChanged(nameof(IsEmpty));
    }

    public Task CreateNewAsync() => RunNextAsync();

    [RelayCommand]
    private async Task RunNextAsync()
    {
        if (CanRun)
        {
            await Services.Navigation.NavigateToAsync<PayrollRunViewModel>(new PayrollRunTarget(_next.Year, _next.Month)).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task OpenAsync(PayrollPeriodDto? period)
    {
        period ??= Selected;
        if (period is not null)
        {
            await Services.Navigation.NavigateToAsync<PayrollRunViewModel>(new PayrollRunTarget(period.Year, period.Month, period.RunId)).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private Task RevisionAsync() => Services.Navigation.NavigateToAsync<SalaryRevisionViewModel>();

    public static string MonthName(int year, int month) => new DateTime(year, month, 1).ToString("MMMM yyyy", CultureInfo.CurrentCulture);
}

/// <summary>A payslip row with a check box for bulk actions.</summary>
public sealed partial class PayslipItem(PayslipRowDto row) : ObservableObject
{
    public PayslipRowDto Row { get; } = row;

    [ObservableProperty]
    public partial bool IsChecked { get; set; }
}

/// <summary>
/// One month's payroll: check readiness, compute, review with adjustments and payslip preview, finalize, then pay and
/// export (P4-T4 run wizard). Viewers can look but every action needs ManagePayroll or FinalizePayroll.
/// </summary>
public sealed partial class PayrollRunViewModel(PageServices services, IFileDialogService files) : PageViewModel(services), IRefreshable
{
    private PayrollRunTarget _target = new(DateTime.Today.Year, DateTime.Today.Month);

    public override string Title => string.Format(CultureInfo.CurrentCulture, Strings.PayrollRun_Title, PayrollViewModel.MonthName(_target.Year, _target.Month));

    public ObservableCollection<WizardStep> Steps { get; } = WizardStep.Create([Strings.PayrollRun_StepPrepare, Strings.PayrollRun_StepReview, Strings.PayrollRun_StepFinalize, Strings.PayrollRun_StepPay]);

    public ObservableCollection<PayslipItem> Payslips { get; } = [];

    public ObservableCollection<PayrollAdjustmentDto> Adjustments { get; } = [];

    public ObservableCollection<Application.Common.LookupItem> Employees { get; } = [];

    public static IReadOnlyList<Choice<ComponentKind>> AdjustmentKinds { get; } =
    [
        new(ComponentKind.Earning, Strings.Adjustment_Earning),
        new(ComponentKind.Deduction, Strings.Adjustment_Deduction),
    ];

    public bool CanManage => Can(Permission.ManagePayroll);

    public bool CanFinalizeRole => Can(Permission.FinalizePayroll);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRun), nameof(IsDraft), nameof(IsFinalized), nameof(IsVoided), nameof(CanCompute), nameof(CanFinalize), nameof(CanVoid), nameof(CanPay), nameof(CanEditAdjustments), nameof(StatusText))]
    public partial PayrollRunDto? Run { get; set; }

    [ObservableProperty]
    public partial PayslipItem? Selected { get; set; }

    [ObservableProperty]
    public partial byte[]? PreviewImage { get; set; }

    [ObservableProperty]
    public partial string? ReadinessText { get; set; }

    [ObservableProperty]
    public partial string? ComputeText { get; set; }

    [ObservableProperty]
    public partial string? Search { get; set; }

    [ObservableProperty]
    public partial bool IsAddingAdjustment { get; set; }

    [ObservableProperty]
    public partial Guid NewAdjEmployeeId { get; set; }

    [ObservableProperty]
    public partial string NewAdjName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial ComponentKind NewAdjKind { get; set; }

    [ObservableProperty]
    public partial double NewAdjAmount { get; set; }

    [ObservableProperty]
    public partial bool NewAdjTaxable { get; set; } = true;

    [ObservableProperty]
    public partial string? NewAdjReason { get; set; }

    public bool HasRun => Run is not null && Run.Status != PayrollRunStatus.Voided;

    public bool IsDraft => Run?.IsDraft == true;

    public bool IsFinalized => Run?.IsFinalized == true;

    public bool IsVoided => Run?.Status == PayrollRunStatus.Voided;

    public bool CanCompute => CanManage && (Run is null || Run.Status != PayrollRunStatus.Finalized);

    public bool CanFinalize => CanFinalizeRole && IsDraft;

    public bool CanVoid => CanFinalizeRole && IsFinalized;

    public bool CanPay => CanManage && IsFinalized;

    public bool CanEditAdjustments => CanManage && !IsFinalized;

    public string? StatusText => Run is null
        ? Strings.PayrollRun_NotComputed
        : string.Format(CultureInfo.CurrentCulture, Strings.PayrollRun_Status, Run.RunNumber, Strings.Get($"PayrollRunStatus_{Run.Status}"),
            Run.ComputedAtUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), Run.ComputedBy);

    public int CheckedCount => Payslips.Count(p => p.IsChecked);

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        if (parameter is PayrollRunTarget target)
        {
            _target = target;
        }

        OnPropertyChanged(nameof(Title));
        var employees = await SendAsync(new Application.Attendance.GetEmployeePickListQuery(), showErrors: false).ConfigureAwait(true);
        Employees.Clear();
        if (employees.IsSuccess)
        {
            foreach (var e in employees.Value)
            {
                Employees.Add(e);
            }
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    public async Task RefreshAsync()
    {
        var runId = _target.RunId;
        if (runId is null)
        {
            var periods = await SendAsync(new GetPayrollPeriodsQuery(), showErrors: false).ConfigureAwait(true);
            runId = periods.IsSuccess ? periods.Value.Periods.FirstOrDefault(p => p.Year == _target.Year && p.Month == _target.Month)?.RunId : null;
        }

        if (runId is { } id)
        {
            await LoadRunAsync(id).ConfigureAwait(true);
        }
        else
        {
            Run = null;
            Payslips.Clear();
        }

        await LoadAdjustmentsAsync().ConfigureAwait(true);
        await CheckReadinessAsync().ConfigureAwait(true);
        UpdateStep();
    }

    partial void OnSearchChanged(string? value) => ApplyFilter();

    async partial void OnSelectedChanged(PayslipItem? value)
    {
        PreviewImage = null;
        if (value is null || Run is null)
        {
            return;
        }

        var image = await SendAsync(new GetPayslipPreviewQuery(Run.Id, value.Row.Id), showErrors: false).ConfigureAwait(true);
        if (image.IsSuccess && Selected == value)
        {
            PreviewImage = image.Value;
        }
    }

    [RelayCommand]
    private async Task ComputeAsync()
    {
        if (!CanCompute)
        {
            return;
        }

        var result = await SendAsync(new ComputePayrollCommand(_target.Year, _target.Month)).ConfigureAwait(true);
        if (result.IsFailure)
        {
            return;
        }

        var s = result.Value;
        ComputeText = string.Format(CultureInfo.CurrentCulture, Strings.PayrollRun_Computed, s.Employees, Format(s.Gross), Format(s.Net), s.WithWarnings);
        _target = _target with { RunId = s.RunId };
        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task FinalizeAsync()
    {
        if (Run is null || !CanFinalize)
        {
            return;
        }

        var title = string.Format(CultureInfo.CurrentCulture, Strings.PayrollRun_FinalizeTitle, PayrollViewModel.MonthName(Run.Year, Run.Month));
        var message = string.Format(CultureInfo.CurrentCulture, Strings.PayrollRun_FinalizeMessage, Run.Payslips.Count, Format(Run.Net));
        if (!await Services.Dialogs.ConfirmAsync(title, message, Strings.PayrollRun_Finalize).ConfigureAwait(true))
        {
            return;
        }

        if ((await SendAsync(new FinalizePayrollRunCommand(Run.Id)).ConfigureAwait(true)).IsSuccess)
        {
            Toast(Strings.PayrollRun_Finalized);
            await RefreshAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task VoidAsync()
    {
        if (Run is null || !CanVoid)
        {
            return;
        }

        var title = string.Format(CultureInfo.CurrentCulture, Strings.PayrollRun_VoidTitle, PayrollViewModel.MonthName(Run.Year, Run.Month));
        var reason = await Services.Dialogs.PromptAsync(title, Strings.PayrollRun_VoidMessage, Strings.PayrollRun_VoidReason).ConfigureAwait(true);
        if (reason is null)
        {
            return;
        }

        if ((await SendAsync(new VoidPayrollRunCommand(Run.Id, reason)).ConfigureAwait(true)).IsSuccess)
        {
            Toast(Strings.PayrollRun_Voided);
            _target = _target with { RunId = null };
            await RefreshAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private Task MarkPaidAsync() => SetPaidAsync(true);

    [RelayCommand]
    private Task MarkUnpaidAsync() => SetPaidAsync(false);

    [RelayCommand]
    private async Task ExportPdfAsync(string? mode)
    {
        if (Run is null)
        {
            return;
        }

        var ids = Targets(p => true);
        var month = $"{Run.Year}-{Run.Month:00}";
        string? path;
        var perEmployee = string.Equals(mode, "split", StringComparison.Ordinal);
        if (perEmployee)
        {
            path = files.PickFolder(Strings.PayrollRun_PdfFolder);
        }
        else
        {
            path = files.SaveFile("PDF (*.pdf)|*.pdf", $"Payslips {month}.pdf");
        }

        if (path is null)
        {
            return;
        }

        var result = await SendAsync(new ExportPayslipsPdfCommand(Run.Id, path, perEmployee, ids)).ConfigureAwait(true);
        if (result.IsSuccess)
        {
            Toast(string.Format(CultureInfo.CurrentCulture, Strings.PayrollRun_Exported, result.Value.Rows));
            if (perEmployee)
            {
                files.OpenFolder(path);
            }
            else
            {
                files.ShellOpen(path);
            }
        }
    }

    [RelayCommand]
    private async Task ExportSheetAsync(string? kind)
    {
        if (Run is null)
        {
            return;
        }

        var sheet = string.Equals(kind, "Cash", StringComparison.Ordinal) ? PaymentSheetKind.Cash : PaymentSheetKind.Bank;
        var name = sheet == PaymentSheetKind.Bank ? Strings.PayrollRun_BankSheet : Strings.PayrollRun_CashSheet;
        var path = files.SaveFile("Excel (*.xlsx)|*.xlsx|CSV (*.csv)|*.csv", $"{name} {Run.Year}-{Run.Month:00}.xlsx");
        if (path is null)
        {
            return;
        }

        var result = await SendAsync(new ExportPaymentSheetCommand(Run.Id, path, sheet)).ConfigureAwait(true);
        if (result.IsSuccess)
        {
            Toast(string.Format(CultureInfo.CurrentCulture, Strings.PayrollRun_Exported, result.Value.Rows));
            files.ShellOpen(path);
        }
    }

    [RelayCommand]
    private async Task RegisterAsync()
    {
        if (Run is not null)
        {
            await Services.Navigation.NavigateToAsync<PayrollRegisterViewModel>(Run.Id).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void SelectAll(bool? select)
    {
        foreach (var p in Payslips)
        {
            p.IsChecked = select ?? true;
        }

        OnPropertyChanged(nameof(CheckedCount));
    }

    [RelayCommand]
    private void StartAdjustment()
    {
        NewAdjEmployeeId = Selected?.Row.EmployeeId ?? Employees.FirstOrDefault()?.Id ?? Guid.Empty;
        NewAdjName = string.Empty;
        NewAdjKind = ComponentKind.Earning;
        NewAdjAmount = 0;
        NewAdjTaxable = true;
        NewAdjReason = null;
        ErrorMessage = null;
        IsAddingAdjustment = true;
    }

    [RelayCommand]
    private void CancelAdjustment() => IsAddingAdjustment = false;

    [RelayCommand]
    private async Task SaveAdjustmentAsync()
    {
        var result = await SendAsync(new SavePayrollAdjustmentCommand(null, NewAdjEmployeeId, _target.Year, _target.Month, NewAdjName, NewAdjKind,
            Domain.Common.Money.FromMajor((decimal)NewAdjAmount).Minor, NewAdjTaxable, NewAdjReason)).ConfigureAwait(true);
        if (result.IsSuccess)
        {
            IsAddingAdjustment = false;
            Toast(HasRun ? Strings.PayrollRun_AdjustmentRecompute : Strings.App_Saved);
            await LoadAdjustmentsAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task DeleteAdjustmentAsync(PayrollAdjustmentDto? adjustment)
    {
        if (adjustment is null
            || !await Services.Dialogs.ConfirmAsync(Strings.App_Delete, $"{adjustment.EmployeeName}: {adjustment.Name} {Format(adjustment.Amount)}", Strings.App_Delete, danger: true).ConfigureAwait(true))
        {
            return;
        }

        if ((await SendAsync(new DeletePayrollAdjustmentCommand(adjustment.Id)).ConfigureAwait(true)).IsSuccess)
        {
            await LoadAdjustmentsAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private Task BackToListAsync() => Services.Navigation.NavigateToAsync<PayrollViewModel>();

    private async Task SetPaidAsync(bool paid)
    {
        if (Run is null || !CanPay)
        {
            return;
        }

        var ids = Targets(p => (p.PaymentStatus == PaymentStatus.Paid) != paid);
        if (ids is { Count: 0 } || (ids is null && Run.Payslips.All(p => (p.PaymentStatus == PaymentStatus.Paid) == paid)))
        {
            return;
        }

        ids ??= Run.Payslips.Where(p => (p.PaymentStatus == PaymentStatus.Paid) != paid).Select(p => p.Id).ToList();
        DateOnly? paidOn = null;
        string? reference = null;
        if (paid)
        {
            var title = string.Format(CultureInfo.CurrentCulture, Strings.PayrollRun_MarkPaidTitle, ids.Count);
            var date = await Services.Dialogs.PickDateAsync(title, Strings.PayrollRun_PaidOn, DateTime.Today).ConfigureAwait(true);
            if (date is null)
            {
                return;
            }

            paidOn = DateOnly.FromDateTime(date.Value);
            reference = await Services.Dialogs.PromptAsync(title, Strings.PayrollRun_ReferenceHint, Strings.PayrollRun_Reference, required: false).ConfigureAwait(true);
        }

        var result = await SendAsync(new MarkPayslipsPaidCommand(ids, paid, paidOn, string.IsNullOrWhiteSpace(reference) ? null : reference)).ConfigureAwait(true);
        if (result.IsSuccess)
        {
            Toast(string.Format(CultureInfo.CurrentCulture, paid ? Strings.PayrollRun_MarkedPaid : Strings.PayrollRun_MarkedUnpaid, result.Value));
            await RefreshAsync().ConfigureAwait(true);
        }
    }

    /// <summary>Checked payslips matching the filter; null means "all" when nothing is checked.</summary>
    private List<Guid>? Targets(Func<PayslipRowDto, bool> filter)
    {
        var checkedRows = Payslips.Where(p => p.IsChecked).Select(p => p.Row).ToList();
        return checkedRows.Count == 0 ? null : checkedRows.Where(filter).Select(r => r.Id).ToList();
    }

    private async Task LoadRunAsync(Guid runId)
    {
        var result = await SendAsync(new GetPayrollRunQuery(runId)).ConfigureAwait(true);
        if (result.IsFailure)
        {
            Run = null;
            return;
        }

        var selectedId = Selected?.Row.Id;
        Run = result.Value;
        _target = _target with { RunId = Run.Status == PayrollRunStatus.Voided ? null : Run.Id };
        ApplyFilter();
        Selected = Payslips.FirstOrDefault(p => p.Row.Id == selectedId);
    }

    private void ApplyFilter()
    {
        Payslips.Clear();
        if (Run is null)
        {
            return;
        }

        var text = Search?.Trim();
        foreach (var row in Run.Payslips.Where(r => string.IsNullOrEmpty(text)
                     || r.EmployeeName.Contains(text, StringComparison.CurrentCultureIgnoreCase)
                     || r.EmployeeCode.Contains(text, StringComparison.OrdinalIgnoreCase)
                     || (r.Department?.Contains(text, StringComparison.CurrentCultureIgnoreCase) ?? false)))
        {
            var item = new PayslipItem(row);
            item.PropertyChanged += (_, _) => OnPropertyChanged(nameof(CheckedCount));
            Payslips.Add(item);
        }

        OnPropertyChanged(nameof(CheckedCount));
    }

    private async Task LoadAdjustmentsAsync()
    {
        var result = await SendAsync(new GetPayrollAdjustmentsQuery(_target.Year, _target.Month), showErrors: false).ConfigureAwait(true);
        Adjustments.Clear();
        if (result.IsSuccess)
        {
            foreach (var a in result.Value)
            {
                Adjustments.Add(a);
            }
        }
    }

    private async Task CheckReadinessAsync()
    {
        var month = await SendAsync(new Application.Attendance.GetAttendanceMonthQuery(_target.Year, _target.Month), showErrors: false).ConfigureAwait(true);
        var end = new DateOnly(_target.Year, _target.Month, 1).AddMonths(1).AddDays(-1);
        var notes = new List<string>();
        if (end >= DateOnly.FromDateTime(DateTime.Today))
        {
            notes.Add(string.Format(CultureInfo.CurrentCulture, Strings.PayrollRun_NotEnded, Format(end.AddDays(1))));
        }

        if (month.IsSuccess && month.Value.MissingDays > 0)
        {
            notes.Add(string.Format(CultureInfo.CurrentCulture, Strings.PayrollRun_MissingPunches, month.Value.MissingDays));
        }

        notes.Add(Adjustments.Count == 0 ? Strings.PayrollRun_NoAdjustments : string.Format(CultureInfo.CurrentCulture, Strings.PayrollRun_AdjustmentCount, Adjustments.Count));
        ReadinessText = string.Join(Environment.NewLine, notes);
    }

    private void UpdateStep()
    {
        var step = Run switch
        {
            null => 0,
            { Status: PayrollRunStatus.Voided } => 0,
            { Status: PayrollRunStatus.Draft } => 1,
            _ => 3,
        };
        WizardStep.Update(Steps, step);
    }
}

/// <summary>Payroll register: every component per employee, department totals and a grand total (P4-T6).</summary>
public sealed partial class PayrollRegisterViewModel(PageServices services, IFileDialogService files) : PageViewModel(services)
{
    private Guid _runId;

    public override string Title => Register is null ? Strings.Register_Title
        : string.Format(CultureInfo.CurrentCulture, Strings.Register_TitleFor, PayrollViewModel.MonthName(Register.Year, Register.Month));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial PayrollRegister? Register { get; set; }

    /// <summary>One row per employee.</summary>
    public ObservableCollection<RegisterRow> Rows { get; } = [];

    /// <summary>Department subtotals followed by the grand total.</summary>
    public ObservableCollection<RegisterRow> TotalRows { get; } = [];

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        if (parameter is Guid id)
        {
            _runId = id;
        }

        var result = await SendAsync(new GetPayrollRegisterQuery(_runId)).ConfigureAwait(true);
        Rows.Clear();
        TotalRows.Clear();
        if (result.IsFailure)
        {
            Register = null;
            return;
        }

        foreach (var row in result.Value.Rows)
        {
            Rows.Add(row);
        }

        foreach (var row in result.Value.Departments.Append(result.Value.Total))
        {
            TotalRows.Add(row);
        }

        // Set last: the view rebuilds its columns when the register changes.
        Register = result.Value;
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (Register is null)
        {
            return;
        }

        var path = files.SaveFile("Excel (*.xlsx)|*.xlsx", $"Payroll register {Register.Year}-{Register.Month:00}.xlsx");
        if (path is null)
        {
            return;
        }

        var result = await SendAsync(new ExportPayrollRegisterCommand(_runId, path)).ConfigureAwait(true);
        if (result.IsSuccess)
        {
            files.ShellOpen(path);
        }
    }
}
