using System.Globalization;
using System.Text.Json;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Common;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Employees;
using SuperOfc.Domain.Security;

namespace SuperOfc.Application.Employees;

public enum EmployeeSort
{
    Code,
    Name,
    JoinDate,
    Department,
}

public sealed record EmployeeListItem(
    Guid Id,
    string Code,
    string FullName,
    string? Department,
    string? Designation,
    string? Location,
    string? Phone,
    DateOnly JoinDate,
    EmployeeStatus Status,
    string? PhotoPath);

public sealed record GetEmployeeListQuery(
    string? Search = null,
    Guid? DepartmentId = null,
    Guid? LocationId = null,
    EmployeeStatus? Status = null,
    bool IncludeLeft = false,
    EmployeeSort Sort = EmployeeSort.Code,
    bool Descending = false,
    int PageIndex = 0,
    int PageSize = 200) : IQuery<PagedResult<EmployeeListItem>>;

public sealed class GetEmployeeListHandler(IAppDbContext db, ICurrentUser user) : IRequestHandler<GetEmployeeListQuery, PagedResult<EmployeeListItem>>
{
    public async Task<Result<PagedResult<EmployeeListItem>>> HandleAsync(GetEmployeeListQuery request, CancellationToken cancellationToken)
    {
        var query = db.Employees.AsNoTracking().Where(e => e.CompanyId == user.CompanyId && e.DeletedAtUtc == null);
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = $"%{request.Search.Trim()}%";
            query = query.Where(e => EF.Functions.Like(e.Code, term) || EF.Functions.Like(e.FullName, term) || (e.Phone != null && EF.Functions.Like(e.Phone, term)));
        }

        if (request.DepartmentId is { } dept)
        {
            query = query.Where(e => e.DepartmentId == dept);
        }

        if (request.LocationId is { } loc)
        {
            query = query.Where(e => e.LocationId == loc);
        }

        if (request.Status is { } status)
        {
            query = query.Where(e => e.Status == status);
        }
        else if (!request.IncludeLeft)
        {
            query = query.Where(e => e.Status != EmployeeStatus.Left);
        }

        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);

        var joined =
            from e in query
            join d in db.Departments on e.DepartmentId equals d.Id into ds
            from d in ds.DefaultIfEmpty()
            join g in db.Designations on e.DesignationId equals g.Id into gs
            from g in gs.DefaultIfEmpty()
            join l in db.Locations on e.LocationId equals l.Id into ls
            from l in ls.DefaultIfEmpty()
            select new { e, Department = d == null ? null : d.Name, Designation = g == null ? null : g.Name, Location = l == null ? null : l.Name };

        joined = (request.Sort, request.Descending) switch
        {
            (EmployeeSort.Name, false) => joined.OrderBy(x => x.e.FullName),
            (EmployeeSort.Name, true) => joined.OrderByDescending(x => x.e.FullName),
            (EmployeeSort.JoinDate, false) => joined.OrderBy(x => x.e.JoinDate),
            (EmployeeSort.JoinDate, true) => joined.OrderByDescending(x => x.e.JoinDate),
            (EmployeeSort.Department, false) => joined.OrderBy(x => x.Department).ThenBy(x => x.e.Code),
            (EmployeeSort.Department, true) => joined.OrderByDescending(x => x.Department).ThenBy(x => x.e.Code),
            (_, true) => joined.OrderByDescending(x => x.e.Code),
            _ => joined.OrderBy(x => x.e.Code),
        };

        var page = new PageRequest(request.PageIndex, request.PageSize);
        var items = await joined.Skip(page.Skip).Take(page.Take)
            .Select(x => new EmployeeListItem(x.e.Id, x.e.Code, x.e.FullName, x.Department, x.Designation, x.Location, x.e.Phone, x.e.JoinDate, x.e.Status, x.e.PhotoPath))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return new PagedResult<EmployeeListItem>(items, total, request.PageIndex, page.Take);
    }
}

