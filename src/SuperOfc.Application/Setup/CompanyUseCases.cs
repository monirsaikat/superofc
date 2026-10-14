using FluentValidation;
using Microsoft.EntityFrameworkCore;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Auth;
using SuperOfc.Application.Common;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Organization;
using SuperOfc.Domain.Security;

namespace SuperOfc.Application.Setup;

public sealed record SetupStatus(bool IsSetupComplete, Guid? CompanyId, string? CompanyName);

[AllowAnonymous]
public sealed record GetSetupStatusQuery : IQuery<SetupStatus>;

public sealed class GetSetupStatusHandler(IAppDbContext db) : IRequestHandler<GetSetupStatusQuery, SetupStatus>
{
    public async Task<Result<SetupStatus>> HandleAsync(GetSetupStatusQuery request, CancellationToken cancellationToken)
    {
        var company = await db.Companies.AsNoTracking().OrderBy(c => c.CreatedAtUtc).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var hasAdmin = company is not null && await db.Users.AnyAsync(u => u.Role == Role.Admin && u.IsActive, cancellationToken).ConfigureAwait(false);
        return new SetupStatus(company is { IsSetupComplete: true } && hasAdmin, company?.Id, company?.Name);
    }
}

public sealed record CompanyDto(
    Guid Id,
    string Name,
    string? Address,
    string? Phone,
    string? Email,
    string? TaxIdentifier,
    string? LogoPath,
    string CurrencyCode,
    string CurrencySymbol,
    NumberGrouping Grouping,
    string TimeZoneId,
    int FiscalYearStartMonth,
    string DateFormat,
    WorkingWeek WorkingWeek)
{
    public static CompanyDto From(Company c) => new(
        c.Id, c.Name, c.Address, c.Phone, c.Email, c.TaxIdentifier, c.LogoPath, c.CurrencyCode, c.CurrencySymbol,
        c.Grouping, c.TimeZoneId, c.FiscalYearStartMonth, c.DateFormat, c.WorkingWeek);
}

/// <summary>Validation shared by setup and company settings.</summary>
internal static class CompanyRules
{
    public static void Apply<T>(AbstractValidator<T> v, Func<T, string> name, Func<T, string> currency, Func<T, string> symbol, Func<T, string> tz, Func<T, int> fyMonth, Func<T, string> dateFormat)
    {
        v.RuleFor(x => name(x)).NotEmpty().WithMessage("Enter the company name.").MaximumLength(200).OverridePropertyName("Name");
        v.RuleFor(x => currency(x)).NotEmpty().Length(3).WithMessage("Use a 3-letter currency code such as BDT or USD.").OverridePropertyName("CurrencyCode");
        v.RuleFor(x => symbol(x)).NotEmpty().MaximumLength(5).OverridePropertyName("CurrencySymbol");
        v.RuleFor(x => tz(x)).Must(BeKnownTimeZone).WithMessage("Choose a valid time zone.").OverridePropertyName("TimeZoneId");
        v.RuleFor(x => fyMonth(x)).InclusiveBetween(1, 12).OverridePropertyName("FiscalYearStartMonth");
        v.RuleFor(x => dateFormat(x)).Must(BeValidDateFormat).WithMessage("The date format is not valid.").OverridePropertyName("DateFormat");
    }

