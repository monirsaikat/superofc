using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace SuperOfc.Desktop.Mvvm;

/// <summary>
/// Picks the view for a view model by convention: FooViewModel -> FooPage or FooView anywhere in the assembly.
/// Used as ContentTemplateSelector so pages never need hand-written DataTemplates.
/// </summary>
public sealed class ViewLocator : DataTemplateSelector
{
    private static readonly Dictionary<string, Type> ViewsByName = typeof(ViewLocator).Assembly.GetTypes()
        .Where(t => typeof(FrameworkElement).IsAssignableFrom(t) && !t.IsAbstract && (t.Name.EndsWith("Page", StringComparison.Ordinal) || t.Name.EndsWith("View", StringComparison.Ordinal)))
        .GroupBy(t => t.Name, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    private static readonly Dictionary<Type, DataTemplate> Cache = [];

    public static ViewLocator Instance { get; } = new();

    public static Type? FindViewType(Type viewModelType)
    {
        ArgumentNullException.ThrowIfNull(viewModelType);
        var baseName = viewModelType.Name.EndsWith("ViewModel", StringComparison.Ordinal) ? viewModelType.Name[..^"ViewModel".Length] : viewModelType.Name;
        return ViewsByName.GetValueOrDefault(baseName + "Page") ?? ViewsByName.GetValueOrDefault(baseName + "View");
    }

    public override DataTemplate? SelectTemplate(object? item, DependencyObject container)
    {
        if (item is null)
        {
            return null;
        }

        var vmType = item.GetType();
        if (Cache.TryGetValue(vmType, out var cached))
        {
            return cached;
        }

        var viewType = FindViewType(vmType);
        DataTemplate template;
        if (viewType is null)
        {
            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetValue(TextBlock.TextProperty, $"No view found for {vmType.Name}");
            template = new DataTemplate { VisualTree = text };
        }
        else
        {
            template = new DataTemplate { VisualTree = new FrameworkElementFactory(viewType) };
        }

        template.Seal();
        Cache[vmType] = template;
        return template;
    }
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        return flag ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility v && (v == Visibility.Visible) ^ Invert;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>Visible when the value is not null and not an empty string (Invert flips it).</summary>
public sealed class HasValueToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var has = value switch
        {
            null => false,
            string s => s.Length > 0,
            int i => i > 0,
            System.Collections.ICollection c => c.Count > 0,
            _ => true,
        };
        return has ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>True when value.ToString() equals the parameter; used for radio buttons bound to enums.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var equal = string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);
        return targetType == typeof(Visibility) ? (equal ? Visibility.Visible : Visibility.Collapsed) : equal;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true && parameter is not null ? Enum.Parse(targetType, parameter.ToString()!) : Binding.DoNothing;
}
