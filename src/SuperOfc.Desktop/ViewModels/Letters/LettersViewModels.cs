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

/// <summary>Where Word letters are put for editing and samples, and the save dialog for reprints.</summary>
public static class WordLetterPaths
{
    /// <summary>The app's folder under %TEMP%; pages can point elsewhere (tests use their own folder).</summary>
    public static string DefaultRoot => Path.Combine(Path.GetTempPath(), "SuperOfc");

    private static string Folder(string root, params string[] names)
    {
        var folder = Path.Combine([root, .. names]);
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>
    /// A stable path per template, so "Edit in Word" and "Use saved changes" use the same file. The folder is the template's id,
    /// so templates whose names differ only in characters a file name cannot hold never share a file.
    /// </summary>
    public static string ForEditing(string root, Guid templateId, string templateName)
        => Path.Combine(Folder(root, "templates", templateId.ToString("N")), LetterIssue.Safe(templateName) + ".docx");

    /// <summary>A new path each time, so a sample still open in Word does not block the next one.</summary>
    public static string ForSample(string root, string templateName)
        => Path.Combine(Folder(root, "samples"), LetterIssue.Safe($"{templateName} {Guid.NewGuid().ToString("N")[..6]}") + ".docx");

    /// <summary>A new empty folder for a short-lived copy; delete it with <see cref="DeleteQuietly"/>.</summary>
    public static string NewScratchFolder(string root) => Folder(root, "upload", Guid.NewGuid().ToString("N"));

    /// <summary>Reads a file that Word may still have open (Word keeps a write handle on the document it shows).</summary>
    public static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    /// <summary>Copies a file that may be open in Word to a scratch folder, keeping its file name.</summary>
    public static string ReadableCopy(string root, string path)
    {
        var bytes = ReadShared(path);
        var copy = Path.Combine(NewScratchFolder(root), Path.GetFileName(path));
        File.WriteAllBytes(copy, bytes);
        return copy;
    }

    /// <summary>The file is locked by another program (sharing or lock violation), for example Word with the file open.</summary>
    public static bool IsLocked(IOException ex) => ex is not null && (ex.HResult & 0xFFFF) is 32 or 33;

    public static void DeleteQuietly(string folder)
    {
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover file in %TEMP% is harmless.
        }
    }

    /// <summary>Asks where to save a reprint: Word letters as PDF (needs Word) or .docx, text letters as PDF.</summary>
    public static string? AskReprintPath(IFileDialogService files, IssuedLetterItem letter, bool wordInstalled)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(letter);
        var name = $"{letter.TemplateName} {letter.Number.Replace('/', '-')} {letter.EmployeeName}";
        if (!letter.HasDocument)
        {
            return files.SaveFile("PDF (*.pdf)|*.pdf", name + ".pdf");
        }

        return wordInstalled ? files.SaveFile(Strings.Letters_PdfOrWordFilter, name + ".pdf") : files.SaveFile(Strings.Letters_WordFilter, name + ".docx");
    }
}

/// <summary>Asks the page to copy a field to the clipboard; the page sets <see cref="Copied"/> when it worked.</summary>
public sealed class LetterFieldCopyEventArgs(string token) : EventArgs
{
    public string Token { get; } = token;

    public bool Copied { get; set; }
}

/// <summary>Settings › Letter templates: write letters once with fields; the preview fills them for a sample employee.</summary>
public sealed partial class LetterTemplatesSettingsViewModel(PageServices services, IFileDialogService files) : EditableViewModel(services)
{
    private int _previewVersion;
    private bool _loading;
    private Guid? _editedTemplateId;

    /// <summary>Last-write time of each edit file when SuperOfc last wrote or used it; a later time means edits not used yet.</summary>
    private readonly Dictionary<string, DateTime> _editFileTimes = new(StringComparer.OrdinalIgnoreCase);

    public override string Title => Strings.Settings_LetterTemplates;

    /// <summary>Where Word files for editing and samples go; tests point it at their own folder.</summary>
    public string TempFolder { get; set; } = WordLetterPaths.DefaultRoot;

    public bool CanManage => Can(Permission.ManageEmployees);

    public ObservableCollection<LetterTemplateDto> Templates { get; } = [];

    public ObservableCollection<LetterField> Fields { get; } = [];

    public ObservableCollection<LookupItem> Employees { get; } = [];

