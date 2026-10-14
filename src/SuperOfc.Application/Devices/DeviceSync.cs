using System.Collections.Concurrent;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Common;
using SuperOfc.Domain.Attendance;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Devices;
using SuperOfc.Domain.Security;

namespace SuperOfc.Application.Devices;

public sealed record SyncSummary(Guid DeviceId, int Pulled, int Inserted, int Duplicates, int Unmapped, int? ClockOffsetSeconds, string? Warning)
{
    public bool HasClockDrift => ClockOffsetSeconds is { } s && Math.Abs(s) > DeviceAccess.MaxClockDriftSeconds;
}

/// <summary>
/// Pulls new punches from one device (docs/06 Sync algorithm). Automatic syncs run as the signed-in user, so they use
/// <see cref="Permission.ViewData"/>; the Devices page itself needs <see cref="Permission.ManageDevices"/>. It only reads from the device and adds punches, so any signed-in user may run it.
/// </summary>
[RequiresPermission(Permission.ViewData)]
public sealed record SyncDeviceCommand(Guid DeviceId, SyncTrigger Trigger = SyncTrigger.Manual) : ICommand<SyncSummary>;

public sealed class SyncDeviceHandler(IAppDbContext db, ICurrentUser user, IIdGenerator ids, IClock clock, IDeviceGateway gateway, DeviceAccess access, PunchIntake intake)
    : IRequestHandler<SyncDeviceCommand, SyncSummary>
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Running = new();

    public async Task<Result<SyncSummary>> HandleAsync(SyncDeviceCommand request, CancellationToken cancellationToken)
    {
        var device = await access.FindAsync(request.DeviceId, cancellationToken).ConfigureAwait(false);
        if (device is null)
        {
            return Error.NotFound("Device");
        }

        if (!device.IsEnabled && request.Trigger == SyncTrigger.Automatic)
        {
            return new SyncSummary(device.Id, 0, 0, 0, 0, device.ClockOffsetSeconds, null);
        }

        var gate = Running.GetOrAdd(device.Id, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return new Error(DeviceBridge.Contracts.BridgeErrors.Busy, "A sync is already running for this device.");
        }

        try
        {
            return await SyncAsync(device, request.Trigger, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<Result<SyncSummary>> SyncAsync(Device device, SyncTrigger trigger, CancellationToken cancellationToken)
    {
        var log = new DeviceSyncLog { Id = ids.NewId(), CompanyId = user.CompanyId, DeviceId = device.Id, Trigger = trigger, StartedAtUtc = clock.UtcNow };
        var endpoint = access.Endpoint(device);
        var zone = access.ZoneOf(device);
        var maxSeen = device.LastSyncLocal;
        Error? failure = null;

        await intake.BeginAsync(device.Id, cancellationToken).ConfigureAwait(false);
        try
        {
            // Step 7 first: reading the clock is cheap and fails fast when the device is off.
            var deviceTime = await gateway.GetTimeAsync(endpoint, cancellationToken).ConfigureAwait(false);
            device.ClockOffsetSeconds = TestDeviceConnectionHandler.ClockOffset(deviceTime, zone, clock);
            if (Math.Abs(device.ClockOffsetSeconds.Value) > DeviceAccess.MaxClockDriftSeconds)
            {
                log.Warning = $"Device clock is off by {Math.Round(device.ClockOffsetSeconds.Value / 60.0)} minutes.";
            }

            await foreach (var batch in gateway.PullLogsAsync(endpoint, device.NextPullFromLocal, null, cancellationToken).ConfigureAwait(false))
            {
                if (!string.IsNullOrEmpty(batch.SerialNumber))
                {
                    device.SerialNumber = batch.SerialNumber;
                }

                var serial = device.SerialNumber ?? device.Ip;
                var punches = batch.Logs.Select(l => new RawPunch(l.DeviceUserId.Trim(), l.TimestampLocal, l.VerifyMode, l.InOutMode,
                    AttendancePunch.DeviceKey(serial, l.DeviceUserId.Trim(), l.TimestampLocal))).ToList();
                if (punches.Count > 0)
                {
                    var latest = punches.Max(p => p.LocalTime);
                    maxSeen = maxSeen is null || latest > maxSeen ? latest : maxSeen;
                }

                await intake.AddBatchAsync(device.Id, punches, PunchSource.Device, zone, cancellationToken).ConfigureAwait(false);
            }

            device.Status = DeviceStatus.Online;
            device.StatusMessage = log.Warning;

            // Step 6: the newest punch seen, not "now", so a wrong device clock never skips punches.
            device.LastSyncLocal = maxSeen;
            device.LastSyncAtUtc = clock.UtcNow;
        }
        catch (DeviceGatewayException ex)
        {
            DeviceAccess.MarkFailed(device, ex);
            log.Error = ex.Message;
            failure = DeviceAccess.ToError(ex);
        }

        // Punches stored before a failure stay stored (idempotent), so their days are recomputed either way.
        var result = await intake.FinishAsync(CancellationToken.None).ConfigureAwait(false);
        log.Pulled = result.Received;
        log.Inserted = result.Inserted;
        log.Duplicates = result.Duplicates;
        log.Unmapped = result.Unmapped;
        log.FinishedAtUtc = clock.UtcNow;
        db.DeviceSyncLogs.Add(log);
        await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

        return failure is not null
            ? failure
            : new SyncSummary(device.Id, result.Received, result.Inserted, result.Duplicates, result.Unmapped, device.ClockOffsetSeconds, log.Warning);
    }
}
