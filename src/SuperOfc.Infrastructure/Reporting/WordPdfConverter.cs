using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using SuperOfc.Application.Letters;

namespace SuperOfc.Infrastructure.Reporting;

/// <summary>
/// <see cref="IWordPdfConverter"/> through Microsoft Word itself (late-bound COM), so the PDF is exactly what Word prints.
/// Each batch runs on its own STA thread with one hidden Word instance that never shows a dialog or runs macros. A document
/// that takes longer than <see cref="DocumentTimeout"/> stops the Word process this batch started.
/// </summary>
public sealed class WordPdfConverter : IWordPdfConverter, IDisposable
{
    internal const string NotInstalledMessage = "Microsoft Word is not installed on this PC, so PDFs of Word templates cannot be made. Save the letters as Word files instead.";

    internal static readonly TimeSpan DocumentTimeout = TimeSpan.FromSeconds(120);

    private const string WordProgId = "Word.Application";
    private const string WordProcessName = "WINWORD";
    private const int WdDoNotSaveChanges = 0;
    private const int WdAlertsNone = 0;
    private const int MsoAutomationSecurityForceDisable = 3;
    private const int WdExportFormatPdf = 17;

    private readonly Lazy<Type?> _wordType = new(() => Type.GetTypeFromProgID(WordProgId), LazyThreadSafetyMode.ExecutionAndPublication);
    private readonly SemaphoreSlim _batchLock = new(1, 1);
    private readonly Lock _processLock = new();
    private HashSet<int> _batchProcesses = [];
    private bool _disposed;

    public bool IsAvailable => _wordType.Value is not null;

