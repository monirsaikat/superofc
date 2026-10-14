using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using SuperOfc.Desktop.Services;
using SuperOfc.Desktop.ViewModels;
using Wpf.Ui;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace SuperOfc.Desktop.Views;

public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        services.GetRequiredService<IContentDialogService>().SetDialogHost(DialogHost);
        services.GetRequiredService<ISnackbarService>().SetSnackbarPresenter(Snackbar);
        if (services.GetRequiredService<IAppThemeService>().Theme == AppTheme.System)
        {
            Loaded += (_, _) => SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, true);
        }

        PreviewKeyDown += OnUserActivity;
        PreviewMouseDown += OnUserActivity;
    }

    private void OnUserActivity(object sender, InputEventArgs e) => _viewModel.RegisterActivity();
}
