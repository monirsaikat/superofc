using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SuperOfc.Application.Common;
using SuperOfc.Application.Loans;
using SuperOfc.Desktop.Mvvm;
using SuperOfc.Desktop.Resources;
using SuperOfc.Desktop.Services;
using SuperOfc.Desktop.ViewModels.Employees;
using SuperOfc.Domain.Loans;
using SuperOfc.Domain.Policies;
using SuperOfc.Domain.Security;

namespace SuperOfc.Desktop.ViewModels.Loans;

/// <summary>Opens the loan form: an existing request to edit, or a new loan for an employee.</summary>
public sealed record LoanFormTarget(Guid? LoanId, Guid? EmployeeId = null);

/// <summary>Loans: every loan and advance with status, balance and next due month (P5-T2).</summary>
public sealed partial class LoansViewModel(PageServices services) : PageViewModel(services), IRefreshable, ICreatesNew
{
    private List<LoanListItem> _all = [];

    public override string Title => Strings.Loans_Title;

    public bool CanManage => Can(Permission.ManageLoans);

    public ObservableCollection<LoanListItem> Loans { get; } = [];

    public static IReadOnlyList<Choice<LoanStatus?>> Statuses { get; } =
    [
        new(null, Strings.Loans_AllOpen),
        new(LoanStatus.Requested, Strings.LoanStatus_Requested),
        new(LoanStatus.Approved, Strings.LoanStatus_Approved),
        new(LoanStatus.Active, Strings.LoanStatus_Active),
        new(LoanStatus.Closed, Strings.LoanStatus_Closed),
        new(LoanStatus.WrittenOff, Strings.LoanStatus_WrittenOff),
        new(LoanStatus.Rejected, Strings.LoanStatus_Rejected),
    ];

    [ObservableProperty]
    public partial LoanStatus? Status { get; set; }

    [ObservableProperty]
    public partial string? Search { get; set; }

    [ObservableProperty]
    public partial LoanListItem? Selected { get; set; }

    [ObservableProperty]
    public partial string? TotalsText { get; set; }

    public bool IsEmpty => Loans.Count == 0;

    public override Task OnNavigatedToAsync(object? parameter) => RefreshAsync();

    public async Task RefreshAsync()
    {
        var result = await SendAsync(new GetLoansQuery(Status, OpenOnly: Status is null)).ConfigureAwait(true);
        _all = result.IsSuccess ? [.. result.Value] : [];
        Filter();
    }

    public Task CreateNewAsync() => NewAsync();

    partial void OnStatusChanged(LoanStatus? value) => _ = RefreshAsync();

    partial void OnSearchChanged(string? value) => Filter();

