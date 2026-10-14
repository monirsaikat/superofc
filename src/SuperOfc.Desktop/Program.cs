using System.Threading;
using SuperOfc.Desktop.Services;
using Velopack;

namespace SuperOfc.Desktop;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Must be first: lets Velopack handle install/update/uninstall hooks and exit early when needed.
        VelopackApp.Build()
            .OnFirstRun(_ => FirstRunFlag.Set())
            .SetArgs(args)
            .Run();

        var restarted = args.Contains(SingleInstance.RestartArgument, StringComparer.Ordinal);
        using var instance = SingleInstance.TryAcquire(restarted ? TimeSpan.FromSeconds(20) : TimeSpan.Zero);
        if (instance is null)
        {
            SingleInstance.SignalExisting();
            return 0;
        }

        var app = new App();
        app.InitializeComponent();
        instance.ActivationRequested += (_, _) => app.Dispatcher.BeginInvoke(app.BringToFront);
        return app.Run();
    }
}
