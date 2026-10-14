using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Audit;
using SuperOfc.Application.Employees;
using SuperOfc.Application.Organization;
using SuperOfc.Application.Tests.Support;
using SuperOfc.Domain.Employees;
using SuperOfc.Domain.Organization;
using SuperOfc.Domain.Security;

namespace SuperOfc.Application.Tests;

public static class EmployeeTestData
{
    public static EmployeeInput Input(string code, string name = "Test Person", DateOnly? join = null, Guid? department = null) => new(
        code, name, Gender.Male, new DateOnly(1990, 1, 1), "01700000000", null, null, null, null, join ?? new DateOnly(2025, 1, 1),
        department, null, null, null, PaymentMode.Bank, "Bank", null, "1234567890", "TIN-1", true, null);

    public static async Task<Guid> CreateAsync(TestHost host, string code, DateOnly? join = null, Guid? department = null)
        => await host.SendOkAsync(new CreateEmployeeCommand(Input(code, $"Person {code}", join, department)));
}

public class MasterDataTests
{
    [Fact]
    public async Task Create_rename_and_soft_delete_with_unique_names()
    {
        await using var host = await TestHost.CreateAsync();
        var id = await host.SendOkAsync(new SaveMasterDataCommand(MasterDataKind.Department, null, "Accounts", "ACC", null, 0));

        (await host.SendFailAsync(new SaveMasterDataCommand(MasterDataKind.Department, null, " accounts ", null, null, 0))).Code.Should().Be("Validation.Name");
        await host.SendOkAsync(new SaveMasterDataCommand(MasterDataKind.Designation, null, "Accounts", null, null, 0));
        await host.SendOkAsync(new SaveMasterDataCommand(MasterDataKind.Department, id, "Finance", "FIN", null, 0));
        await EmployeeTestData.CreateAsync(host, "E1", department: id);

        var list = await host.SendOkAsync(new GetMasterDataQuery(MasterDataKind.Department));
        list.Should().ContainSingle(d => d.Name == "Finance" && d.EmployeeCount == 1);

        await host.SendOkAsync(new DeleteMasterDataCommand(MasterDataKind.Department, id));
        (await host.SendOkAsync(new GetMasterDataQuery(MasterDataKind.Department))).Should().BeEmpty();
        (await host.SendOkAsync(new GetOrganizationLookupsQuery())).Departments.Should().BeEmpty();
    }

    [Fact]
    public async Task Viewer_cannot_change_master_data()
    {
        await using var host = await TestHost.CreateAsync();
        await host.SignInAsAsync(Role.Viewer);

        (await host.SendFailAsync(new SaveMasterDataCommand(MasterDataKind.Location, null, "X", null, null, 0))).Code.Should().Be("Auth.Forbidden");
    }
}

public class HolidayTests
{
    [Fact]
    public async Task Holidays_cannot_clash_and_copy_moves_to_next_year()
    {
        await using var host = await TestHost.CreateAsync();
        await host.SendOkAsync(new SaveHolidayCommand(null, new DateOnly(2025, 12, 16), "Victory Day", true, null));
        await host.SendOkAsync(new SaveHolidayCommand(null, new DateOnly(2025, 3, 26), "Independence Day", true, null));
        (await host.SendFailAsync(new SaveHolidayCommand(null, new DateOnly(2025, 3, 26), "Dup", true, null))).Code.Should().Be("Validation.Date");

        (await host.SendOkAsync(new CopyHolidaysFromPreviousYearCommand(2026))).Should().Be(2);
        (await host.SendOkAsync(new CopyHolidaysFromPreviousYearCommand(2026))).Should().Be(0);

        var holidays = await host.SendOkAsync(new GetHolidaysQuery(2026));
        holidays.Select(h => h.Date).Should().Equal(new DateOnly(2026, 3, 26), new DateOnly(2026, 12, 16));
    }
}