public sealed record EmployeeDetail(
    Guid Id,
    string Code,
    string FullName,
    Gender Gender,
    DateOnly? DateOfBirth,
    string? Phone,
    string? Email,
    string? Address,
    string? NationalId,
    string? EmergencyContact,
    DateOnly JoinDate,
    DateOnly? ConfirmationDate,
    DateOnly? ExitDate,
    string? ExitReason,
    EmployeeStatus Status,
    Guid? DepartmentId,
    Guid? DesignationId,
    Guid? LocationId,
    Guid? GradeId,
    string? PhotoPath,
    PaymentMode PaymentMode,
    string? BankName,
    string? BankBranch,
    string? BankAccount,
    string? TaxIdentifier,
    bool IsOvertimeEligible,
    IReadOnlyDictionary<string, string> CustomFields,
    long RowVersion,
    string? FatherName = null);

public sealed record GetEmployeeQuery(Guid Id) : IQuery<EmployeeDetail>;

public sealed class GetEmployeeHandler(IAppDbContext db, ICurrentUser user) : IRequestHandler<GetEmployeeQuery, EmployeeDetail>
{
    public async Task<Result<EmployeeDetail>> HandleAsync(GetEmployeeQuery request, CancellationToken cancellationToken)
    {
        var e = await db.Employees.AsNoTracking().FirstOrDefaultAsync(x => x.Id == request.Id && x.DeletedAtUtc == null, cancellationToken).ConfigureAwait(false);
        if (e is null)
        {
            return Error.NotFound("Employee");
        }

        // Bank and tax details are financial data: masked for roles that cannot see salaries.
        var canSeeFinance = PermissionMatrix.Has(user.Role, Permission.ViewSalaries) || PermissionMatrix.Has(user.Role, Permission.ManageEmployees);
        return new EmployeeDetail(
            e.Id, e.Code, e.FullName, e.Gender, e.DateOfBirth, e.Phone, e.Email, e.Address, e.NationalId, e.EmergencyContact,
            e.JoinDate, e.ConfirmationDate, e.ExitDate, e.ExitReason, e.Status, e.DepartmentId, e.DesignationId, e.LocationId, e.GradeId,
            e.PhotoPath, e.PaymentMode, e.BankName, e.BankBranch, canSeeFinance ? e.BankAccount : Mask(e.BankAccount),
            canSeeFinance ? e.TaxIdentifier : Mask(e.TaxIdentifier), e.IsOvertimeEligible, CustomFieldJson.Parse(e.CustomFieldsJson), e.RowVersion, e.FatherName);
    }

    private static string? Mask(string? value)
        => string.IsNullOrEmpty(value) ? value : new string('•', Math.Max(0, value.Length - 4)) + value[^Math.Min(4, value.Length)..];
}

