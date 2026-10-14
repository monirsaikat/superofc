using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Common;
using SuperOfc.Application.Employees;
using SuperOfc.Application.Letters;
using SuperOfc.Application.Organization;
using SuperOfc.Desktop.Mvvm;
using SuperOfc.Desktop.Resources;
using SuperOfc.Desktop.Services;
using SuperOfc.Desktop.ViewModels.Employees;
using SuperOfc.Domain.Letters;
using SuperOfc.Domain.Security;

namespace SuperOfc.Desktop.ViewModels.Letters;

public static class LetterKinds
{
    public static IReadOnlyList<Choice<LetterKind>> All { get; } = [.. Enum.GetValues<LetterKind>().Select(k => new Choice<LetterKind>(k, Strings.Get("LetterKind_" + k)))];

    /// <summary>Preview text without the ** bold markers, which only the PDF renders.</summary>
    public static string? Plain(string? text) => text?.Replace("**", string.Empty, StringComparison.Ordinal);
}

/// <summary>Settings › Letter templates: write letters once with fields; the preview fills them for a sample employee.</summary>
public sealed partial class LetterTemplatesSettingsViewModel(PageServices services) : EditableViewModel(services)
{
    private int _previewVersion;
    private bool _loading;

    public override string Title => Strings.Settings_LetterTemplates;

    public bool CanManage => Can(Permission.ManageEmployees);

    public ObservableCollection<LetterTemplateDto> Templates { get; } = [];

    public ObservableCollection<LetterField> Fields { get; } = [];

    public ObservableCollection<LookupItem> Employees { get; } = [];

    public static IReadOnlyList<Choice<LetterKind>> Kinds => LetterKinds.All;

    /// <summary>Delay before the preview refreshes after typing; zero in tests.</summary>
    public TimeSpan PreviewDelay { get; set; } = TimeSpan.FromMilliseconds(400);

    /// <summary>Raised to put a field at the caret; the page inserts it into the box that had focus.</summary>
    public event EventHandler<string>? InsertRequested;

    private List<LetterField> _allFields = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial LetterTemplateDto? Selected { get; set; }

    [ObservableProperty]
    public partial Guid? EditingId { get; set; }

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial LetterKind Kind { get; set; } = LetterKind.Other;

    [ObservableProperty]
    public partial string NumberPrefix { get; set; } = "HR";

    [ObservableProperty]
    public partial bool IsActive { get; set; } = true;

