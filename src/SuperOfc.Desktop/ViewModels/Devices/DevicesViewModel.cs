using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SuperOfc.Application.Devices;
using SuperOfc.Desktop.Mvvm;
using SuperOfc.Desktop.Resources;
using SuperOfc.Desktop.Services;
using SuperOfc.Domain.Devices;
using SuperOfc.Domain.Security;

namespace SuperOfc.Desktop.ViewModels.Devices;

/// <summary>Attendance devices: cards with status and actions, an add/edit panel and the sync log (docs/04 Devices).</summary>
public sealed partial class DevicesViewModel(PageServices services, DeviceAutoSync autoSync) : PageViewModel(services), IRefreshable, ICreatesNew
{
    private bool _subscribed;

    public override string Title => Strings.Devices_Title;

    public bool CanManage => Can(Permission.ManageDevices);

    public bool CanImport => Can(Permission.ManageAttendance);

    public BulkObservableCollection<DeviceCard> Devices { get; } = [];

    public BulkObservableCollection<DeviceSyncLogDto> SyncLog { get; } = [];

    public static IReadOnlyList<Choice<string?>> TimeZones { get; } =
        [new(null, Strings.Devices_CompanyZone), .. TimeZoneInfo.GetSystemTimeZones().Select(z => new Choice<string?>(z.Id, z.DisplayName))];

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    public partial bool IsEditing { get; set; }

    [ObservableProperty]
    public partial Guid? EditId { get; set; }

    [ObservableProperty]
    public partial string EditName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EditIp { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EditPort { get; set; } = "4370";

    /// <summary>Empty on edit keeps the stored key.</summary>
    [ObservableProperty]
    public partial string? EditCommKey { get; set; }

    [ObservableProperty]
    public partial bool EditHasCommKey { get; set; }

    [ObservableProperty]
    public partial string? EditTimeZoneId { get; set; }

    [ObservableProperty]
    public partial bool EditEnabled { get; set; } = true;

    [ObservableProperty]
    public partial string? TestResult { get; set; }

    [ObservableProperty]
    public partial bool TestSucceeded { get; set; }

    [ObservableProperty]
    public partial bool IsSyncingAll { get; set; }

    public string EditTitle => EditId is null ? Strings.Devices_Add : Strings.Devices_Edit;

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        if (!_subscribed)
        {
            autoSync.Synced += OnSynced;
            _subscribed = true;
        }

        await LoadAsync().ConfigureAwait(true);
    }

    public override Task<bool> CanNavigateAwayAsync()
    {
        // Pages are transient; unsubscribe so the singleton sync service does not keep this one alive.
        autoSync.Synced -= OnSynced;
        _subscribed = false;
        return Task.FromResult(true);
    }

    public Task RefreshAsync() => LoadAsync();

    public Task CreateNewAsync()
    {
        Add();
        return Task.CompletedTask;
    }

    partial void OnEditIdChanged(Guid? value) => OnPropertyChanged(nameof(EditTitle));

    [RelayCommand(CanExecute = nameof(CanManage))]
    private void Add()
    {
        EditId = null;
        EditName = Devices.Count == 0 ? Strings.Devices_DefaultName : string.Empty;
        EditIp = string.Empty;
        EditPort = "4370";
        EditCommKey = null;
        EditHasCommKey = false;
        EditTimeZoneId = null;
        EditEnabled = true;
        TestResult = null;
        ErrorMessage = null;
        IsEditing = true;
    }

    [RelayCommand]
    private void Edit(DeviceCard? card)
    {
        if (card is null || !CanManage)
        {
            return;
        }

        var d = card.Device;
        EditId = d.Id;
        EditName = d.Name;
        EditIp = d.Ip;
        EditPort = d.Port.ToString(CultureInfo.InvariantCulture);
        EditCommKey = null;
        EditHasCommKey = d.HasCommKey;
        EditTimeZoneId = d.TimeZoneId;
        EditEnabled = d.IsEnabled;
        TestResult = null;
        ErrorMessage = null;
        IsEditing = true;
    }

    [RelayCommand]
    private void CancelEdit() => IsEditing = false;

    [RelayCommand]
    private async Task TestAsync()
    {
        if (!TryReadPort(out var port) || !TryReadKey(out var key))
        {
            return;
        }

        TestResult = null;
        var result = await SendAsync(new TestDeviceConnectionCommand(EditId, EditIp, port, key), showErrors: false).ConfigureAwait(true);
        TestSucceeded = result.IsSuccess;
        TestResult = result.IsSuccess
            ? string.Format(CultureInfo.CurrentCulture, Strings.Devices_TestOk, result.Value.DeviceName ?? result.Value.Platform, result.Value.SerialNumber, result.Value.UserCount, result.Value.LogCount)
            : ErrorText.For(result.Errors);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!TryReadPort(out var port) || !TryReadKey(out var key))
        {
            return;
        }