internal static class CustomFieldJson
{
    public static IReadOnlyDictionary<string, string> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>();
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    public static string? Serialize(IReadOnlyDictionary<string, string>? values)
    {
        var clean = values?.Where(kv => !string.IsNullOrWhiteSpace(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value.Trim());
        return clean is null || clean.Count == 0 ? null : JsonSerializer.Serialize(clean);
    }
}

/// <summary>Fields edited on the employee profile. Lifecycle changes (exit, confirm, transfer) use their own commands.</summary>
public sealed record EmployeeInput(
    string Code,
    string FullName,
    Gender Gender,
    DateOnly? DateOfBirth,
    string? Phone,
    string? Email,
    string? Address,
    string? NationalId,
    string? EmergencyContact,
    DateOnly JoinDate,
    Guid? DepartmentId,
    Guid? DesignationId,
    Guid? LocationId,
    Guid? GradeId,
    PaymentMode PaymentMode,
    string? BankName,
    string? BankBranch,
    string? BankAccount,
    string? TaxIdentifier,
    bool IsOvertimeEligible,
    IReadOnlyDictionary<string, string>? CustomFields,
    string? FatherName = null);

public sealed class EmployeeInputValidator : AbstractValidator<EmployeeInput>
{
    public EmployeeInputValidator()
    {
        RuleFor(x => x.Code).NotEmpty().WithMessage("Enter an employee code.").MaximumLength(20)
            .Matches("^[A-Za-z0-9._/-]+$").WithMessage("Use letters, numbers, dot, dash, slash or underscore.");
        RuleFor(x => x.FullName).NotEmpty().WithMessage("Enter the employee's name.").MaximumLength(150);
        RuleFor(x => x.Email).EmailAddress().When(x => !string.IsNullOrWhiteSpace(x.Email)).WithMessage("Enter a valid email address.");
        RuleFor(x => x.Phone).MaximumLength(30);
        RuleFor(x => x.FatherName).MaximumLength(200);
        RuleFor(x => x.DateOfBirth).Must((x, dob) => dob is null || dob < x.JoinDate).WithMessage("Date of birth must be before the join date.");
        RuleFor(x => x.BankAccount).NotEmpty().When(x => x.PaymentMode == PaymentMode.Bank && !string.IsNullOrWhiteSpace(x.BankName))
            .WithMessage("Enter the bank account number.");
    }
}

[RequiresPermission(Permission.ManageEmployees)]
public sealed record CreateEmployeeCommand(EmployeeInput Input, bool StartConfirmed = false) : ICommand<Guid>;

public sealed class CreateEmployeeValidator : AbstractValidator<CreateEmployeeCommand>
{
    public CreateEmployeeValidator()
    {
        RuleFor(x => x.Input).SetValidator(new EmployeeInputValidator());
    }
}

public sealed class CreateEmployeeHandler(IAppDbContext db, ICurrentUser user, IIdGenerator ids, IAuditWriter audit) : IRequestHandler<CreateEmployeeCommand, Guid>
{
    public async Task<Result<Guid>> HandleAsync(CreateEmployeeCommand request, CancellationToken cancellationToken)
    {
        var input = request.Input;
        var code = input.Code.Trim();
        if (await db.Employees.AnyAsync(e => e.CompanyId == user.CompanyId && e.Code == code, cancellationToken).ConfigureAwait(false))
        {
            return new Error("Validation.Code", $"Employee code '{code}' is already used.");
        }

        var employee = new Employee { Id = ids.NewId(), CompanyId = user.CompanyId };
        EmployeeMapping.Apply(employee, input);
        employee.Status = request.StartConfirmed ? EmployeeStatus.Active : EmployeeStatus.Probation;
        if (request.StartConfirmed)
        {
            employee.ConfirmationDate = employee.JoinDate;
        }

        db.Employees.Add(employee);
        db.EmploymentEvents.Add(new EmploymentEvent
        {
            Id = ids.NewId(),
            CompanyId = user.CompanyId,
            EmployeeId = employee.Id,
            Type = EmploymentEventType.Joined,
            Date = employee.JoinDate,
            Description = "Joined",
        });
        audit.Record("Employee.Created", $"Employee {employee.Code} created.", nameof(Employee), employee.Id, after: EmployeeMapping.AuditView(employee));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return employee.Id;
    }
}

[RequiresPermission(Permission.ManageEmployees)]
public sealed record UpdateEmployeeCommand(Guid Id, EmployeeInput Input, long RowVersion) : ICommand<Unit>;

public sealed class UpdateEmployeeValidator : AbstractValidator<UpdateEmployeeCommand>
{
    public UpdateEmployeeValidator()
    {
        RuleFor(x => x.Input).SetValidator(new EmployeeInputValidator());
    }
}

public sealed class UpdateEmployeeHandler(IAppDbContext db, ICurrentUser user, IAuditWriter audit) : IRequestHandler<UpdateEmployeeCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(UpdateEmployeeCommand request, CancellationToken cancellationToken)
    {
        var employee = await db.Employees.FirstOrDefaultAsync(e => e.Id == request.Id && e.DeletedAtUtc == null, cancellationToken).ConfigureAwait(false);
        if (employee is null)
        {
            return Error.NotFound("Employee");
        }

