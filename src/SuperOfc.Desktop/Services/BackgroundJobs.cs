using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SuperOfc.Application.Abstractions;
using SuperOfc.Desktop.Resources;
using SuperOfc.Desktop.ViewModels;
using SuperOfc.Domain.Platform;
using Wpf.Ui.Controls;

namespace SuperOfc.Desktop.Services;

/// <summary>Daily backup, backup retention, and update checks (docs/07).</summary>
public sealed class BackgroundJobs(
    AppReadySignal ready,
    IBackupService backups,
    IUpdateService updates,
    ISettingsStore settings,
    INotificationService notify,
    ILogger<BackgroundJobs> logger) : BackgroundService
{
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        updates.StatusChanged += OnUpdateStatus;
        await ready.WhenReady.WaitAsync(stoppingToken).ConfigureAwait(false);
        await RunBackupJobsAsync(stoppingToken).ConfigureAwait(false);

        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            await CheckUpdatesAsync(stoppingToken).ConfigureAwait(false);
            await Task.Delay(UpdateInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task RunBackupJobsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await backups.ReconcileAsync(cancellationToken).ConfigureAwait(false);
            if (!await backups.HasBackupTodayAsync(cancellationToken).ConfigureAwait(false))
            {
                var result = await backups.CreateBackupAsync(BackupType.Auto, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (result.IsFailure)
                {
                    notify.ShowInfoBar("backup", Strings.Backup_Title, result.FirstError!.Message, InfoBarSeverity.Warning);
                }
            }

            await backups.ApplyRetentionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Automatic backup failed");
            notify.ShowInfoBar("backup", Strings.Backup_Title, ex.Message, InfoBarSeverity.Warning);
        }
    }

    private async Task CheckUpdatesAsync(CancellationToken cancellationToken)
    {
        if (!updates.IsInstalled)
        {
            return;
        }

        var config = await settings.GetAsync<UpdateSettings>(UpdateSettings.Key, cancellationToken).ConfigureAwait(false);
        if (config.Mode == UpdateMode.Manual)
        {
            return;
        }

        await updates.CheckAsync(downloadIfAvailable: config.Mode == UpdateMode.Automatic, cancellationToken).ConfigureAwait(false);
    }

    private void OnUpdateStatus(object? sender, UpdateStatus status)
    {
        switch (status.State)
        {
            case UpdateState.ReadyToRestart:
                notify.ShowInfoBar(
                    "update",
                    Strings.Updates_Title,
                    string.Format(CultureInfo.CurrentCulture, Strings.Updates_Ready, status.NewVersion),
                    InfoBarSeverity.Success,
                    Strings.Updates_Restart,
                    () =>
                    {
                        var reason = updates.ApplyAndRestart();
                        if (reason is not null)
                        {
                            notify.ToastError(reason);
                        }
                    });
                break;
            case UpdateState.Available:
                notify.ShowInfoBar(
                    "update",
                    Strings.Updates_Title,
                    string.Format(CultureInfo.CurrentCulture, Strings.Updates_Available, status.NewVersion),
                    InfoBarSeverity.Informational,
                    Strings.Updates_Check,
                    () => _ = updates.CheckAsync(downloadIfAvailable: true));
                break;
        }
    }
}
