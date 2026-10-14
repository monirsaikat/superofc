using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SuperOfc.Application.Payroll;
using SuperOfc.Desktop.Mvvm;
using SuperOfc.Desktop.Resources;
using SuperOfc.Desktop.ViewModels.Payroll;
using SuperOfc.Domain.Common;

namespace SuperOfc.Desktop.Views.Payroll;

/// <summary>The register has one column per payslip line, so the grid columns are built here when the register loads.</summary>
public partial class PayrollRegisterPage : UserControl
{
    private PayrollRegisterViewModel? _viewModel;

    public PayrollRegisterPage()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
        }

        _viewModel = DataContext as PayrollRegisterViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelChanged;
        }

        BuildColumns();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PayrollRegisterViewModel.Register))
        {
            BuildColumns();
        }
    }

    private void BuildColumns()
    {
        var register = _viewModel?.Register;
        Build(Grid, register, perEmployee: true);
        Build(Totals, register, perEmployee: false);
    }

    private static void Build(DataGrid grid, PayrollRegister? register, bool perEmployee)
    {
        grid.Columns.Clear();
        if (register is null)
        {
            return;
        }

        if (perEmployee)
        {
            grid.Columns.Add(Text(Strings.Employees_Code, nameof(RegisterRow.EmployeeCode), 80));
        }

        grid.Columns.Add(Text(perEmployee ? Strings.Employees_Name : Strings.Employees_Department, nameof(RegisterRow.EmployeeName), 180));
        if (perEmployee)
        {
            grid.Columns.Add(Text(Strings.Employees_Department, nameof(RegisterRow.Department), 120));
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = Strings.Payroll_Days,
                Width = 60,
                Binding = new Binding(nameof(RegisterRow.PayableDays)) { StringFormat = "0.##" },
            });
        }

        foreach (var column in register.Columns)
        {
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = column.Name,
                Width = new DataGridLength(1, DataGridLengthUnitType.Auto),
                MinWidth = 90,
                Binding = new Binding(nameof(RegisterRow.Amounts)) { Converter = AmountConverter.Instance, ConverterParameter = column.Code },
                ElementStyle = RightAligned,
            });
        }

        grid.Columns.Add(Money(Strings.Payroll_Gross, nameof(RegisterRow.Gross)));
        grid.Columns.Add(Money(Strings.Payroll_Deductions, nameof(RegisterRow.Deductions)));
        grid.Columns.Add(Money(Strings.Payroll_Net, nameof(RegisterRow.Net)));
    }

    private static readonly Style RightAligned = CreateRightAligned();

    private static Style CreateRightAligned()
    {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right));
        style.Seal();
        return style;
    }

    private static DataGridTextColumn Text(string header, string path, double width)
        => new() { Header = header, Width = width, Binding = new Binding(path) };

    private static DataGridTextColumn Money(string header, string path)
        => new()
        {
            Header = header,
            Width = new DataGridLength(1, DataGridLengthUnitType.Auto),
            MinWidth = 110,
            Binding = new Binding(path) { Converter = new MoneyConverter(), ConverterParameter = "plain" },
            ElementStyle = RightAligned,
        };

    /// <summary>Picks one column's amount from the row's dictionary (adjustment names may contain spaces, so no indexer paths).</summary>
    private sealed class AmountConverter : IValueConverter
    {
        public static AmountConverter Instance { get; } = new();

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => value is IReadOnlyDictionary<string, Money> amounts && parameter is string code && amounts.TryGetValue(code, out var amount) && !amount.IsZero
                ? MoneyConverter.Current().Format(amount, withSymbol: false)
                : string.Empty;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
    }
}
