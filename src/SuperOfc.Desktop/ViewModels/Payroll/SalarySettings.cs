using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SuperOfc.Application.Payroll;
using SuperOfc.Desktop.Mvvm;
using SuperOfc.Desktop.Resources;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Payroll;
using SuperOfc.Domain.Security;

namespace SuperOfc.Desktop.ViewModels.Payroll;

/// <summary>Settings › Salary components: pay items with their calculation (P4-T1).</summary>
public sealed partial class SalaryComponentsSettingsViewModel(PageServices services) : PageViewModel(services)
{
    private Guid? _editId;

    public override string Title => Strings.Settings_SalaryComponents;

    public bool CanEdit => Can(Permission.ManagePayroll);

    public ObservableCollection<SalaryComponentDto> Components { get; } = [];

    public static IReadOnlyList<Choice<ComponentKind>> Kinds { get; } =
    [
        new(ComponentKind.Earning, Strings.ComponentKind_Earning),
        new(ComponentKind.Deduction, Strings.ComponentKind_Deduction),
        new(ComponentKind.EmployerContribution, Strings.ComponentKind_EmployerContribution),
    ];

    public static IReadOnlyList<Choice<ComponentCalc>> Calcs { get; } =
    [
        new(ComponentCalc.Fixed, Strings.ComponentCalc_Fixed),
        new(ComponentCalc.PercentOfBasic, Strings.ComponentCalc_PercentOfBasic),
        new(ComponentCalc.PercentOfGross, Strings.ComponentCalc_PercentOfGross),
        new(ComponentCalc.Formula, Strings.ComponentCalc_Formula),
    ];

    [ObservableProperty]
    public partial SalaryComponentDto? Selected { get; set; }

    [ObservableProperty]
    public partial bool IsEditing { get; set; }

    [ObservableProperty]
    public partial string EditCode { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EditName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial ComponentKind EditKind { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditUsesAmount), nameof(EditUsesRate), nameof(EditUsesFormula))]
    public partial ComponentCalc EditCalc { get; set; }

    [ObservableProperty]
    public partial double EditRate { get; set; }

    [ObservableProperty]
    public partial double EditAmount { get; set; }

    [ObservableProperty]
    public partial string? EditFormula { get; set; }

    [ObservableProperty]
    public partial bool EditIsBasic { get; set; }

    [ObservableProperty]
    public partial bool EditTaxable { get; set; } = true;

    [ObservableProperty]
    public partial bool EditLinked { get; set; } = true;

    [ObservableProperty]
    public partial bool EditActive { get; set; } = true;

    public bool EditUsesAmount => EditCalc == ComponentCalc.Fixed;

    public bool EditUsesRate => EditCalc is ComponentCalc.PercentOfBasic or ComponentCalc.PercentOfGross;

    public bool EditUsesFormula => EditCalc == ComponentCalc.Formula;

    public bool IsNew => _editId is null;

    public override Task OnNavigatedToAsync(object? parameter) => LoadAsync();

    partial void OnSelectedChanged(SalaryComponentDto? value)
    {
        if (value is null || !CanEdit)
        {
            return;
        }

        _editId = value.Id;
        EditCode = value.Code;
        EditName = value.Name;
        EditKind = value.Kind;
        EditCalc = value.Calc;
        EditRate = (double)value.DefaultRate;
        EditAmount = (double)value.DefaultAmount.Major;
        EditFormula = value.Formula;
        EditIsBasic = value.IsBasic;
        EditTaxable = value.IsTaxable;
        EditLinked = value.IsAttendanceLinked;
        EditActive = value.IsActive;
        IsEditing = true;
        ErrorMessage = null;
        OnPropertyChanged(nameof(IsNew));
    }

    [RelayCommand]
    private void New()
    {
        Selected = null;
        _editId = null;
        EditCode = EditName = string.Empty;
        EditKind = ComponentKind.Earning;
        EditCalc = ComponentCalc.Fixed;
        EditRate = EditAmount = 0;
        EditFormula = null;
        EditIsBasic = false;
        EditTaxable = EditLinked = EditActive = true;
        IsEditing = true;
        ErrorMessage = null;
        OnPropertyChanged(nameof(IsNew));
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var order = _editId is null ? (Components.Count == 0 ? 1 : Components.Max(c => c.DisplayOrder) + 1) : Selected?.DisplayOrder ?? 0;
        var result = await SendAsync(new SaveSalaryComponentCommand(_editId, EditCode, EditName, EditKind, EditCalc, (decimal)EditRate,
            Domain.Common.Money.FromMajor((decimal)EditAmount).Minor, EditUsesFormula ? EditFormula : null, EditIsBasic, EditTaxable, EditLinked, order, EditActive)).ConfigureAwait(true);
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
        if (_editId is not { } id
            || !await Services.Dialogs.ConfirmAsync(string.Format(CultureInfo.CurrentCulture, Strings.SalaryComponents_DeleteTitle, EditName), Strings.SalaryComponents_DeleteMessage, Strings.App_Delete, danger: true).ConfigureAwait(true))
        {
            return;
        }

        if ((await SendAsync(new DeleteSalaryComponentCommand(id)).ConfigureAwait(true)).IsSuccess)
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
        var result = await SendAsync(new GetSalaryComponentsQuery()).ConfigureAwait(true);
        Components.Clear();
        if (result.IsSuccess)
        {
            foreach (var c in result.Value)
            {
                Components.Add(c);
            }
        }
    }
}

