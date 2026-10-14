using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using SuperOfc.Application.Abstractions;
using SuperOfc.Desktop.Mvvm;
using SuperOfc.Desktop.Resources;
using Wpf.Ui;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Wpf.Ui.Extensions;

namespace SuperOfc.Desktop.Services;

public interface IAppNavigator
{
    event EventHandler<object>? Navigated;

    object? Current { get; }

    bool CanGoBack { get; }

    Task<bool> NavigateToAsync<TViewModel>(object? parameter = null)
        where TViewModel : class;

    Task<bool> NavigateToAsync(Type viewModelType, object? parameter = null, bool clearHistory = false);

    Task<bool> GoBackAsync();

    void Reset();
}

/// <summary>View-model-first navigation for the shell content area with a simple back stack.</summary>
public sealed class AppNavigator(IServiceProvider services, UnsavedChangesTracker unsaved) : IAppNavigator
{
    private readonly Stack<(Type Type, object? Parameter)> _history = new();
    private (Type Type, object? Parameter)? _currentEntry;

    public event EventHandler<object>? Navigated;

    public object? Current { get; private set; }

    public bool CanGoBack => _history.Count > 0;

    public Task<bool> NavigateToAsync<TViewModel>(object? parameter = null)
        where TViewModel : class
        => NavigateToAsync(typeof(TViewModel), parameter);

    public async Task<bool> NavigateToAsync(Type viewModelType, object? parameter = null, bool clearHistory = false)
    {
        if (!await LeaveCurrentAsync().ConfigureAwait(true))
        {
            return false;
        }

        if (clearHistory)
        {
            _history.Clear();
        }
        else if (_currentEntry is not null)
        {
            _history.Push(_currentEntry.Value);
        }

        await ShowAsync(viewModelType, parameter).ConfigureAwait(true);
        return true;
    }

    public async Task<bool> GoBackAsync()
    {
        if (_history.Count == 0 || !await LeaveCurrentAsync().ConfigureAwait(true))
        {
            return false;
        }

        var (type, parameter) = _history.Pop();
        await ShowAsync(type, parameter).ConfigureAwait(true);
        return true;
    }

    public void Reset()
    {
        _history.Clear();
        _currentEntry = null;
        Current = null;
        unsaved.Current = null;
    }

    private async Task<bool> LeaveCurrentAsync()
        => Current is not INavigationAware aware || await aware.CanNavigateAwayAsync().ConfigureAwait(true);

    private async Task ShowAsync(Type type, object? parameter)
    {
        var viewModel = services.GetRequiredService(type);
        Current = viewModel;
        _currentEntry = (type, parameter);
        unsaved.Current = viewModel as EditableViewModel;
        Navigated?.Invoke(this, viewModel);
        if (viewModel is INavigationAware aware)
        {
            await aware.OnNavigatedToAsync(parameter).ConfigureAwait(true);
        }
    }
}

/// <summary>Blocks update restarts while an edit page has unsaved changes.</summary>
public sealed class UnsavedChangesTracker : IUpdateBlocker
{
    public EditableViewModel? Current { get; set; }

    public string? BlockReason => Current is { IsDirty: true } ? Strings.Updates_Blocked : null;
}

public interface IDialogService
{
    Task<bool> ConfirmAsync(string title, string message, string primaryText, bool danger = false);

    Task AlertAsync(string title, string message);

    /// <summary>Asks for a text (for example a reason). Returns null when cancelled.</summary>
    Task<string?> PromptAsync(string title, string message, string label, bool required = true, string? initial = null);

    Task<ContentDialogResult> ShowAsync(ContentDialog dialog);

    /// <summary>Asks for a date. Returns null when cancelled.</summary>
    Task<DateTime?> PickDateAsync(string title, string label, DateTime initial);

    /// <summary>Promote/transfer form. Returns false when cancelled.</summary>
    Task<bool> EditJobChangeAsync(string title, ViewModels.Employees.JobChangeDialogModel model);
}

public sealed class DialogService(IContentDialogService dialogs) : IDialogService
{
    public async Task<bool> ConfirmAsync(string title, string message, string primaryText, bool danger = false)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new System.Windows.Controls.TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 },
            PrimaryButtonText = primaryText,
            CloseButtonText = Strings.App_Cancel,
            PrimaryButtonAppearance = danger ? ControlAppearance.Danger : ControlAppearance.Primary,
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialogs.ShowAsync(dialog, CancellationToken.None).ConfigureAwait(true) == ContentDialogResult.Primary;
    }

    public async Task AlertAsync(string title, string message)
        => await dialogs.ShowAlertAsync(title, message, Strings.App_Ok, CancellationToken.None).ConfigureAwait(true);

    public async Task<string?> PromptAsync(string title, string message, string label, bool required = true, string? initial = null)
    {
        var input = new Wpf.Ui.Controls.TextBox { Text = initial ?? string.Empty, MinWidth = 380, PlaceholderText = label, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MinLines = 2 };
        var panel = new System.Windows.Controls.StackPanel();
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 460, Margin = new Thickness(0, 0, 0, 12) });
        panel.Children.Add(input);
        var dialog = new ContentDialog
        {
            Title = title,
            Content = panel,
            PrimaryButtonText = Strings.App_Ok,
            CloseButtonText = Strings.App_Cancel,
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = !required || input.Text.Trim().Length > 0,
        };
        input.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !required || input.Text.Trim().Length > 0;
        dialog.Loaded += (_, _) => input.Focus();
        var result = await dialogs.ShowAsync(dialog, CancellationToken.None).ConfigureAwait(true);
        return result == ContentDialogResult.Primary ? input.Text.Trim() : null;
    }

    public Task<ContentDialogResult> ShowAsync(ContentDialog dialog) => dialogs.ShowAsync(dialog, CancellationToken.None);

    public Task<DateTime?> PickDateAsync(string title, string label, DateTime initial) => ViewModels.Employees.DateDialog.ShowAsync(this, title, label, initial);

    public Task<bool> EditJobChangeAsync(string title, ViewModels.Employees.JobChangeDialogModel model) => ViewModels.Employees.JobChangeDialog.ShowAsync(this, title, model);
}

