using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SuperOfc.Application.Attendance;
using SuperOfc.Application.Common;
using SuperOfc.Application.Devices;
using SuperOfc.Desktop.Mvvm;
using SuperOfc.Desktop.Resources;

namespace SuperOfc.Desktop.ViewModels.Devices;

/// <summary>Users on one device and the employee each maps to (docs/06 Mapping UI).</summary>
public sealed partial class DeviceUsersViewModel(PageServices services) : PageViewModel(services), IRefreshable
{
    private DeviceDto? _device;

    public override string Title => _device is null ? Strings.DeviceUsers_Title : string.Format(CultureInfo.CurrentCulture, Strings.DeviceUsers_TitleFor, _device.Name);

    public BulkObservableCollection<DeviceUserRow> Users { get; } = [];

    public ObservableCollection<LookupItem> Employees { get; } = [];

    public ObservableCollection<DeviceDto> OtherDevices { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial DeviceUserRow? Selected { get; set; }

    [ObservableProperty]
    public partial Guid? LinkEmployeeId { get; set; }

    [ObservableProperty]
    public partial Guid? CopyTargetId { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    public partial string? Summary { get; set; }

    [ObservableProperty]
    public partial int NotOnDevice { get; set; }

    public bool HasSelection => Selected is not null;

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        _device = parameter as DeviceDto;
        OnPropertyChanged(nameof(Title));
        var employees = await SendAsync(new GetEmployeePickListQuery()).ConfigureAwait(true);
        Employees.Clear();
        if (employees.IsSuccess)
        {
            foreach (var e in employees.Value)
            {
                Employees.Add(e);
            }
        }

        var devices = await SendAsync(new GetDevicesQuery(), showErrors: false).ConfigureAwait(true);
        OtherDevices.Clear();
        if (devices.IsSuccess)
        {
            foreach (var d in devices.Value.Where(d => d.Id != _device?.Id))
            {
                OtherDevices.Add(d);
            }
        }

        await LoadAsync().ConfigureAwait(true);
    }

    public Task RefreshAsync() => LoadAsync();

    partial void OnSelectedChanged(DeviceUserRow? value) => LinkEmployeeId = value?.EmployeeId;

    [RelayCommand]
    private Task BackAsync() => Services.Navigation.GoBackAsync();

    [RelayCommand]
    private async Task LinkAsync()
    {
        if (_device is null || Selected is null || LinkEmployeeId is not { } employeeId)
        {
            return;
        }

        var result = await SendAsync(new MapDeviceUserCommand(_device.Id, Selected.DeviceUserId, employeeId, Selected.NameOnDevice)).ConfigureAwait(true);
        if (result.IsSuccess)
        {
            Toast(result.Value > 0 ? string.Format(CultureInfo.CurrentCulture, Strings.DeviceUsers_LinkedMoved, result.Value) : Strings.DeviceUsers_Linked);
            await LoadAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task UnlinkAsync()
    {
        if (_device is null || Selected is not { Match: DeviceUserMatch.Mapped })
        {
            return;
        }

        if ((await SendAsync(new MapDeviceUserCommand(_device.Id, Selected.DeviceUserId, null)).ConfigureAwait(true)).IsSuccess)
        {
            await LoadAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task AutoMapAsync()
    {
        if (_device is null)
        {
            return;
        }

        var result = await SendAsync(new AutoMapDeviceUsersCommand(_device.Id)).ConfigureAwait(true);
        if (result.IsSuccess)
        {
            Toast(string.Format(CultureInfo.CurrentCulture, Strings.DeviceUsers_AutoMapped, result.Value.Mapped, result.Value.PunchesMoved));
            await LoadAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task DeleteFromDeviceAsync()
    {
        if (_device is null || Selected is null)
        {
            return;
        }

        var ok = await Services.Dialogs.ConfirmAsync(
            string.Format(CultureInfo.CurrentCulture, Strings.DeviceUsers_DeleteTitle, Selected.NameOnDevice, Selected.DeviceUserId),
            Strings.DeviceUsers_DeleteMessage, Strings.App_Delete, danger: true).ConfigureAwait(true);
        if (ok && (await SendAsync(new DeleteDeviceUserCommand(_device.Id, Selected.DeviceUserId)).ConfigureAwait(true)).IsSuccess)
        {
            await LoadAsync().ConfigureAwait(true);
        }
    }

    /// <summary>Sends every active employee who is not on the device yet, restoring backed-up fingerprints.</summary>
    [RelayCommand]
    private async Task PushMissingAsync()
    {
        if (_device is null)
        {
            return;
        }

        var onDevice = Users.Where(u => u.EmployeeId is not null).Select(u => u.EmployeeId!.Value).ToHashSet();
        var missing = Employees.Where(e => !onDevice.Contains(e.Id)).Select(e => e.Id).ToList();
        if (missing.Count == 0)
        {
            Toast(Strings.DeviceUsers_NothingToPush);
            return;
        }

        var ok = await Services.Dialogs.ConfirmAsync(Strings.DeviceUsers_PushTitle, string.Format(CultureInfo.CurrentCulture, Strings.DeviceUsers_PushMessage, missing.Count, _device.Name), Strings.DeviceUsers_Push).ConfigureAwait(true);
        if (!ok)
        {
            return;
        }

        await PushAsync(_device, missing).ConfigureAwait(true);
        await LoadAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task BackupAsync()
    {
        if (_device is null)
        {
            return;
        }

        var result = await SendAsync(new BackupFingerprintsCommand(_device.Id)).ConfigureAwait(true);
        if (result.IsSuccess)
        {
            Toast(string.Format(CultureInfo.CurrentCulture, Strings.DeviceUsers_BackedUp, result.Value.Fingers, result.Value.Employees));
            await LoadAsync().ConfigureAwait(true);
        }
    }

    /// <summary>Backs up the linked users of this device, then sends them with their fingerprints to the chosen device.</summary>
    [RelayCommand]
    private async Task CopyToDeviceAsync()
    {
        if (_device is null || OtherDevices.FirstOrDefault(d => d.Id == CopyTargetId) is not { } target)
        {
            return;
        }

        var employees = Users.Where(u => u.EmployeeId is not null).Select(u => u.EmployeeId!.Value).Distinct().ToList();
        if (employees.Count == 0)
        {
            Toast(Strings.DeviceUsers_NothingToPush);
            return;
        }

        var backup = await SendAsync(new BackupFingerprintsCommand(_device.Id, employees)).ConfigureAwait(true);
        if (backup.IsSuccess)
        {
            await PushAsync(target, employees).ConfigureAwait(true);
        }
    }

    private async Task PushAsync(DeviceDto device, IReadOnlyList<Guid> employees)
    {
        var result = await SendAsync(new PushEmployeesToDeviceCommand(device.Id, employees, RestoreFingerprints: true)).ConfigureAwait(true);
        if (result.IsSuccess)
        {
            Toast(string.Format(CultureInfo.CurrentCulture, Strings.DeviceUsers_Pushed, result.Value.Pushed, device.Name, result.Value.FingersRestored));
            if (result.Value.Failed.Count > 0)
            {
                ErrorMessage = string.Join(Environment.NewLine, result.Value.Failed.Take(10));
            }
        }
    }

    private async Task LoadAsync()
    {
        if (_device is null)
        {
            return;
        }

        var result = await SendAsync(new GetDeviceUsersQuery(_device.Id)).ConfigureAwait(true);
        if (result.IsFailure)
        {
            Users.Clear();
            IsEmpty = false;
            return;
        }

        var selected = Selected?.DeviceUserId;
        Users.ReplaceAll(result.Value);
        Selected = Users.FirstOrDefault(u => u.DeviceUserId == selected);
        IsEmpty = Users.Count == 0;
        var linked = Users.Where(u => u.EmployeeId is not null).Select(u => u.EmployeeId!.Value).ToHashSet();
        NotOnDevice = Employees.Count(e => !linked.Contains(e.Id));
        Summary = string.Format(CultureInfo.CurrentCulture, Strings.DeviceUsers_Summary, Users.Count, linked.Count, Users.Count(u => u.EmployeeId is null), NotOnDevice);
    }
}

/// <summary>Punches from device users that are not linked to an employee yet.</summary>
public sealed partial class UnmappedPunchesViewModel(PageServices services) : PageViewModel(services), IRefreshable
{
    public override string Title => Strings.Unmapped_Title;

    public BulkObservableCollection<UnmappedGroupDto> Groups { get; } = [];

    public ObservableCollection<LookupItem> Employees { get; } = [];

    [ObservableProperty]
    public partial UnmappedGroupDto? Selected { get; set; }

    [ObservableProperty]
    public partial Guid? AssignEmployeeId { get; set; }

    [ObservableProperty]
    public partial bool Remember { get; set; } = true;

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        var employees = await SendAsync(new GetEmployeePickListQuery(IncludeLeft: true)).ConfigureAwait(true);
        Employees.Clear();
        if (employees.IsSuccess)
        {
            foreach (var e in employees.Value)
            {
                Employees.Add(e);
            }
        }

        await LoadAsync().ConfigureAwait(true);
    }

    public Task RefreshAsync() => LoadAsync();

    partial void OnSelectedChanged(UnmappedGroupDto? value) => AssignEmployeeId = value?.SuggestedEmployeeId;

    [RelayCommand]
    private Task BackAsync() => Services.Navigation.GoBackAsync();

    [RelayCommand]
    private async Task AssignAsync()
    {
        if (Selected is null || AssignEmployeeId is not { } employeeId)
        {
            return;
        }

        var result = await SendAsync(new AssignUnmappedPunchesCommand(Selected.DeviceId, Selected.DeviceUserId, employeeId, Remember)).ConfigureAwait(true);
        if (result.IsSuccess)
        {
            Toast(string.Format(CultureInfo.CurrentCulture, Strings.Unmapped_Assigned, result.Value));
            await LoadAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task DiscardAsync()
    {
        if (Selected is null)
        {
            return;
        }

        var ok = await Services.Dialogs.ConfirmAsync(
            string.Format(CultureInfo.CurrentCulture, Strings.Unmapped_DiscardTitle, Selected.Count, Selected.DeviceUserId), Strings.Unmapped_DiscardMessage, Strings.Unmapped_Discard, danger: true).ConfigureAwait(true);
        if (ok && (await SendAsync(new DiscardUnmappedPunchesCommand(Selected.DeviceId, Selected.DeviceUserId)).ConfigureAwait(true)).IsSuccess)
        {
            await LoadAsync().ConfigureAwait(true);
        }
    }

    private async Task LoadAsync()
    {
        var result = await SendAsync(new GetUnmappedPunchesQuery()).ConfigureAwait(true);
        if (result.IsSuccess)
        {
            Groups.ReplaceAll(result.Value);
            Selected = Groups.FirstOrDefault();
            IsEmpty = Groups.Count == 0;
        }
    }
}
