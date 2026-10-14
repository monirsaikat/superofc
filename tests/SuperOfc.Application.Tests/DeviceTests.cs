using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Attendance;
using SuperOfc.Application.Devices;
using SuperOfc.Application.Tests.Support;
using SuperOfc.DeviceBridge.Contracts;
using SuperOfc.Domain.Attendance;
using SuperOfc.Domain.Devices;

namespace SuperOfc.Application.Tests;

// Test clock: 2026-03-15 10:00 in Dhaka. Default shift 09:00-18:00. Employees "E1", "E2"; device users "1" and "E2" match them.
public class DeviceSyncTests
{
    private static DateTime At(int day, int hour, int minute = 0) => new(2026, 3, day, hour, minute, 0);

    private static async Task<(TestHost Host, FakeDeviceGateway Gateway)> CreateAsync()
    {
        var host = await TestHost.CreateAsync(configure: s => s.AddSingleton<IDeviceGateway>(sp => new FakeDeviceGateway((FakeClock)sp.GetRequiredService<IClock>())));
        return (host, (FakeDeviceGateway)host.Services.GetRequiredService<IDeviceGateway>());
    }

    private static Task<Guid> AddDeviceAsync(TestHost host, string ip = "10.0.0.1", int? key = null)
        => host.SendOkAsync(new SaveDeviceCommand(null, "Gate " + ip, ip, 4370, key, 1, null, true));

    private static Task<AttendanceStatus> StatusAsync(TestHost host, Guid employee, int day)
        => host.WithDbAsync(db => db.AttendanceDays.Where(d => d.EmployeeId == employee && d.Date == new DateOnly(2026, 3, day)).Select(d => d.Status).SingleAsync());

    [Fact]
    public async Task Sync_stores_mapped_punches_keeps_unmapped_and_recomputes_days()
    {
        var (host, gateway) = await CreateAsync();
        await using var _ = host;
        var e1 = await EmployeeTestData.CreateAsync(host, "E1");
        var e2 = await EmployeeTestData.CreateAsync(host, "E2");
        var device = gateway.Add("10.0.0.1");
        device.Log("1", At(10, 8, 58));
        device.Log("1", At(10, 18, 5), status: 1);
        device.Log("E2", At(10, 9, 40));
        device.Log("E2", At(10, 18, 0), status: 1);
        device.Log("99", At(10, 9, 0));
        var id = await AddDeviceAsync(host);

        var summary = await host.SendOkAsync(new SyncDeviceCommand(id));

        summary.Should().BeEquivalentTo(new { Pulled = 5, Inserted = 4, Duplicates = 0, Unmapped = 1 });
        (await StatusAsync(host, e1, 10)).Should().Be(AttendanceStatus.Present);
        (await StatusAsync(host, e2, 10)).Should().Be(AttendanceStatus.Late);
        var punches = await host.WithDbAsync(db => db.AttendancePunches.AsNoTracking().Where(p => p.Source == PunchSource.Device).ToListAsync());
        punches.Should().HaveCount(4).And.OnlyContain(p => p.DeviceId == id && p.DedupKey.StartsWith("D:SN-TEST:", StringComparison.Ordinal));
        punches.Single(p => p.LocalTime == At(10, 18, 5)).Type.Should().Be(PunchType.Out);

        // Code and number matches are remembered as maps so the users page shows them.
        var maps = await host.WithDbAsync(db => db.DeviceUserMaps.AsNoTracking().ToListAsync());
        maps.Select(m => (m.DeviceUserId, m.EmployeeId)).Should().BeEquivalentTo([("1", e1), ("E2", e2)]);

        var saved = await host.WithDbAsync(db => db.Devices.AsNoTracking().SingleAsync());
        saved.Status.Should().Be(DeviceStatus.Online);
        saved.SerialNumber.Should().Be("SN-TEST");
        saved.LastSyncLocal.Should().Be(At(10, 18, 5));
        var log = (await host.SendOkAsync(new GetDeviceSyncLogQuery(id))).Single();
        log.Should().BeEquivalentTo(new { Pulled = 5, Inserted = 4, Unmapped = 1, Succeeded = true, Trigger = SyncTrigger.Manual });

        var unmapped = (await host.SendOkAsync(new GetUnmappedPunchesQuery())).Single();
        unmapped.DeviceUserId.Should().Be("99");
        (await host.SendOkAsync(new GetDevicesQuery())).Single().UnmappedPunches.Should().Be(1);
    }