public class EmployeeCrudTests
{
    [Fact]
    public async Task Create_records_joined_event_and_rejects_duplicate_code()
    {
        await using var host = await TestHost.CreateAsync();
        var id = await EmployeeTestData.CreateAsync(host, "E0001");

        (await host.SendFailAsync(new CreateEmployeeCommand(EmployeeTestData.Input("e0001")))).Code.Should().Be("Validation.Code");
        var history = await host.SendOkAsync(new GetEmploymentHistoryQuery(id));
        history.Should().ContainSingle(h => h.Type == EmploymentEventType.Joined);
        (await host.SendOkAsync(new GetEmployeeQuery(id))).Status.Should().Be(EmployeeStatus.Probation);
    }

    [Fact]
    public async Task Validation_errors_map_to_field_names()
    {
        await using var host = await TestHost.CreateAsync();

        var result = await host.Dispatcher.SendAsync(new CreateEmployeeCommand(EmployeeTestData.Input(string.Empty, string.Empty) with { Email = "not-an-email" }));

        result.Errors.Select(e => e.Code).Should().Contain(["Validation.Code", "Validation.FullName", "Validation.Email"]);
    }

    [Fact]
    public async Task Update_detects_concurrent_edits()
    {
        await using var host = await TestHost.CreateAsync();
        var id = await EmployeeTestData.CreateAsync(host, "E0001");
        var detail = await host.SendOkAsync(new GetEmployeeQuery(id));

        await host.SendOkAsync(new UpdateEmployeeCommand(id, EmployeeTestData.Input("E0001", "First Edit"), detail.RowVersion));
        (await host.SendFailAsync(new UpdateEmployeeCommand(id, EmployeeTestData.Input("E0001", "Stale Edit"), detail.RowVersion))).Code.Should().Be("Concurrency.Conflict");
        (await host.SendOkAsync(new GetEmployeeQuery(id))).FullName.Should().Be("First Edit");
    }

    [Fact]
    public async Task Viewer_sees_masked_bank_account()
    {
        await using var host = await TestHost.CreateAsync();
        var id = await EmployeeTestData.CreateAsync(host, "E0001");
        await host.SignInAsAsync(Role.Viewer);

        var detail = await host.SendOkAsync(new GetEmployeeQuery(id));

        detail.BankAccount.Should().Be("••••••7890");
    }

    [Fact]
    public async Task Lifecycle_confirm_transfer_exit_rejoin()
    {
        await using var host = await TestHost.CreateAsync();
        var sales = await host.SendOkAsync(new SaveMasterDataCommand(MasterDataKind.Department, null, "Sales", null, null, 0));
        var it = await host.SendOkAsync(new SaveMasterDataCommand(MasterDataKind.Department, null, "IT", null, null, 0));
        var id = await EmployeeTestData.CreateAsync(host, "E0001", department: sales);

        await host.SendOkAsync(new RecordEmploymentEventCommand(id, EmploymentAction.Confirm, new DateOnly(2025, 7, 1), null));
        await host.SendOkAsync(new RecordEmploymentEventCommand(id, EmploymentAction.Transfer, new DateOnly(2025, 9, 1), "Restructure", DepartmentId: it));
        (await host.SendFailAsync(new RecordEmploymentEventCommand(id, EmploymentAction.Transfer, new DateOnly(2025, 9, 2), null, DepartmentId: it))).Code.Should().Be("Validation.DepartmentId");
        (await host.SendFailAsync(new RecordEmploymentEventCommand(id, EmploymentAction.Exit, new DateOnly(2026, 1, 31), null))).Code.Should().Be("Validation.Notes");
        await host.SendOkAsync(new RecordEmploymentEventCommand(id, EmploymentAction.Exit, new DateOnly(2026, 1, 31), "Resigned"));
        (await host.SendFailAsync(new RecordEmploymentEventCommand(id, EmploymentAction.Confirm, new DateOnly(2026, 2, 1), null))).Code.Should().Be("Employee.HasLeft");
        await host.SendOkAsync(new RecordEmploymentEventCommand(id, EmploymentAction.Rejoin, new DateOnly(2026, 3, 1), null));

        var detail = await host.SendOkAsync(new GetEmployeeQuery(id));
        detail.DepartmentId.Should().Be(it);
        detail.Status.Should().Be(EmployeeStatus.Probation);
        var history = await host.SendOkAsync(new GetEmploymentHistoryQuery(id));
        history.Select(h => h.Type).Should().Equal(EmploymentEventType.Rejoined, EmploymentEventType.Exited, EmploymentEventType.Transferred, EmploymentEventType.Confirmed, EmploymentEventType.Joined);
        history.Single(h => h.Type == EmploymentEventType.Transferred).Description.Should().Contain("Sales → IT");
    }

