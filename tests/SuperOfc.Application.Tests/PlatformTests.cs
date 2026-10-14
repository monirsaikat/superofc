using Microsoft.EntityFrameworkCore;
using SuperOfc.Application.Auth;
using SuperOfc.Application.Backups;
using SuperOfc.Application.Common;
using SuperOfc.Application.Policies;
using SuperOfc.Application.Setup;
using SuperOfc.Application.Tests.Support;
using SuperOfc.Application.Users;
using SuperOfc.Domain.Organization;
using SuperOfc.Domain.Policies;
using SuperOfc.Domain.Security;

namespace SuperOfc.Application.Tests;

public class SetupTests
{
    [Fact]
    public async Task Fresh_database_reports_setup_needed_then_complete()
    {
        await using var host = await TestHost.CreateAsync(completeSetup: false);

        (await host.SendOkAsync(new GetSetupStatusQuery())).IsSetupComplete.Should().BeFalse();
        await host.CompleteSetupAsync();
        var status = await host.SendOkAsync(new GetSetupStatusQuery());

        status.IsSetupComplete.Should().BeTrue();
        status.CompanyName.Should().Be("Test Co");
    }

    [Fact]
    public async Task Setup_cannot_run_twice()
    {
        await using var host = await TestHost.CreateAsync();

        var error = await host.SendFailAsync(new CompleteSetupCommand(
            "Other", "USD", "$", NumberGrouping.Western, "UTC", 1, "yyyy-MM-dd", WorkingWeek.Default, "x", "X", "Password1", false));

        error.Code.Should().Be("Setup.AlreadyDone");
    }

    [Fact]
    public async Task Setup_validates_inputs()
    {
        await using var host = await TestHost.CreateAsync(completeSetup: false);

        var result = await host.Dispatcher.SendAsync(new CompleteSetupCommand(
            string.Empty, "TAKA", "Tk", NumberGrouping.Western, "Mars/Olympus", 13, "dd MMM yyyy", WorkingWeek.Default, "a b", "A", "short", false));

        result.Errors.Select(e => e.Code).Should().Contain(["Validation.Name", "Validation.CurrencyCode", "Validation.TimeZoneId", "Validation.FiscalYearStartMonth"]);
        result.Errors.Should().Contain(e => e.Code.StartsWith("Validation.AdminPassword", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Company_update_is_audited_and_refreshes_context()
    {
        await using var host = await TestHost.CreateAsync();
        var company = await host.SendOkAsync(new GetCompanyQuery());

        await host.SendOkAsync(new UpdateCompanyCommand(
            "Renamed Co", "Dhaka", null, "hr@example.com", null, null, company.CurrencyCode, company.CurrencySymbol, company.Grouping,
            company.TimeZoneId, company.FiscalYearStartMonth, company.DateFormat, company.WorkingWeek));

        (await host.SendOkAsync(new GetCompanyQuery())).Name.Should().Be("Renamed Co");
        var audits = await host.WithDbAsync(db => db.AuditEntries.Where(a => a.Action == "Company.Updated").ToListAsync());
        audits.Should().ContainSingle().Which.BeforeJson.Should().Contain("Test Co");
    }
}

public class AuthTests
{
    [Fact]
    public async Task Login_succeeds_with_correct_password_and_is_audited()
    {
        await using var host = await TestHost.CreateAsync();
        host.Session.SignOut();

        var user = await host.SendOkAsync(new LoginCommand("ADMIN", TestHost.AdminPassword));

        user.Role.Should().Be(Role.Admin);
        host.Session.IsAuthenticated.Should().BeTrue();
        (await host.WithDbAsync(db => db.AuditEntries.CountAsync(a => a.Action == "Auth.Login"))).Should().Be(1);
    }

    [Fact]
    public async Task Five_failed_logins_lock_the_account_for_ten_minutes()
    {
        await using var host = await TestHost.CreateAsync();
        host.Session.SignOut();

        for (var i = 0; i < 4; i++)
        {
            (await host.SendFailAsync(new LoginCommand("admin", "wrong"))).Code.Should().Be("Auth.InvalidCredentials");
        }

        (await host.SendFailAsync(new LoginCommand("admin", "wrong"))).Code.Should().Be("Auth.LockedOut");
        (await host.SendFailAsync(new LoginCommand("admin", TestHost.AdminPassword))).Code.Should().Be("Auth.LockedOut");

        host.Clock.Advance(TimeSpan.FromMinutes(11));
        await host.SendOkAsync(new LoginCommand("admin", TestHost.AdminPassword));
        (await host.WithDbAsync(db => db.AuditEntries.CountAsync(a => a.Action == "Auth.LoginFailed"))).Should().Be(5);
    }

    [Fact]
    public async Task Passwords_are_hashed_never_stored_plain()
    {
        await using var host = await TestHost.CreateAsync();

        var hash = await host.WithDbAsync(db => db.Users.Where(u => u.Username == "admin").Select(u => u.PasswordHash).SingleAsync());

        hash.Should().NotContain(TestHost.AdminPassword);
        hash.Length.Should().BeGreaterThan(40);
    }

    [Fact]
    public async Task Requests_need_sign_in()
    {
        await using var host = await TestHost.CreateAsync();
        host.Session.SignOut();

        (await host.SendFailAsync(new GetCompanyQuery())).Code.Should().Be("Auth.NotSignedIn");
    }
}

public class UserAndPermissionTests
{
    [Fact]
    public async Task Viewer_cannot_execute_mutating_commands()
    {
        await using var host = await TestHost.CreateAsync();
        await host.SignInAsAsync(Role.Viewer);

        (await host.SendFailAsync(new CreateUserCommand("bob", "Bob", "Password1", Role.HR))).Should().Be(SuperOfc.Domain.Common.Error.Forbidden);
        (await host.SendFailAsync(new CreateBackupCommand(null))).Should().Be(SuperOfc.Domain.Common.Error.Forbidden);
        (await host.SendOkAsync(new GetCompanyQuery())).Name.Should().Be("Test Co");
    }

    [Fact]
    public async Task Admin_manages_users_and_last_admin_is_protected()
    {
        await using var host = await TestHost.CreateAsync();

        var id = await host.SendOkAsync(new CreateUserCommand("hr.user", "HR User", "Password1", Role.HR));
        (await host.SendFailAsync(new CreateUserCommand("HR.USER", "Dup", "Password1", Role.HR))).Code.Should().Be("User.DuplicateUsername");
        await host.SendOkAsync(new UpdateUserCommand(id, "HR Lead", Role.Admin, true));

        var users = await host.SendOkAsync(new GetUsersQuery());
        var admin = users.Single(u => u.Username == "admin");
        await host.SendOkAsync(new UpdateUserCommand(id, "HR Lead", Role.HR, true));
        (await host.SendFailAsync(new UpdateUserCommand(admin.Id, "Admin", Role.HR, true))).Code.Should().Be("User.LastAdmin");
    }

    [Fact]
    public async Task Every_command_declares_its_permission()
    {
        var commands = DependencyInjection.RequestTypes()
            .Where(t => t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICommand<>)));

        var missing = commands.Where(t =>
            !t.IsDefined(typeof(RequiresPermissionAttribute), false) && !t.IsDefined(typeof(AllowAnonymousAttribute), false)).ToList();

        missing.Should().BeEmpty("every mutating request must state the permission it needs");
        await Task.CompletedTask;
    }
}

public class PolicyTests
{
    [Fact]
    public async Task Policy_versions_apply_from_their_effective_date()
    {
        await using var host = await TestHost.CreateAsync();
        var from = new DateOnly(2026, 4, 1);

        await host.SendOkAsync(new SavePolicyCommand(PolicySnapshot.PayrollSection, from, new PayrollPolicy { OvertimeMultiplier = 2m }, "Raise OT"));

        (await host.SendOkAsync(new GetPolicySnapshotQuery(new DateOnly(2026, 3, 31)))).Payroll.OvertimeMultiplier.Should().Be(1.5m);
        (await host.SendOkAsync(new GetPolicySnapshotQuery(from))).Payroll.OvertimeMultiplier.Should().Be(2m);
        (await host.SendOkAsync(new GetPolicyHistoryQuery(PolicySnapshot.PayrollSection))).Should().ContainSingle();
    }