    [Fact]
    public async Task Next_sync_overlaps_24_hours_and_skips_duplicates()
    {
        var (host, gateway) = await CreateAsync();
        await using var _ = host;
        await EmployeeTestData.CreateAsync(host, "E1");
        var device = gateway.Add("10.0.0.1");
        device.Log("1", At(12, 9));
        device.Log("1", At(12, 18));
        var id = await AddDeviceAsync(host);
        await host.SendOkAsync(new SyncDeviceCommand(id));

        device.Log("1", At(13, 9));
        var second = await host.SendOkAsync(new SyncDeviceCommand(id));

        device.PullsFrom.Should().Equal(null, At(11, 18));
        second.Should().BeEquivalentTo(new { Pulled = 3, Inserted = 1, Duplicates = 2, Unmapped = 0 });
        (await host.WithDbAsync(db => db.AttendancePunches.CountAsync())).Should().Be(3);
    }

    [Fact]
    public async Task Failure_mid_pull_keeps_stored_batches_and_marks_device_offline()
    {
        var (host, gateway) = await CreateAsync();
        await using var _ = host;
        var e1 = await EmployeeTestData.CreateAsync(host, "E1");
        var device = gateway.Add("10.0.0.1");
        var start = At(1, 0);
        for (var i = 0; i < 1200; i++)
        {
            device.Log("1", start.AddMinutes(i * 7));
        }

        device.FailAfterBatches = 1;
        var id = await AddDeviceAsync(host);

        var error = await host.SendFailAsync(new SyncDeviceCommand(id));

        error.Code.Should().Be(BridgeErrors.Unreachable);
        (await host.WithDbAsync(db => db.AttendancePunches.CountAsync(p => p.EmployeeId == e1))).Should().Be(500);
        var saved = await host.WithDbAsync(db => db.Devices.AsNoTracking().SingleAsync());
        saved.Status.Should().Be(DeviceStatus.Offline);
        saved.LastSyncLocal.Should().BeNull("a failed pull must start from the beginning again");
        var log = (await host.SendOkAsync(new GetDeviceSyncLogQuery(id))).Single();
        log.Succeeded.Should().BeFalse();
        log.Inserted.Should().Be(500);

        device.FailAfterBatches = null;
        var retry = await host.SendOkAsync(new SyncDeviceCommand(id));
        retry.Should().BeEquivalentTo(new { Inserted = 700, Duplicates = 500 });
    }

    [Fact]
    public async Task Night_shift_punch_after_midnight_belongs_to_the_start_day()
    {
        var (host, gateway) = await CreateAsync();
        await using var _ = host;
        var e1 = await EmployeeTestData.CreateAsync(host, "E1");
        var night = await host.SendOkAsync(new SaveShiftCommand(null, "Night", "N", new TimeOnly(22, 0), new TimeOnly(6, 0), 10, 0, 30, 210, 390, 0, false));
        await host.SendOkAsync(new AssignShiftCommand([e1], night, new DateOnly(2026, 3, 1), null));
        var device = gateway.Add("10.0.0.1");
        device.Log("1", At(10, 21, 55));
        var id = await AddDeviceAsync(host);
        await host.SendOkAsync(new SyncDeviceCommand(id));

        // The morning punch arrives in a later sync; the overlap re-reads the evening one as a duplicate.
        device.Log("1", At(11, 6, 2), status: 1);
        await host.SendOkAsync(new SyncDeviceCommand(id));

        (await StatusAsync(host, e1, 10)).Should().Be(AttendanceStatus.Present);
        var day = await host.SendOkAsync(new GetAttendanceDayQuery(e1, new DateOnly(2026, 3, 10)));
        day.LastOut.Should().Be(At(11, 6, 2));
    }

