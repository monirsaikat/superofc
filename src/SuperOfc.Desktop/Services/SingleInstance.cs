using System.IO;
using System.Threading;

namespace SuperOfc.Desktop.Services;

/// <summary>One running copy per Windows session (ADR-11). A second launch activates the first window.</summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\SuperOfc.Desktop.SingleInstance";
    private const string EventName = @"Local\SuperOfc.Desktop.Activate";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activate;
    private readonly RegisteredWaitHandle _registration;

    private SingleInstance(Mutex mutex)
    {
        _mutex = mutex;
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
        _registration = ThreadPool.RegisterWaitForSingleObject(
            _activate,
            (_, _) => ActivationRequested?.Invoke(this, EventArgs.Empty),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    public event EventHandler? ActivationRequested;

    public const string RestartArgument = "--restarted";

    /// <summary>Gets the instance lock. After a restart, waits for the previous process to exit.</summary>
    public static SingleInstance? TryAcquire(TimeSpan wait = default)
    {
        var mutex = new Mutex(true, MutexName, out var created);
        if (!created)
        {
            bool acquired;
            try
            {
                acquired = wait > TimeSpan.Zero && mutex.WaitOne(wait);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            if (!acquired)
            {
                mutex.Dispose();
                return null;
            }
        }

        return new SingleInstance(mutex);
    }

    /// <summary>Starts a new copy of the app that waits for this one to exit.</summary>
    public static void Restart()
    {
        var path = Environment.ProcessPath;
        if (path is not null)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path, RestartArgument) { UseShellExecute = false });
        }
    }

    public static void SignalExisting()
    {
        if (EventWaitHandle.TryOpenExisting(EventName, out var handle))
        {
            using (handle)
            {
                handle.Set();
            }
        }
    }

    public void Dispose()
    {
        _registration.Unregister(null);
        _activate.Dispose();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}

/// <summary>Remembers that Velopack reported the first run after install.</summary>
public static class FirstRunFlag
{
    private static string FlagPath => Path.Combine(Path.GetTempPath(), "SuperOfc.firstrun");

    public static void Set()
    {
        try
        {
            File.WriteAllText(FlagPath, DateTime.UtcNow.ToString("O"));
        }
        catch (IOException)
        {
            // Only used for a welcome hint; safe to ignore.
        }
    }

    public static bool Consume()
    {
        if (!File.Exists(FlagPath))
        {
            return false;
        }

        try
        {
            File.Delete(FlagPath);
        }
        catch (IOException)
        {
            // Ignore: the flag will be consumed next time.
        }

        return true;
    }
}
