using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Common;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Employees;
using SuperOfc.Domain.Organization;
using SuperOfc.Application.Organization;
using SuperOfc.Domain.Security;

namespace SuperOfc.Application.Employees;

/// <summary>Column names of the employee import template. Matching is case-insensitive.</summary>
public static class EmployeeImportColumns
{
    public const string Code = "Code";
    public const string FullName = "Full name";
    public const string Gender = "Gender";
    public const string DateOfBirth = "Date of birth";
    public const string Phone = "Phone";
    public const string Email = "Email";
    public const string Address = "Address";

    /// <summary>Optional; printed in letters such as "Son of ...".</summary>
    public const string FatherName = "Father's name";
    public const string NationalId = "National ID";
    public const string JoinDate = "Join date";
    public const string Confirmed = "Confirmed";
    public const string Department = "Department";
    public const string Designation = "Designation";
    public const string Location = "Location";
    public const string Grade = "Grade";
    public const string PaymentMode = "Payment mode";
    public const string BankName = "Bank name";
    public const string BankBranch = "Bank branch";
    public const string BankAccount = "Bank account";
    public const string TaxId = "Tax ID";
    public const string Overtime = "Overtime eligible";

    public static IReadOnlyList<string> All { get; } =
    [
        Code, FullName, Gender, DateOfBirth, Phone, Email, Address, FatherName, NationalId, JoinDate, Confirmed, Department, Designation,
        Location, Grade, PaymentMode, BankName, BankBranch, BankAccount, TaxId, Overtime,
    ];

    public static IReadOnlyList<string> Required { get; } = [Code, FullName, JoinDate];
}

public sealed record ImportRowResult(int RowNumber, string Code, string FullName, IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;

    public string ErrorText => string.Join("; ", Errors);
}

public sealed record ImportPreview(IReadOnlyList<ImportRowResult> Rows, IReadOnlyList<string> NewLookups)
{
    public int ValidCount => Rows.Count(r => r.IsValid);

    public int ErrorCount => Rows.Count(r => !r.IsValid);
}

/// <summary>Checks an import file without saving anything.</summary>
[RequiresPermission(Permission.ManageEmployees)]
public sealed record PreviewEmployeeImportQuery(string FilePath, bool CreateMissingLookups) : IQuery<ImportPreview>;

/// <summary>Imports every row in one transaction; nothing is saved if any row has an error.</summary>
[RequiresPermission(Permission.ManageEmployees)]
public sealed record CommitEmployeeImportCommand(string FilePath, bool CreateMissingLookups) : ICommand<int>;