    [Fact]
    public async Task Device_in_another_time_zone_is_converted_to_company_time()
    {
        var (host, gateway) = await CreateAsync();
        await using var _ = host;
        var e1 = await EmployeeTestData.CreateAsync(host, "E1");
        var device = gateway.Add("10.0.0.1");
        device.Log("1", At(10, 8, 0)); // 08:00 in India = 08:30 in Dhaka
        var id = await host.SendOkAsync(new SaveDeviceCommand(null, "Kolkata", "10.0.0.1", 4370, null, 1, "India Standard Time", true));
        await host.SendOkAsync(new SyncDeviceCommand(id));

        var punch = await host.WithDbAsync(db => db.AttendancePunches.AsNoTracking().SingleAsync(p => p.EmployeeId == e1));
        punch.LocalTime.Should().Be(At(10, 8, 30));
        punch.PunchAtUtc.Should().Be(At(10, 2, 30));
    }

    [Fact]
    public async Task Linking_an_unmapped_user_moves_their_punches_and_future_ones()
    {
        var (host, gateway) = await CreateAsync();
        await using var _ = host;
        var e2 = await EmployeeTestData.CreateAsync(host, "E2");
        var device = gateway.Add("10.0.0.1");
        device.Users.Add(new DeviceUser { DeviceUserId = "77", Name = "Guard" });
        device.Log("77", At(10, 9));
        device.Log("77", At(10, 18));
        var id = await AddDeviceAsync(host);
        (await host.SendOkAsync(new SyncDeviceCommand(id))).Unmapped.Should().Be(2);
        (await host.WithDbAsync(db => db.AttendancePunches.CountAsync(p => p.EmployeeId == e2))).Should().Be(0);

        var moved = await host.SendOkAsync(new MapDeviceUserCommand(id, "77", e2, "Guard"));

        moved.Should().Be(2);
        (await StatusAsync(host, e2, 10)).Should().Be(AttendanceStatus.Present);
        (await host.SendOkAsync(new GetUnmappedPunchesQuery())).Should().BeEmpty();
        var users = await host.SendOkAsync(new GetDeviceUsersQuery(id));
        users.Single().Should().BeEquivalentTo(new { DeviceUserId = "77", EmployeeId = e2, Match = DeviceUserMatch.Mapped });

        device.Log("77", At(11, 9));
        (await host.SendOkAsync(new SyncDeviceCommand(id))).Inserted.Should().Be(1);
    }

    [Fact]
    public async Task Assigning_unmapped_punches_and_discarding_others()
    {
        var (host, gateway) = await CreateAsync();
        await using var _ = host;
        var e1 = await EmployeeTestData.CreateAsync(host, "E1");
        var device = gateway.Add("10.0.0.1");
        device.Log("A7", At(10, 9));
        device.Log("VISITOR", At(10, 11));
        var id = await AddDeviceAsync(host);
        await host.SendOkAsync(new SyncDeviceCommand(id));

        (await host.SendOkAsync(new AssignUnmappedPunchesCommand(id, "A7", e1, Remember: false))).Should().Be(1);
        (await host.SendOkAsync(new DiscardUnmappedPunchesCommand(id, "VISITOR"))).Should().Be(1);

        (await host.SendOkAsync(new GetUnmappedPunchesQuery())).Should().BeEmpty();
        (await host.WithDbAsync(db => db.DeviceUserMaps.CountAsync())).Should().Be(0);
        (await host.WithDbAsync(db => db.AttendancePunches.CountAsync(p => p.EmployeeId == e1))).Should().Be(1);
    }

