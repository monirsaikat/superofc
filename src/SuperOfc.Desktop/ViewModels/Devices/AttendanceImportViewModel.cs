using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SuperOfc.Application.Devices;
using SuperOfc.Desktop.Mvvm;
using SuperOfc.Desktop.Resources;
using SuperOfc.Desktop.Services;

namespace SuperOfc.Desktop.ViewModels.Devices;

/// <summary>Imports punches from a USB export or a spreadsheet: choose file, preview counts, import (docs/06 File import).</summary>
public sealed partial class AttendanceImportViewModel(PageServices services, IFileDialogService files) : PageViewModel(services)
{
    public override string Title => Strings.AttImport_Title;

    public ObservableCollection<DeviceDto> Devices { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileName), nameof(IsDeviceExport))]
    public partial string? FilePath { get; set; }

    [ObservableProperty]
    public partial Guid? DeviceId { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanImport))]
    public partial AttendanceFilePreview? Preview { get; set; }

    [ObservableProperty]
    public partial string? ResultText { get; set; }

    public string? FileName => FilePath is null ? null : Path.GetFileName(FilePath);

    public bool IsDeviceExport => FilePath is not null && PunchFileParser.IsDeviceExport(FilePath);

    public bool CanImport => Preview is { Valid: > 0 } or { Unmapped: > 0 };

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        var devices = await SendAsync(new GetDevicesQuery(), showErrors: false).ConfigureAwait(true);
        Devices.Clear();
        if (devices.IsSuccess)
        {
            foreach (var d in devices.Value)
            {
                Devices.Add(d);
            }
        }
    }

    async partial void OnDeviceIdChanged(Guid? value)
    {
        if (FilePath is not null)
        {
            await PreviewFileAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private Task BackAsync() => Services.Navigation.GoBackAsync();

    [RelayCommand]
    private async Task ChooseFileAsync()
    {
        var path = files.OpenFile(Strings.AttImport_Filter);
        if (path is null)
        {
            return;
        }

        FilePath = path;
        ResultText = null;
        await PreviewFileAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ImportAsync()
    {
        if (FilePath is null)
        {
            return;
        }

        var result = await SendAsync(new ImportAttendanceFileCommand(FilePath, IsDeviceExport ? DeviceId : null)).ConfigureAwait(true);
        if (result.IsSuccess)
        {
            var s = result.Value;
            ResultText = string.Format(CultureInfo.CurrentCulture, Strings.AttImport_Done, s.Inserted, s.Duplicates, s.Unmapped);
            Toast(ResultText);
            Preview = null;
        }
    }

    [RelayCommand]
    private Task OpenUnmappedAsync() => Services.Navigation.NavigateToAsync<UnmappedPunchesViewModel>();

    private async Task PreviewFileAsync()
    {
        Preview = null;
        if (FilePath is null)
        {
            return;
        }

        var result = await SendAsync(new PreviewAttendanceFileQuery(FilePath, IsDeviceExport ? DeviceId : null)).ConfigureAwait(true);
        if (result.IsSuccess)
        {
            Preview = result.Value;
        }
    }
}