    [Fact]
    public async Task Next_code_follows_highest_number()
    {
        await using var host = await TestHost.CreateAsync();
        await EmployeeTestData.CreateAsync(host, "E0007");
        await EmployeeTestData.CreateAsync(host, "E0002");
        await EmployeeTestData.CreateAsync(host, "X99");

        (await host.SendOkAsync(new GetNextEmployeeCodeQuery())).Should().Be("E0008");
    }

    [Fact]
    public async Task Photo_and_documents_are_stored_under_files_folder()
    {
        await using var host = await TestHost.CreateAsync();
        var id = await EmployeeTestData.CreateAsync(host, "E0001");
        var source = Path.Combine(host.Root, "contract.pdf");
        await File.WriteAllTextAsync(source, "pdf");

        var docId = await host.SendOkAsync(new AddEmployeeDocumentCommand(id, "Contract", "Contract", source, new DateOnly(2026, 4, 1), null));
        var docs = await host.SendOkAsync(new GetEmployeeDocumentsQuery(id));
        var storage = host.Services.GetRequiredService<IFileStorage>();
        File.Exists(storage.GetFullPath(docs.Single().FilePath)).Should().BeTrue();

        var expiring = await host.SendOkAsync(new GetExpiringDocumentsQuery(new DateOnly(2026, 3, 15), 30));
        expiring.Should().ContainSingle(d => d.Title == "Contract");

        await host.SendOkAsync(new DeleteEmployeeDocumentCommand(docId));
        File.Exists(storage.GetFullPath(docs.Single().FilePath)).Should().BeFalse();
        (await host.SendFailAsync(new SetEmployeePhotoCommand(id, source))).Code.Should().Be("Validation.Photo");
    }

    [Fact]
    public async Task File_storage_rejects_paths_outside_data_folder()
    {
        await using var host = await TestHost.CreateAsync();
        var storage = host.Services.GetRequiredService<IFileStorage>();

        var act = () => storage.GetFullPath(@"..\..\Windows\system.ini");

        act.Should().Throw<UnauthorizedAccessException>();
    }
}

public class EmployeeListTests
{
    [Fact]
    public async Task List_searches_filters_sorts_and_pages()
    {
        await using var host = await TestHost.CreateAsync();
        var sales = await host.SendOkAsync(new SaveMasterDataCommand(MasterDataKind.Department, null, "Sales", null, null, 0));
        for (var i = 1; i <= 25; i++)
        {
            await EmployeeTestData.CreateAsync(host, $"E{i:000}", department: i % 2 == 0 ? sales : null);
        }

        var exited = (await host.SendOkAsync(new GetEmployeeListQuery(Search: "E025"))).Items.Single().Id;
        await host.SendOkAsync(new RecordEmploymentEventCommand(exited, EmploymentAction.Exit, new DateOnly(2026, 1, 1), "Left"));

        var page = await host.SendOkAsync(new GetEmployeeListQuery(PageSize: 10, PageIndex: 1));
        page.TotalCount.Should().Be(24);
        page.Items.Should().HaveCount(10);
        page.Items[0].Code.Should().Be("E011");

        (await host.SendOkAsync(new GetEmployeeListQuery(DepartmentId: sales))).TotalCount.Should().Be(12);
        (await host.SendOkAsync(new GetEmployeeListQuery(Search: "person e01"))).TotalCount.Should().Be(10);
        (await host.SendOkAsync(new GetEmployeeListQuery(IncludeLeft: true))).TotalCount.Should().Be(25);
        (await host.SendOkAsync(new GetEmployeeListQuery(Status: EmployeeStatus.Left))).Items.Single().Code.Should().Be("E025");
        (await host.SendOkAsync(new GetEmployeeListQuery(Sort: EmployeeSort.Code, Descending: true))).Items[0].Code.Should().Be("E024");
        (await host.SendOkAsync(new GetEmployeeListQuery(Sort: EmployeeSort.Department))).Items.Last().Department.Should().Be("Sales");
    }