        if (employee.RowVersion != request.RowVersion)
        {
            return Error.Conflict("Someone else changed this employee. Reload and try again.");
        }

        var code = request.Input.Code.Trim();
        if (await db.Employees.AnyAsync(e => e.CompanyId == user.CompanyId && e.Id != employee.Id && e.Code == code, cancellationToken).ConfigureAwait(false))
        {
            return new Error("Validation.Code", $"Employee code '{code}' is already used.");
        }

        var before = EmployeeMapping.AuditView(employee);
        var input = request.Input;
        if (!PermissionMatrix.Has(user.Role, Permission.ViewSalaries) && input.BankAccount?.Contains('•', StringComparison.Ordinal) == true)
        {
            // The masked value came back unchanged; keep the stored one.
            input = input with { BankAccount = employee.BankAccount, TaxIdentifier = employee.TaxIdentifier };
        }

        if (input.JoinDate != employee.JoinDate && employee.ExitDate is { } exit && input.JoinDate > exit)
        {
            return new Error("Validation.JoinDate", "Join date cannot be after the exit date.");
        }

        EmployeeMapping.Apply(employee, input);
        audit.Record("Employee.Updated", $"Employee {employee.Code} updated.", nameof(Employee), employee.Id, before, EmployeeMapping.AuditView(employee));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}

internal static class EmployeeMapping
{
    public static void Apply(Employee e, EmployeeInput i)
    {
        e.Code = i.Code.Trim();
        e.FullName = i.FullName.Trim();
        e.Gender = i.Gender;
        e.DateOfBirth = i.DateOfBirth;
        e.Phone = Clean(i.Phone);
        e.Email = Clean(i.Email);
        e.Address = Clean(i.Address);
        e.FatherName = Clean(i.FatherName);
        e.NationalId = Clean(i.NationalId);
        e.EmergencyContact = Clean(i.EmergencyContact);
        e.JoinDate = i.JoinDate;
        e.DepartmentId = i.DepartmentId;
        e.DesignationId = i.DesignationId;
        e.LocationId = i.LocationId;
        e.GradeId = i.GradeId;
        e.PaymentMode = i.PaymentMode;
        e.BankName = Clean(i.BankName);
        e.BankBranch = Clean(i.BankBranch);
        e.BankAccount = Clean(i.BankAccount);
        e.TaxIdentifier = Clean(i.TaxIdentifier);
        e.IsOvertimeEligible = i.IsOvertimeEligible;
        e.CustomFieldsJson = CustomFieldJson.Serialize(i.CustomFields);
    }

