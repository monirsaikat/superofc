using System.Globalization;
using System.Windows.Data;
using SuperOfc.Application.Common;
using SuperOfc.Domain.Common;

namespace SuperOfc.Desktop.Mvvm;

/// <summary>
/// Formats <see cref="Money"/> with the company currency and grouping. ConverterParameter "plain" leaves out the
/// symbol (for grid columns). The formatter is set once at startup from the company context.
/// </summary>
public sealed class MoneyConverter : IValueConverter
{
    public static Func<MoneyFormatter> Current { get; set; } = () => MoneyFormatter.Default;

    /// <summary>The company date format for code that formats dates outside bindings (report preview).</summary>
    public static Func<string> DateFormat { get; set; } = () => "dd MMM yyyy";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var plain = string.Equals(parameter as string, "plain", StringComparison.OrdinalIgnoreCase);
        return value switch
        {
            Money money => Current().Format(money, withSymbol: !plain),
            long minor => Current().Format(new Money(minor), withSymbol: !plain),
            _ => string.Empty,
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Background brush for payroll period, run and payment states (palette in Styles.xaml).</summary>
public sealed class PayrollStatusBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            Domain.Payroll.PayrollPeriodStatus.Open => "StatusOffBrush",
            Domain.Payroll.PayrollPeriodStatus.Computed => "StatusLateBrush",
            Domain.Payroll.PayrollPeriodStatus.Locked => "StatusLeaveBrush",
            Domain.Payroll.PayrollPeriodStatus.Paid => "StatusPresentBrush",
            Domain.Payroll.PayrollRunStatus.Draft => "StatusLateBrush",
            Domain.Payroll.PayrollRunStatus.Finalized => "StatusPresentBrush",
            Domain.Payroll.PayrollRunStatus.Voided => "StatusAbsentBrush",
            Domain.Payroll.PaymentStatus.Paid => "StatusPresentBrush",
            Domain.Payroll.PaymentStatus.Unpaid => "StatusOffBrush",
            _ => null,
        };
        return key is null ? System.Windows.Media.Brushes.Transparent : System.Windows.Application.Current?.TryFindResource(key) ?? System.Windows.Media.Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Turns PNG bytes into an image source (payslip preview).</summary>
public sealed class PngBytesConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not byte[] { Length: > 0 } bytes)
        {
            return null;
        }

        using var stream = new System.IO.MemoryStream(bytes);
        var image = new System.Windows.Media.Imaging.BitmapImage();
        image.BeginInit();
        image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
