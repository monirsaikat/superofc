using System.Windows;
using System.Windows.Controls;
using SuperOfc.Desktop.ViewModels.Letters;

namespace SuperOfc.Desktop.Views.Letters;

/// <summary>Puts a chosen field at the caret of the subject or letter box, whichever had focus last.</summary>
public partial class LetterTemplatesSettingsPage : UserControl
{
    private LetterTemplatesSettingsViewModel? _viewModel;
    private TextBox _target;

    public LetterTemplatesSettingsPage()
    {
        InitializeComponent();
        _target = BodyBox;
        SubjectBox.GotKeyboardFocus += (_, _) => _target = SubjectBox;
        BodyBox.GotKeyboardFocus += (_, _) => _target = BodyBox;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.InsertRequested -= OnInsertRequested;
        }

        _viewModel = DataContext as LetterTemplatesSettingsViewModel;
        if (_viewModel is not null)
        {
            _viewModel.InsertRequested += OnInsertRequested;
        }
    }

    private void OnInsertRequested(object? sender, string token)
    {
        var caret = Math.Clamp(_target.CaretIndex, 0, _target.Text.Length);
        _target.Text = _target.Text.Insert(caret, token);
        _target.CaretIndex = caret + token.Length;
        _target.Focus();
    }
}
