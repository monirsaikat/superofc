using System.Windows;
using System.Windows.Controls;
using SuperOfc.Desktop.ViewModels.Letters;

namespace SuperOfc.Desktop.Views.Letters;

/// <summary>
/// Puts a chosen field at the caret of the subject or letter box, whichever had focus last; for Word templates the field is
/// copied to the clipboard to paste into Word.
/// </summary>
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
            _viewModel.CopyRequested -= OnCopyRequested;
        }

        _viewModel = DataContext as LetterTemplatesSettingsViewModel;
        if (_viewModel is not null)
        {
            _viewModel.InsertRequested += OnInsertRequested;
            _viewModel.CopyRequested += OnCopyRequested;
        }
    }

    private void OnInsertRequested(object? sender, string token)
    {
        var caret = Math.Clamp(_target.CaretIndex, 0, _target.Text.Length);
        _target.Text = _target.Text.Insert(caret, token);
        _target.CaretIndex = caret + token.Length;
        _target.Focus();
    }

    /// <summary>Copies the field, retrying briefly while another program holds the clipboard; reports whether it worked.</summary>
    private static void OnCopyRequested(object? sender, LetterFieldCopyEventArgs e)
    {
        const int Attempts = 3;
        for (var attempt = 1; attempt <= Attempts && !e.Copied; attempt++)
        {
            try
            {
                Clipboard.SetText(e.Token);
                e.Copied = true;
            }
            catch (System.Runtime.InteropServices.ExternalException) when (attempt < Attempts)
            {
                Thread.Sleep(100);
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                // Another program still holds the clipboard; the view model shows an error instead of "Copied".
            }
        }
    }
}
