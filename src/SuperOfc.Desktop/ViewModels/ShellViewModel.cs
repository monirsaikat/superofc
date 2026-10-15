using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SuperOfc.Application.Abstractions;
using SuperOfc.Desktop.Mvvm;
using SuperOfc.Desktop.Resources;
using SuperOfc.Desktop.Services;
using SuperOfc.Desktop.ViewModels.Settings;
using SuperOfc.Domain.Security;
using Wpf.Ui.Controls;

namespace SuperOfc.Desktop.ViewModels;

public sealed record NavItem(string Title, SymbolRegular Icon, Type ViewModelType, Permission Permission, object? Parameter = null);

/// <summary>The main shell: left navigation and the current page.</summary>
public sealed partial class ShellViewModel : ViewModelBase
{
    private readonly IAppNavigator _navigation;
    private readonly ICurrentUser _user;
    private readonly IAppEnvironment _environment;
    private readonly MainViewModel _main;
    private bool _syncingSelection;

    public ShellViewModel(IAppNavigator navigation, ICurrentUser user, IAppEnvironment environment, MainViewModel main)
    {
        _navigation = navigation;
        _user = user;
        _environment = environment;
        _main = main;
        _navigation.Navigated += OnNavigated;
    }

    /// <summary>All top-level areas in display order (docs/04). Modules replace placeholders as they ship.</summary>
    public static IReadOnlyList<NavItem> AllItems { get; } = NavigationMap.Items;

    public ObservableCollection<NavItem> Items { get; } = [];

    public ObservableCollection<NavItem> FooterItems { get; } = [];

    [ObservableProperty]
    public partial NavItem? SelectedItem { get; set; }

    /// <summary>
    /// The selection shown in the main list: empty while a footer page (Settings) is open. Each list needs its own, or the
    /// list that does not contain the current page keeps its old highlight and that item cannot be clicked again.
    /// </summary>
    public NavItem? SelectedMainItem
    {
        get => SelectedItem is { } item && Items.Contains(item) ? item : null;
        set
        {
            if (value is not null)
            {
                SelectedItem = value;
            }
        }
    }

    /// <summary>The selection shown in the footer list (Settings); empty while any other page is open.</summary>
    public NavItem? SelectedFooterItem
    {
        get => SelectedItem is { } item && FooterItems.Contains(item) ? item : null;
        set
        {
            if (value is not null)
            {
                SelectedItem = value;
            }
        }
    }

    [ObservableProperty]
    public partial object? CurrentPage { get; set; }

    [ObservableProperty]
    public partial bool CanGoBack { get; set; }

    public string UserDisplayName => _user.DisplayName;

    public string RoleText => _user.Role.ToString();

    public string Initials => string.Concat(_user.DisplayName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(p => char.ToUpperInvariant(p[0])));

    public string VersionText => string.Format(CultureInfo.CurrentCulture, Strings.App_Version, _environment.AppVersion);

    public MainViewModel Main => _main;

    public async Task ActivateAsync()
    {
        Items.Clear();
        FooterItems.Clear();
        foreach (var item in AllItems.Where(i => PermissionMatrix.Has(_user.Role, i.Permission)))
        {
            (item.ViewModelType == typeof(SettingsViewModel) ? FooterItems : Items).Add(item);
        }

        OnPropertyChanged(nameof(UserDisplayName));
        OnPropertyChanged(nameof(RoleText));
        OnPropertyChanged(nameof(Initials));
        _navigation.Reset();
        await _navigation.NavigateToAsync(typeof(DashboardViewModel), clearHistory: true).ConfigureAwait(true);
    }

    partial void OnSelectedItemChanged(NavItem? value)
    {
        OnPropertyChanged(nameof(SelectedMainItem));
        OnPropertyChanged(nameof(SelectedFooterItem));
    }

    async partial void OnSelectedItemChanged(NavItem? oldValue, NavItem? newValue)
    {
        if (_syncingSelection || newValue is null)
        {
            return;
        }

        var navigated = await _navigation.NavigateToAsync(newValue.ViewModelType, newValue.Parameter, clearHistory: true).ConfigureAwait(true);
        if (!navigated)
        {
            _syncingSelection = true;
            SelectedItem = oldValue;
            _syncingSelection = false;
        }
    }

    [RelayCommand]
    private Task GoBackAsync() => _navigation.GoBackAsync();

    private void OnNavigated(object? sender, object page)
    {
        CurrentPage = page;
        CanGoBack = _navigation.CanGoBack;
        var match = Items.Concat(FooterItems).FirstOrDefault(i => i.ViewModelType == page.GetType() && (i.Parameter is null || Equals(i.Parameter, (page as ComingSoonViewModel)?.Area)));
        if (match is not null && !ReferenceEquals(match, SelectedItem))
        {
            _syncingSelection = true;
            SelectedItem = match;
            _syncingSelection = false;
        }
    }
}

public sealed partial class ComingSoonViewModel(PageServices services) : PageViewModel(services)
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial string Area { get; set; } = string.Empty;

    public override string Title => Area;

    public override Task OnNavigatedToAsync(object? parameter)
    {
        Area = parameter as string ?? string.Empty;
        return Task.CompletedTask;
    }
}
