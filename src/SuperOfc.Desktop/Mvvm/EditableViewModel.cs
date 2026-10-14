using System.Collections;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SuperOfc.Desktop.Resources;
using SuperOfc.Domain.Common;

namespace SuperOfc.Desktop.Mvvm;

/// <summary>
/// Base for edit pages: tracks unsaved changes, maps "Validation.Field" errors onto properties for inline
/// messages (INotifyDataErrorInfo) and guards navigation.
/// </summary>
public abstract partial class EditableViewModel(PageServices services) : PageViewModel(services), INotifyDataErrorInfo
{
    private readonly Dictionary<string, List<string>> _errors = new(StringComparer.Ordinal);
    private bool _loading;

    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;

    [ObservableProperty]
    public partial bool IsDirty { get; set; }

    public bool HasErrors => _errors.Count > 0;

    public IEnumerable GetErrors(string? propertyName)
        => propertyName is not null && _errors.TryGetValue(propertyName, out var list) ? list : Array.Empty<string>();

    public override async Task<bool> CanNavigateAwayAsync()
    {
        if (!IsDirty)
        {
            return true;
        }

        return await Services.Dialogs.ConfirmAsync(Strings.App_UnsavedTitle, Strings.App_UnsavedMessage, Strings.App_Discard, danger: true).ConfigureAwait(true);
    }

    /// <summary>Wrap property assignments made while loading so they do not mark the page dirty.</summary>
    protected IDisposable Loading()
    {
        _loading = true;
        return new Scope(() =>
        {
            _loading = false;
            IsDirty = false;
            ClearErrors();
        });
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        ArgumentNullException.ThrowIfNull(e);
        if (_loading || e.PropertyName is nameof(IsDirty) or nameof(IsBusy) or nameof(ErrorMessage) or nameof(HasErrors))
        {
            return;
        }

        if (IsTrackedProperty(e.PropertyName))
        {
            IsDirty = true;
            ClearError(e.PropertyName!);
        }
    }

    /// <summary>Override to exclude helper properties from dirty tracking.</summary>
    protected virtual bool IsTrackedProperty(string? propertyName) => propertyName is not null;

    /// <summary>Shows validation errors next to fields; other errors go to the error banner.</summary>
    protected void ShowErrors(IReadOnlyList<Error> errors)
    {
        ClearErrors();
        var general = new List<Error>();
        foreach (var error in errors)
        {
            if (error.Code.StartsWith("Validation.", StringComparison.Ordinal))
            {
                var field = error.Code["Validation.".Length..];
                var property = MapField(field);
                if (!_errors.TryGetValue(property, out var list))
                {
                    _errors[property] = list = [];
                }

                list.Add(ErrorText.For(error));
                ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(property));
            }
            else
            {
                general.Add(error);
            }
        }

        ErrorMessage = general.Count > 0 ? ErrorText.For(general) : null;
        OnPropertyChanged(nameof(HasErrors));
    }

    /// <summary>Maps a command field name to the view-model property name when they differ.</summary>
    protected virtual string MapField(string field) => field;

    protected void ClearErrors()
    {
        var keys = _errors.Keys.ToList();
        _errors.Clear();
        foreach (var key in keys)
        {
            ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(key));
        }

        OnPropertyChanged(nameof(HasErrors));
    }

    private void ClearError(string property)
    {
        if (_errors.Remove(property))
        {
            ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(property));
            OnPropertyChanged(nameof(HasErrors));
        }
    }

    private sealed class Scope(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}

/// <summary>One step in a wizard step indicator (docs/04 wizard pattern).</summary>
public sealed partial class WizardStep(string title) : ObservableObject
{
    public string Title { get; } = title;

    [ObservableProperty]
    public partial bool IsReached { get; set; }

    public static System.Collections.ObjectModel.ObservableCollection<WizardStep> Create(IEnumerable<string> titles)
        => new(titles.Select((t, i) => new WizardStep(t) { IsReached = i == 0 }));

    public static void Update(IEnumerable<WizardStep> steps, int current)
    {
        var index = 0;
        foreach (var step in steps)
        {
            step.IsReached = index++ <= current;
        }
    }
}