    public static IReadOnlyList<Choice<LetterKind>> Kinds => LetterKinds.All;

    /// <summary>Delay before the preview refreshes after typing; zero in tests.</summary>
    public TimeSpan PreviewDelay { get; set; } = TimeSpan.FromMilliseconds(400);

    /// <summary>Raised to put a field at the caret; the page inserts it into the box that had focus.</summary>
    public event EventHandler<string>? InsertRequested;

    /// <summary>Raised for Word templates instead of <see cref="InsertRequested"/>: the page copies the field to the clipboard.</summary>
    public event EventHandler<LetterFieldCopyEventArgs>? CopyRequested;

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

    /// <summary>The template being edited is a Word file; its letter text is edited in Word.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsText))]
    public partial bool IsWord { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WordFileText))]
    public partial string? DocumentFileName { get; set; }

    /// <summary>The file exported by "Edit in Word"; "Use saved changes" uploads it again.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEditedFile))]
    public partial string? EditedPath { get; set; }

    public bool IsText => !IsWord;

    public bool HasEditedFile => EditedPath is not null;

    public string WordFileText => string.Format(CultureInfo.CurrentCulture, Strings.LetterTemplates_WordFile, DocumentFileName);

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
            IsWord = value.IsWord;
            DocumentFileName = value.DocumentFileName;
            _loading = false;
        }

        if (value.Id != _editedTemplateId || !value.IsWord)
        {
            EditedPath = null;
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
        IsWord = false;
        DocumentFileName = null;
        EditedPath = null;
        _loading = false;
        IsEditing = true;
        IsDirty = false;
        ClearErrors();
        _ = RefreshPreviewAsync();
    }

    [RelayCommand]
    private void Insert(LetterField? field)
    {
        if (field is null)
        {
            return;
        }

        if (IsWord)
        {
            var copy = new LetterFieldCopyEventArgs(field.Token);
            CopyRequested?.Invoke(this, copy);
            if (copy.Copied)
            {
                Toast(string.Format(CultureInfo.CurrentCulture, Strings.LetterTemplates_Copied, field.Token));
            }
            else
            {
                Services.Notify.ToastError(string.Format(CultureInfo.CurrentCulture, Strings.LetterTemplates_CopyFailed, field.Token));
            }
        }
        else
        {
            InsertRequested?.Invoke(this, field.Token);
        }
    }

    /// <summary>Uploads a Word file as a new template.</summary>
    [RelayCommand]
    private async Task AddWordAsync()
    {
        // The new template is selected afterwards, so unsaved edits to this one would be lost.
        if (!await CanNavigateAwayAsync().ConfigureAwait(true))
        {
            return;
        }

        var path = files.OpenFile(Strings.Letters_WordFilter);
        if (path is null)
        {
            return;
        }

        var added = await ImportAsync(null, path).ConfigureAwait(true);
        if (added is { } id)
        {
            IsDirty = false;
            Toast(Strings.LetterTemplates_WordAdded);
            await LoadAsync(id).ConfigureAwait(true);
        }
    }

    /// <summary>Saves the template's Word file to a temporary folder and opens it in Word.</summary>
    [RelayCommand]
    private async Task EditInWordAsync()
    {
        if (EditingId is not { } id || !IsWord || !await SaveChangesFirstAsync().ConfigureAwait(true))
        {
            return;
        }

        string path;
        try
        {
            path = WordLetterPaths.ForEditing(TempFolder, id, Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = ex.Message;
            return;
        }

        // Edits saved in Word but not used yet must not be overwritten without asking.
        if (File.Exists(path) && !IsAsLastWritten(path))
        {
            var changed = await DiffersFromStoredAsync(id, path).ConfigureAwait(true);
            if (changed is null)
            {
                return;
            }

            if (changed.Value)
            {
                _editedTemplateId = id;
                EditedPath = path;
                if (await Services.Dialogs.ConfirmAsync(Strings.LetterTemplates_UnusedEditsTitle,
                        string.Format(CultureInfo.CurrentCulture, Strings.LetterTemplates_UnusedEditsMessage, Name), Strings.LetterTemplates_UseSavedChanges)
                    .ConfigureAwait(true))
                {
                    if (await ImportIntoSelectedAsync(path).ConfigureAwait(true))
                    {
                        files.ShellOpen(path);
                    }

                    return;
                }

                if (!await Services.Dialogs.ConfirmAsync(Strings.LetterTemplates_StartAgainTitle, Strings.LetterTemplates_StartAgainMessage,
                        Strings.LetterTemplates_StartAgain, danger: true).ConfigureAwait(true))
                {
                    return;
                }
            }
        }

        if ((await SendAsync(new ExportLetterTemplateDocumentCommand(id, path)).ConfigureAwait(true)).IsSuccess)
        {
            _editedTemplateId = id;
            EditedPath = path;
            RememberWritten(path);
            files.ShellOpen(path);
        }
    }

    /// <summary>Uploads the file edited in Word into this template.</summary>
    [RelayCommand]
    private Task UseSavedChangesAsync() => EditedPath is { } path ? ImportIntoSelectedAsync(path) : Task.CompletedTask;

    [RelayCommand]
    private Task ReplaceWordAsync() => files.OpenFile(Strings.Letters_WordFilter) is { } path ? ImportIntoSelectedAsync(path) : Task.CompletedTask;

    [RelayCommand]
    private async Task RemoveWordAsync()
    {
        if (EditingId is not { } id || !IsWord || !await Services.Dialogs.ConfirmAsync(string.Format(CultureInfo.CurrentCulture, Strings.LetterTemplates_RemoveWordTitle, Name),
                Strings.LetterTemplates_RemoveWordMessage, Strings.LetterTemplates_RemoveWordConfirm, danger: true).ConfigureAwait(true)
            || !await SaveChangesFirstAsync().ConfigureAwait(true))
        {
            return;
        }

        if ((await SendAsync(new RemoveLetterTemplateDocumentCommand(id)).ConfigureAwait(true)).IsSuccess)
        {
            IsDirty = false;
            EditedPath = null;
            await LoadAsync(id).ConfigureAwait(true);
        }
    }

    /// <summary>Opens the Word file filled for the sample employee.</summary>
    [RelayCommand]
    private async Task OpenSampleAsync()
    {
        if (EditingId is not { } id || !IsWord || SampleEmployeeId == Guid.Empty || !await SaveChangesFirstAsync().ConfigureAwait(true))
        {
            return;
        }

        string path;
        try
        {
            path = WordLetterPaths.ForSample(TempFolder, Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = ex.Message;
            return;
        }

        if ((await SendAsync(new PreviewLetterDocumentCommand(SampleEmployeeId, id, null, null, path)).ConfigureAwait(true)).IsSuccess)
        {
            files.ShellOpen(path);
        }
    }

    private async Task<bool> ImportIntoSelectedAsync(string path)
    {
        if (EditingId is not { } id || !await SaveChangesFirstAsync().ConfigureAwait(true))
        {
            return false;
        }

        if (await ImportAsync(id, path).ConfigureAwait(true) is null)
        {
            return false;
        }

        if (string.Equals(path, EditedPath, StringComparison.OrdinalIgnoreCase))
        {
            RememberWritten(path);
        }

        IsDirty = false;
        Toast(Strings.LetterTemplates_WordUpdated);
        await LoadAsync(id).ConfigureAwait(true);
        return true;
    }

    /// <summary>
    /// Uploads a Word file. It is read through a copy, because Word keeps the document it shows open for writing and a plain read
    /// of it fails; a file Word holds exclusively gets a clear message instead.
    /// </summary>
    private async Task<Guid?> ImportAsync(Guid? templateId, string path)
    {
        string? scratch = null;
        var source = path;
        try
        {
            try
            {
                source = WordLetterPaths.ReadableCopy(TempFolder, path);
                scratch = Path.GetDirectoryName(source);
            }
            catch (IOException ex) when (WordLetterPaths.IsLocked(ex))
            {
                ErrorMessage = Strings.LetterTemplates_CloseInWord;
                return null;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // The import reports a missing file in its own words.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ErrorMessage = ex.Message;
                return null;
            }

            var result = await SendAsync(new ImportLetterTemplateDocumentCommand(templateId, source)).ConfigureAwait(true);
            return result.IsSuccess ? result.Value : null;
        }
        finally
        {
            if (scratch is not null)
            {
                WordLetterPaths.DeleteQuietly(scratch);
            }
        }
    }

    /// <summary>Saves edits to name, subject, kind and prefix before a Word action reloads the template; false when they are not valid.</summary>
    private async Task<bool> SaveChangesFirstAsync()
    {
        if (!IsDirty)
        {
            return true;
        }

        if (await SaveCoreAsync().ConfigureAwait(true) is not { } id)
        {
            return false;
        }

        Toast(Strings.App_Saved);
        await LoadAsync(id).ConfigureAwait(true);
        return true;
    }

    private void RememberWritten(string path)
    {
        try
        {
            _editFileTimes[path] = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _editFileTimes.Remove(path);
        }
    }

    private bool IsAsLastWritten(string path)
        => _editFileTimes.TryGetValue(path, out var written) && File.GetLastWriteTimeUtc(path) == written;

    /// <summary>Whether the edit file holds something other than the stored Word file; null when the stored file could not be read.</summary>
    private async Task<bool?> DiffersFromStoredAsync(Guid id, string path)
    {
        string scratch;
        try
        {
            scratch = WordLetterPaths.NewScratchFolder(TempFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = ex.Message;
            return null;
        }

        try
        {
            var stored = Path.Combine(scratch, "stored.docx");
            if ((await SendAsync(new ExportLetterTemplateDocumentCommand(id, stored)).ConfigureAwait(true)).IsFailure)
            {
                return null;
            }

            try
            {
                return !WordLetterPaths.ReadShared(path).AsSpan().SequenceEqual(File.ReadAllBytes(stored));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The edit file cannot be read; treat it as changed so nothing is overwritten without asking.
                return true;
            }
        }
        finally
        {
            WordLetterPaths.DeleteQuietly(scratch);
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (await SaveCoreAsync().ConfigureAwait(true) is not { } id)
        {
            return;
        }

        Toast(Strings.App_Saved);
        await LoadAsync(id).ConfigureAwait(true);
    }

    private async Task<Guid?> SaveCoreAsync()
    {
        ClearErrors();
        var saved = await SendAsync(new SaveLetterTemplateCommand(EditingId, Name, Kind, Subject, Body, NumberPrefix, IsActive), showErrors: false).ConfigureAwait(true);
        if (saved.IsFailure)
        {
            ShowErrors(saved.Errors);
            return null;
        }

        IsDirty = false;
        return saved.Value;
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

    /// <summary>Where sample Word files go; tests point it at their own folder.</summary>
    public string TempFolder { get; set; } = WordLetterPaths.DefaultRoot;

    public bool CanIssue => Can(Permission.ManageEmployees);

    public ObservableCollection<LetterTemplateDto> Templates { get; } = [];

    public ObservableCollection<LetterRecipient> People { get; } = [];

    public ObservableCollection<LookupItem> Departments { get; } = [];

    public ObservableCollection<LetterInput> Inputs { get; } = [];

    public ObservableCollection<IssuedLetterItem> Issued { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInputs), nameof(IsWordTemplate), nameof(PerEmployeeForced), nameof(PerEmployeeEnabled), nameof(PerEmployeeChecked),
        nameof(OnePerEmployeeText), nameof(IssueText), nameof(ShowPdfNeedsWord))]
    public partial LetterTemplateDto? Template { get; set; }

    /// <summary>Microsoft Word is installed, so Word templates can be saved as PDF.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPdfNeedsWord))]
    public partial bool WordInstalled { get; set; }

    /// <summary>How a Word template's letters are saved; text templates are always PDF.</summary>
    [ObservableProperty]
    public partial LetterOutput Output { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PerEmployeeForced), nameof(PerEmployeeEnabled), nameof(PerEmployeeChecked))]
    public partial int SelectedCount { get; set; }

    public bool IsWordTemplate => Template?.IsWord == true;

    public bool ShowPdfNeedsWord => IsWordTemplate && !WordInstalled;

    /// <summary>Word files are never merged: several people always get one file each.</summary>
    public bool PerEmployeeForced => IsWordTemplate && SelectedCount > 1;

    public bool PerEmployeeEnabled => !PerEmployeeForced;

    /// <summary>The tick box: checked when forced, otherwise <see cref="OneFilePerEmployee"/>.</summary>
    public bool PerEmployeeChecked
    {
        get => PerEmployeeForced || OneFilePerEmployee;
        set
        {
            if (!PerEmployeeForced)
            {
                OneFilePerEmployee = value;
            }

            OnPropertyChanged();
        }
    }

    public string OnePerEmployeeText => IsWordTemplate ? Strings.Letters_OnePerEmployeeFile : Strings.Letters_OnePerEmployee;

    public string IssueText => IsWordTemplate ? Strings.Letters_IssueWord : Strings.Letters_Issue;

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
        var options = await SendAsync(new GetLetterOutputOptionsQuery(), showErrors: false).ConfigureAwait(true);
        WordInstalled = options.IsSuccess && options.Value.WordInstalled;
        ChooseOutput();
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
        ChooseOutput();
        _ = RefreshPreviewAsync();
    }

    partial void OnOneFilePerEmployeeChanged(bool value) => OnPropertyChanged(nameof(PerEmployeeChecked));

    partial void OnWordInstalledChanged(bool value) => ChooseOutput();

    /// <summary>Text templates are PDF; Word templates are PDF when Word is installed, otherwise Word files.</summary>
    private void ChooseOutput()
    {
        if (!IsWordTemplate)
        {
            Output = LetterOutput.Pdf;
        }
        else if (!WordInstalled)
        {
            Output = LetterOutput.Word;
        }
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

        var output = template.IsWord ? Output : LetterOutput.Pdf;
        var perEmployee = OneFilePerEmployee || (template.IsWord && ids.Count > 1);
        var (filter, extension) = output == LetterOutput.Word ? (Strings.Letters_WordFilter, ".docx") : ("PDF (*.pdf)|*.pdf", ".pdf");
        string? path;
        if (perEmployee)
        {
            var folder = files.PickFolder(Strings.Letters_PickFolder);

            // Word letters go straight into the chosen folder; text letters into a folder named after the letter and date.
            path = folder is null || template.IsWord ? folder : Path.Combine(folder, $"{template.Name} {date:yyyy-MM-dd}");
        }
        else
        {
            var single = ids.Count == 1 ? _people.First(p => p.IsSelected).Employee : null;
            path = files.SaveFile(filter, single is null ? $"{template.Name} {date:yyyy-MM-dd}{extension}" : $"{template.Name} {single.Code} {single.FullName}{extension}");
        }

        if (path is null)
        {
            return;
        }

        var result = await SendAsync(new IssueLettersCommand(template.Id, ids, DateOnly.FromDateTime(date), InputValues(), path, perEmployee, output), showErrors: false)
            .ConfigureAwait(true);
        if (result.IsFailure)
        {
            ErrorMessage = ErrorText.For(result.Errors);
            return;
        }

        var r = result.Value;
        Toast(string.Format(CultureInfo.CurrentCulture, Strings.Letters_Issued, r.Count, r.Numbers[0], r.Numbers[^1]));
        if (perEmployee)
        {
            files.OpenFolder(path);
        }
        else
        {
            files.ShellOpen(path);
        }

        await RefreshAsync().ConfigureAwait(true);
    }

    /// <summary>Opens the Word template filled for the first ticked person (nothing is numbered or kept).</summary>
    [RelayCommand]
    private async Task OpenSampleAsync()
    {
        ErrorMessage = null;
        var first = _people.FirstOrDefault(p => p.IsSelected);
        if (Template is not { IsWord: true } template || first is null || Date is not { } date)
        {
            ErrorMessage = Strings.Letters_ChooseTemplateAndPeople;
            return;
        }

        string path;
        try
        {
            path = WordLetterPaths.ForSample(TempFolder, template.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = ex.Message;
            return;
        }

        var result = await SendAsync(new PreviewLetterDocumentCommand(first.Employee.Id, template.Id, DateOnly.FromDateTime(date), InputValues(), path))
            .ConfigureAwait(true);
        if (result.IsSuccess)
        {
            files.ShellOpen(path);
        }
    }

    [RelayCommand]
    private async Task ReprintAsync(IssuedLetterItem? letter)
    {
        letter ??= SelectedIssued;
        if (letter is null)
        {
            return;
        }

        var path = WordLetterPaths.AskReprintPath(files, letter, WordInstalled);
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
        SelectedCount = count;
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

        var wordInstalled = false;
        if (letter.HasDocument)
        {
            var options = await SendAsync(new GetLetterOutputOptionsQuery(), showErrors: false).ConfigureAwait(true);
            wordInstalled = options.IsSuccess && options.Value.WordInstalled;
        }

        var path = WordLetterPaths.AskReprintPath(files, letter, wordInstalled);
        if (path is not null && (await SendAsync(new ReprintLettersCommand([letter.Id], path)).ConfigureAwait(true)).IsSuccess)
        {
            files.ShellOpen(path);
        }
    }
}
