using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Common;
using SuperOfc.Application.Employees;
using SuperOfc.Application.Organization;
using SuperOfc.Desktop.Mvvm;
using SuperOfc.Desktop.Resources;
using SuperOfc.Desktop.Services;
using SuperOfc.Domain.Employees;
using SuperOfc.Domain.Security;

namespace SuperOfc.Desktop.ViewModels.Employees;

/// <summary>Create or edit one employee. Parameter: employee id (Guid) or null for a new employee.</summary>
public sealed partial class EmployeeDetailViewModel(PageServices services, IFileDialogService files, IFileStorage storage, IServiceProvider provider)
    : EditableViewModel(services)
{
    private long _rowVersion;

    public override string Title => IsNew ? Strings.Employee_NewTitle : $"{FullName} · {Code}";

    public bool CanEdit => Can(Permission.ManageEmployees);

    public ObservableCollection<LookupItem> Departments { get; } = [];

    public ObservableCollection<LookupItem> Designations { get; } = [];

    public ObservableCollection<LookupItem> Locations { get; } = [];

    public ObservableCollection<LookupItem> Grades { get; } = [];

    public ObservableCollection<EmploymentEventDto> History { get; } = [];

    public ObservableCollection<EmployeeDocumentDto> Documents { get; } = [];

    /// <summary>Tabs contributed by other modules (salary, attendance, leave, loans).</summary>
    public ObservableCollection<EmployeeTabViewModel> ModuleTabs { get; } = [];

    public static IReadOnlyList<Choice<Gender>> Genders { get; } = Enum.GetValues<Gender>().Select(g => new Choice<Gender>(g, Strings.Get($"Gender_{g}"))).ToList();

    public static IReadOnlyList<Choice<PaymentMode>> PaymentModes { get; } = Enum.GetValues<PaymentMode>().Select(p => new Choice<PaymentMode>(p, Strings.Get($"Pay_{p}"))).ToList();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(IsExisting))]
    public partial Guid? EmployeeId { get; set; }

    public bool IsNew => EmployeeId is null;

    public bool IsExisting => EmployeeId is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial string Code { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial string FullName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Gender Gender { get; set; }

    [ObservableProperty]
    public partial DateTime? DateOfBirth { get; set; }

    [ObservableProperty]
    public partial string? Phone { get; set; }

    [ObservableProperty]
    public partial string? Email { get; set; }

    [ObservableProperty]
    public partial string? Address { get; set; }

    [ObservableProperty]
    public partial string? NationalId { get; set; }

    [ObservableProperty]
    public partial string? EmergencyContact { get; set; }

    [ObservableProperty]
    public partial DateTime? JoinDate { get; set; } = DateTime.Today;

    [ObservableProperty]
    public partial Guid DepartmentId { get; set; }

    [ObservableProperty]
    public partial Guid DesignationId { get; set; }

    [ObservableProperty]
    public partial Guid LocationId { get; set; }

    [ObservableProperty]
    public partial Guid GradeId { get; set; }

    [ObservableProperty]
    public partial PaymentMode PaymentMode { get; set; }

    [ObservableProperty]
    public partial string? BankName { get; set; }

    [ObservableProperty]
    public partial string? BankBranch { get; set; }

    [ObservableProperty]
    public partial string? BankAccount { get; set; }

    [ObservableProperty]
    public partial string? TaxIdentifier { get; set; }

    [ObservableProperty]
    public partial bool IsOvertimeEligible { get; set; } = true;

    [ObservableProperty]
    public partial bool StartConfirmed { get; set; }

    [ObservableProperty]
    public partial EmployeeStatus Status { get; set; } = EmployeeStatus.Probation;

    [ObservableProperty]
    public partial string? ConfirmationText { get; set; }

    [ObservableProperty]
    public partial string? ExitText { get; set; }

    [ObservableProperty]
    public partial string? PhotoFullPath { get; set; }

    [ObservableProperty]
    public partial int SelectedTab { get; set; }

    public bool HasLeft => Status == EmployeeStatus.Left;

    public bool IsOnProbation => Status == EmployeeStatus.Probation;

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        var lookups = await SendAsync(new GetOrganizationLookupsQuery()).ConfigureAwait(true);
        if (lookups.IsSuccess)
        {
            Fill(Departments, lookups.Value.Departments);
            Fill(Designations, lookups.Value.Designations);
            Fill(Locations, lookups.Value.Locations);
            Fill(Grades, lookups.Value.Grades);
        }

        if (parameter is Guid id)
        {
            await LoadAsync(id).ConfigureAwait(true);
        }
        else
        {
            var code = await SendAsync(new GetNextEmployeeCodeQuery()).ConfigureAwait(true);
            using (Loading())
            {
                Code = code.IsSuccess ? code.Value : string.Empty;
            }
        }
    }

    protected override bool IsTrackedProperty(string? propertyName) => propertyName is not (
        nameof(SelectedTab) or nameof(Title) or nameof(PhotoFullPath) or nameof(ConfirmationText) or nameof(ExitText)
        or nameof(Status) or nameof(EmployeeId) or nameof(IsNew) or nameof(IsExisting) or nameof(HasLeft) or nameof(IsOnProbation));

    partial void OnStatusChanged(EmployeeStatus value)
    {
        OnPropertyChanged(nameof(HasLeft));
        OnPropertyChanged(nameof(IsOnProbation));
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (JoinDate is null)
        {
            ShowErrors([SuperOfc.Domain.Common.Error.Validation(nameof(JoinDate), "Enter the join date.")]);
            return;
        }

        var input = new EmployeeInput(
            Code, FullName, Gender, DateOfBirth is null ? null : DateOnly.FromDateTime(DateOfBirth.Value), Phone, Email, Address, NationalId,
            EmergencyContact, DateOnly.FromDateTime(JoinDate.Value), NullIfEmpty(DepartmentId), NullIfEmpty(DesignationId),
            NullIfEmpty(LocationId), NullIfEmpty(GradeId), PaymentMode, BankName, BankBranch, BankAccount, TaxIdentifier, IsOvertimeEligible, null);

        if (EmployeeId is { } id)
        {
            var result = await SendAsync(new UpdateEmployeeCommand(id, input, _rowVersion), showErrors: false).ConfigureAwait(true);
            if (result.IsFailure)
            {
                ShowErrors(result.Errors);
                return;
            }

            Toast(Strings.App_Saved);
            await LoadAsync(id).ConfigureAwait(true);
        }
        else
        {
            var result = await SendAsync(new CreateEmployeeCommand(input, StartConfirmed), showErrors: false).ConfigureAwait(true);
            if (result.IsFailure)
            {
                ShowErrors(result.Errors);
                return;
            }

            Toast(Strings.Employee_Created);
            await LoadAsync(result.Value).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task CancelAsync()
    {
        if (EmployeeId is { } id)
        {
            await LoadAsync(id).ConfigureAwait(true);
        }
        else
        {
            IsDirty = false;
            await Services.Navigation.GoBackAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task ChangePhotoAsync()
    {
        if (EmployeeId is not { } id)
        {
            return;
        }

        var path = files.OpenFile("Images (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp");
        if (path is null)
        {
            return;
        }

        var result = await SendAsync(new SetEmployeePhotoCommand(id, path)).ConfigureAwait(true);
        if (result.IsSuccess)
        {
            PhotoFullPath = storage.GetFullPath(result.Value);
        }
    }

    [RelayCommand]
    private Task ConfirmAsync() => RecordAsync(EmploymentAction.Confirm, null, Strings.Employee_Confirm);

    [RelayCommand]
    private Task PromoteAsync() => ChangeJobAsync(EmploymentAction.Promote, Strings.Employee_Promote);

    [RelayCommand]
    private Task TransferAsync() => ChangeJobAsync(EmploymentAction.Transfer, Strings.Employee_Transfer);

    [RelayCommand]
    private async Task MarkLeftAsync()
    {
        var reason = await Services.Dialogs.PromptAsync(Strings.Employee_MarkLeft, FullName, Strings.Employee_ExitReason).ConfigureAwait(true);
        if (reason is not null)
        {
            await RecordAsync(EmploymentAction.Exit, reason, Strings.Employee_MarkLeft).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private Task RejoinAsync() => RecordAsync(EmploymentAction.Rejoin, null, Strings.Employee_Rejoin);

    [RelayCommand]
    private async Task AddNoteAsync()
    {
        var note = await Services.Dialogs.PromptAsync(Strings.Employee_AddNote, FullName, Strings.Employee_ActionNotes).ConfigureAwait(true);
        if (note is not null)
        {
            await RecordAsync(EmploymentAction.Note, note, null).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task AddDocumentAsync()
    {
        if (EmployeeId is not { } id)
        {
            return;
        }

        var path = files.OpenFile("All files (*.*)|*.*");
        if (path is null)
        {
            return;
        }

        var title = await Services.Dialogs.PromptAsync(Strings.Employee_AddDocument, System.IO.Path.GetFileName(path), Strings.Employee_DocTitle, initial: System.IO.Path.GetFileNameWithoutExtension(path)).ConfigureAwait(true);
        if (title is null)
        {
            return;
        }

        var result = await SendAsync(new AddEmployeeDocumentCommand(id, title, null, path, null, null)).ConfigureAwait(true);
        if (result.IsSuccess)
        {
            await LoadDocumentsAsync(id).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void OpenDocument(EmployeeDocumentDto? doc)
    {
        if (doc is not null)
        {
            files.ShellOpen(storage.GetFullPath(doc.FilePath));
        }
    }

    [RelayCommand]
    private async Task DeleteDocumentAsync(EmployeeDocumentDto? doc)
    {
        if (doc is null || EmployeeId is not { } id || !await Services.Dialogs.ConfirmAsync(Strings.Employee_DeleteDocTitle, doc.Title, Strings.App_Delete, danger: true).ConfigureAwait(true))
        {
            return;
        }

        var result = await SendAsync(new DeleteEmployeeDocumentCommand(doc.Id)).ConfigureAwait(true);
        if (result.IsSuccess)
        {
            await LoadDocumentsAsync(id).ConfigureAwait(true);
        }
    }

    private async Task ChangeJobAsync(EmploymentAction action, string title)
    {
        if (EmployeeId is not { } id)
        {
            return;
        }

        var dialog = new JobChangeDialogModel(Departments, Designations, Locations, Grades);
        if (!await Services.Dialogs.EditJobChangeAsync(title, dialog).ConfigureAwait(true))
        {
            return;
        }

        var result = await SendAsync(new RecordEmploymentEventCommand(
            id, action, DateOnly.FromDateTime(dialog.Date ?? DateTime.Today), dialog.Notes,
            NullIfEmpty(dialog.DepartmentId), NullIfEmpty(dialog.DesignationId), NullIfEmpty(dialog.LocationId), NullIfEmpty(dialog.GradeId))).ConfigureAwait(true);
        if (result.IsSuccess)
        {
            await LoadAsync(id).ConfigureAwait(true);
        }
    }

    private async Task RecordAsync(EmploymentAction action, string? notes, string? dateTitle)
    {
        if (EmployeeId is not { } id)
        {
            return;
        }

        var date = DateTime.Today;
        if (dateTitle is not null)
        {
            var picked = await Services.Dialogs.PickDateAsync(dateTitle, Strings.Employee_ActionDate, date).ConfigureAwait(true);
            if (picked is null)
            {
                return;
            }

            date = picked.Value;
        }

        var result = await SendAsync(new RecordEmploymentEventCommand(id, action, DateOnly.FromDateTime(date), notes)).ConfigureAwait(true);
        if (result.IsSuccess)
        {
            await LoadAsync(id).ConfigureAwait(true);
        }
    }

    private async Task LoadAsync(Guid id)
    {
        var result = await SendAsync(new GetEmployeeQuery(id)).ConfigureAwait(true);
        if (result.IsFailure)
        {
            return;
        }

        var e = result.Value;
        using (Loading())
        {
            EmployeeId = e.Id;
            Code = e.Code;
            FullName = e.FullName;
            Gender = e.Gender;
            DateOfBirth = e.DateOfBirth?.ToDateTime(TimeOnly.MinValue);
            Phone = e.Phone;
            Email = e.Email;
            Address = e.Address;
            NationalId = e.NationalId;
            EmergencyContact = e.EmergencyContact;
            JoinDate = e.JoinDate.ToDateTime(TimeOnly.MinValue);
            DepartmentId = e.DepartmentId ?? Guid.Empty;
            DesignationId = e.DesignationId ?? Guid.Empty;
            LocationId = e.LocationId ?? Guid.Empty;
            GradeId = e.GradeId ?? Guid.Empty;
            PaymentMode = e.PaymentMode;
            BankName = e.BankName;
            BankBranch = e.BankBranch;
            BankAccount = e.BankAccount;
            TaxIdentifier = e.TaxIdentifier;
            IsOvertimeEligible = e.IsOvertimeEligible;
            Status = e.Status;
            _rowVersion = e.RowVersion;
            ConfirmationText = e.ConfirmationDate is { } c ? $"{Strings.Employee_Confirmation} {Format(c)}" : null;
            ExitText = e.ExitDate is { } x ? $"{Strings.Employee_Exit} {Format(x)} · {e.ExitReason}" : null;
            PhotoFullPath = e.PhotoPath is null ? null : storage.GetFullPath(e.PhotoPath);
        }

        OnPropertyChanged(nameof(Title));
        var history = await SendAsync(new GetEmploymentHistoryQuery(id)).ConfigureAwait(true);
        History.Clear();
        if (history.IsSuccess)
        {
            foreach (var h in history.Value)
            {
                History.Add(h);
            }
        }

        await LoadDocumentsAsync(id).ConfigureAwait(true);

        if (ModuleTabs.Count == 0)
        {
            foreach (var tab in EmployeeTabs.Create(provider))
            {
                ModuleTabs.Add(tab);
            }
        }

        foreach (var tab in ModuleTabs)
        {
            await tab.LoadAsync(id).ConfigureAwait(true);
        }
    }

    private async Task LoadDocumentsAsync(Guid id)
    {
        var docs = await SendAsync(new GetEmployeeDocumentsQuery(id)).ConfigureAwait(true);
        Documents.Clear();
        if (docs.IsSuccess)
        {
            foreach (var d in docs.Value)
            {
                Documents.Add(d);
            }
        }
    }

    private static Guid? NullIfEmpty(Guid value) => value == Guid.Empty ? null : value;

    private static void Fill(ObservableCollection<LookupItem> target, IEnumerable<LookupItem> items)
    {
        target.Clear();
        target.Add(new LookupItem(Guid.Empty, Strings.Employee_None));
        foreach (var item in items)
        {
            target.Add(item);
        }
    }
}

/// <summary>Values for the promote/transfer dialog. Guid.Empty means "no change".</summary>
public sealed partial class JobChangeDialogModel(
    IEnumerable<LookupItem> departments,
    IEnumerable<LookupItem> designations,
    IEnumerable<LookupItem> locations,
    IEnumerable<LookupItem> grades) : ObservableObject
{
    public IReadOnlyList<LookupItem> Departments { get; } = WithNoChange(departments);

    public IReadOnlyList<LookupItem> Designations { get; } = WithNoChange(designations);

    public IReadOnlyList<LookupItem> Locations { get; } = WithNoChange(locations);

    public IReadOnlyList<LookupItem> Grades { get; } = WithNoChange(grades);

    [ObservableProperty]
    public partial DateTime? Date { get; set; } = DateTime.Today;

    [ObservableProperty]
    public partial Guid DepartmentId { get; set; }

    [ObservableProperty]
    public partial Guid DesignationId { get; set; }

    [ObservableProperty]
    public partial Guid LocationId { get; set; }

    [ObservableProperty]
    public partial Guid GradeId { get; set; }

    [ObservableProperty]
    public partial string? Notes { get; set; }

    private static List<LookupItem> WithNoChange(IEnumerable<LookupItem> items)
        => [new LookupItem(Guid.Empty, Strings.Employee_NoChange), .. items.Where(i => i.Id != Guid.Empty)];
}

/// <summary>A tab on the employee page contributed by another module.</summary>
public abstract partial class EmployeeTabViewModel(PageServices services) : PageViewModel(services)
{
    public abstract string Header { get; }

    /// <summary>False hides the tab (for example salary for roles without ViewSalaries).</summary>
    public virtual bool IsAllowed => true;

    public abstract Task LoadAsync(Guid employeeId);
}

/// <summary>Modules register their employee tab view model types here (resolved from DI per page).</summary>
public static class EmployeeTabs
{
    private static readonly List<Type> Types = [];
    private static readonly Lock Gate = new();

    /// <summary>Thread-safe: several hosts can register at once (parallel tests).</summary>
    public static void Register<T>()
        where T : EmployeeTabViewModel
    {
        lock (Gate)
        {
            if (!Types.Contains(typeof(T)))
            {
                Types.Add(typeof(T));
            }
        }
    }

    public static IEnumerable<EmployeeTabViewModel> Create(IServiceProvider provider)
    {
        List<Type> types;
        lock (Gate)
        {
            types = [.. Types];
        }

        return types.Select(t => (EmployeeTabViewModel)provider.GetService(t)!).Where(t => t is not null && t.IsAllowed).ToList();
    }
}