    [Fact]
    public async Task Auto_map_links_users_by_code_including_users_only_seen_in_punches()
    {
        var (host, gateway) = await CreateAsync();
        await using var _ = host;
        var e1 = await EmployeeTestData.CreateAsync(host, "E1");
        var e2 = await EmployeeTestData.CreateAsync(host, "E0002");
        var device = gateway.Add("10.0.0.1");
        var id = await AddDeviceAsync(host);
        device.Log("5", At(10, 9));
        await host.SendOkAsync(new SyncDeviceCommand(id)); // "5" matches nobody yet

        await EmployeeTestData.CreateAsync(host, "E5");
        device.Users.Add(new DeviceUser { DeviceUserId = "1", Name = "One" });
        device.Users.Add(new DeviceUser { DeviceUserId = "2", Name = "Two" });
        device.Users.Add(new DeviceUser { DeviceUserId = "3", Name = "Three" });

        var result = await host.SendOkAsync(new AutoMapDeviceUsersCommand(id));

        result.Should().Be(new AutoMapResult(3, 1));
        var maps = await host.WithDbAsync(db => db.DeviceUserMaps.AsNoTracking().ToDictionaryAsync(m => m.DeviceUserId, m => m.EmployeeId));
        maps["1"].Should().Be(e1);
        maps["2"].Should().Be(e2);
        maps.Should().ContainKey("5").And.NotContainKey("3");
    }

    [Fact]
    public async Task Clock_drift_is_reported_and_fixed()
    {
        var (host, gateway) = await CreateAsync();
        await using var _ = host;
        var device = gateway.Add("10.0.0.1");
        device.ClockOffsetMinutes = -7;
        var id = await AddDeviceAsync(host);

        var summary = await host.SendOkAsync(new SyncDeviceCommand(id));
        summary.HasClockDrift.Should().BeTrue();
        summary.ClockOffsetSeconds.Should().Be(-420);
        (await host.SendOkAsync(new GetDeviceSyncLogQuery(id))).Single().Warning.Should().Contain("-7");
        (await host.SendOkAsync(new GetDevicesQuery())).Single().HasClockDrift.Should().BeTrue();

        (await host.SendOkAsync(new SetDeviceTimeCommand(id))).Should().Be(0);
        device.ClockOffsetMinutes.Should().Be(0);
        (await host.SendOkAsync(new GetDevicesQuery())).Single().HasClockDrift.Should().BeFalse();
    }

    [Fact]
    public async Task Comm_key_is_stored_encrypted_kept_on_edit_and_used_to_connect()
    {
        var (host, gateway) = await CreateAsync();
        await using var _ = host;
        gateway.Add("10.0.0.5", commKey: 4321);
        var id = await AddDeviceAsync(host, "10.0.0.5", 4321);

        var stored = await host.WithDbAsync(db => db.Devices.AsNoTracking().SingleAsync());
        stored.CommKeyProtected.Should().NotBeNullOrEmpty().And.NotContain("4321");
        (await host.SendOkAsync(new TestDeviceConnectionCommand(id, "10.0.0.5", 4370, null))).SerialNumber.Should().Be("SN-TEST");

        // Editing without a key keeps it; a wrong key is reported with its own code.
        await host.SendOkAsync(new SaveDeviceCommand(id, "Renamed", "10.0.0.5", 4370, null, 1, null, true));
        await host.SendOkAsync(new SyncDeviceCommand(id));
        (await host.SendFailAsync(new TestDeviceConnectionCommand(id, "10.0.0.5", 4370, 1))).Code.Should().Be(BridgeErrors.WrongCommKey);
        var audit = await host.WithDbAsync(db => db.AuditEntries.AsNoTracking().Where(a => a.Action.StartsWith("Device.")).ToListAsync());
        audit.Should().NotBeEmpty().And.OnlyContain(a => (a.AfterJson ?? string.Empty).Contains("4321") == false);
    }