    [Fact]
    public async Task Wrong_policy_type_is_rejected()
    {
        await using var host = await TestHost.CreateAsync();

        var error = await host.SendFailAsync(new SavePolicyCommand(PolicySnapshot.PayrollSection, new DateOnly(2026, 1, 1), new LoanPolicy(), null));

        error.Code.Should().StartWith("Validation");
    }
}

public class BackupTests
{
    [Fact]
    public async Task Manual_backup_is_verified_recorded_and_restorable()
    {
        await using var host = await TestHost.CreateAsync();

        var backup = await host.SendOkAsync(new CreateBackupCommand("before test"));
        File.Exists(backup.FilePath).Should().BeTrue();
        backup.Verified.Should().BeTrue();

        await host.SendOkAsync(new CreateUserCommand("later", "Later User", "Password1", Role.Viewer));
        (await host.SendOkAsync(new GetUsersQuery())).Should().Contain(u => u.Username == "later");

        await host.SendOkAsync(new RestoreBackupCommand(backup.FilePath));

        var usernames = await host.WithDbAsync(db => db.Users.Select(u => u.Username).ToListAsync());
        usernames.Should().NotContain("later");
        var types = await host.WithDbAsync(db => db.BackupRecords.Select(b => b.Type).ToListAsync());
        types.Should().Contain(SuperOfc.Domain.Platform.BackupType.Manual);
    }

    [Fact]
    public async Task Restore_rejects_non_backup_files()
    {
        await using var host = await TestHost.CreateAsync();
        var bogus = Path.Combine(host.Root, "bogus.db");
        await File.WriteAllTextAsync(bogus, "not a database");

        (await host.SendFailAsync(new RestoreBackupCommand(bogus))).Code.Should().Be("Backup.Invalid");
    }
}
