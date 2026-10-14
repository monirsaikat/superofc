using System.IO;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SuperOfc.Application.Common;
using SuperOfc.Application.Employees;
using SuperOfc.Desktop.Services;
using SuperOfc.Desktop.Tests.Support;
using SuperOfc.Desktop.ViewModels.Devices;
using SuperOfc.Domain.Devices;
using SuperOfc.Domain.Employees;
using SuperOfc.Infrastructure.Devices;

namespace SuperOfc.Desktop.Tests;

/// <summary>Devices pages against the real bridge in fake mode with the demo session in tests/Fixtures/Attendance.</summary>
public class DevicesFlowTests
{
    private static string DemoSession => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Attendance", "fake-devices.json");

    private static Task<DesktopTestHost> CreateAsync()
        => DesktopTestHost.CreateAsync(completeSetup: true, configure: s => s.AddSingleton(new DeviceBridgeOptions
        {
            BridgePath = Path.Combine(AppContext.BaseDirectory, "bridge", "SuperOfc.DeviceBridge.exe"),
            FakeSessionPath = DemoSession,

            // CI machines can take far longer than a desktop PC to start the bridge the first time.
            ConnectTimeout = TimeSpan.FromSeconds(60),
        }));

    private static async Task CreateEmployeeAsync(DesktopTestHost host, string code)
    {
        var input = new EmployeeInput(code, $"Person {code}", Gender.Unspecified, null, null, null, null, null, null, new DateOnly(2025, 1, 1),
            null, null, null, null, PaymentMode.Bank, null, null, null, null, true, null);
        (await host.Get<IDispatcher>().SendAsync(new CreateEmployeeCommand(input))).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Add_test_sync_fix_clock_and_link_users()
    {
        await using var host = await CreateAsync();
        await host.SignInAsync();
        await CreateEmployeeAsync(host, "E1");
        await CreateEmployeeAsync(host, "E2");
        var page = host.Get<DevicesViewModel>();
        await page.OnNavigatedToAsync(null);
        page.IsEmpty.Should().BeTrue();

        page.AddCommand.Execute(null);
        page.EditIp = "192.168.1.201";
        await page.TestCommand.ExecuteAsync(null);
        page.TestSucceeded.Should().BeTrue(page.TestResult);
        page.TestResult.Should().Contain("DEMO0001");
        await page.SaveCommand.ExecuteAsync(null);
        page.IsEditing.Should().BeFalse(page.ErrorMessage);
        var card = page.Devices.Should().ContainSingle().Subject;

        await page.SyncCommand.ExecuteAsync(card);
        page.ErrorMessage.Should().BeNull();
        card = page.Devices.Single();
        card.Device.Status.Should().Be(DeviceStatus.Online);
        card.Device.HasClockDrift.Should().BeTrue();
        card.Device.UnmappedPunches.Should().BeGreaterThan(0, "user 3 has no employee");
        page.SyncLog.Should().ContainSingle().Which.Inserted.Should().BeGreaterThan(200);

        await page.FixClockCommand.ExecuteAsync(card);
        page.Devices.Single().Device.HasClockDrift.Should().BeFalse();

        var users = host.Get<DeviceUsersViewModel>();
        await users.OnNavigatedToAsync(page.Devices.Single().Device);
        users.Users.Should().HaveCount(3);
        users.Users.Count(u => u.EmployeeId is not null).Should().Be(2, "users 1 and 2 match E1 and E2 by number");
        users.Selected = users.Users.Single(u => u.DeviceUserId == "900");
        users.LinkEmployeeId = users.Employees.First().Id;
        await users.LinkCommand.ExecuteAsync(null);
        users.Users.Single(u => u.DeviceUserId == "900").Match.Should().Be(Application.Devices.DeviceUserMatch.Mapped);

        var unmapped = host.Get<UnmappedPunchesViewModel>();
        await unmapped.OnNavigatedToAsync(null);
        unmapped.Groups.Should().ContainSingle().Which.DeviceUserId.Should().Be("3");
        unmapped.AssignEmployeeId = unmapped.Employees.First().Id;
        await unmapped.AssignCommand.ExecuteAsync(null);
        unmapped.IsEmpty.Should().BeTrue();
    }

    [Fact]
    public async Task Wrong_comm_key_and_unknown_address_show_readable_errors()
    {
        await using var host = await CreateAsync();
        await host.SignInAsync();
        var page = host.Get<DevicesViewModel>();
        await page.OnNavigatedToAsync(null);

        page.AddCommand.Execute(null);
        page.EditIp = "192.168.1.202";
        await page.TestCommand.ExecuteAsync(null);
        page.TestSucceeded.Should().BeFalse();
        page.TestResult.Should().Contain("communication key");

        page.EditCommKey = "1234";
        await page.TestCommand.ExecuteAsync(null);
        page.TestSucceeded.Should().BeTrue(page.TestResult);

        page.EditIp = "10.9.9.9";
        await page.TestCommand.ExecuteAsync(null);
        page.TestResult.Should().Contain("10.9.9.9");

        page.EditPort = "99999";
        await page.SaveCommand.ExecuteAsync(null);
        page.IsEditing.Should().BeTrue();
        page.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Sync_all_runs_every_enabled_device_and_import_page_previews_a_file()
    {
        await using var host = await CreateAsync();
        await host.SignInAsync();
        await CreateEmployeeAsync(host, "E1");
        var page = host.Get<DevicesViewModel>();
        await page.OnNavigatedToAsync(null);
        page.AddCommand.Execute(null);
        page.EditIp = "192.168.1.201";
        await page.SaveCommand.ExecuteAsync(null);

        var results = await host.Get<DeviceAutoSync>().SyncAllAsync(SyncTrigger.Manual);
        results.Should().ContainSingle().Which.Result.IsSuccess.Should().BeTrue();

        var import = host.Get<AttendanceImportViewModel>();
        await import.OnNavigatedToAsync(null);
        host.Files.OpenFile(Arg.Any<string>()).Returns(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Attendance", "punches.csv"));
        await import.ChooseFileCommand.ExecuteAsync(null);
        import.IsDeviceExport.Should().BeFalse();
        import.Preview.Should().NotBeNull(import.ErrorMessage);
        import.Preview!.Valid.Should().Be(2);
        import.CanImport.Should().BeTrue();
        await import.ImportCommand.ExecuteAsync(null);
        import.ResultText.Should().Contain("2");
    }
}