/// <summary>Settings › Salary structures: templates of components with a live preview for a sample basic (P4-T1).</summary>
public sealed partial class SalaryStructuresSettingsViewModel(PageServices services) : PageViewModel(services)
{
    private Guid? _editId;
    private IReadOnlyList<SalaryComponentDto> _components = [];

    public override string Title => Strings.Settings_SalaryStructures;

    public bool CanEdit => Can(Permission.ManagePayroll);

    public ObservableCollection<SalaryStructureDto> Structures { get; } = [];

    [ObservableProperty]
    public partial SalaryStructureDto? Selected { get; set; }

    [ObservableProperty]
    public partial bool IsEditing { get; set; }

    [ObservableProperty]
    public partial string EditName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? EditDescription { get; set; }

    [ObservableProperty]
    public partial SalaryLinesEditor? Editor { get; set; }

    public bool IsNew => _editId is null;

    public override Task OnNavigatedToAsync(object? parameter) => LoadAsync();

    async partial void OnSelectedChanged(SalaryStructureDto? value)
    {
        if (value is null || !CanEdit)
        {
            return;
        }

        _editId = value.Id;
        EditName = value.Name;
        EditDescription = value.Description;
        await OpenEditorAsync(value.Lines).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task NewAsync()
    {
        Selected = null;
        _editId = null;
        EditName = string.Empty;
        EditDescription = null;
        await OpenEditorAsync(null).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (Editor is null)
        {
            return;
        }

        var result = await SendAsync(new SaveSalaryStructureCommand(_editId, EditName, EditDescription, Editor.ToInputs())).ConfigureAwait(true);
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
        if (_editId is not { } id
            || !await Services.Dialogs.ConfirmAsync(string.Format(CultureInfo.CurrentCulture, Strings.SalaryStructures_DeleteTitle, EditName), Strings.SalaryStructures_DeleteMessage, Strings.App_Delete, danger: true).ConfigureAwait(true))
        {
            return;
        }

        if ((await SendAsync(new DeleteSalaryStructureCommand(id)).ConfigureAwait(true)).IsSuccess)
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

    private async Task OpenEditorAsync(IReadOnlyList<SalaryLineDto>? lines)
    {
        var editor = new SalaryLinesEditor(Dispatcher, _components);
        if (lines is not null)
        {
            editor.Load(lines);
        }
        else if (editor.Basic is { } basic)
        {
            // A sample basic so the preview shows something useful straight away.
            basic.Amount = 20_000;
        }

        Editor = editor;
        IsEditing = true;
        ErrorMessage = null;
        OnPropertyChanged(nameof(IsNew));
        await editor.RefreshPreviewAsync().ConfigureAwait(true);
    }

    private async Task LoadAsync()
    {
        var components = await SendAsync(new GetSalaryComponentsQuery(ActiveOnly: true)).ConfigureAwait(true);
        _components = components.IsSuccess ? components.Value : [];
        var result = await SendAsync(new GetSalaryStructuresQuery()).ConfigureAwait(true);
        Structures.Clear();
        if (result.IsSuccess)
        {
            foreach (var s in result.Value)
            {
                Structures.Add(s);
            }
        }
    }
}