    [RelayCommand]
    private async Task NewAsync()
    {
        if (CanManage)
        {
            await Services.Navigation.NavigateToAsync<LoanFormViewModel>(new LoanFormTarget(null)).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task OpenAsync(LoanListItem? loan)
    {
        loan ??= Selected;
        if (loan is not null)
        {
            await Services.Navigation.NavigateToAsync<LoanDetailViewModel>(loan.Id).ConfigureAwait(true);
        }
    }

    private void Filter()
    {
        var text = Search?.Trim();
        Loans.Clear();
        foreach (var loan in _all.Where(l => string.IsNullOrEmpty(text) || l.Number.Contains(text, StringComparison.OrdinalIgnoreCase)
                     || l.EmployeeCode.Contains(text, StringComparison.OrdinalIgnoreCase) || l.EmployeeName.Contains(text, StringComparison.CurrentCultureIgnoreCase)))
        {
            Loans.Add(loan);
        }

        var outstanding = new Domain.Common.Money(Loans.Sum(l => l.Outstanding.Minor));
        TotalsText = string.Format(CultureInfo.CurrentCulture, Strings.Loans_Totals, Loans.Count, Format(outstanding), Loans.Count(l => l.IsReceivable));
        OnPropertyChanged(nameof(IsEmpty));
    }
}

/// <summary>New or edited loan request with a live schedule and projected net pay (P5-T2).</summary>
public sealed partial class LoanFormViewModel(PageServices services) : EditableViewModel(services)
{
    private Guid? _loanId;
    private int _previewVersion;

    public override string Title => _loanId is null ? Strings.LoanForm_NewTitle : Strings.LoanForm_EditTitle;

    public bool CanApprove => Can(Permission.ApproveLoans);

    public ObservableCollection<LookupItem> Employees { get; } = [];

    public ObservableCollection<ScheduledInstallment> Schedule { get; } = [];

    public ObservableCollection<string> Warnings { get; } = [];

    public static IReadOnlyList<Choice<LoanKind>> Kinds { get; } =
    [
        new(LoanKind.Loan, Strings.LoanKind_Loan),
        new(LoanKind.Advance, Strings.LoanKind_Advance),
    ];

    public static IReadOnlyList<Choice<InterestMethod>> Methods { get; } =
    [
        new(InterestMethod.None, Strings.InterestMethod_None),
        new(InterestMethod.FlatRate, Strings.InterestMethod_FlatRate),
    ];

    /// <summary>Delay before the preview refreshes after typing; zero in tests.</summary>
    public TimeSpan PreviewDelay { get; set; } = TimeSpan.FromMilliseconds(250);

    [ObservableProperty]
    public partial Guid EmployeeId { get; set; }

    [ObservableProperty]
    public partial LoanKind Kind { get; set; }

    [ObservableProperty]
    public partial double Amount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsesRate))]
    public partial InterestMethod Method { get; set; }

    [ObservableProperty]
    public partial double Rate { get; set; }

    [ObservableProperty]
    public partial int Count { get; set; } = 12;

    [ObservableProperty]
    public partial DateTime? FirstMonth { get; set; } = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(1);

    [ObservableProperty]
    public partial DateTime? RequestedOn { get; set; } = DateTime.Today;

    [ObservableProperty]
    public partial string? Purpose { get; set; }

    [ObservableProperty]
    public partial string? TotalText { get; set; }

    [ObservableProperty]
    public partial string? NetText { get; set; }

    [ObservableProperty]
    public partial bool IsBelowMinimum { get; set; }

    public bool UsesRate => Method == InterestMethod.FlatRate;

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        var target = parameter as LoanFormTarget ?? new LoanFormTarget(null);
        _loanId = target.LoanId;
        var employees = await SendAsync(new Application.Attendance.GetEmployeePickListQuery()).ConfigureAwait(true);
        Employees.Clear();
        if (employees.IsSuccess)
        {
            foreach (var e in employees.Value)
            {
                Employees.Add(e);
            }
        }

        using (Loading())
        {
            if (_loanId is { } id && (await SendAsync(new GetLoanQuery(id)).ConfigureAwait(true)) is { IsSuccess: true } loaded)
            {
                var loan = loaded.Value;
                EmployeeId = loan.Summary.EmployeeId;
                Kind = loan.Summary.Kind;
                Amount = (double)loan.Summary.Principal.Major;
                Method = loan.InterestMethod;
                Rate = (double)loan.AnnualRate;
                Count = loan.Summary.InstallmentCount;
                FirstMonth = loan.Summary.FirstMonth.ToDateTime(TimeOnly.MinValue);
                RequestedOn = loan.Summary.RequestedOn.ToDateTime(TimeOnly.MinValue);
                Purpose = loan.Purpose;
            }
            else
            {
                EmployeeId = target.EmployeeId ?? Guid.Empty;
                var policy = await SendAsync(new Application.Policies.GetPolicySnapshotQuery(DateOnly.FromDateTime(DateTime.Today)), showErrors: false).ConfigureAwait(true);
                Method = policy.IsSuccess ? policy.Value.Loan.DefaultInterestMethod : InterestMethod.None;
            }
        }

        OnPropertyChanged(nameof(Title));
        await RefreshPreviewAsync().ConfigureAwait(true);
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e?.PropertyName is nameof(EmployeeId) or nameof(Amount) or nameof(Method) or nameof(Rate) or nameof(Count) or nameof(FirstMonth))
        {
            _ = DebouncedPreviewAsync();
        }
    }

    protected override bool IsTrackedProperty(string? propertyName)
        => propertyName is not (nameof(TotalText) or nameof(NetText) or nameof(IsBelowMinimum) or nameof(UsesRate) or nameof(Title));

    protected override string MapField(string field) => field switch
    {
        "PrincipalMinor" => nameof(Amount),
        "InstallmentCount" => nameof(Count),
        "AnnualRate" => nameof(Rate),
        "StartMonth" or "StartYear" => nameof(FirstMonth),
        _ => field,
    };

    /// <summary>Recomputes the schedule and the projected net pay now.</summary>
    public async Task RefreshPreviewAsync()
    {
        if (FirstMonth is not { } first)
        {
            return;
        }

        var result = await Dispatcher.SendAsync(new PreviewLoanQuery(EmployeeId == Guid.Empty ? null : EmployeeId, _loanId, Domain.Common.Money.FromMajor((decimal)Amount).Minor, Method,
            (decimal)Rate, Count, first.Year, first.Month)).ConfigureAwait(true);
        Schedule.Clear();
        Warnings.Clear();
        if (result.IsFailure)
        {
            return;
        }

        var preview = result.Value;
        foreach (var installment in preview.Schedule?.Installments ?? [])
        {
            Schedule.Add(installment);
        }

        foreach (var warning in preview.Warnings)
        {
            Warnings.Add(warning);
        }

        TotalText = preview.Schedule is { } s
            ? string.Format(CultureInfo.CurrentCulture, Strings.LoanForm_Total, Format(s.Total), Format(s.Interest), s.Installments.Count, Format(s.Installment))
            : null;
        NetText = preview.CurrentNet is { } now && preview.ProjectedNet is { } after
            ? string.Format(CultureInfo.CurrentCulture, Strings.LoanForm_Net, Format(now), Format(after))
            : null;
        IsBelowMinimum = preview.Warnings.Any(w => w.Contains("below the minimum", StringComparison.Ordinal));
    }

    [RelayCommand]
    private Task SaveAsync() => SaveCoreAsync(approve: false);

    [RelayCommand]
    private Task SaveAndApproveAsync() => SaveCoreAsync(approve: true);

    private async Task SaveCoreAsync(bool approve)
    {
        if (FirstMonth is not { } first || RequestedOn is not { } requested)
        {
            ErrorMessage = Strings.LoanForm_DatesRequired;
            return;
        }

        var saved = await SendAsync(new SaveLoanCommand(_loanId, EmployeeId, Kind, Domain.Common.Money.FromMajor((decimal)Amount).Minor, Method, (decimal)Rate, Count,
            first.Year, first.Month, DateOnly.FromDateTime(requested), Purpose), showErrors: false).ConfigureAwait(true);
        if (saved.IsFailure)
        {
            ShowErrors(saved.Errors);
            return;
        }

        _loanId = saved.Value;
        IsDirty = false;
        if (approve && CanApprove)
        {
            var approved = await SendAsync(new ApproveLoanCommand(saved.Value)).ConfigureAwait(true);
            if (approved.IsFailure)
            {
                return;
            }
        }

        Toast(Strings.App_Saved);
        await Services.Navigation.NavigateToAsync<LoanDetailViewModel>(saved.Value).ConfigureAwait(true);
    }

    private async Task DebouncedPreviewAsync()
    {
        var version = ++_previewVersion;
        if (PreviewDelay > TimeSpan.Zero)
        {
            await Task.Delay(PreviewDelay).ConfigureAwait(true);
        }

        if (version == _previewVersion)
        {
            await RefreshPreviewAsync().ConfigureAwait(true);
        }
    }
}

