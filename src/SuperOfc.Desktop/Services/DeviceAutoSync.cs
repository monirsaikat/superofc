using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Common;
using SuperOfc.Domain.Common;
using SuperOfc.Application.Devices;
using SuperOfc.Desktop.Mvvm;
using SuperOfc.Desktop.Resources;
using SuperOfc.Desktop.ViewModels;
using SuperOfc.Domain.Devices;
using Wpf.Ui.Controls;

namespace SuperOfc.Desktop.Services;

/// <summary>
/// Syncs every enabled device each AutoSyncMinutes (policy) while someone is signed in (docs/06 Auto-sync scheduler).
/// Failures retry after 1, 5 and 15 minutes. Paused through <see cref="IDeviceSyncGate"/> (payroll finalize).
/// </summary>
public sealed class DeviceAutoSync(
    AppReadySignal ready,
    IUserSession session,
    IDispatcher dispatcher,
    IClock clock,
    IPolicyProvider policies,
    ICompanyContext company,
    IDeviceSyncGate gate,
    INotificationService notify,
    ILogger<DeviceAutoSync> logger) : BackgroundService
{
    /// <summary>How often the loop checks for due devices.</summary>
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);

    /// <summary>Failures in a row before the user sees an offline warning.</summary>
    public const int WarnAfterFailures = 3;

    private readonly DeviceSyncSchedule _schedule = new();
    private readonly SemaphoreSlim _running = new(1, 1);

    /// <summary>Raised after any sync (automatic or "Sync all") so open pages can refresh.</summary>
    public event EventHandler? Synced;

    /// <summary>Syncs every enabled device now. Returns the summaries of the devices that succeeded.</summary>
    public async Task<IReadOnlyList<(DeviceDto Device, Result<SyncSummary> Result)>> SyncAllAsync(SyncTrigger trigger, CancellationToken cancellationToken = default)
    {
        await _running.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var devices = await dispatcher.SendAsync(new GetDevicesQuery(), cancellationToken).ConfigureAwait(false);
            if (devices.IsFailure)
            {
                return [];
            }

            var results = new List<(DeviceDto, Result<SyncSummary>)>();
            foreach (var device in devices.Value.Where(d => d.IsEnabled))
            {
                results.Add((device, await SyncOneAsync(device, trigger, cancellationToken).ConfigureAwait(false)));
            }

            return results;
        }
        finally
        {
            _running.Release();
            Synced?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ready.WhenReady.WaitAsync(stoppingToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(Tick);
        do
        {
            try
            {
                await RunDueAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Automatic device sync failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    private async Task RunDueAsync(CancellationToken cancellationToken)
    {
        if (!session.IsAuthenticated || gate.IsPaused || company.Company is null)
        {
            return;
        }

        var devices = await dispatcher.SendAsync(new GetDevicesQuery(), cancellationToken).ConfigureAwait(false);
        if (devices.IsFailure)
        {
            return;
        }

        var enabled = devices.Value.Where(d => d.IsEnabled).ToDictionary(d => d.Id);
        var due = _schedule.Due(enabled.Keys, clock.UtcNow);
        if (due.Count == 0 || !await _running.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            foreach (var id in due)
            {
                if (gate.IsPaused || !session.IsAuthenticated)
                {
                    break;
                }

                await SyncOneAsync(enabled[id], SyncTrigger.Automatic, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _running.Release();
            Synced?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task<Result<SyncSummary>> SyncOneAsync(DeviceDto device, SyncTrigger trigger, CancellationToken cancellationToken)
    {
        var policy = await policies.GetSnapshotAsync(clock.TodayIn(company.TimeZone), cancellationToken).ConfigureAwait(false);
        var interval = TimeSpan.FromMinutes(Math.Clamp(policy.General.AutoSyncMinutes, 1, 1440));
        var result = await dispatcher.SendAsync(new SyncDeviceCommand(device.Id, trigger), cancellationToken).ConfigureAwait(false);
        var failures = _schedule.Record(device.Id, result.IsSuccess, clock.UtcNow, interval);
        var offlineKey = "device-offline-" + device.Id.ToString("N");
        var clockKey = "device-clock-" + device.Id.ToString("N");
        if (result.IsSuccess)
        {
            notify.HideInfoBar(offlineKey);
            if (result.Value.HasClockDrift)
            {
                ShowClockWarning(device, result.Value.ClockOffsetSeconds!.Value, clockKey);
            }
            else
            {
                notify.HideInfoBar(clockKey);
            }
        }
        else if (failures >= WarnAfterFailures || trigger != SyncTrigger.Automatic)
        {
            logger.LogWarning("Device {Device} sync failed {Failures} times: {Error}", device.Name, failures, result.FirstError?.Message);
            notify.ShowInfoBar(offlineKey, device.Name, string.Format(CultureInfo.CurrentCulture, Strings.Devices_SyncFailed, ErrorText.For(result.Errors)), InfoBarSeverity.Warning);
        }

        return result;
    }

    private void ShowClockWarning(DeviceDto device, int offsetSeconds, string key)
    {
        var minutes = (int)Math.Round(offsetSeconds / 60.0);
        notify.ShowInfoBar(
            key,
            device.Name,
            string.Format(CultureInfo.CurrentCulture, Strings.Devices_ClockOff, minutes),
            InfoBarSeverity.Warning,
            Strings.Devices_FixClock,
            () => _ = FixClockAsync(device, key));
    }

    private async Task FixClockAsync(DeviceDto device, string key)
    {
        var result = await dispatcher.SendAsync(new SetDeviceTimeCommand(device.Id)).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            notify.HideInfoBar(key);
            notify.Toast(Strings.Devices_ClockFixed);
        }
        else
        {
            notify.ToastError(ErrorText.For(result.Errors));
        }
    }
}