    [ObservableProperty]
    public partial string Subject { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Body { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? FieldSearch { get; set; }

    [ObservableProperty]
    public partial Guid SampleEmployeeId { get; set; }

    [ObservableProperty]
    public partial string? PreviewSubject { get; set; }

    [ObservableProperty]
    public partial string? PreviewBody { get; set; }

    [ObservableProperty]
    public partial string? PreviewMissing { get; set; }

    [ObservableProperty]
    public partial bool IsEditing { get; set; }

    public bool HasSelection => Selected is not null;

    public bool IsEmpty => Templates.Count == 0;

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        var fields = await SendAsync(new GetLetterFieldsQuery(), showErrors: false).ConfigureAwait(true);
        _allFields = fields.IsSuccess ? [.. fields.Value] : [];
        FilterFields();
        var employees = await SendAsync(new Application.Attendance.GetEmployeePickListQuery(IncludeLeft: true), showErrors: false).ConfigureAwait(true);
        Employees.Clear();
        foreach (var e in employees.IsSuccess ? employees.Value : [])
        {
            Employees.Add(e);
        }

        SampleEmployeeId = Employees.FirstOrDefault()?.Id ?? Guid.Empty;
        await LoadAsync(parameter as Guid?).ConfigureAwait(true);
    }

    private async Task LoadAsync(Guid? select)
    {
        var result = await SendAsync(new GetLetterTemplatesQuery()).ConfigureAwait(true);
        Templates.Clear();
        foreach (var t in result.IsSuccess ? result.Value : [])
        {
            Templates.Add(t);
        }

        OnPropertyChanged(nameof(IsEmpty));
        Selected = Templates.FirstOrDefault(t => t.Id == select) ?? Templates.FirstOrDefault();
        if (Selected is null)
        {
            New();
        }
    }

    partial void OnSelectedChanged(LetterTemplateDto? value)
    {
        if (value is null)
        {
            return;
        }

        using (Loading())
        {
            _loading = true;
            EditingId = value.Id;
            Name = value.Name;
            Kind = value.Kind;
            NumberPrefix = value.NumberPrefix;
            IsActive = value.IsActive;
            Subject = value.Subject;
            Body = value.Body;
            _loading = false;
        }

        IsEditing = true;
        IsDirty = false;
        ClearErrors();
        _ = RefreshPreviewAsync();
    }

    partial void OnFieldSearchChanged(string? value) => FilterFields();

    partial void OnSampleEmployeeIdChanged(Guid value) => _ = RefreshPreviewAsync();

    partial void OnSubjectChanged(string value) => Debounce();

    partial void OnBodyChanged(string value) => Debounce();

    protected override bool IsTrackedProperty(string? propertyName)
        => propertyName is nameof(Name) or nameof(Kind) or nameof(NumberPrefix) or nameof(IsActive) or nameof(Subject) or nameof(Body);

    [RelayCommand]
    private void New()
    {
        _loading = true;
        Selected = null;
        EditingId = null;
        Name = string.Empty;
        Kind = LetterKind.Other;
        NumberPrefix = "HR";
        IsActive = true;
        Subject = string.Empty;
        Body = Strings.LetterTemplates_NewBody;
        _loading = false;
        IsEditing = true;
        IsDirty = false;
        ClearErrors();
        _ = RefreshPreviewAsync();
    }

    [RelayCommand]
    private void Insert(LetterField? field)
    {
        if (field is not null)
        {
            InsertRequested?.Invoke(this, field.Token);
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        ClearErrors();
        var saved = await SendAsync(new SaveLetterTemplateCommand(EditingId, Name, Kind, Subject, Body, NumberPrefix, IsActive), showErrors: false).ConfigureAwait(true);
        if (saved.IsFailure)
        {
            ShowErrors(saved.Errors);
            return;
        }

        IsDirty = false;
        Toast(Strings.App_Saved);
        await LoadAsync(saved.Value).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (EditingId is not { } id || !await Services.Dialogs.ConfirmAsync(string.Format(CultureInfo.CurrentCulture, Strings.LetterTemplates_DeleteTitle, Name),
                Strings.LetterTemplates_DeleteMessage, Strings.App_Delete, danger: true).ConfigureAwait(true))
        {
            return;
        }

        if ((await SendAsync(new DeleteLetterTemplateCommand(id)).ConfigureAwait(true)).IsSuccess)
        {
            IsDirty = false;
            await LoadAsync(null).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task AddStandardAsync()
    {
        var added = await SendAsync(new AddStandardLetterTemplatesCommand()).ConfigureAwait(true);
        if (added.IsSuccess)
        {
            Toast(string.Format(CultureInfo.CurrentCulture, Strings.LetterTemplates_StandardAdded, added.Value));
            await LoadAsync(EditingId).ConfigureAwait(true);
        }
    }

    /// <summary>Fills the fields for the sample employee now.</summary>
    public async Task RefreshPreviewAsync()
    {
        if (SampleEmployeeId == Guid.Empty || !CanManage)
        {
            PreviewSubject = LetterKinds.Plain(Subject);
            PreviewBody = LetterKinds.Plain(Body);
            PreviewMissing = null;
            return;
        }

        var result = await Dispatcher.SendAsync(new PreviewLetterQuery(SampleEmployeeId, null, Subject, Body, null, null)).ConfigureAwait(true);
        if (result.IsFailure)
        {
            return;
        }

        PreviewSubject = LetterKinds.Plain(result.Value.Subject);
        PreviewBody = LetterKinds.Plain(result.Value.Body);
        var missing = result.Value.Missing.Where(m => !m.StartsWith(LetterText.InputGroup + ".", StringComparison.OrdinalIgnoreCase)).ToList();
        PreviewMissing = missing.Count == 0 ? null : string.Format(CultureInfo.CurrentCulture, Strings.LetterTemplates_Missing, string.Join(", ", missing.Select(m => "{" + m + "}")));
    }

    private void Debounce()
    {
        if (!_loading)
        {
            _ = DebouncedAsync();
        }
    }

    private async Task DebouncedAsync()
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

    private void FilterFields()
    {
        var term = FieldSearch?.Trim();
        Fields.Clear();
        foreach (var f in _allFields.Where(f => string.IsNullOrEmpty(term) || f.Key.Contains(term, StringComparison.OrdinalIgnoreCase)
                     || f.Description.Contains(term, StringComparison.CurrentCultureIgnoreCase)))
        {
            Fields.Add(f);
        }
    }
}

/// <summary>An employee row on the Letters page with a tick box.</summary>
public sealed partial class LetterRecipient(EmployeeListItem employee) : ObservableObject
{
    public EmployeeListItem Employee { get; } = employee;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>A value typed when issuing, for <c>{Input.X}</c> fields.</summary>
public sealed partial class LetterInput(string name) : ObservableObject
{
    public string Name { get; } = name;

    [ObservableProperty]
    public partial string? Value { get; set; }
}

/// <summary>Opens the Letters page with employees already ticked.</summary>
public sealed record LettersTarget(IReadOnlyList<Guid> EmployeeIds, Guid? TemplateId = null);

/// <summary>Letters: issue a letter to one or many employees, and the register of letters issued (doc 03 R8).</summary>
public sealed partial class LettersViewModel(PageServices services, IFileDialogService files, IClock clock, ICompanyContext company) : PageViewModel(services), IRefreshable
{
    private List<LetterRecipient> _people = [];

    public override string Title => Strings.Letters_Title;

    public bool CanIssue => Can(Permission.ManageEmployees);

    public ObservableCollection<LetterTemplateDto> Templates { get; } = [];

    public ObservableCollection<LetterRecipient> People { get; } = [];

    public ObservableCollection<LookupItem> Departments { get; } = [];

    public ObservableCollection<LetterInput> Inputs { get; } = [];

    public ObservableCollection<IssuedLetterItem> Issued { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInputs))]
    public partial LetterTemplateDto? Template { get; set; }

    [ObservableProperty]
    public partial Guid? DepartmentId { get; set; } = Guid.Empty;

    [ObservableProperty]
    public partial string? Search { get; set; }

    [ObservableProperty]
    public partial bool IncludeLeft { get; set; }

    [ObservableProperty]
    public partial DateTime? Date { get; set; }

    [ObservableProperty]
    public partial bool OneFilePerEmployee { get; set; }

    [ObservableProperty]
    public partial string? PreviewSubject { get; set; }

    [ObservableProperty]
    public partial string? PreviewBody { get; set; }

    [ObservableProperty]
    public partial string? SelectionText { get; set; }

    [ObservableProperty]
    public partial string? RegisterSearch { get; set; }

    [ObservableProperty]
    public partial IssuedLetterItem? SelectedIssued { get; set; }

    [ObservableProperty]
    public partial int Tab { get; set; }

    public bool HasInputs => Inputs.Count > 0;

    public bool IsRegisterEmpty => Issued.Count == 0;

    public IReadOnlyList<Guid> SelectedIds => [.. _people.Where(p => p.IsSelected).Select(p => p.Employee.Id)];

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        Date = clock.TodayIn(company.TimeZone).ToDateTime(TimeOnly.MinValue);
        var templates = await SendAsync(new GetLetterTemplatesQuery(ActiveOnly: true)).ConfigureAwait(true);
        Templates.Clear();
        foreach (var t in templates.IsSuccess ? templates.Value : [])
        {
            Templates.Add(t);
        }

        var lookups = await SendAsync(new GetOrganizationLookupsQuery(), showErrors: false).ConfigureAwait(true);
        Departments.Clear();
        Departments.Add(new LookupItem(Guid.Empty, Strings.Reports_AllDepartments));
        foreach (var d in lookups.IsSuccess ? lookups.Value.Departments : [])
        {
            Departments.Add(d);
        }

        await LoadPeopleAsync().ConfigureAwait(true);
        if (parameter is LettersTarget target)
        {
            if (target.EmployeeIds.Any(id => _people.All(p => p.Employee.Id != id)))
            {
                IncludeLeft = true;
                await LoadPeopleAsync().ConfigureAwait(true);
            }

            foreach (var p in _people)
            {
                p.IsSelected = target.EmployeeIds.Contains(p.Employee.Id);
            }

            Template = Templates.FirstOrDefault(t => t.Id == target.TemplateId) ?? Templates.FirstOrDefault();
        }
        else
        {
            Template ??= Templates.FirstOrDefault();
        }

        UpdateSelection();
        await RefreshAsync().ConfigureAwait(true);
    }

    public async Task RefreshAsync()
    {
        var issued = await SendAsync(new GetIssuedLettersQuery(Search: RegisterSearch)).ConfigureAwait(true);
        Issued.Clear();
        foreach (var l in issued.IsSuccess ? issued.Value : [])
        {
            Issued.Add(l);
        }

        OnPropertyChanged(nameof(IsRegisterEmpty));
    }

    partial void OnTemplateChanged(LetterTemplateDto? value)
    {
        Inputs.Clear();
        foreach (var name in value?.Inputs ?? [])
        {
            var input = new LetterInput(name);
            input.PropertyChanged += (_, _) => _ = RefreshPreviewAsync();
            Inputs.Add(input);
        }

        OnPropertyChanged(nameof(HasInputs));
        _ = RefreshPreviewAsync();
    }

    partial void OnDepartmentIdChanged(Guid? value) => Filter();

    partial void OnSearchChanged(string? value) => Filter();

    partial void OnIncludeLeftChanged(bool value) => _ = LoadPeopleAsync();

    partial void OnDateChanged(DateTime? value) => _ = RefreshPreviewAsync();

    partial void OnRegisterSearchChanged(string? value) => _ = RefreshAsync();

    [RelayCommand]
    private void SelectAll(bool? select)
    {
        foreach (var p in People)
        {
            p.IsSelected = select ?? true;
        }
    }

    /// <summary>The letter for the first ticked employee, with fields filled.</summary>
    public async Task RefreshPreviewAsync()
    {
        var first = _people.FirstOrDefault(p => p.IsSelected);
        if (Template is null || first is null || !CanIssue || Date is not { } date)
        {
            PreviewSubject = LetterKinds.Plain(Template?.Subject);
            PreviewBody = LetterKinds.Plain(Template?.Body);
            return;
        }

        var result = await Dispatcher.SendAsync(new PreviewLetterQuery(first.Employee.Id, Template.Id, null, null, DateOnly.FromDateTime(date), InputValues()))
            .ConfigureAwait(true);
        if (result.IsSuccess)
        {
            PreviewSubject = LetterKinds.Plain(result.Value.Subject);
            PreviewBody = LetterKinds.Plain(result.Value.Body);
        }
    }

    [RelayCommand]
    private async Task IssueAsync()
    {
        ErrorMessage = null;
        var ids = SelectedIds;
        if (Template is not { } template || ids.Count == 0 || Date is not { } date)
        {
            ErrorMessage = Strings.Letters_ChooseTemplateAndPeople;
            return;
        }

        string? path;
        if (OneFilePerEmployee)
        {
            var folder = files.PickFolder(Strings.Letters_PickFolder);
            path = folder is null ? null : Path.Combine(folder, $"{template.Name} {date:yyyy-MM-dd}");
        }
        else
        {
            var single = ids.Count == 1 ? _people.First(p => p.IsSelected).Employee : null;
            path = files.SaveFile("PDF (*.pdf)|*.pdf", single is null ? $"{template.Name} {date:yyyy-MM-dd}.pdf" : $"{template.Name} {single.Code} {single.FullName}.pdf");
        }

        if (path is null)
        {
            return;
        }

        var result = await SendAsync(new IssueLettersCommand(template.Id, ids, DateOnly.FromDateTime(date), InputValues(), path, OneFilePerEmployee), showErrors: false)
            .ConfigureAwait(true);
        if (result.IsFailure)
        {
            ErrorMessage = ErrorText.For(result.Errors);
            return;
        }

        var r = result.Value;
        Toast(string.Format(CultureInfo.CurrentCulture, Strings.Letters_Issued, r.Count, r.Numbers[0], r.Numbers[^1]));
        if (OneFilePerEmployee)
        {
            files.OpenFolder(path);
        }
        else
        {
            files.ShellOpen(path);
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ReprintAsync(IssuedLetterItem? letter)
    {
        letter ??= SelectedIssued;
        if (letter is null)
        {
            return;
        }

        var path = files.SaveFile("PDF (*.pdf)|*.pdf", $"{letter.TemplateName} {letter.Number.Replace('/', '-')} {letter.EmployeeName}.pdf");
        if (path is not null && (await SendAsync(new ReprintLettersCommand([letter.Id], path)).ConfigureAwait(true)).IsSuccess)
        {
            files.ShellOpen(path);
        }
    }

    private Dictionary<string, string?> InputValues() => Inputs.ToDictionary(i => i.Name, i => i.Value);

    private async Task LoadPeopleAsync()
    {
        var all = new List<EmployeeListItem>();
        for (var page = 0; ; page++)
        {
            var result = await SendAsync(new GetEmployeeListQuery(IncludeLeft: IncludeLeft, PageIndex: page, PageSize: 1000)).ConfigureAwait(true);
            if (result.IsFailure)
            {
                break;
            }

            all.AddRange(result.Value.Items);
            if (all.Count >= result.Value.TotalCount || result.Value.Items.Count == 0)
            {
                break;
            }
        }

        var ticked = SelectedIds.ToHashSet();
        foreach (var p in _people)
        {
            p.PropertyChanged -= OnPersonChanged;
        }

        _people = [.. all.Select(e => new LetterRecipient(e) { IsSelected = ticked.Contains(e.Id) })];
        foreach (var p in _people)
        {
            p.PropertyChanged += OnPersonChanged;
        }

        Filter();
    }

    private void OnPersonChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LetterRecipient.IsSelected))
        {
            UpdateSelection();
        }
    }

    private void UpdateSelection()
    {
        var count = _people.Count(p => p.IsSelected);
        SelectionText = string.Format(CultureInfo.CurrentCulture, Strings.Letters_Selected, count);
        _ = RefreshPreviewAsync();
    }

    private void Filter()
    {
        var term = Search?.Trim();
        var department = Departments.FirstOrDefault(d => d.Id == DepartmentId && d.Id != Guid.Empty)?.Name;
        People.Clear();
        foreach (var p in _people.Where(p => (department is null || p.Employee.Department == department)
                                             && (string.IsNullOrEmpty(term) || p.Employee.Code.Contains(term, StringComparison.OrdinalIgnoreCase)
                                                 || p.Employee.FullName.Contains(term, StringComparison.CurrentCultureIgnoreCase))))
        {
            People.Add(p);
        }
    }
}

/// <summary>Employee › Letters: letters issued to this employee, reprint, and a shortcut to issue a new one.</summary>
public sealed partial class EmployeeLettersTabViewModel(PageServices services, IFileDialogService files) : EmployeeTabViewModel(services)
{
    private Guid _employeeId;