public sealed partial class InfoBarItem : ObservableObject
{
    public required string Title { get; init; }

    public required string Message { get; init; }

    public InfoBarSeverity Severity { get; init; }

    public string? ActionText { get; init; }

    public IRelayCommand? ActionCommand { get; init; }

    public string? Key { get; init; }

    [ObservableProperty]
    public partial bool IsOpen { get; set; } = true;

    [RelayCommand]
    private void Close() => IsOpen = false;
}

public interface INotificationService
{
    ObservableCollection<InfoBarItem> InfoBars { get; }

    /// <summary>Shows or replaces a persistent notice. Items with the same key replace each other.</summary>
    void ShowInfoBar(string key, string title, string message, InfoBarSeverity severity, string? actionText = null, Action? action = null);

    void HideInfoBar(string key);

    void Toast(string message, ControlAppearance appearance = ControlAppearance.Success);

    void ToastError(string message);
}

public sealed class NotificationService(ISnackbarService snackbar) : INotificationService
{
    public ObservableCollection<InfoBarItem> InfoBars { get; } = [];

    public void ShowInfoBar(string key, string title, string message, InfoBarSeverity severity, string? actionText = null, Action? action = null)
    {
        OnUi(() =>
        {
            HideInfoBarCore(key);
            InfoBars.Add(new InfoBarItem
            {
                Key = key,
                Title = title,
                Message = message,
                Severity = severity,
                ActionText = actionText,
                ActionCommand = action is null ? null : new RelayCommand(action),
            });
        });
    }

    public void HideInfoBar(string key) => OnUi(() => HideInfoBarCore(key));

    public void Toast(string message, ControlAppearance appearance = ControlAppearance.Success)
        => OnUi(() => snackbar.Show(Strings.App_Name, message, appearance, TimeSpan.FromSeconds(3)));

    public void ToastError(string message) => Toast(message, ControlAppearance.Danger);

    private static void OnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }

    private void HideInfoBarCore(string key)
    {
        foreach (var item in InfoBars.Where(i => i.Key == key).ToList())
        {
            InfoBars.Remove(item);
        }
    }
}

public interface IBusyService
{
    event EventHandler? Changed;

    bool IsBusy { get; }

    IDisposable Begin();
}

public sealed class BusyService : IBusyService
{
    private int _count;

    public event EventHandler? Changed;

    public bool IsBusy => Volatile.Read(ref _count) > 0;

    public IDisposable Begin()
    {
        Interlocked.Increment(ref _count);
        Changed?.Invoke(this, EventArgs.Empty);
        return new Token(this);
    }

    private void End()
    {
        Interlocked.Decrement(ref _count);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class Token(BusyService owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.End();
            }
        }
    }
}

public enum AppTheme
{
    System,
    Light,
    Dark,
}

public interface IAppThemeService
{
    AppTheme Theme { get; }

    void Apply(AppTheme theme);

    void ApplySaved();
}

/// <summary>Theme preference is per Windows user, stored in %LocalAppData%\SuperOfc\ui.json.</summary>
public sealed class AppThemeService : IAppThemeService
{
    private static readonly string PrefsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SuperOfc", "ui.json");

    public AppTheme Theme { get; private set; } = AppTheme.System;

    public void ApplySaved()
    {
        try
        {
            if (File.Exists(PrefsPath))
            {
                var prefs = JsonSerializer.Deserialize<UiPrefs>(File.ReadAllText(PrefsPath));
                Theme = prefs?.Theme ?? AppTheme.System;
            }
        }
        catch (JsonException)
        {
            Theme = AppTheme.System;
        }

        ApplyCore();
    }

    public void Apply(AppTheme theme)
    {
        Theme = theme;
        ApplyCore();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PrefsPath)!);
            File.WriteAllText(PrefsPath, JsonSerializer.Serialize(new UiPrefs { Theme = theme }));
        }
        catch (IOException)
        {
            // Preference not saved; the theme still applies for this session.
        }
    }

    private void ApplyCore()
    {
        switch (Theme)
        {
            case AppTheme.Light:
                ApplicationThemeManager.Apply(ApplicationTheme.Light, WindowBackdropType.Mica, true);
                break;
            case AppTheme.Dark:
                ApplicationThemeManager.Apply(ApplicationTheme.Dark, WindowBackdropType.Mica, true);
                break;
            default:
                ApplicationThemeManager.ApplySystemTheme(true);
                break;
        }
    }

    private sealed class UiPrefs
    {
        public AppTheme Theme { get; set; }
    }
}

public interface IFileDialogService
{
    string? OpenFile(string filter, string? initialDirectory = null);

    string? SaveFile(string filter, string defaultFileName);

    /// <summary>Asks for a folder. Returns null when cancelled.</summary>
    string? PickFolder(string title);

    void OpenFolder(string path);

    void ShellOpen(string path);
}

public sealed class FileDialogService : IFileDialogService
{
    public string? OpenFile(string filter, string? initialDirectory = null)
    {
        var dialog = new OpenFileDialog { Filter = filter, InitialDirectory = initialDirectory ?? string.Empty };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? SaveFile(string filter, string defaultFileName)
    {
        var dialog = new SaveFileDialog { Filter = filter, FileName = defaultFileName };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog { Title = title };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    public void ShellOpen(string path)
        => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = path, UseShellExecute = true });
}