    [Fact]
    public async Task Device_validation_and_duplicate_address()
    {
        var (host, _) = await CreateAsync();
        await using var __ = host;
        await AddDeviceAsync(host, "10.0.0.1");
        (await host.SendFailAsync(new SaveDeviceCommand(null, "Copy", "10.0.0.1", 4370, null, 1, null, true))).Code.Should().Be("Validation.Ip");
        (await host.SendFailAsync(new SaveDeviceCommand(null, "Bad", "not an ip!", 4370, null, 1, null, true))).Code.Should().Be("Validation.Ip");
        (await host.SendFailAsync(new SaveDeviceCommand(null, "Bad", "10.0.0.2", 0, null, 1, null, true))).Code.Should().Be("Validation.Port");
        (await host.SendFailAsync(new SaveDeviceCommand(null, "Bad", "10.0.0.2", 4370, 1_000_000, 1, null, true))).Code.Should().Be("Validation.CommKey");
        (await host.SendFailAsync(new SaveDeviceCommand(null, "Bad", "10.0.0.2", 4370, null, 1, "Mars/Olympus", true))).Code.Should().Be("Validation.TimeZoneId");
    }

    [Fact]
    public async Task Fingerprints_backed_up_from_one_device_are_restored_to_another()
    {
        var (host, gateway) = await CreateAsync();
        await using var _ = host;
        var e1 = await EmployeeTestData.CreateAsync(host, "E1");
        var e2 = await EmployeeTestData.CreateAsync(host, "E2");
        var source = gateway.Add("10.0.0.1", "SRC");
        var target = gateway.Add("10.0.0.2", "DST");
        source.Users.Add(new DeviceUser { DeviceUserId = "1", Name = "Person E1" });
        source.Templates["1"] = [new FingerTemplate { FingerIndex = 0, Data = "TPL-A" }, new FingerTemplate { FingerIndex = 5, Data = "TPL-B" }];
        var sourceId = await AddDeviceAsync(host, "10.0.0.1");
        var targetId = await AddDeviceAsync(host, "10.0.0.2");
        await host.SendOkAsync(new TestDeviceConnectionCommand(sourceId, "10.0.0.1", 4370, null)); // learns the serial
        await host.SendOkAsync(new MapDeviceUserCommand(sourceId, "1", e1));

        var backup = await host.SendOkAsync(new BackupFingerprintsCommand(sourceId));
        backup.Should().BeEquivalentTo(new { Employees = 1, Fingers = 2 });
        var rows = await host.WithDbAsync(db => db.FingerprintBackups.AsNoTracking().ToListAsync());
        rows.Should().HaveCount(2).And.OnlyContain(r => r.SourceDeviceSerial == "SRC" && !r.DataProtected.Contains("TPL"));

        var push = await host.SendOkAsync(new PushEmployeesToDeviceCommand(targetId, [e1, e2], RestoreFingerprints: true));

        push.Should().BeEquivalentTo(new { Pushed = 2, FingersRestored = 2 });
        target.Users.Select(u => (u.DeviceUserId, u.Name)).Should().BeEquivalentTo([("1", "Person E1"), ("2", "Person E2")]);
        target.Templates["1"].Select(t => t.Data).Should().Equal("TPL-A", "TPL-B");
        (await host.SendOkAsync(new GetDeviceUsersQuery(targetId))).Should().OnlyContain(u => u.Match == DeviceUserMatch.Mapped);

        await host.SendOkAsync(new DeleteDeviceUserCommand(targetId, "2"));
        target.Users.Should().ContainSingle();
        (await host.WithDbAsync(db => db.DeviceUserMaps.CountAsync(m => m.DeviceId == targetId))).Should().Be(1);
    }

