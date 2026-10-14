using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using SuperOfc.Desktop.Mvvm;
using SuperOfc.Desktop.ViewModels.Employees;

namespace SuperOfc.Desktop.Views.Employees;

public partial class EmployeeDetailPage : UserControl
{
    private const int FixedTabCount = 3;
    private EmployeeDetailViewModel? _viewModel;

    public EmployeeDetailPage()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.ModuleTabs.CollectionChanged -= OnModuleTabsChanged;
        }

        _viewModel = DataContext as EmployeeDetailViewModel;
        if (_viewModel is not null)
        {
            _viewModel.ModuleTabs.CollectionChanged += OnModuleTabsChanged;
            SyncModuleTabs();
        }
    }

    private void OnModuleTabsChanged(object? sender, NotifyCollectionChangedEventArgs e) => SyncModuleTabs();

    /// <summary>Module tabs (salary, attendance, leave, loans) come after the three built-in tabs.</summary>
    private void SyncModuleTabs()
    {
        while (Tabs.Items.Count > FixedTabCount)
        {
            Tabs.Items.RemoveAt(Tabs.Items.Count - 1);
        }

        if (_viewModel is null)
        {
            return;
        }

        foreach (var tab in _viewModel.ModuleTabs)
        {
            Tabs.Items.Add(new TabItem
            {
                Header = tab.Header,
                Content = new ContentControl
                {
                    Content = tab,
                    ContentTemplateSelector = ViewLocator.Instance,
                    Margin = new Thickness(0, 12, 0, 0),
                    Focusable = false,
                },
            });
        }
    }
}
