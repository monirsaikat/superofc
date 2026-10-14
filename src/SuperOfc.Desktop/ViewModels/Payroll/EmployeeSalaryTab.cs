using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SuperOfc.Application.Employees;
using SuperOfc.Application.Payroll;
using SuperOfc.Desktop.Mvvm;
using SuperOfc.Desktop.Resources;
using SuperOfc.Desktop.ViewModels.Employees;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Security;

namespace SuperOfc.Desktop.ViewModels.Payroll;

/// <summary>Employee › Salary: current salary, history, new salary from a structure, edit or delete future salaries (P4-T2).</summary>
public sealed partial class EmployeeSalaryTabViewModel(PageServices services) : EmployeeTabViewModel(services)
{
    private Guid _employeeId;
    private Guid? _editId;
    private IReadOnlyList<SalaryComponentDto> _components = [];

    public override string Header => Strings.EmpTab_Salary;

    public override string Title => Header;

    public override bool IsAllowed => Can(Permission.ViewSalaries);

    public bool CanEdit => Can(Permission.ManagePayroll);

    public ObservableCollection<SalaryAssignmentDto> History { get; } = [];

    public ObservableCollection<SalaryStructureDto> Structures { get; } = [];

    [ObservableProperty]
    public partial SalaryAssignmentDto? Current { get; set; }

    [ObservableProperty]
    public partial bool IsEditing { get; set; }

    [ObservableProperty]
    public partial DateTime? EditFrom { get; set; }

    [ObservableProperty]
    public partial Guid? EditStructureId { get; set; }

    [ObservableProperty]
    public partial string? EditNote { get; set; }

    [ObservableProperty]
    public partial SalaryLinesEditor? Editor { get; set; }

    public bool IsEmpty => History.Count == 0;