internal sealed class EmployeeImportParser(IAppDbContext db, ICurrentUser user, ISpreadsheetService sheets)
{
    private static readonly string[] DateFormats = ["yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "dd-MMM-yyyy", "d-MMM-yyyy", "dd MMM yyyy", "d MMM yyyy", "MM/dd/yyyy"];

    public sealed record ParsedRow(int RowNumber, EmployeeInput Input, bool Confirmed, List<string> Errors, Dictionary<MasterDataKind, string?> LookupNames);

    public async Task<(List<ParsedRow> Rows, List<string> NewLookups, string? FileError)> ParseAsync(string path, bool createMissing, CancellationToken ct)
    {
        IReadOnlyList<IReadOnlyDictionary<string, string>> raw;
        try
        {
            raw = sheets.Read(path);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or ArgumentException)
        {
            return ([], [], $"The file could not be read: {ex.Message}");
        }

        if (raw.Count == 0)
        {
            return ([], [], "The file has no data rows.");
        }

        var missingColumns = EmployeeImportColumns.Required.Where(c => !raw[0].ContainsKey(c)).ToList();
        if (missingColumns.Count > 0)
        {
            return ([], [], $"Missing columns: {string.Join(", ", missingColumns)}. Download the template to see the expected layout.");
        }

        var lookups = new Dictionary<MasterDataKind, Dictionary<string, Guid>>();
        foreach (var kind in Enum.GetValues<MasterDataKind>())
        {
            lookups[kind] = (await MasterDataSets.Query(db, kind).AsNoTracking()
                    .Where(m => m.CompanyId == user.CompanyId && m.DeletedAtUtc == null)
                    .Select(m => new { m.Name, m.Id }).ToListAsync(ct).ConfigureAwait(false))
                .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
        }

        var existingCodes = (await db.Employees.AsNoTracking().Where(e => e.CompanyId == user.CompanyId).Select(e => e.Code).ToListAsync(ct).ConfigureAwait(false))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var newLookups = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<ParsedRow>();
        var validator = new EmployeeInputValidator();

        for (var i = 0; i < raw.Count; i++)
        {
            var r = raw[i];
            var rowNumber = i + 2;
            string Get(string column) => r.TryGetValue(column, out var v) ? v.Trim() : string.Empty;
            if (EmployeeImportColumns.All.All(c => string.IsNullOrEmpty(Get(c))))
            {
                continue;
            }

            var errors = new List<string>();
            var code = Get(EmployeeImportColumns.Code);
            if (!string.IsNullOrEmpty(code) && existingCodes.Contains(code))
            {
                errors.Add($"Code '{code}' already exists");
            }

            if (!string.IsNullOrEmpty(code) && !seenCodes.Add(code))
            {
                errors.Add($"Code '{code}' appears more than once in the file");
            }

            var joinDate = ParseDate(Get(EmployeeImportColumns.JoinDate), "Join date", errors, required: true) ?? DateOnly.MinValue;
            var dob = ParseDate(Get(EmployeeImportColumns.DateOfBirth), "Date of birth", errors, required: false);
            var gender = ParseEnum(Get(EmployeeImportColumns.Gender), Domain.Employees.Gender.Unspecified, "Gender", errors);
            var paymentMode = ParseEnum(Get(EmployeeImportColumns.PaymentMode).Replace(" ", string.Empty, StringComparison.Ordinal), Domain.Employees.PaymentMode.Bank, "Payment mode", errors);
            var overtime = ParseBool(Get(EmployeeImportColumns.Overtime), true, "Overtime eligible", errors);
            var confirmed = ParseBool(Get(EmployeeImportColumns.Confirmed), false, "Confirmed", errors);

            var names = new Dictionary<MasterDataKind, string?>
            {
                [MasterDataKind.Department] = Get(EmployeeImportColumns.Department),
                [MasterDataKind.Designation] = Get(EmployeeImportColumns.Designation),
                [MasterDataKind.Location] = Get(EmployeeImportColumns.Location),
                [MasterDataKind.Grade] = Get(EmployeeImportColumns.Grade),
            };
            var ids = new Dictionary<MasterDataKind, Guid?>();
            foreach (var (kind, name) in names)
            {
                if (string.IsNullOrEmpty(name))
                {
                    ids[kind] = null;
                }
                else if (lookups[kind].TryGetValue(name, out var id))
                {
                    ids[kind] = id;
                }
                else if (createMissing)
                {
                    newLookups.Add($"{kind}: {name}");
                    ids[kind] = null;
                }
                else
                {
                    errors.Add($"{kind} '{name}' does not exist");
                    ids[kind] = null;
                }
            }

            var input = new EmployeeInput(
                code, Get(EmployeeImportColumns.FullName), gender, dob, Get(EmployeeImportColumns.Phone), Get(EmployeeImportColumns.Email),
                Get(EmployeeImportColumns.Address), Get(EmployeeImportColumns.NationalId), null, joinDate,
                ids[MasterDataKind.Department], ids[MasterDataKind.Designation], ids[MasterDataKind.Location], ids[MasterDataKind.Grade],
                paymentMode, Get(EmployeeImportColumns.BankName), Get(EmployeeImportColumns.BankBranch), Get(EmployeeImportColumns.BankAccount),
                Get(EmployeeImportColumns.TaxId), overtime, null, Get(EmployeeImportColumns.FatherName));
            if (joinDate != DateOnly.MinValue)
            {
                errors.AddRange((await validator.ValidateAsync(input, ct).ConfigureAwait(false)).Errors.Select(e => e.ErrorMessage));
            }

            rows.Add(new ParsedRow(rowNumber, input, confirmed, errors, names));
        }

        return (rows, newLookups.ToList(), null);
    }

    private static DateOnly? ParseDate(string text, string label, List<string> errors, bool required)
    {
        if (string.IsNullOrEmpty(text))
        {
            if (required)
            {
                errors.Add($"{label} is required");
            }

            return null;
        }

        if (DateOnly.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date;
        }

        errors.Add($"{label} '{text}' is not a date (use yyyy-MM-dd)");
        return null;
    }

    private static T ParseEnum<T>(string text, T fallback, string label, List<string> errors)
        where T : struct, Enum
    {
        if (string.IsNullOrEmpty(text))
        {
            return fallback;
        }

        if (Enum.TryParse<T>(text, ignoreCase: true, out var value) && Enum.IsDefined(value))
        {
            return value;
        }

        errors.Add($"{label} '{text}' is not one of: {string.Join(", ", Enum.GetNames<T>())}");
        return fallback;
    }

    private static bool ParseBool(string text, bool fallback, string label, List<string> errors)
    {
        switch (text.ToUpperInvariant())
        {
            case "":
                return fallback;
            case "YES" or "Y" or "TRUE" or "1":
                return true;
            case "NO" or "N" or "FALSE" or "0":
                return false;
            default:
                errors.Add($"{label} must be Yes or No");
                return fallback;
        }
    }
}

public sealed class PreviewEmployeeImportHandler(IAppDbContext db, ICurrentUser user, ISpreadsheetService sheets) : IRequestHandler<PreviewEmployeeImportQuery, ImportPreview>
{
    public async Task<Result<ImportPreview>> HandleAsync(PreviewEmployeeImportQuery request, CancellationToken cancellationToken)
    {
        var (rows, lookups, fileError) = await new EmployeeImportParser(db, user, sheets).ParseAsync(request.FilePath, request.CreateMissingLookups, cancellationToken).ConfigureAwait(false);
        if (fileError is not null)
        {
            return new Error("Import.File", fileError);
        }

        return new ImportPreview(rows.Select(r => new ImportRowResult(r.RowNumber, r.Input.Code, r.Input.FullName, r.Errors)).ToList(), lookups);
    }
}

public sealed class CommitEmployeeImportHandler(IAppDbContext db, ICurrentUser user, ISpreadsheetService sheets, IIdGenerator ids, IAuditWriter audit)
    : IRequestHandler<CommitEmployeeImportCommand, int>
{
    public async Task<Result<int>> HandleAsync(CommitEmployeeImportCommand request, CancellationToken cancellationToken)
    {
        var (rows, _, fileError) = await new EmployeeImportParser(db, user, sheets).ParseAsync(request.FilePath, request.CreateMissingLookups, cancellationToken).ConfigureAwait(false);
        if (fileError is not null)
        {
            return new Error("Import.File", fileError);
        }

        var invalid = rows.Where(r => r.Errors.Count > 0).ToList();
        if (invalid.Count > 0)
        {
            return new Error("Import.HasErrors", $"{invalid.Count} row(s) have errors (first: row {invalid[0].RowNumber}: {string.Join("; ", invalid[0].Errors)}). Nothing was imported.");
        }

        if (rows.Count == 0)
        {
            return new Error("Import.File", "The file has no data rows.");
        }

        await using var tx = await db.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var created = new Dictionary<(MasterDataKind, string), Guid>();
        Guid? Resolve(MasterDataKind kind, string? name, Guid? existing)
        {
            if (existing is not null || string.IsNullOrEmpty(name))
            {
                return existing;
            }

            var key = (kind, name.ToUpperInvariant());
            if (!created.TryGetValue(key, out var id))
            {
                var entity = MasterDataSets.Create(kind);
                entity.Id = id = ids.NewId();
                entity.CompanyId = user.CompanyId;
                entity.Name = name;
                db.Add(entity);
                created[key] = id;
            }

            return id;
        }

        foreach (var row in rows)
        {
            var input = row.Input with
            {
                DepartmentId = Resolve(MasterDataKind.Department, row.LookupNames[MasterDataKind.Department], row.Input.DepartmentId),
                DesignationId = Resolve(MasterDataKind.Designation, row.LookupNames[MasterDataKind.Designation], row.Input.DesignationId),
                LocationId = Resolve(MasterDataKind.Location, row.LookupNames[MasterDataKind.Location], row.Input.LocationId),
                GradeId = Resolve(MasterDataKind.Grade, row.LookupNames[MasterDataKind.Grade], row.Input.GradeId),
            };
            var employee = new Employee { Id = ids.NewId(), CompanyId = user.CompanyId };
            EmployeeMapping.Apply(employee, input);
            if (row.Confirmed)
            {
                employee.Status = EmployeeStatus.Active;
                employee.ConfirmationDate = employee.JoinDate;
            }

            db.Employees.Add(employee);
            db.EmploymentEvents.Add(new EmploymentEvent { Id = ids.NewId(), CompanyId = user.CompanyId, EmployeeId = employee.Id, Type = EmploymentEventType.Joined, Date = employee.JoinDate, Description = "Joined (imported)" });
        }

        audit.Record("Employee.Imported", $"{rows.Count} employees imported from {Path.GetFileName(request.FilePath)}.");
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return rows.Count;
    }
}

/// <summary>Writes the import template with headers and one example row.</summary>
[RequiresPermission(Permission.ManageEmployees)]
public sealed record WriteEmployeeImportTemplateCommand(string FilePath) : ICommand<Unit>;

public sealed class WriteEmployeeImportTemplateHandler(ISpreadsheetService sheets) : IRequestHandler<WriteEmployeeImportTemplateCommand, Unit>
{
    public Task<Result<Unit>> HandleAsync(WriteEmployeeImportTemplateCommand request, CancellationToken cancellationToken)
    {
        object?[] example =
        [
            "E0001", "Rahim Uddin", "Male", "1990-05-14", "01700000000", "rahim@example.com", "Dhaka", "Karim Uddin", "1234567890", "2024-01-01", "Yes",
            "Accounts", "Officer", "Head office", string.Empty, "Bank", "Example Bank", "Gulshan", "0011223344", string.Empty, "Yes",
        ];
        sheets.Write(request.FilePath, [new SheetData("Employees", EmployeeImportColumns.All, [example])]);
        return Task.FromResult<Result<Unit>>(Unit.Value);
    }
}

/// <summary>Exports the employee list with the given filters to Excel.</summary>
public sealed record ExportEmployeesQuery(string FilePath, GetEmployeeListQuery Filter) : IQuery<int>;

public sealed class ExportEmployeesHandler(IAppDbContext db, ICurrentUser user, ISpreadsheetService sheets) : IRequestHandler<ExportEmployeesQuery, int>
{
    public async Task<Result<int>> HandleAsync(ExportEmployeesQuery request, CancellationToken cancellationToken)
    {
        var list = new GetEmployeeListHandler(db, user);
        var all = new List<EmployeeListItem>();
        for (var page = 0; ; page++)
        {
            var result = await list.HandleAsync(request.Filter with { PageIndex = page, PageSize = 1000 }, cancellationToken).ConfigureAwait(false);
            all.AddRange(result.Value.Items);
            if (!result.Value.HasNext)
            {
                break;
            }
        }

        // The list does not carry the father's name; one query fetches it for the whole company.
        var fathers = (await db.Employees.AsNoTracking().Where(e => e.CompanyId == user.CompanyId && e.FatherName != null)
                .Select(e => new { e.Id, e.FatherName }).ToListAsync(cancellationToken).ConfigureAwait(false))
            .ToDictionary(e => e.Id, e => e.FatherName);
        sheets.Write(request.FilePath, [new SheetData(
            "Employees",
            ["Code", "Full name", EmployeeImportColumns.FatherName, "Department", "Designation", "Location", "Phone", "Join date", "Status"],
            all.Select(e => (IReadOnlyList<object?>)[e.Code, e.FullName, fathers.GetValueOrDefault(e.Id), e.Department, e.Designation, e.Location, e.Phone, e.JoinDate,
                e.Status.ToString()]))]);
        return all.Count;
    }
}
