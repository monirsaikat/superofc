using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using SuperOfc.Desktop.Resources;
using SuperOfc.Desktop.ViewModels.Attendance;

namespace SuperOfc.Desktop.Views.Attendance;

/// <summary>Builds one column per day of the month plus total columns; cell clicks open the day panel.</summary>
public partial class AttendancePage : UserControl
{
    private const int FixedColumns = 2;
    private AttendanceViewModel? _viewModel;
    private int _builtFor = -1;
    private DateOnly _builtMonth;

    public AttendancePage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null)
            {
                _viewModel.SheetReloaded -= OnSheetReloaded;
            }

            _viewModel = DataContext as AttendanceViewModel;
            if (_viewModel is not null)
            {
                _viewModel.SheetReloaded += OnSheetReloaded;
                BuildColumns();
            }
        };
    }

    private void OnSheetReloaded(object? sender, EventArgs e) => BuildColumns();

    private void BuildColumns()
    {
        if (_viewModel is null || _viewModel.Days.Count == 0)
        {
            return;
        }

        var first = _viewModel.Days[0].Date;
        if (_builtFor == _viewModel.Days.Count && _builtMonth == first)
        {
            return;
        }

        while (Sheet.Columns.Count > FixedColumns)
        {
            Sheet.Columns.RemoveAt(Sheet.Columns.Count - 1);
        }

        var dayStyle = (Style)Sheet.Resources["DayCell"];
        foreach (var day in _viewModel.Days)
        {
            Sheet.Columns.Add(new DataGridTemplateColumn
            {
                Header = new TextBlock
                {
                    Text = day.Header,
                    TextAlignment = TextAlignment.Center,
                    FontSize = 11,
                    Opacity = day.IsOff ? 0.6 : 1,
                },
                Width = 38,
                CellTemplate = CellTemplate(day.Index),
                CellStyle = dayStyle,
                CanUserResize = false,
            });
        }

        AddTotal(Strings.Attendance_Present, nameof(SheetRow.Present));
        AddTotal(Strings.Attendance_Late, nameof(SheetRow.Late));
        AddTotal(Strings.Attendance_HalfDays, nameof(SheetRow.HalfDays));
        AddTotal(Strings.Attendance_Absent, nameof(SheetRow.Absent));
        AddTotal(Strings.Attendance_Leave, nameof(SheetRow.Leave));
        AddTotal(Strings.Attendance_OvertimeHours, nameof(SheetRow.OvertimeHours));
        _builtFor = _viewModel.Days.Count;
        _builtMonth = first;
    }

    private void AddTotal(string header, string path)
        => Sheet.Columns.Add(new DataGridTextColumn
        {
            Header = header,
            Binding = new System.Windows.Data.Binding(path) { StringFormat = "0.#" },
            Width = 58,
        });

    private static DataTemplate CellTemplate(int index)
    {
        var i = index.ToString(CultureInfo.InvariantCulture);
        const string Template = """
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
              <Border Margin="1" CornerRadius="3" Background="{Binding Cells[#].Status, Converter={StaticResource StatusBrush}}" ToolTip="{Binding Cells[#].ToolTip}">
                <Grid>
                  <TextBlock HorizontalAlignment="Center" VerticalAlignment="Center" FontSize="11" FontWeight="SemiBold" Foreground="White" Text="{Binding Cells[#].Code}" />
                  <Ellipse Width="5" Height="5" Margin="0,2,2,0" HorizontalAlignment="Right" VerticalAlignment="Top" Fill="White"
                           Visibility="{Binding Cells[#].IsCorrected, Converter={StaticResource BoolToVisibility}}" />
                </Grid>
              </Border>
            </DataTemplate>
            """;
        return (DataTemplate)XamlReader.Parse(Template.Replace("#", i, StringComparison.Ordinal));
    }

    private async void OnSelectedCellsChanged(object sender, SelectedCellsChangedEventArgs e)
    {
        if (_viewModel is null || e.AddedCells.Count == 0)
        {
            return;
        }

        var cell = e.AddedCells[0];
        var columnIndex = Sheet.Columns.IndexOf(cell.Column) - FixedColumns;
        if (cell.Item is SheetRow row && columnIndex >= 0 && columnIndex < _viewModel.Days.Count)
        {
            await _viewModel.OpenDayAsync(row, columnIndex).ConfigureAwait(true);
        }
    }
}