    [Fact]
    public async Task First_page_of_ten_thousand_employees_loads_fast()
    {
        await using var host = await TestHost.CreateAsync();
        await host.WithDbAsync(async db =>
        {
            for (var i = 0; i < 10_000; i++)
            {
                db.Employees.Add(new Employee { Id = Guid.CreateVersion7(), CompanyId = host.CompanyId, Code = $"B{i:00000}", FullName = $"Bulk {i}", JoinDate = new DateOnly(2024, 1, 1), Status = EmployeeStatus.Active });
            }

            return await db.SaveChangesAsync();
        });

        await host.SendOkAsync(new GetEmployeeListQuery(PageSize: 200));
        var sw = Stopwatch.StartNew();
        var page = await host.SendOkAsync(new GetEmployeeListQuery(PageSize: 200));
        sw.Stop();

        page.TotalCount.Should().Be(10_000);
        sw.ElapsedMilliseconds.Should().BeLessThan(150);
    }
}

public class ImportTests
{
    [Fact]
    public async Task Template_round_trip_preview_and_commit()
    {
        await using var host = await TestHost.CreateAsync();
        var path = Path.Combine(host.Root, "template.xlsx");
        await host.SendOkAsync(new WriteEmployeeImportTemplateCommand(path));

        var preview = await host.SendOkAsync(new PreviewEmployeeImportQuery(path, CreateMissingLookups: false));
        preview.ErrorCount.Should().Be(1, "lookups in the example row do not exist yet");
        preview.Rows[0].ErrorText.Should().Contain("Department 'Accounts' does not exist");

        var withLookups = await host.SendOkAsync(new PreviewEmployeeImportQuery(path, CreateMissingLookups: true));
        withLookups.ValidCount.Should().Be(1);
        withLookups.NewLookups.Should().Contain("Department: Accounts");

        (await host.SendOkAsync(new CommitEmployeeImportCommand(path, true))).Should().Be(1);
        var employee = (await host.SendOkAsync(new GetEmployeeListQuery())).Items.Single();
        employee.Department.Should().Be("Accounts");
        (await host.SendOkAsync(new GetEmployeeQuery(employee.Id))).Status.Should().Be(EmployeeStatus.Active);
    }

    [Fact]
    public async Task Any_bad_row_blocks_the_whole_import()
    {
        await using var host = await TestHost.CreateAsync();
        await EmployeeTestData.CreateAsync(host, "E0002");
        var path = Path.Combine(host.Root, "people.csv");
        await File.WriteAllTextAsync(path, "Code,Full name,Join date,Gender\nE0001,Good Row,2025-01-01,Female\nE0002,Duplicate,2025-01-01,Male\nE0003,Bad Date,31/31/2025,\nE0001,Repeat,2025-01-01,\n");

        var preview = await host.SendOkAsync(new PreviewEmployeeImportQuery(path, false));
        preview.Rows.Select(r => r.IsValid).Should().Equal(true, false, false, false);
        preview.Rows[1].ErrorText.Should().Contain("already exists");
        preview.Rows[2].ErrorText.Should().Contain("not a date");
        preview.Rows[3].ErrorText.Should().Contain("more than once");

        (await host.SendFailAsync(new CommitEmployeeImportCommand(path, false))).Code.Should().Be("Import.HasErrors");
        (await host.SendOkAsync(new GetEmployeeListQuery())).TotalCount.Should().Be(1);
    }