/// <summary>One loan: terms, actions for its state, the schedule and the statement (P5-T2).</summary>
public sealed partial class LoanDetailViewModel(PageServices services, IFileDialogService files) : PageViewModel(services), IRefreshable
{
    private Guid _id;

    public override string Title => Loan is null ? Strings.Loans_Title : $"{Loan.Summary.Number} · {Loan.Summary.EmployeeName}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(CanEdit), nameof(CanApprove), nameof(CanDisburse), nameof(CanRepay), nameof(CanWriteOff), nameof(CanDelete), nameof(TermsText),
        nameof(SettlementText))]
    public partial LoanDetail? Loan { get; set; }

    public bool CanEdit => Can(Permission.ManageLoans) && Loan?.Summary.Status == LoanStatus.Requested;

    public bool CanApprove => Can(Permission.ApproveLoans) && Loan?.Summary.Status == LoanStatus.Requested;

    public bool CanDisburse => Can(Permission.ManageLoans) && Loan?.Summary.Status == LoanStatus.Approved;

    public bool CanRepay => Can(Permission.ManageLoans) && Loan?.Summary.Status == LoanStatus.Active;

    public bool CanWriteOff => Can(Permission.ApproveLoans) && Loan?.Summary.Status == LoanStatus.Active;

    public bool CanDelete => Can(Permission.ManageLoans) && Loan?.Summary.Status is LoanStatus.Requested or LoanStatus.Rejected;

    public string? TermsText => Loan is not { } l ? null : string.Format(CultureInfo.CurrentCulture, Strings.LoanDetail_Terms,
        Format(l.Summary.Principal), l.InterestMethod == InterestMethod.FlatRate ? $"{l.AnnualRate:0.##} %" : Strings.InterestMethod_None, l.Summary.InstallmentCount,
        Format(l.Summary.Installment), l.Summary.FirstMonth.ToString("MMMM yyyy", CultureInfo.CurrentCulture));

    public string? SettlementText => Loan?.Settlement is not { } q ? null : string.Format(CultureInfo.CurrentCulture, Strings.LoanDetail_SettleQuote, Format(q.Payable), Format(q.InterestWaived));

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        if (parameter is Guid id)
        {
            _id = id;
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    public async Task RefreshAsync()
    {
        var result = await SendAsync(new GetLoanQuery(_id)).ConfigureAwait(true);
        Loan = result.IsSuccess ? result.Value : null;
    }

    [RelayCommand]
    private Task EditAsync() => Services.Navigation.NavigateToAsync<LoanFormViewModel>(new LoanFormTarget(_id));

    [RelayCommand]
    private Task BackAsync() => Services.Navigation.NavigateToAsync<LoansViewModel>();

    [RelayCommand]
    private Task ApproveAsync() => RunAsync(new ApproveLoanCommand(_id), Strings.LoanDetail_Approved);

    [RelayCommand]
    private async Task RejectAsync()
    {
        var reason = await Services.Dialogs.PromptAsync(Strings.LoanDetail_Reject, Strings.LoanDetail_RejectMessage, Strings.PayrollRun_VoidReason).ConfigureAwait(true);
        if (reason is not null)
        {
            await RunAsync(new RejectLoanCommand(_id, reason), Strings.App_Saved).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task DisburseAsync()
    {
        var date = await Services.Dialogs.PickDateAsync(Strings.LoanDetail_Disburse, Strings.LoanDetail_PaidOutOn, DateTime.Today).ConfigureAwait(true);
        if (date is null)
        {
            return;
        }

        var reference = await Services.Dialogs.PromptAsync(Strings.LoanDetail_Disburse, Strings.PayrollRun_ReferenceHint, Strings.PayrollRun_Reference, required: false).ConfigureAwait(true);
        await RunAsync(new DisburseLoanCommand(_id, DateOnly.FromDateTime(date.Value), reference), Strings.LoanDetail_Disbursed).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RepayAsync()
    {
        var text = await Services.Dialogs.PromptAsync(Strings.LoanDetail_Repay, Strings.LoanDetail_RepayMessage, Strings.PayrollRun_Amount).ConfigureAwait(true);
        if (text is null)
        {
            return;
        }

        if (!Application.Common.MoneyFormatter.TryParse(text, out var amount) || amount.Minor <= 0)
        {
            ErrorMessage = Strings.LoanDetail_InvalidAmount;
            return;
        }

        var date = await Services.Dialogs.PickDateAsync(Strings.LoanDetail_Repay, Strings.LoanDetail_RepaidOn, DateTime.Today).ConfigureAwait(true);
        if (date is null)
        {
            return;
        }

        var reference = await Services.Dialogs.PromptAsync(Strings.LoanDetail_Repay, Strings.PayrollRun_ReferenceHint, Strings.PayrollRun_Reference, required: false).ConfigureAwait(true);
        await RunAsync(new RecordLoanRepaymentCommand(_id, DateOnly.FromDateTime(date.Value), amount.Minor, reference, null), Strings.LoanDetail_Repaid).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task SettleAsync()
    {
        if (Loan?.Settlement is not { } quote)
        {
            return;
        }

        var title = string.Format(CultureInfo.CurrentCulture, Strings.LoanDetail_SettleTitle, Loan.Summary.Number);
        if (!await Services.Dialogs.ConfirmAsync(title, SettlementText ?? string.Empty, Strings.LoanDetail_Settle).ConfigureAwait(true))
        {
            return;
        }

        var reference = await Services.Dialogs.PromptAsync(title, Strings.PayrollRun_ReferenceHint, Strings.PayrollRun_Reference, required: false).ConfigureAwait(true);
        await RunAsync(new SettleLoanCommand(_id, DateOnly.FromDateTime(DateTime.Today), reference), string.Format(CultureInfo.CurrentCulture, Strings.LoanDetail_Settled, Format(quote.Payable)))
            .ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task WriteOffAsync()
    {
        if (Loan is null)
        {
            return;
        }

        var title = string.Format(CultureInfo.CurrentCulture, Strings.LoanDetail_WriteOffTitle, Loan.Summary.Number, Format(Loan.Balance.Outstanding));
        var reason = await Services.Dialogs.PromptAsync(title, Strings.LoanDetail_WriteOffMessage, Strings.PayrollRun_VoidReason).ConfigureAwait(true);
        if (reason is not null)
        {
            await RunAsync(new WriteOffLoanCommand(_id, DateOnly.FromDateTime(DateTime.Today), reason), Strings.App_Saved).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (Loan is null || !await Services.Dialogs.ConfirmAsync(string.Format(CultureInfo.CurrentCulture, Strings.LoanDetail_DeleteTitle, Loan.Summary.Number), Strings.LoanDetail_DeleteMessage,
                Strings.App_Delete, danger: true).ConfigureAwait(true))
        {
            return;
        }

        if ((await SendAsync(new DeleteLoanCommand(_id)).ConfigureAwait(true)).IsSuccess)
        {
            await Services.Navigation.NavigateToAsync<LoansViewModel>().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (Loan is null)
        {
            return;
        }

        var path = files.SaveFile("PDF (*.pdf)|*.pdf", $"Statement {Loan.Summary.Number} {Loan.Summary.EmployeeName}.pdf");
        if (path is not null && (await SendAsync(new ExportLoanStatementCommand(_id, path)).ConfigureAwait(true)).IsSuccess)
        {
            files.ShellOpen(path);
        }
    }

    private async Task RunAsync<T>(IRequest<T> command, string done)
    {
        if ((await SendAsync(command).ConfigureAwait(true)).IsSuccess)
        {
            Toast(done);
            await RefreshAsync().ConfigureAwait(true);
        }
    }
}

/// <summary>Employee › Loans: the employee's loans with balances and a shortcut to a new loan.</summary>
public sealed partial class EmployeeLoansTabViewModel(PageServices services) : EmployeeTabViewModel(services)
{
    private Guid _employeeId;

    public override string Header => Strings.EmpTab_Loans;

    public override string Title => Header;

    public override bool IsAllowed => Can(Permission.ViewSalaries);

    public bool CanManage => Can(Permission.ManageLoans);

    public ObservableCollection<LoanListItem> Loans { get; } = [];

    [ObservableProperty]
    public partial string? Summary { get; set; }

    public bool IsEmpty => Loans.Count == 0;

    public override async Task LoadAsync(Guid employeeId)
    {
        _employeeId = employeeId;
        var result = await SendAsync(new GetLoansQuery(EmployeeId: employeeId)).ConfigureAwait(true);
        Loans.Clear();
        if (result.IsSuccess)
        {
            foreach (var loan in result.Value)
            {
                Loans.Add(loan);
            }
        }

        var open = Loans.Where(l => l.Status == LoanStatus.Active).ToList();
        Summary = open.Count == 0 ? null : string.Format(CultureInfo.CurrentCulture, Strings.EmpLoans_Summary, open.Count, Format(new Domain.Common.Money(open.Sum(l => l.Outstanding.Minor))));
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private Task NewAsync() => Services.Navigation.NavigateToAsync<LoanFormViewModel>(new LoanFormTarget(null, _employeeId));

    [RelayCommand]
    private async Task OpenAsync(LoanListItem? loan)
    {
        if (loan is not null)
        {
            await Services.Navigation.NavigateToAsync<LoanDetailViewModel>(loan.Id).ConfigureAwait(true);
        }
    }
}