    public async Task ConvertAsync(IReadOnlyList<WordPdfJob> jobs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var wordType = _wordType.Value ?? throw new WordConversionException(NotInstalledMessage);
        if (jobs.Count == 0)
        {
            return;
        }

        // One Word session at a time: the processes a batch started are found by comparing WINWORD before and after.
        await _batchLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    RunBatch(wordType, jobs, cancellationToken);
                    done.SetResult();
                }
                catch (OperationCanceledException ex)
                {
                    done.SetCanceled(ex.CancellationToken);
                }
                catch (Exception ex)
                {
                    done.SetException(ex);
                }
            })
            {
                IsBackground = true,
                Name = "Word PDF export",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            await done.Task.ConfigureAwait(false);
        }
        finally
        {
            if (!_disposed)
            {
                _batchLock.Release();
            }
        }
    }

    /// <summary>Stops a Word instance that is still converting, so no hidden WINWORD.EXE outlives the app.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Kill(TakeBatchProcesses());
        _batchLock.Dispose();
    }

    private void RunBatch(Type wordType, IReadOnlyList<WordPdfJob> jobs, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = NativeMethods.CoRegisterMessageFilter(new RetryMessageFilter(), out var previousFilter);
        dynamic? word = null;
        try
        {
            var before = WordProcessIds();
            try
            {
                word = Activator.CreateInstance(wordType);
            }
            catch (Exception ex) when (ex is COMException or TargetInvocationException or InvalidComObjectException)
            {
                throw new WordConversionException($"Microsoft Word could not be started: {ex.GetBaseException().Message}", ex);
            }
            finally
            {
                var started = WordProcessIds();
                started.ExceptWith(before);
                lock (_processLock)
                {
                    _batchProcesses = started;
                }
            }

            if (word is null)
            {
                throw new WordConversionException(NotInstalledMessage);
            }

            try
            {
                word.Visible = false;
                word.DisplayAlerts = WdAlertsNone;
                word.AutomationSecurity = MsoAutomationSecurityForceDisable;
            }
            catch (COMException ex)
            {
                throw new WordConversionException($"Microsoft Word could not be prepared for saving PDFs: {ex.Message}", ex);
            }

            foreach (var job in jobs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Convert((object)word, job);
            }
        }
        finally
        {
            if (word is not null)
            {
                try
                {
                    word.Quit(WdDoNotSaveChanges);
                }
                catch (Exception ex) when (ex is COMException or InvalidComObjectException)
                {
                    // Word was stopped after a timeout or has already gone.
                }

                Marshal.FinalReleaseComObject((object)word);
            }

            _ = NativeMethods.CoRegisterMessageFilter(previousFilter, out _);
            StopLeftovers(TakeBatchProcesses());
        }
    }

    private void Convert(object application, WordPdfJob job)
    {
        var docxPath = Path.GetFullPath(job.DocxPath);
        var pdfPath = Path.GetFullPath(job.PdfPath);
        var name = Path.GetFileName(docxPath);
        if (!File.Exists(docxPath))
        {
            throw new WordConversionException($"The Word file {name} was not found.");
        }

        if (Path.GetDirectoryName(pdfPath) is { } folder)
        {
            Directory.CreateDirectory(folder);
        }

        var timedOut = false;
        using var watchdog = new Timer(_ =>
        {
            timedOut = true;
            Kill(Snapshot());
        }, null, DocumentTimeout, Timeout.InfiniteTimeSpan);

        dynamic? documents = null;
        dynamic? document = null;
        try
        {
            documents = ((dynamic)application).Documents;
            // Positional: FileName, ConfirmConversions, ReadOnly, AddToRecentFiles, PasswordDocument, PasswordTemplate,
            // Revert, WritePasswordDocument, WritePasswordTemplate. Empty passwords make a protected file fail instead of asking.
            document = documents.Open(docxPath, false, true, false, string.Empty, string.Empty, false, string.Empty, string.Empty);
            document.ExportAsFixedFormat(pdfPath, WdExportFormatPdf);
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            if (timedOut)
            {
                throw new WordConversionException($"Word took longer than {DocumentTimeout.TotalMinutes:0} minutes to save {name} as PDF and was stopped.", ex);
            }

            throw new WordConversionException($"Word could not save {name} as PDF: {ex.Message}", ex);
        }
        finally
        {
            if (document is not null)
            {
                try
                {
                    document.Close(WdDoNotSaveChanges);
                }
                catch (Exception ex) when (ex is COMException or InvalidComObjectException)
                {
                    // Already closed, or Word was stopped.
                }

                Marshal.FinalReleaseComObject((object)document);
            }

            if (documents is not null)
            {
                Marshal.FinalReleaseComObject((object)documents);
            }
        }

        if (timedOut)
        {
            throw new WordConversionException($"Word took longer than {DocumentTimeout.TotalMinutes:0} minutes to save {name} as PDF and was stopped.");
        }
    }

    private HashSet<int> Snapshot()
    {
        lock (_processLock)
        {
            return [.. _batchProcesses];
        }
    }

    private HashSet<int> TakeBatchProcesses()
    {
        lock (_processLock)
        {
            var ids = _batchProcesses;
            _batchProcesses = [];
            return ids;
        }
    }

    private static HashSet<int> WordProcessIds()
    {
        var ids = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName(WordProcessName))
        {
            ids.Add(process.Id);
            process.Dispose();
        }

        return ids;
    }

    /// <summary>Word exits shortly after Quit; one that is still running after a few seconds is stopped.</summary>
    private static void StopLeftovers(HashSet<int> ids)
    {
        foreach (var id in ids)
        {
            try
            {
                using var process = Process.GetProcessById(id);
                if (!process.WaitForExit(TimeSpan.FromSeconds(10)))
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already exited.
            }
        }
    }

    private static void Kill(HashSet<int> ids)
    {
        foreach (var id in ids)
        {
            try
            {
                using var process = Process.GetProcessById(id);
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Already exited.
            }
        }
    }

    /// <summary>
    /// OLE message filter: Word rejects calls while it is busy (opening, laying out); retry every 100 ms for up to a minute
    /// instead of failing with RPC_E_CALL_REJECTED.
    /// </summary>
    private sealed class RetryMessageFilter : IMessageFilter
    {
        private const int ServerCallIsHandled = 0;
        private const int ServerCallRetryLater = 2;
        private const int PendingMsgWaitDefProcess = 2;
        private const int RetryAfterMilliseconds = 100;
        private const int GiveUpAfterMilliseconds = 60_000;

        public int HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo) => ServerCallIsHandled;

        public int RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType)
            => dwRejectType == ServerCallRetryLater && dwTickCount < GiveUpAfterMilliseconds ? RetryAfterMilliseconds : -1;

        public int MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType) => PendingMsgWaitDefProcess;
    }
}

/// <summary>The OLE <c>IMessageFilter</c> interface (objidl.h).</summary>
[ComImport]
[Guid("00000016-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMessageFilter
{
    [PreserveSig]
    int HandleInComingCall(int dwCallType, IntPtr hTaskCaller, int dwTickCount, IntPtr lpInterfaceInfo);

    [PreserveSig]
    int RetryRejectedCall(IntPtr hTaskCallee, int dwTickCount, int dwRejectType);

    [PreserveSig]
    int MessagePending(IntPtr hTaskCallee, int dwTickCount, int dwPendingType);
}

internal static class NativeMethods
{
    [DllImport("ole32.dll")]
    internal static extern int CoRegisterMessageFilter(IMessageFilter? newFilter, out IMessageFilter? oldFilter);
}
