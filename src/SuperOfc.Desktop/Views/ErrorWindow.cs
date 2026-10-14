using System.Windows;
using System.Windows.Controls;
using SuperOfc.Desktop.Resources;

namespace SuperOfc.Desktop.Views;

/// <summary>Last-resort error dialog for unhandled exceptions. Built in code so it works even if XAML resources fail.</summary>
public static class ErrorWindow
{
    public static void Show(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var details = exception.ToString();
        var window = new Window
        {
            Title = Strings.App_ErrorTitle,
            Width = 560,
            Height = 380,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = System.Windows.Application.Current?.MainWindow is { IsVisible: true } owner ? owner : null,
            ResizeMode = ResizeMode.CanResize,
        };
        var panel = new DockPanel { Margin = new Thickness(16) };
        var message = new TextBlock { Text = Strings.App_ErrorMessage, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(message, Dock.Top);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        var copy = new Button { Content = Strings.App_CopyDetails, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
        copy.Click += (_, _) => Clipboard.SetText(details);
        var close = new Button { Content = Strings.App_Close, Padding = new Thickness(12, 4, 12, 4), IsDefault = true };
        close.Click += (_, _) => window.Close();
        buttons.Children.Add(copy);
        buttons.Children.Add(close);
        var box = new TextBox { Text = details, IsReadOnly = true, TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new System.Windows.Media.FontFamily("Consolas") };
        panel.Children.Add(message);
        panel.Children.Add(buttons);
        panel.Children.Add(box);
        window.Content = panel;
        window.ShowDialog();
    }
}