    public override string Header => Strings.EmpTab_Letters;

    public override string Title => Header;

    public bool CanIssue => Can(Permission.ManageEmployees);

    public ObservableCollection<IssuedLetterItem> Letters { get; } = [];

    public bool IsEmpty => Letters.Count == 0;

    public override async Task LoadAsync(Guid employeeId)
    {
        _employeeId = employeeId;
        var result = await SendAsync(new GetIssuedLettersQuery(EmployeeId: employeeId)).ConfigureAwait(true);
        Letters.Clear();
        foreach (var l in result.IsSuccess ? result.Value : [])
        {
            Letters.Add(l);
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private Task NewAsync() => Services.Navigation.NavigateToAsync<LettersViewModel>(new LettersTarget([_employeeId]));

    [RelayCommand]
    private async Task ReprintAsync(IssuedLetterItem? letter)
    {
        if (letter is null)
        {
            return;
        }

        var path = files.SaveFile("PDF (*.pdf)|*.pdf", $"{letter.TemplateName} {letter.Number.Replace('/', '-')} {letter.EmployeeName}.pdf");
        if (path is not null && (await SendAsync(new ReprintLettersCommand([letter.Id], path)).ConfigureAwait(true)).IsSuccess)
        {
            files.ShellOpen(path);
        }
    }
}