    private static bool BeKnownTimeZone(string id)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(id) && TimeZoneInfo.FindSystemTimeZoneById(id) is not null;
        }
        catch (TimeZoneNotFoundException)
        {
            return false;
        }
        catch (InvalidTimeZoneException)
        {
            return false;
        }
    }

    private static bool BeValidDateFormat(string format)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            return false;
        }

        try
        {
            _ = new DateTime(2026, 12, 31).ToString(format, System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

[AllowAnonymous]
public sealed record CompleteSetupCommand(
    string CompanyName,
    string CurrencyCode,
    string CurrencySymbol,
    NumberGrouping Grouping,
    string TimeZoneId,
    int FiscalYearStartMonth,
    string DateFormat,
    WorkingWeek WorkingWeek,
    string AdminUsername,
    string AdminDisplayName,
    string AdminPassword,
    bool LoadSampleData) : ICommand<Guid>;

public sealed class CompleteSetupValidator : AbstractValidator<CompleteSetupCommand>
{
    public CompleteSetupValidator()
    {
        CompanyRules.Apply(this, x => x.CompanyName, x => x.CurrencyCode, x => x.CurrencySymbol, x => x.TimeZoneId, x => x.FiscalYearStartMonth, x => x.DateFormat);
        RuleFor(x => x.AdminUsername).NotEmpty().MaximumLength(50).Matches("^[A-Za-z0-9._-]+$").WithMessage("Use letters, numbers, dot, dash or underscore.");
        RuleFor(x => x.AdminDisplayName).NotEmpty().MaximumLength(100);
        RuleFor(x => x.AdminPassword).SetValidator(new PasswordRule());
    }
}

public sealed class CompleteSetupHandler(
    IAppDbContext db,
    IIdGenerator ids,
    IPasswordHasher hasher,
    IUserSession session,
    IAuditWriter audit,
    ISampleDataSeeder sampleData,
    ICompanyContext companyContext) : IRequestHandler<CompleteSetupCommand, Guid>
{
    public async Task<Result<Guid>> HandleAsync(CompleteSetupCommand request, CancellationToken cancellationToken)
    {
        if (await db.Companies.AnyAsync(c => c.IsSetupComplete, cancellationToken).ConfigureAwait(false))
        {
            return new Error("Setup.AlreadyDone", "Setup has already been completed.");
        }

        var company = new Company
        {
            Id = ids.NewId(),
            Name = request.CompanyName.Trim(),
            CurrencyCode = request.CurrencyCode.Trim().ToUpperInvariant(),
            CurrencySymbol = request.CurrencySymbol.Trim(),
            Grouping = request.Grouping,
            TimeZoneId = request.TimeZoneId,
            FiscalYearStartMonth = request.FiscalYearStartMonth,
            DateFormat = request.DateFormat,
            WorkingWeek = request.WorkingWeek,
            IsSetupComplete = true,
        };
        var admin = new User
        {
            Id = ids.NewId(),
            CompanyId = company.Id,
            Username = request.AdminUsername.Trim(),
            DisplayName = request.AdminDisplayName.Trim(),
            PasswordHash = hasher.Hash(request.AdminPassword),
            Role = Role.Admin,
        };

        await using (var tx = await db.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
        {
            db.Companies.Add(company);
            db.Users.Add(admin);
            db.Shifts.Add(Attendance.AttendanceDefaults.GeneralShift(company.Id, ids.NewId()));
            foreach (var leaveType in Attendance.AttendanceDefaults.LeaveTypes(company.Id, ids.NewId))
            {
                db.LeaveTypes.Add(leaveType);
            }

            var (components, structure) = Payroll.PayrollDefaults.Create(company.Id, ids.NewId);
            db.SalaryComponents.AddRange(components);
            db.SalaryStructures.Add(structure);
            db.LetterTemplates.AddRange(Letters.LetterDefaults.Templates(company.Id, ids.NewId));

            session.SetCompany(company.Id);
            audit.Record("Setup.Completed", $"Company '{company.Name}' created with administrator {admin.Username}.", nameof(Company), company.Id);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        await companyContext.RefreshAsync(cancellationToken).ConfigureAwait(false);

        if (request.LoadSampleData)
        {
            await sampleData.SeedAsync(company.Id, cancellationToken).ConfigureAwait(false);
        }

        return company.Id;
    }
}

public sealed record GetCompanyQuery : IQuery<CompanyDto>;

public sealed class GetCompanyHandler(IAppDbContext db, ICurrentUser current) : IRequestHandler<GetCompanyQuery, CompanyDto>
{
    public async Task<Result<CompanyDto>> HandleAsync(GetCompanyQuery request, CancellationToken cancellationToken)
    {
        var company = await db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == current.CompanyId, cancellationToken).ConfigureAwait(false);
        return company is null ? Error.NotFound("Company") : CompanyDto.From(company);
    }
}

[RequiresPermission(Permission.ManageSettings)]
public sealed record UpdateCompanyCommand(
    string Name,
    string? Address,
    string? Phone,
    string? Email,
    string? TaxIdentifier,
    string? LogoPath,
    string CurrencyCode,
    string CurrencySymbol,
    NumberGrouping Grouping,
    string TimeZoneId,
    int FiscalYearStartMonth,
    string DateFormat,
    WorkingWeek WorkingWeek) : ICommand<Unit>;

public sealed class UpdateCompanyValidator : AbstractValidator<UpdateCompanyCommand>
{
    public UpdateCompanyValidator()
    {
        CompanyRules.Apply(this, x => x.Name, x => x.CurrencyCode, x => x.CurrencySymbol, x => x.TimeZoneId, x => x.FiscalYearStartMonth, x => x.DateFormat);
        RuleFor(x => x.Email).EmailAddress().When(x => !string.IsNullOrWhiteSpace(x.Email));
    }
}

public sealed class UpdateCompanyHandler(IAppDbContext db, ICurrentUser current, IAuditWriter audit, ICompanyContext companyContext)
    : IRequestHandler<UpdateCompanyCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(UpdateCompanyCommand request, CancellationToken cancellationToken)
    {
        var company = await db.Companies.FirstOrDefaultAsync(c => c.Id == current.CompanyId, cancellationToken).ConfigureAwait(false);
        if (company is null)
        {
            return Error.NotFound("Company");
        }

        var before = CompanyDto.From(company);
        company.Name = request.Name.Trim();
        company.Address = request.Address?.Trim();
        company.Phone = request.Phone?.Trim();
        company.Email = request.Email?.Trim();
        company.TaxIdentifier = request.TaxIdentifier?.Trim();
        company.LogoPath = request.LogoPath;
        company.CurrencyCode = request.CurrencyCode.Trim().ToUpperInvariant();
        company.CurrencySymbol = request.CurrencySymbol.Trim();
        company.Grouping = request.Grouping;
        company.TimeZoneId = request.TimeZoneId;
        company.FiscalYearStartMonth = request.FiscalYearStartMonth;
        company.DateFormat = request.DateFormat;
        company.WorkingWeek = request.WorkingWeek;
        audit.Record("Company.Updated", "Company settings updated.", nameof(Company), company.Id, before, CompanyDto.From(company));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await companyContext.RefreshAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}