    [Fact]
    public async Task Viewer_cannot_change_devices_but_auto_sync_still_runs()
    {
        var (host, gateway) = await CreateAsync();
        await using var _ = host;
        gateway.Add("10.0.0.1");
        var id = await AddDeviceAsync(host);
        await host.SignInAsAsync(Domain.Security.Role.Viewer);

        (await host.SendFailAsync(new SaveDeviceCommand(null, "X", "10.0.0.9", 4370, null, 1, null, true))).Code.Should().Be("Auth.Forbidden");
        (await host.SendOkAsync(new SyncDeviceCommand(id, SyncTrigger.Automatic))).Pulled.Should().Be(0);
    }
}

public class AttendanceFileImportTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Attendance", name);

    [Fact]
    public async Task Device_export_preview_then_import_and_later_sync_finds_duplicates()
    {
        await using var host = await TestHost.CreateAsync(configure: s => s.AddSingleton<IDeviceGateway>(sp => new FakeDeviceGateway((FakeClock)sp.GetRequiredService<IClock>())));
        var gateway = (FakeDeviceGateway)host.Services.GetRequiredService<IDeviceGateway>();
        var e1 = await EmployeeTestData.CreateAsync(host, "E1");
        await EmployeeTestData.CreateAsync(host, "E2");
        var device = gateway.Add("10.0.0.1", "SN-USB");
        var id = await host.SendOkAsync(new SaveDeviceCommand(null, "Gate", "10.0.0.1", 4370, null, 1, null, true));
        await host.SendOkAsync(new TestDeviceConnectionCommand(id, "10.0.0.1", 4370, null)); // learns the serial

        var preview = await host.SendOkAsync(new PreviewAttendanceFileQuery(Fixture("sample_attlog.dat"), id));
        preview.Should().BeEquivalentTo(new { Valid = 4, Duplicates = 1, Unmapped = 1, Invalid = 1 }, o => o.ExcludingMissingMembers());
        preview.UnmappedIds.Should().Equal("77");
        preview.First.Should().Be(new DateOnly(2026, 3, 10));

        var imported = await host.SendOkAsync(new ImportAttendanceFileCommand(Fixture("sample_attlog.dat"), id));
        imported.Should().BeEquivalentTo(new { Inserted = 4, Duplicates = 1, Unmapped = 1 }, o => o.ExcludingMissingMembers());
        (await host.WithDbAsync(db => db.AttendancePunches.CountAsync(p => p.Source == PunchSource.Import && p.EmployeeId == e1))).Should().Be(2);
        (await host.SendOkAsync(new GetDeviceSyncLogQuery(null))).Single().Trigger.Should().Be(SyncTrigger.FileImport);

        // The same punches arriving later over the network are recognised.
        device.Log("1", new DateTime(2026, 3, 10, 8, 58, 12));
        device.Log("1", new DateTime(2026, 3, 10, 18, 3, 40));
        var sync = await host.SendOkAsync(new SyncDeviceCommand(id));
        sync.Should().BeEquivalentTo(new { Inserted = 0, Duplicates = 2 }, o => o.ExcludingMissingMembers());
    }

    [Fact]
    public async Task Csv_import_maps_by_employee_code_and_reports_bad_rows()
    {
        await using var host = await TestHost.CreateAsync();
        var e1 = await EmployeeTestData.CreateAsync(host, "E1");

        var preview = await host.SendOkAsync(new PreviewAttendanceFileQuery(Fixture("punches.csv"), null));
        preview.Should().BeEquivalentTo(new { Valid = 2, Unmapped = 1, Invalid = 1 }, o => o.ExcludingMissingMembers());
        preview.Errors.Single().Should().Contain("Row 5");

        await host.SendOkAsync(new ImportAttendanceFileCommand(Fixture("punches.csv"), null));
        var day = await host.SendOkAsync(new GetAttendanceDayQuery(e1, new DateOnly(2026, 3, 11)));
        day.Status.Should().Be(AttendanceStatus.Present);
        (await host.SendOkAsync(new GetUnmappedPunchesQuery())).Single().Should().BeEquivalentTo(new { DeviceId = (Guid?)null, DeviceUserId = "E9" }, o => o.ExcludingMissingMembers());

        // Importing the same file twice adds nothing.
        (await host.SendOkAsync(new ImportAttendanceFileCommand(Fixture("punches.csv"), null))).Inserted.Should().Be(0);
    }

    [Fact]
    public async Task Unreadable_file_is_rejected()
    {
        await using var host = await TestHost.CreateAsync();
        var path = Path.Combine(host.Root, "junk.dat");
        await File.WriteAllTextAsync(path, "nothing useful here\nreally\n");
        (await host.SendFailAsync(new PreviewAttendanceFileQuery(path, null))).Code.Should().Be("Validation.Path");
        (await host.SendFailAsync(new ImportAttendanceFileCommand(Path.Combine(host.Root, "missing.csv"), null))).Code.Should().Be("Validation.Path");
    }

    [Theory]
    [InlineData("2026-03-10 08:58:12", 2026, 3, 10, 8, 58)]
    [InlineData("2026/03/10 08:58", 2026, 3, 10, 8, 58)]
    [InlineData("10/03/2026 08:58", 2026, 3, 10, 8, 58)]
    [InlineData("46091.5", 2026, 3, 10, 12, 0)]
    public void Punch_times_parse_in_common_formats(string text, int y, int m, int d, int h, int min)
    {
        PunchFileParser.TryParseTime(text, out var time).Should().BeTrue();
        time.Should().Be(new DateTime(y, m, d, h, min, time.Second));
    }
}

