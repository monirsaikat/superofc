using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SuperOfc.DeviceBridge.Contracts;
using SuperOfc.DeviceBridge.Protocols;

namespace SuperOfc.DeviceBridge;

/// <summary>
/// SuperOfc.DeviceBridge.exe --pipe &lt;suffix&gt; [--parent &lt;pid&gt;] [--fake &lt;session.json&gt;]
/// Exits when the app closes the pipe or the parent process ends. Never touches the database (docs/06).
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        string? pipe = null, fake = null;
        int? parent = null;
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "--pipe": pipe = args[i + 1]; break;
                case "--parent": parent = int.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture); break;
                case "--fake": fake = args[i + 1]; break;
            }
        }

        if (string.IsNullOrWhiteSpace(pipe))
        {
            Console.Error.WriteLine("Usage: SuperOfc.DeviceBridge --pipe <name> [--parent <pid>] [--fake <session.json>]");
            return 2;
        }

        using var cts = new CancellationTokenSource();
        if (parent is { } pid)
        {
            WatchParent(pid, cts);
        }

        IDeviceProtocolFactory factory = fake is not null
            ? new FakeDeviceProtocolFactory(FakeSession.Load(fake))
            : new ZkemkeeperProtocolFactory(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sdk"));

        using var server = new BridgeServer(BridgeFraming.PipeName(pipe!), factory);
        try
        {
            await server.RunAsync(cts.Token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        finally
        {
            (factory as IDisposable)?.Dispose();
        }
    }

    private static void WatchParent(int pid, CancellationTokenSource cts)
    {
        try
        {
            var parent = Process.GetProcessById(pid);
            parent.EnableRaisingEvents = true;
            parent.Exited += (_, _) =>
            {
                cts.Cancel();
                Environment.Exit(0);
            };
        }
        catch (ArgumentException)
        {
            Environment.Exit(0); // parent already gone
        }
    }
}
