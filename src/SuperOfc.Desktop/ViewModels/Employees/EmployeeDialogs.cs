using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SuperOfc.Desktop.Resources;
using SuperOfc.Desktop.Services;
using Wpf.Ui.Controls;

namespace SuperOfc.Desktop.ViewModels.Employees;

/// <summary>Small code-built dialogs used by the employee page. Called only through IDialogService.</summary>
internal static class DateDialog
{
    public static async Task<DateTime?> ShowAsync(IDialogService dialogs, string title, string label, DateTime initial)
    {
        ArgumentNullException.ThrowIfNull(dialogs);
        var picker = new DatePicker { SelectedDate = initial, MinWidth = 240 };
        var panel = new StackPanel();
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 6) });
        panel.Children.Add(picker);
        var dialog = new ContentDialog
        {
            Title = title,
            Content = panel,
            PrimaryButtonText = Strings.App_Ok,
            CloseButtonText = Strings.App_Cancel,
            DefaultButton = ContentDialogButton.Primary,
        };
        var result = await dialogs.ShowAsync(dialog).ConfigureAwait(true);
        return result == ContentDialogResult.Primary ? picker.SelectedDate ?? initial : null;
    }
}

internal static class JobChangeDialog
{
    public static async Task<bool> ShowAsync(IDialogService dialogs, string title, JobChangeDialogModel model)
    {
        ArgumentNullException.ThrowIfNull(dialogs);
        var panel = new StackPanel { MinWidth = 380, DataContext = model };
        panel.Children.Add(Labeled(Strings.Employee_ActionDate, Bound(new DatePicker(), DatePicker.SelectedDateProperty, nameof(model.Date))));
        panel.Children.Add(Labeled(Strings.Employees_Department, Combo(nameof(model.Departments), nameof(model.DepartmentId))));
        panel.Children.Add(Labeled(Strings.Employees_Designation, Combo(nameof(model.Designations), nameof(model.DesignationId))));
        panel.Children.Add(Labeled(Strings.Employees_Location, Combo(nameof(model.Locations), nameof(model.LocationId))));
        panel.Children.Add(Labeled(Strings.Employee_Grade, Combo(nameof(model.Grades), nameof(model.GradeId))));
        panel.Children.Add(Labeled(Strings.Employee_ActionNotes, Bound(new Wpf.Ui.Controls.TextBox(), System.Windows.Controls.TextBox.TextProperty, nameof(model.Notes))));
        var dialog = new ContentDialog
        {
            Title = title,
            Content = panel,
            PrimaryButtonText = Strings.App_Save,
            CloseButtonText = Strings.App_Cancel,
            DefaultButton = ContentDialogButton.Primary,
        };
        return await dialogs.ShowAsync(dialog).ConfigureAwait(true) == ContentDialogResult.Primary;
    }

    private static ComboBox Combo(string items, string value)
    {
        var combo = new ComboBox { DisplayMemberPath = "Name", SelectedValuePath = "Id" };
        combo.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(items));
        combo.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedValueProperty, new Binding(value) { Mode = BindingMode.TwoWay });
        return combo;
    }

    private static T Bound<T>(T control, DependencyProperty property, string path)
        where T : FrameworkElement
    {
        control.SetBinding(property, new Binding(path) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        return control;
    }

    private static StackPanel Labeled(string label, UIElement child)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(child);
        return panel;
    }
}