        var result = await SendAsync(new SaveDeviceCommand(EditId, EditName, EditIp, port, key, 1, EditTimeZoneId, EditEnabled)).ConfigureAwait(true);
        if (result.IsSuccess)
        {
            IsEditing = false;
            Toast(Strings.Devices_Saved);
            await LoadAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(DeviceCard? card)
    {
        if (card is null)
        {
            return;
        }

        var ok = await Services.Dialogs.ConfirmAsync(
            string.Format(CultureInfo.CurrentCulture, Strings.Devices_DeleteTitle, card.Device.Name), Strings.Devices_DeleteMessage, Strings.App_Delete, danger: true).ConfigureAwait(true);
        if (ok && (await SendAsync(new DeleteDeviceCommand(card.Device.Id)).ConfigureAwait(true)).IsSuccess)
        {
            IsEditing = false;
            await LoadAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task SyncAsync(DeviceCard? card)
    {
        if (card is null || card.IsSyncing)
        {
            return;
        }

        card.IsSyncing = true;
        try
        {
            var result = await SendAsync(new SyncDeviceCommand(card.Device.Id)).ConfigureAwait(true);
            if (result.IsSuccess)
            {
                Toast(SummaryText(card.Device.Name, result.Value));
            }
        }
        finally
        {
            card.IsSyncing = false;
        }

        await LoadAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task SyncAllAsync()
    {
        IsSyncingAll = true;
        try
        {
            var results = await autoSync.SyncAllAsync(SyncTrigger.Manual).ConfigureAwait(true);
            var ok = results.Where(r => r.Result.IsSuccess).ToList();
            Toast(string.Format(CultureInfo.CurrentCulture, Strings.Devices_SyncAllDone, ok.Count, results.Count, ok.Sum(r => r.Result.Value.Inserted)));
        }
        finally
        {
            IsSyncingAll = false;
        }

        await LoadAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task FixClockAsync(DeviceCard? card)
    {
        if (card is not null && (await SendAsync(new SetDeviceTimeCommand(card.Device.Id)).ConfigureAwait(true)).IsSuccess)
        {
            Toast(Strings.Devices_ClockFixed);
            await LoadAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private Task OpenUsersAsync(DeviceCard? card)
        => card is null ? Task.CompletedTask : Services.Navigation.NavigateToAsync<DeviceUsersViewModel>(card.Device);

    [RelayCommand]
    private Task OpenUnmappedAsync() => Services.Navigation.NavigateToAsync<UnmappedPunchesViewModel>();

    [RelayCommand]
    private Task ImportFileAsync() => Services.Navigation.NavigateToAsync<AttendanceImportViewModel>();

    internal static string SummaryText(string device, SyncSummary s)
        => string.Format(CultureInfo.CurrentCulture, Strings.Devices_SyncDone, device, s.Inserted, s.Duplicates, s.Unmapped);

    private void OnSynced(object? sender, EventArgs e)
        => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => _ = LoadAsync());

    private async Task LoadAsync()
    {
        var devices = await SendAsync(new GetDevicesQuery()).ConfigureAwait(true);
        if (devices.IsSuccess)
        {
            var syncing = Devices.Where(c => c.IsSyncing).Select(c => c.Device.Id).ToHashSet();
            Devices.ReplaceAll(devices.Value.Select(d => new DeviceCard(d) { IsSyncing = syncing.Contains(d.Id) }));
            IsEmpty = Devices.Count == 0;
        }

        var log = await SendAsync(new GetDeviceSyncLogQuery(null, 200), showErrors: false).ConfigureAwait(true);
        if (log.IsSuccess)
        {
            SyncLog.ReplaceAll(log.Value);
        }
    }

    private bool TryReadPort(out int port)
    {
        if (int.TryParse(EditPort, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is > 0 and <= 65535)
        {
            return true;
        }

        ErrorMessage = Strings.Devices_InvalidPort;
        return false;
    }

    /// <summary>Empty means "keep the stored key" on edit and "no key" (0) on add.</summary>
    private bool TryReadKey(out int? key)
    {
        key = null;
        if (string.IsNullOrWhiteSpace(EditCommKey))
        {
            key = EditId is null ? 0 : null;
            return true;
        }

        if (int.TryParse(EditCommKey, NumberStyles.None, CultureInfo.InvariantCulture, out var k) && k <= 999999)
        {
            key = k;
            return true;
        }

        ErrorMessage = Strings.Devices_InvalidKey;
        return false;
    }
}

/// <summary>One device on the Devices page.</summary>
public sealed partial class DeviceCard(DeviceDto device) : ObservableObject
{
    public DeviceDto Device { get; } = device;

    [ObservableProperty]
    public partial bool IsSyncing { get; set; }

    public string LastSyncText => Device.LastSyncAtUtc is { } t
        ? string.Format(CultureInfo.CurrentCulture, Strings.Devices_LastSync, t.ToLocalTime().ToString("g", CultureInfo.CurrentCulture))
        : Strings.Devices_NeverSynced;

    public string ClockText => string.Format(CultureInfo.CurrentCulture, Strings.Devices_ClockOff, Device.ClockOffsetMinutes);

    public string DetailText => string.Join(" · ", new[] { Device.Address, Device.Model, Device.SerialNumber }.Where(s => !string.IsNullOrWhiteSpace(s)));

    public string UsersText => string.Format(CultureInfo.CurrentCulture, Strings.Devices_UsersMapped, Device.MappedUsers);

    public bool HasUnmapped => Device.UnmappedPunches > 0;

    public string UnmappedText => string.Format(CultureInfo.CurrentCulture, Strings.Devices_UnmappedCount, Device.UnmappedPunches);
}