    [Fact]
    public async Task Missing_required_columns_are_reported()
    {
        await using var host = await TestHost.CreateAsync();
        var path = Path.Combine(host.Root, "bad.csv");
        await File.WriteAllTextAsync(path, "Name,Phone\nA,1\n");

        (await host.SendFailAsync(new PreviewEmployeeImportQuery(path, false))).Message.Should().Contain("Missing columns: Code, Full name, Join date");
    }

    [Fact]
    public async Task Thousand_rows_import_under_five_seconds_and_export_matches()
    {
        await using var host = await TestHost.CreateAsync();
        var path = Path.Combine(host.Root, "big.csv");
        var lines = new List<string> { "Code,Full name,Join date,Department" };
        lines.AddRange(Enumerable.Range(1, 1000).Select(i => $"I{i:0000},Imported {i},2025-02-01,Dept {i % 5}"));
        await File.WriteAllLinesAsync(path, lines);

        var sw = Stopwatch.StartNew();
        (await host.SendOkAsync(new CommitEmployeeImportCommand(path, true))).Should().Be(1000);
        sw.Stop();
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        (await host.SendOkAsync(new GetMasterDataQuery(MasterDataKind.Department))).Should().HaveCount(5);

        var export = Path.Combine(host.Root, "export.xlsx");
        (await host.SendOkAsync(new ExportEmployeesQuery(export, new GetEmployeeListQuery()))).Should().Be(1000);
        var rows = host.Services.GetRequiredService<ISpreadsheetService>().Read(export);
        rows.Should().HaveCount(1000);
        rows[0]["Code"].Should().Be("I0001");
    }
}

public class AuditLogTests
{
    [Fact]
    public async Task Audit_log_filters_and_is_admin_only()
    {
        await using var host = await TestHost.CreateAsync();
        var id = await EmployeeTestData.CreateAsync(host, "E0001");
        host.Clock.Advance(TimeSpan.FromMinutes(1));
        await host.SendOkAsync(new UpdateEmployeeCommand(id, EmployeeTestData.Input("E0001", "Renamed"), (await host.SendOkAsync(new GetEmployeeQuery(id))).RowVersion));

        var forEmployee = await host.SendOkAsync(new GetAuditLogQuery(EntityId: id));
        forEmployee.Items.Select(a => a.Action).Should().Equal("Employee.Updated", "Employee.Created");
        forEmployee.Items[0].BeforeJson.Should().Contain("Person E0001").And.NotContain("1234567890");
        (await host.SendOkAsync(new GetAuditLogQuery(Search: "Setup"))).TotalCount.Should().Be(1);

        await host.SignInAsAsync(Role.HR);
        (await host.SendFailAsync(new GetAuditLogQuery())).Code.Should().Be("Auth.Forbidden");
    }
}

public class SampleDataTests
{
    [Fact]
    public async Task Sample_data_is_loaded_once()
    {
        await using var host = await TestHost.CreateAsync(completeSetup: false);
        await host.CompleteSetupAsync(sampleData: true);
        var seeder = host.Services.CreateScope().ServiceProvider.GetRequiredService<ISampleDataSeeder>();
        await seeder.SeedAsync(host.CompanyId);

        (await host.SendOkAsync(new GetEmployeeListQuery())).TotalCount.Should().Be(40);
        (await host.SendOkAsync(new GetMasterDataQuery(MasterDataKind.Department))).Should().HaveCount(5);
        (await host.WithDbAsync(db => db.Holidays.CountAsync())).Should().Be(4);
    }
}
