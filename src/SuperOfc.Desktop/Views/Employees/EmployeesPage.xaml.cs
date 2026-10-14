using System.Windows.Controls;
using System.Windows.Input;
using SuperOfc.Desktop.ViewModels.Employees;

namespace SuperOfc.Desktop.Views.Employees;

public partial class EmployeesPage : UserControl
{
    public EmployeesPage()
    {
        InitializeComponent();
        Loaded += (_, _) => SearchBox.Focus();
    }

    private EmployeesViewModel? ViewModel => DataContext as EmployeesViewModel;

    private void OnGridSorting(object sender, DataGridSortingEventArgs e)
    {
        // Sorting happens in the database so it covers every page, not just the visible rows.
        e.Handled = true;
        if (ViewModel is { } vm && e.Column.SortMemberPath is { Length: > 0 } column)
        {
            _ = vm.SortByAsync(column);
        }
    }

    private void OnGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Only rows open the employee; double-clicking a header or scrollbar does nothing.
        if (ViewModel is { Selected: not null } vm && FindRow(e.OriginalSource as System.Windows.DependencyObject) is not null)
        {
            vm.OpenCommand.Execute(vm.Selected);
        }
    }

    private void OnGridKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ViewModel is { Selected: not null } vm)
        {
            e.Handled = true;
            vm.OpenCommand.Execute(vm.Selected);
        }
    }

    private static DataGridRow? FindRow(System.Windows.DependencyObject? source)
    {
        while (source is not null and not DataGridRow)
        {
            source = source is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(source)
                : System.Windows.LogicalTreeHelper.GetParent(source);
        }

        return source as DataGridRow;
    }
}
