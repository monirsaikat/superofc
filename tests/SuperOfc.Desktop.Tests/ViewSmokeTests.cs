using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Markup;

namespace SuperOfc.Desktop.Tests;

/// <summary>
/// Loads every page and view with the app's resources so XAML mistakes (missing resources, bad markup)
/// fail here instead of at run time.
/// </summary>
public class ViewSmokeTests
{
    public static TheoryData<string> ViewTypes()
    {
        var data = new TheoryData<string>();
        foreach (var type in typeof(SuperOfc.Desktop.App).Assembly.GetTypes()
                     .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(UserControl).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) is not null)
                     .OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            data.Add(type.FullName!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ViewTypes))]
    public void View_loads_and_lays_out(string typeName)
    {
        RunOnSta(() =>
        {
            EnsureApplication();
            var type = typeof(SuperOfc.Desktop.App).Assembly.GetType(typeName)!;
            var view = (FrameworkElement)Activator.CreateInstance(type)!;
            var window = new Window { Content = view, Width = 1280, Height = 800, ShowInTaskbar = false, WindowStyle = WindowStyle.None, Left = -10000, Top = -10000 };
            view.Measure(new Size(1280, 800));
            view.Arrange(new Rect(0, 0, 1280, 800));
            view.UpdateLayout();
            window.Close();
        });
    }

    private static void EnsureApplication()
    {
        if (System.Windows.Application.Current is not null)
        {
            return;
        }

        var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Light });
        app.Resources.MergedDictionaries.Add(new ControlsDictionary());
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/SuperOfc.Desktop;component/Styles/Styles.xaml") });
    }

    // One STA thread for all tests: WPF allows a single Application per process, bound to its thread.
    private static readonly Lazy<StaWorker> Worker = new(() => new StaWorker());

    private static void RunOnSta(Action action) => Worker.Value.Run(action);

    private sealed class StaWorker
    {
        private readonly System.Collections.Concurrent.BlockingCollection<(Action Action, TaskCompletionSource Done)> _queue = [];

        public StaWorker()
        {
            var thread = new Thread(() =>
            {
                foreach (var (action, done) in _queue.GetConsumingEnumerable())
                {
                    try
                    {
                        action();
                        done.SetResult();
                    }
                    catch (Exception ex)
                    {
                        done.SetException(ex);
                    }
                }
            })
            { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        public void Run(Action action)
        {
            var done = new TaskCompletionSource();
            _queue.Add((action, done));
            try
            {
                done.Task.GetAwaiter().GetResult();
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            }
        }
    }
}