public class DeviceSyncScheduleTests
{
    private static readonly DateTime T0 = new(2026, 3, 15, 4, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void New_devices_are_due_at_once_and_then_every_interval()
    {
        var schedule = new DeviceSyncSchedule();
        var a = Guid.NewGuid();
        schedule.Due([a], T0).Should().Equal(a);
        schedule.Record(a, succeeded: true, T0, TimeSpan.FromMinutes(15));
        schedule.Due([a], T0.AddMinutes(14)).Should().BeEmpty();
        schedule.Due([a], T0.AddMinutes(15)).Should().Equal(a);
    }

    [Fact]
    public void Failures_back_off_1_5_15_minutes_and_success_resets()
    {
        var schedule = new DeviceSyncSchedule();
        var a = Guid.NewGuid();
        var interval = TimeSpan.FromMinutes(30);
        var now = T0;
        var waits = new List<double>();
        for (var i = 0; i < 4; i++)
        {
            schedule.Record(a, succeeded: false, now, interval).Should().Be(i + 1);
            var next = schedule.Get(a)!.NextDueUtc;
            waits.Add((next - now).TotalMinutes);
            schedule.Due([a], next.AddSeconds(-1)).Should().BeEmpty();
            schedule.Due([a], next).Should().Equal(a);
            now = next;
        }

        waits.Should().Equal(1, 5, 15, 15);
        schedule.Record(a, succeeded: true, now, interval).Should().Be(0);
        (schedule.Get(a)!.NextDueUtc - now).Should().Be(interval);
    }

    [Fact]
    public void Backoff_never_exceeds_a_short_interval_and_removed_devices_are_forgotten()
    {
        var schedule = new DeviceSyncSchedule();
        var a = Guid.NewGuid();
        schedule.Record(a, false, T0, TimeSpan.FromMinutes(2));
        schedule.Record(a, false, T0, TimeSpan.FromMinutes(2));
        (schedule.Get(a)!.NextDueUtc - T0).Should().Be(TimeSpan.FromMinutes(2));

        schedule.Due([], T0);
        schedule.Get(a).Should().BeNull();
    }
}