    public override async Task LoadAsync(Guid employeeId)
    {
        _employeeId = employeeId;
        var components = await SendAsync(new GetSalaryComponentsQuery(ActiveOnly: true), showErrors: false).ConfigureAwait(true);
        _components = components.IsSuccess ? components.Value : [];
        var structures = await SendAsync(new GetSalaryStructuresQuery(), showErrors: false).ConfigureAwait(true);
        Structures.Clear();
        if (structures.IsSuccess)
        {
            foreach (var s in structures.Value)
            {
                Structures.Add(s);
            }
        }

        var history = await SendAsync(new GetSalaryAssignmentsQuery(employeeId)).ConfigureAwait(true);
        History.Clear();
        if (history.IsSuccess)
        {
            foreach (var a in history.Value)
            {
                History.Add(a);
            }
        }

        var today = DateOnly.FromDateTime(DateTime.Today);
        Current = History.FirstOrDefault(a => a.IsCurrent(today)) ?? History.FirstOrDefault(a => a.EffectiveFrom > today);
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>New salary: starts from the current one (a raise) or the first structure.</summary>
    [RelayCommand]
    private async Task NewAsync()
    {
        _editId = null;
        EditNote = null;
        var latest = History.FirstOrDefault();
        var today = DateTime.Today;
        EditFrom = latest is null ? today : new DateTime(today.Year, today.Month, 1).AddMonths(1);
        EditStructureId = latest?.StructureId ?? Structures.FirstOrDefault()?.Id;
        await OpenEditorAsync(latest?.Lines ?? Structures.FirstOrDefault(s => s.Id == EditStructureId)?.Lines).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task EditAsync(SalaryAssignmentDto? assignment)
    {
        if (assignment is null || assignment.IsLocked)
        {
            return;
        }

        _editId = assignment.Id;
        EditFrom = assignment.EffectiveFrom.ToDateTime(TimeOnly.MinValue);
        EditStructureId = assignment.StructureId;
        EditNote = assignment.Note;
        await OpenEditorAsync(assignment.Lines).ConfigureAwait(true);
    }

    /// <summary>Applies the chosen structure's components and default values, keeping the basic already entered.</summary>
    [RelayCommand]
    private async Task ApplyStructureAsync()
    {
        if (Editor is null || Structures.FirstOrDefault(s => s.Id == EditStructureId) is not { } structure)
        {
            return;
        }

        var basic = Editor.Basic?.Amount ?? 0;
        Editor.Load(structure.Lines);
        if (Editor.Basic is { } row && basic > 0)
        {
            row.Amount = basic;
        }

        await Editor.RefreshPreviewAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (Editor is null || EditFrom is null)
        {
            return;
        }

        var result = await SendAsync(new SaveSalaryAssignmentCommand(_editId, _employeeId, DateOnly.FromDateTime(EditFrom.Value), EditStructureId, EditNote, Editor.ToInputs()))
            .ConfigureAwait(true);
        if (result.IsSuccess)
        {
            IsEditing = false;
            Toast(Strings.App_Saved);
            await LoadAsync(_employeeId).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void CancelEdit() => IsEditing = false;

    [RelayCommand]
    private async Task DeleteAsync(SalaryAssignmentDto? assignment)
    {
        if (assignment is null || assignment.IsLocked)
        {
            return;
        }

        var title = string.Format(CultureInfo.CurrentCulture, Strings.Salary_DeleteTitle, Format(assignment.EffectiveFrom));
        if (!await Services.Dialogs.ConfirmAsync(title, Strings.Salary_DeleteMessage, Strings.App_Delete, danger: true).ConfigureAwait(true))
        {
            return;
        }

        if ((await SendAsync(new DeleteSalaryAssignmentCommand(assignment.Id)).ConfigureAwait(true)).IsSuccess)
        {
            await LoadAsync(_employeeId).ConfigureAwait(true);
        }
    }

    private async Task OpenEditorAsync(IReadOnlyList<SalaryLineDto>? lines)
    {
        var editor = new SalaryLinesEditor(Dispatcher, _components);
        if (lines is not null)
        {
            editor.Load(lines);
        }

        Editor = editor;
        ErrorMessage = null;
        IsEditing = true;
        await editor.RefreshPreviewAsync().ConfigureAwait(true);
    }
}

/// <summary>One employee in the bulk revision picker.</summary>
public sealed partial class RevisionCandidate(EmployeeListItem employee) : ObservableObject
{
    public EmployeeListItem Employee { get; } = employee;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>Bulk salary revision wizard: choose employees, set the percent and date, preview, apply (P4-T2).</summary>
public sealed partial class SalaryRevisionViewModel(PageServices services) : PageViewModel(services)
{
    private List<RevisionCandidate> _all = [];

    public override string Title => Strings.Revision_Title;

    public ObservableCollection<WizardStep> Steps { get; } = WizardStep.Create([Strings.Revision_StepEmployees, Strings.Revision_StepChange, Strings.Revision_StepReview]);

    public ObservableCollection<RevisionCandidate> Candidates { get; } = [];

    public ObservableCollection<SalaryRevisionRow> Preview { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChoosing), nameof(IsSetting), nameof(IsReviewing))]
    public partial int Step { get; set; }

    public bool IsChoosing => Step == 0;

    public bool IsSetting => Step == 1;

    public bool IsReviewing => Step == 2;

    [ObservableProperty]
    public partial string? Search { get; set; }

    [ObservableProperty]
    public partial double Percent { get; set; } = 10;

    [ObservableProperty]
    public partial DateTime? EffectiveFrom { get; set; } = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(1);

    [ObservableProperty]
    public partial bool AllFixedEarnings { get; set; }

    [ObservableProperty]
    public partial string? Note { get; set; }

    [ObservableProperty]
    public partial string? Summary { get; set; }

    public int SelectedCount => _all.Count(c => c.IsSelected);

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        var result = await SendAsync(new GetEmployeeListQuery(PageSize: 10_000)).ConfigureAwait(true);
        _all = result.IsSuccess ? result.Value.Items.Select(e => new RevisionCandidate(e)).ToList() : [];
        foreach (var c in _all)
        {
            c.PropertyChanged += (_, _) => OnPropertyChanged(nameof(SelectedCount));
        }

        Filter();
    }

    partial void OnSearchChanged(string? value) => Filter();

    [RelayCommand]
    private void SelectAll(bool? select)
    {
        foreach (var c in Candidates)
        {
            c.IsSelected = select ?? true;
        }
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        ErrorMessage = null;
        if (Step == 0)
        {
            if (SelectedCount == 0)
            {
                ErrorMessage = Strings.Revision_ChooseEmployees;
                return;
            }

            Step = 1;
        }
        else if (Step == 1)
        {
            if (EffectiveFrom is null || Percent == 0)
            {
                ErrorMessage = Strings.Revision_EnterChange;
                return;
            }

            var result = await SendAsync(new PreviewSalaryRevisionQuery(SelectedIds(), DateOnly.FromDateTime(EffectiveFrom.Value), (decimal)Percent, AllFixedEarnings)).ConfigureAwait(true);
            if (result.IsFailure)
            {
                return;
            }

            Preview.Clear();
            foreach (var row in result.Value)
            {
                Preview.Add(row);
            }

            var ready = Preview.Count(r => r.Problem is null);
            Summary = string.Format(CultureInfo.CurrentCulture, Strings.Revision_Summary, ready, Preview.Count - ready,
                Format(new Money(Preview.Where(r => r.Problem is null).Sum(r => (r.NewGross - r.CurrentGross).Minor))));
            Step = 2;
        }

        WizardStep.Update(Steps, Step);
    }

    [RelayCommand]
    private void Back()
    {
        if (Step > 0)
        {
            Step--;
            ErrorMessage = null;
            WizardStep.Update(Steps, Step);
        }
    }

    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (EffectiveFrom is null)
        {
            return;
        }

        var ready = Preview.Count(r => r.Problem is null);
        var title = string.Format(CultureInfo.CurrentCulture, Strings.Revision_ConfirmTitle, ready);
        if (!await Services.Dialogs.ConfirmAsync(title, Strings.Revision_ConfirmMessage, Strings.Revision_Apply).ConfigureAwait(true))
        {
            return;
        }

        var result = await SendAsync(new ApplySalaryRevisionCommand(SelectedIds(), DateOnly.FromDateTime(EffectiveFrom.Value), (decimal)Percent, AllFixedEarnings, Note)).ConfigureAwait(true);
        if (result.IsSuccess)
        {
            Toast(string.Format(CultureInfo.CurrentCulture, Strings.Revision_Done, result.Value));
            await Services.Navigation.GoBackAsync().ConfigureAwait(true);
        }
    }

    private List<Guid> SelectedIds() => _all.Where(c => c.IsSelected).Select(c => c.Employee.Id).ToList();

    private void Filter()
    {
        var text = Search?.Trim();
        Candidates.Clear();
        foreach (var c in _all.Where(c => string.IsNullOrEmpty(text)
                     || c.Employee.FullName.Contains(text, StringComparison.CurrentCultureIgnoreCase)
                     || c.Employee.Code.Contains(text, StringComparison.OrdinalIgnoreCase)
                     || (c.Employee.Department?.Contains(text, StringComparison.CurrentCultureIgnoreCase) ?? false)))
        {
            Candidates.Add(c);
        }
    }
}
