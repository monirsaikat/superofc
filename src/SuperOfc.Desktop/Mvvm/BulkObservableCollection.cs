using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace SuperOfc.Desktop.Mvvm;

/// <summary>ObservableCollection that can replace all items with a single Reset notification (fast grids).</summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

/// <summary>Converts an enum value to its localized text using the key "Prefix_Value" (ConverterParameter = prefix).</summary>
public sealed class EnumTextConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => value is null ? string.Empty : Resources.Strings.Get($"{parameter}_{value}");

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => System.Windows.Data.Binding.DoNothing;
}

/// <summary>Loads an image file without locking it, scaled down for display.</summary>
public sealed class ImagePathConverter : System.Windows.Data.IValueConverter
{
    /// <summary>Turns stored relative paths into full paths. Set once at startup from IFileStorage.</summary>
    public static Func<string, string>? Resolve { get; set; }

    public int DecodeWidth { get; set; } = 160;

    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (value is not string { Length: > 0 } path)
        {
            return null;
        }

        if (!System.IO.Path.IsPathRooted(path) && Resolve is not null)
        {
            path = Resolve(path);
        }

        if (!System.IO.File.Exists(path))
        {
            return null;
        }

        try
        {
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreImageCache;
            image.DecodePixelWidth = DecodeWidth;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (System.IO.IOException)
        {
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => System.Windows.Data.Binding.DoNothing;
}

/// <summary>Shows a UTC DateTime in local time.</summary>
public sealed class LocalTimeConverter : System.Windows.Data.IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => value is DateTime utc ? DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime() : value;

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => System.Windows.Data.Binding.DoNothing;
}

/// <summary>Background brush for an attendance or device status (palette in Styles.xaml).</summary>
public sealed class AttendanceStatusBrushConverter : System.Windows.Data.IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        var key = value switch
        {
            Domain.Attendance.AttendanceStatus.Present => "StatusPresentBrush",
            Domain.Attendance.AttendanceStatus.Late => "StatusLateBrush",
            Domain.Attendance.AttendanceStatus.HalfDay => "StatusHalfBrush",
            Domain.Attendance.AttendanceStatus.Absent => "StatusAbsentBrush",
            Domain.Attendance.AttendanceStatus.Leave => "StatusLeaveBrush",
            Domain.Attendance.AttendanceStatus.Holiday => "StatusHolidayBrush",
            Domain.Attendance.AttendanceStatus.WeeklyOff => "StatusOffBrush",
            Domain.Devices.DeviceStatus.Online => "StatusPresentBrush",
            Domain.Devices.DeviceStatus.Offline => "StatusAbsentBrush",
            Domain.Devices.DeviceStatus.Error => "StatusLateBrush",
            Domain.Devices.DeviceStatus.Unknown => "StatusOffBrush",
            _ => null,
        };
        return key is null ? System.Windows.Media.Brushes.Transparent : System.Windows.Application.Current?.TryFindResource(key) ?? System.Windows.Media.Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => System.Windows.Data.Binding.DoNothing;
}