    /// <summary>Audit snapshot without bank account or national id numbers (never logged in full).</summary>
    public static object AuditView(Employee e) => new
    {
        e.Code,
        e.FullName,
        e.Phone,
        e.Email,
        e.JoinDate,
        e.Status,
        e.DepartmentId,
        e.DesignationId,
        e.LocationId,
        e.GradeId,
        e.PaymentMode,
        e.BankName,
        HasBankAccount = !string.IsNullOrEmpty(e.BankAccount),
        e.IsOvertimeEligible,
    };

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record EmploymentEventDto(Guid Id, EmploymentEventType Type, DateOnly Date, string Description, string CreatedBy);

public sealed record GetEmploymentHistoryQuery(Guid EmployeeId) : IQuery<IReadOnlyList<EmploymentEventDto>>;

public sealed class GetEmploymentHistoryHandler(IAppDbContext db) : IRequestHandler<GetEmploymentHistoryQuery, IReadOnlyList<EmploymentEventDto>>
{
    public async Task<Result<IReadOnlyList<EmploymentEventDto>>> HandleAsync(GetEmploymentHistoryQuery request, CancellationToken cancellationToken)
        => await db.EmploymentEvents.AsNoTracking()
            .Where(e => e.EmployeeId == request.EmployeeId)
            .OrderByDescending(e => e.Date).ThenByDescending(e => e.CreatedAtUtc)
            .Select(e => new EmploymentEventDto(e.Id, e.Type, e.Date, e.Description, e.CreatedBy))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
}

public enum EmploymentAction
{
    Confirm,
    Promote,
    Transfer,
    Exit,
    Rejoin,
    Note,
}

/// <summary>
/// One lifecycle step. Promote/Transfer may change department, designation, location or grade (null keeps the current one).
/// </summary>
[RequiresPermission(Permission.ManageEmployees)]
public sealed record RecordEmploymentEventCommand(
    Guid EmployeeId,
    EmploymentAction Action,
    DateOnly Date,
    string? Notes,
    Guid? DepartmentId = null,
    Guid? DesignationId = null,
    Guid? LocationId = null,
    Guid? GradeId = null) : ICommand<Unit>;

public sealed class RecordEmploymentEventValidator : AbstractValidator<RecordEmploymentEventCommand>
{
    public RecordEmploymentEventValidator()
    {
        RuleFor(x => x.Notes).NotEmpty().When(x => x.Action is EmploymentAction.Exit or EmploymentAction.Note)
            .WithMessage("Enter a reason.");
        RuleFor(x => x.Notes).MaximumLength(500);
    }
}

public sealed class RecordEmploymentEventHandler(IAppDbContext db, ICurrentUser user, IIdGenerator ids, IAuditWriter audit)
    : IRequestHandler<RecordEmploymentEventCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(RecordEmploymentEventCommand request, CancellationToken cancellationToken)
    {
        var e = await db.Employees.FirstOrDefaultAsync(x => x.Id == request.EmployeeId && x.DeletedAtUtc == null, cancellationToken).ConfigureAwait(false);
        if (e is null)
        {
            return Error.NotFound("Employee");
        }

        var before = EmployeeMapping.AuditView(e);
        string description;
        EmploymentEventType type;
        try
        {
            switch (request.Action)
            {
                case EmploymentAction.Confirm:
                    if (e.Status == EmployeeStatus.Left)
                    {
                        return new Error("Employee.HasLeft", "This employee has left. Rejoin them first.");
                    }

                    e.Confirm(request.Date);
                    (type, description) = (EmploymentEventType.Confirmed, "Confirmed");
                    break;
                case EmploymentAction.Promote:
                case EmploymentAction.Transfer:
                    if (e.Status == EmployeeStatus.Left)
                    {
                        return new Error("Employee.HasLeft", "This employee has left. Rejoin them first.");
                    }

                    var changes = await DescribeChangesAsync(e, request, cancellationToken).ConfigureAwait(false);
                    if (changes.Count == 0)
                    {
                        return new Error("Validation.DepartmentId", "Choose at least one change.");
                    }

                    e.DepartmentId = request.DepartmentId ?? e.DepartmentId;
                    e.DesignationId = request.DesignationId ?? e.DesignationId;
                    e.LocationId = request.LocationId ?? e.LocationId;
                    e.GradeId = request.GradeId ?? e.GradeId;
                    type = request.Action == EmploymentAction.Promote ? EmploymentEventType.Promoted : EmploymentEventType.Transferred;
                    description = string.Join("; ", changes);
                    break;
                case EmploymentAction.Exit:
                    if (e.Status == EmployeeStatus.Left)
                    {
                        return new Error("Employee.HasLeft", "This employee has already left.");
                    }

                    e.Exit(request.Date, request.Notes!.Trim());
                    (type, description) = (EmploymentEventType.Exited, "Left");
                    break;
                case EmploymentAction.Rejoin:
                    if (e.Status != EmployeeStatus.Left)
                    {
                        return new Error("Employee.NotLeft", "Only employees who have left can rejoin.");
                    }

                    e.Rejoin(request.Date);
                    (type, description) = (EmploymentEventType.Rejoined, "Rejoined");
                    break;
                default:
                    (type, description) = (EmploymentEventType.Note, "Note");
                    break;
            }
        }
        catch (DomainException ex)
        {
            return new Error("Validation.Date", ex.Message);
        }

        if (!string.IsNullOrWhiteSpace(request.Notes))
        {
            description = request.Action == EmploymentAction.Note ? request.Notes.Trim() : $"{description}. {request.Notes.Trim()}";
        }

        db.EmploymentEvents.Add(new EmploymentEvent
        {
            Id = ids.NewId(),
            CompanyId = user.CompanyId,
            EmployeeId = e.Id,
            Type = type,
            Date = request.Date,
            Description = description,
        });
        audit.Record($"Employee.{request.Action}", $"Employee {e.Code}: {description}", nameof(Employee), e.Id, before, EmployeeMapping.AuditView(e));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }

    private async Task<List<string>> DescribeChangesAsync(Employee e, RecordEmploymentEventCommand r, CancellationToken ct)
    {
        var changes = new List<string>();
        async Task Add(string label, Guid? from, Guid? to, IQueryable<Domain.Organization.MasterDataEntity> set)
        {
            if (to is null || to == from)
            {
                return;
            }

            var names = await set.Where(m => m.Id == to || m.Id == from).Select(m => new { m.Id, m.Name }).ToListAsync(ct).ConfigureAwait(false);
            var fromName = names.FirstOrDefault(n => n.Id == from)?.Name ?? "none";
            var toName = names.FirstOrDefault(n => n.Id == to)?.Name ?? "?";
            changes.Add(string.Create(CultureInfo.InvariantCulture, $"{label}: {fromName} → {toName}"));
        }

        await Add("Department", e.DepartmentId, r.DepartmentId, db.Departments).ConfigureAwait(false);
        await Add("Designation", e.DesignationId, r.DesignationId, db.Designations).ConfigureAwait(false);
        await Add("Location", e.LocationId, r.LocationId, db.Locations).ConfigureAwait(false);
        await Add("Grade", e.GradeId, r.GradeId, db.Grades).ConfigureAwait(false);
        return changes;
    }
}

/// <summary>Suggests the next free employee code, e.g. E0042.</summary>
public sealed record GetNextEmployeeCodeQuery(string Prefix = "E", int Digits = 4) : IQuery<string>;

public sealed class GetNextEmployeeCodeHandler(IAppDbContext db, ICurrentUser user) : IRequestHandler<GetNextEmployeeCodeQuery, string>
{
    public async Task<Result<string>> HandleAsync(GetNextEmployeeCodeQuery request, CancellationToken cancellationToken)
    {
        var codes = await db.Employees.AsNoTracking().Where(e => e.CompanyId == user.CompanyId && e.Code.StartsWith(request.Prefix))
            .Select(e => e.Code).ToListAsync(cancellationToken).ConfigureAwait(false);
        var max = codes.Select(c => int.TryParse(c[request.Prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0).DefaultIfEmpty(0).Max();
        return request.Prefix + (max + 1).ToString(new string('0', request.Digits), CultureInfo.InvariantCulture);
    }
}

[RequiresPermission(Permission.ManageEmployees)]
public sealed record SetEmployeePhotoCommand(Guid EmployeeId, string SourceFilePath) : ICommand<string>;

public sealed class SetEmployeePhotoHandler(IAppDbContext db, IFileStorage files, IAuditWriter audit) : IRequestHandler<SetEmployeePhotoCommand, string>
{
    private static readonly string[] Allowed = [".jpg", ".jpeg", ".png", ".bmp"];

    public async Task<Result<string>> HandleAsync(SetEmployeePhotoCommand request, CancellationToken cancellationToken)
    {
        var e = await db.Employees.FirstOrDefaultAsync(x => x.Id == request.EmployeeId, cancellationToken).ConfigureAwait(false);
        if (e is null)
        {
            return Error.NotFound("Employee");
        }

        var ext = Path.GetExtension(request.SourceFilePath).ToLowerInvariant();
        if (!Allowed.Contains(ext) || !File.Exists(request.SourceFilePath))
        {
            return new Error("Validation.Photo", "Choose a JPG, PNG or BMP image.");
        }

        if (new FileInfo(request.SourceFilePath).Length > 10 * 1024 * 1024)
        {
            return new Error("Validation.Photo", "The photo must be smaller than 10 MB.");
        }

        await using var stream = File.OpenRead(request.SourceFilePath);
        var path = await files.SaveAsync("employees", e.Id, "photo" + ext, stream, cancellationToken).ConfigureAwait(false);
        var old = e.PhotoPath;
        e.PhotoPath = path;
        audit.Record("Employee.PhotoChanged", $"Photo changed for {e.Code}.", nameof(Employee), e.Id);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (old is not null)
        {
            files.Delete(old);
        }

        return path;
    }
}

public sealed record EmployeeDocumentDto(Guid Id, string Title, string? DocumentType, string FileName, string FilePath, DateOnly? ExpiryDate, string? Notes, DateTime CreatedAtUtc);

public sealed record GetEmployeeDocumentsQuery(Guid EmployeeId) : IQuery<IReadOnlyList<EmployeeDocumentDto>>;

public sealed class GetEmployeeDocumentsHandler(IAppDbContext db) : IRequestHandler<GetEmployeeDocumentsQuery, IReadOnlyList<EmployeeDocumentDto>>
{
    public async Task<Result<IReadOnlyList<EmployeeDocumentDto>>> HandleAsync(GetEmployeeDocumentsQuery request, CancellationToken cancellationToken)
        => await db.EmployeeDocuments.AsNoTracking().Where(d => d.EmployeeId == request.EmployeeId)
            .OrderBy(d => d.Title)
            .Select(d => new EmployeeDocumentDto(d.Id, d.Title, d.DocumentType, d.FileName, d.FilePath, d.ExpiryDate, d.Notes, d.CreatedAtUtc))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
}

[RequiresPermission(Permission.ManageEmployees)]
public sealed record AddEmployeeDocumentCommand(Guid EmployeeId, string Title, string? DocumentType, string SourceFilePath, DateOnly? ExpiryDate, string? Notes) : ICommand<Guid>;

public sealed class AddEmployeeDocumentValidator : AbstractValidator<AddEmployeeDocumentCommand>
{
    public AddEmployeeDocumentValidator()
    {
        RuleFor(x => x.Title).NotEmpty().WithMessage("Enter a title.").MaximumLength(150);
        RuleFor(x => x.SourceFilePath).Must(File.Exists).WithMessage("Choose a file.");
    }
}

public sealed class AddEmployeeDocumentHandler(IAppDbContext db, ICurrentUser user, IIdGenerator ids, IFileStorage files, IAuditWriter audit)
    : IRequestHandler<AddEmployeeDocumentCommand, Guid>
{
    public async Task<Result<Guid>> HandleAsync(AddEmployeeDocumentCommand request, CancellationToken cancellationToken)
    {
        if (!await db.Employees.AnyAsync(e => e.Id == request.EmployeeId, cancellationToken).ConfigureAwait(false))
        {
            return Error.NotFound("Employee");
        }

        if (new FileInfo(request.SourceFilePath).Length > 50 * 1024 * 1024)
        {
            return new Error("Validation.SourceFilePath", "The file must be smaller than 50 MB.");
        }

        await using var stream = File.OpenRead(request.SourceFilePath);
        var fileName = Path.GetFileName(request.SourceFilePath);
        var path = await files.SaveAsync("employees", request.EmployeeId, fileName, stream, cancellationToken).ConfigureAwait(false);
        var doc = new EmployeeDocument
        {
            Id = ids.NewId(),
            CompanyId = user.CompanyId,
            EmployeeId = request.EmployeeId,
            Title = request.Title.Trim(),
            DocumentType = request.DocumentType?.Trim(),
            FilePath = path,
            FileName = fileName,
            ExpiryDate = request.ExpiryDate,
            Notes = request.Notes?.Trim(),
        };
        db.EmployeeDocuments.Add(doc);
        audit.Record("Employee.DocumentAdded", $"Document '{doc.Title}' added.", nameof(EmployeeDocument), doc.Id);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return doc.Id;
    }
}

[RequiresPermission(Permission.ManageEmployees)]
public sealed record DeleteEmployeeDocumentCommand(Guid Id) : ICommand<Unit>;

public sealed class DeleteEmployeeDocumentHandler(IAppDbContext db, IFileStorage files, IAuditWriter audit) : IRequestHandler<DeleteEmployeeDocumentCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(DeleteEmployeeDocumentCommand request, CancellationToken cancellationToken)
    {
        var doc = await db.EmployeeDocuments.FirstOrDefaultAsync(d => d.Id == request.Id, cancellationToken).ConfigureAwait(false);
        if (doc is null)
        {
            return Error.NotFound("Document");
        }

        db.EmployeeDocuments.Remove(doc);
        audit.Record("Employee.DocumentDeleted", $"Document '{doc.Title}' deleted.", nameof(EmployeeDocument), doc.Id);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        files.Delete(doc.FilePath);
        return Unit.Value;
    }
}

/// <summary>Documents expiring within the given number of days (for reminders).</summary>
public sealed record ExpiringDocumentDto(Guid EmployeeId, string EmployeeCode, string EmployeeName, string Title, DateOnly ExpiryDate);

public sealed record GetExpiringDocumentsQuery(DateOnly Today, int WithinDays = 30) : IQuery<IReadOnlyList<ExpiringDocumentDto>>;

public sealed class GetExpiringDocumentsHandler(IAppDbContext db, ICurrentUser user) : IRequestHandler<GetExpiringDocumentsQuery, IReadOnlyList<ExpiringDocumentDto>>
{
    public async Task<Result<IReadOnlyList<ExpiringDocumentDto>>> HandleAsync(GetExpiringDocumentsQuery request, CancellationToken cancellationToken)
    {
        var until = request.Today.AddDays(request.WithinDays);
        return await (
            from d in db.EmployeeDocuments.AsNoTracking()
            join e in db.Employees on d.EmployeeId equals e.Id
            where e.CompanyId == user.CompanyId && e.Status != EmployeeStatus.Left && d.ExpiryDate != null && d.ExpiryDate <= until
            orderby d.ExpiryDate
            select new ExpiringDocumentDto(e.Id, e.Code, e.FullName, d.Title, d.ExpiryDate!.Value))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}

public sealed record HeadcountSummary(int Active, int Probation, int OnLeave, int JoinedThisMonth, int LeftThisMonth);

public sealed record GetHeadcountQuery(DateOnly Today) : IQuery<HeadcountSummary>;

public sealed class GetHeadcountHandler(IAppDbContext db, ICurrentUser user) : IRequestHandler<GetHeadcountQuery, HeadcountSummary>
{
    public async Task<Result<HeadcountSummary>> HandleAsync(GetHeadcountQuery request, CancellationToken cancellationToken)
    {
        var monthStart = new DateOnly(request.Today.Year, request.Today.Month, 1);
        var employees = db.Employees.AsNoTracking().Where(e => e.CompanyId == user.CompanyId && e.DeletedAtUtc == null);
        var byStatus = await employees.GroupBy(e => e.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var joined = await employees.CountAsync(e => e.JoinDate >= monthStart && e.JoinDate <= request.Today, cancellationToken).ConfigureAwait(false);
        var left = await employees.CountAsync(e => e.ExitDate >= monthStart && e.ExitDate <= request.Today, cancellationToken).ConfigureAwait(false);
        int Count(EmployeeStatus s) => byStatus.FirstOrDefault(x => x.Key == s)?.Count ?? 0;
        return new HeadcountSummary(Count(EmployeeStatus.Active), Count(EmployeeStatus.Probation), Count(EmployeeStatus.OnLeave), joined, left);
    }
}
