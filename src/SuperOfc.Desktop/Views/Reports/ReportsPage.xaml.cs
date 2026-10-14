using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SuperOfc.Application.Reports;
using SuperOfc.Desktop.Mvvm;
using SuperOfc.Desktop.ViewModels.Reports;
using SuperOfc.Domain.Common;

namespace SuperOfc.Desktop.Views.Reports;

/// <summary>Each report has its own columns, so the preview grid's columns are built here whenever a report runs.</summary>
public partial class ReportsPage : UserControl
{
    private static readonly Style RightAligned = CreateRightAligned();
    private ReportsViewModel? _viewModel;

    public ReportsPage()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private static Style CreateRightAligned()
    {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right));
        style.Seal();
        return style;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
        }

        _viewModel = DataContext as ReportsViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelChanged;
        }

        BuildColumns();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ReportsViewModel.Table))
        {
            BuildColumns();
        }
    }

    private void BuildColumns()
    {
        Preview.Columns.Clear();
        if (_viewModel?.Table is not { } table)
        {
            return;
        }

        for (var i = 0; i < table.Columns.Count; i++)
        {
            var column = table.Columns[i];
            var numeric = column.Kind is ReportColumnKind.Integer or ReportColumnKind.Number or ReportColumnKind.Money;
            Preview.Columns.Add(new DataGridTextColumn
            {
                Header = column.Header,
                Width = new DataGridLength(column.Width, DataGridLengthUnitType.Star),
                MinWidth = numeric ? 80 : 70,
                Binding = new Binding(nameof(ReportRowView.Cells)) { Converter = CellConverter.Instance, ConverterParameter = (i, column) },
                ElementStyle = numeric ? RightAligned : null,
            });
        }
    }

    /// <summary>Formats one cell of a row like the PDF: money with the company's grouping, days and hours with up to two decimals.</summary>
    private sealed class CellConverter : IValueConverter
    {
        public static CellConverter Instance { get; } = new();

        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is not IReadOnlyList<object?> cells || parameter is not ValueTuple<int, ReportColumn> p || p.Item1 >= cells.Count)
            {
                return string.Empty;
            }

            var (index, column) = p;
            return cells[index] switch
            {
                null => string.Empty,
                decimal d when column.Kind == ReportColumnKind.Money => MoneyConverter.Current().Format(Money.FromMajor(d), withSymbol: false),
                decimal d => d.ToString("#,##0.##", culture),
                int n => n.ToString("#,##0", culture),
                DateOnly date => date.ToString(MoneyConverter.DateFormat(), culture),
                var other => System.Convert.ToString(other, culture) ?? string.Empty,
            };
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
    }
}
