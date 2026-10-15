using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Common;
using SuperOfc.Application.Tests.Support;
using SuperOfc.Domain.Security;

namespace SuperOfc.Application.Tests;

/// <summary>Security pass (P8-T2): permissions on every request, secrets kept out of logs, file paths kept inside the data folder.</summary>
public partial class SecurityTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SuperOfc.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    [Fact]
    public void Only_sign_in_and_first_run_requests_work_without_signing_in()
    {
        var anonymous = DependencyInjection.RequestTypes().Where(t => t.IsDefined(typeof(AllowAnonymousAttribute), false)).Select(t => t.Name).Order(StringComparer.Ordinal);
        anonymous.Should().Equal("CompleteSetupCommand", "GetSetupStatusQuery", "LoginCommand", "LogoutCommand", "ResumeSignInCommand", "UnlockCommand");
    }

    [Fact]
    public void Requests_about_pay_need_more_than_view_data()
    {
        // Payroll lock dates are shown on attendance and leave pages, so they are deliberately visible to everyone.
        string[] allowed = ["GetPayrollLockQuery"];
        var weak = DependencyInjection.RequestTypes()
            .Where(t => PayWords().IsMatch(t.Name) && !allowed.Contains(t.Name))
            .Where(t => (t.GetCustomAttribute<RequiresPermissionAttribute>()?.Permission ?? Permission.ViewData) == Permission.ViewData)
            .Select(t => t.Name).ToList();
        weak.Should().BeEmpty("salary, payroll, loan and tax data is only for roles with ViewSalaries");
    }

    [Fact]
    public async Task Viewers_cannot_change_anything()
    {
        await using var host = await TestHost.CreateAsync();
        await host.SignInAsAsync(Role.Viewer);
        var commands = DependencyInjection.RequestTypes()
            .Where(t => t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICommand<>)))
            .Where(t => !t.IsDefined(typeof(AllowAnonymousAttribute), false))
            .ToList();
        var viewerCan = commands.Where(t => PermissionMatrix.Has(Role.Viewer, t.GetCustomAttribute<RequiresPermissionAttribute>()!.Permission)).Select(t => t.Name).ToList();

        // A viewer may change their own password, export what they can see and save diagnostics for support. Device sync only adds punches from the
        // terminals and runs automatically as whoever is signed in (DeviceSync.cs), so it is allowed too. Nothing else.
        viewerCan.Should().BeSubsetOf(["ChangeOwnPasswordCommand", "ExportReportCommand", "ExportEmployeesCommand", "ExportAttendanceMonthCommand", "SyncDeviceCommand", "ExportDiagnosticsCommand"]);
    }

    [Fact]
    public void Log_calls_never_include_passwords_keys_or_templates()
    {
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                // The whole log statement, which may wrap over a few lines, up to its semicolon.
                if (!LogCall().IsMatch(lines[i]))
                {
                    continue;
                }

                var call = lines[i];
                for (var j = i + 1; !call.TrimEnd().EndsWith(';') && j < lines.Length && j < i + 6; j++)
                {
                    call += " " + lines[j];
                }
                if (Secret().IsMatch(call))
                {
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        offenders.Should().BeEmpty("secrets must never reach the log files (doc 05, doc 06)");
    }

    [Theory]
    [InlineData(@"..\..\outside.txt")]
    [InlineData(@"employees\..\..\outside.txt")]
    [InlineData(@"C:\Windows\win.ini")]
    [InlineData(@"\\server\share\file.txt")]
    public async Task File_storage_refuses_paths_outside_the_data_folder(string path)
    {
        await using var host = await TestHost.CreateAsync();
        var storage = host.Services.GetRequiredService<IFileStorage>();
        var act = () => storage.GetFullPath(path);
        act.Should().Throw<UnauthorizedAccessException>();
        var open = () => storage.OpenRead(path);
        open.Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task Saved_files_stay_inside_the_data_folder_whatever_the_name()
    {
        await using var host = await TestHost.CreateAsync();
        var storage = host.Services.GetRequiredService<IFileStorage>();
        using var content = new MemoryStream([1, 2, 3]);
        var relative = await storage.SaveAsync(@"..\..", Guid.NewGuid(), @"..\..\evil.exe", content);
        relative.Should().NotContain("..");
        Path.GetFullPath(storage.GetFullPath(relative)).Should().StartWith(Path.GetFullPath(Path.Combine(host.Root, "files")));
    }

    [GeneratedRegex(@"Salary|Payroll|Payslip|Loan|Tax|Bank|Cash|Register")]
    private static partial Regex PayWords();

    [GeneratedRegex(@"\b(_?logger|Log)\.(Log\w*|Write|Information|Warning|Error|Debug|Fatal)\(|\blog\.(Information|Warning|Error|Debug)\(")]
    private static partial Regex LogCall();

    [GeneratedRegex(@"Password|CommKey|Secret|Template|PasswordHash|\bKey\b", RegexOptions.IgnoreCase)]
    private static partial Regex Secret();
}
