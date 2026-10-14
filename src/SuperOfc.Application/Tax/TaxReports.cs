using Microsoft.EntityFrameworkCore;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Common;
using SuperOfc.Application.Payroll;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Security;

namespace SuperOfc.Application.Tax;

public sealed record TaxStatementRow(int Year, int Month, Money Taxable, Money Deducted, Money ProjectedIncome, Money AnnualTax, IReadOnlyList<string> Explanation)
{
    public DateOnly MonthStart => new(Year, Month, 1);
}

/// <summary>An employee's tax for one fiscal year from finalized payroll (P6-T3). Totals equal the payslip deductions.</summary>
public sealed record TaxStatement(Guid EmployeeId, string EmployeeCode, string EmployeeName, string? TaxIdentifier, int FiscalYear, string Category, IReadOnlyList<TaxStatementRow> Rows,
    Money TotalTaxable, Money TotalDeducted, Money LatestAnnualTax, Money PriorEmployerIncome, Money PriorEmployerTax);

[RequiresPermission(Permission.ViewSalaries)]
public sealed record GetTaxStatementQuery(Guid EmployeeId, int FiscalYear) : IQuery<TaxStatement>;

public sealed class GetTaxStatementHandler(IAppDbContext db, ICurrentUser user) : IRequestHandler<GetTaxStatementQuery, TaxStatement>
{
    public Task<Result<TaxStatement>> HandleAsync(GetTaxStatementQuery request, CancellationToken cancellationToken)
        => BuildAsync(db, user, request.EmployeeId, request.FiscalYear, cancellationToken);

    internal static async Task<Result<TaxStatement>> BuildAsync(IAppDbContext db, ICurrentUser user, Guid employeeId, int fiscalYear, CancellationToken cancellationToken)
    {
        var employee = await db.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.Id == employeeId && e.CompanyId == user.CompanyId, cancellationToken).ConfigureAwait(false);
        if (employee is null)
        {
            return Error.NotFound("Employee");
        }

        var rows = await db.TaxComputations.AsNoTracking().Where(c => c.EmployeeId == employeeId && c.FiscalYear == fiscalYear)
            .OrderBy(c => c.Year).ThenBy(c => c.Month).ToListAsync(cancellationToken).ConfigureAwait(false);
        var profile = await db.EmployeeTaxProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.EmployeeId == employeeId && p.FiscalYear == fiscalYear, cancellationToken).ConfigureAwait(false);
        var list = rows.Select(c => new TaxStatementRow(c.Year, c.Month, new Money(c.TaxableThisMonthMinor), new Money(c.DeductedMinor), new Money(c.ProjectedIncomeMinor),
            new Money(c.AnnualTaxMinor), System.Text.Json.JsonSerializer.Deserialize<List<string>>(c.ExplanationJson) ?? [])).ToList();
        return new TaxStatement(employee.Id, employee.Code, employee.FullName, employee.TaxIdentifier, fiscalYear, profile?.Category ?? "General", list,
            new Money(list.Sum(r => r.Taxable.Minor)), new Money(list.Sum(r => r.Deducted.Minor)), list.LastOrDefault()?.AnnualTax ?? Money.Zero,
            new Money(profile?.PriorEmployerIncomeMinor ?? 0), new Money(profile?.PriorEmployerTaxMinor ?? 0));
    }
}

public interface ITaxStatementPdfService
{
    void Write(PayslipCompany company, TaxStatement statement, string path);
}

[RequiresPermission(Permission.ViewSalaries)]
public sealed record ExportTaxStatementCommand(Guid EmployeeId, int FiscalYear, string Path) : ICommand<string>;

public sealed class ExportTaxStatementHandler(IAppDbContext db, ICurrentUser user, ICompanyContext company, IFileStorage files, ITaxStatementPdfService pdf, IAuditWriter audit)
    : IRequestHandler<ExportTaxStatementCommand, string>
{
    public async Task<Result<string>> HandleAsync(ExportTaxStatementCommand request, CancellationToken cancellationToken)
    {
        var statement = await GetTaxStatementHandler.BuildAsync(db, user, request.EmployeeId, request.FiscalYear, cancellationToken).ConfigureAwait(false);
        if (statement.IsFailure)
        {
            return Result<string>.Failure(statement.Errors);
        }

        var info = company.Company ?? throw new InvalidOperationException("Company is not loaded.");
        try
        {
            pdf.Write(PayrollExportData.Header(info, files), statement.Value, request.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Error.Validation("Path", "The file could not be written: " + ex.Message);
        }

        audit.Record("Tax.StatementExported", $"Tax statement {statement.Value.EmployeeCode} {request.FiscalYear} saved as PDF", "Employee", request.EmployeeId);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return request.Path;
    }
}

public sealed record TaxSummaryRow(Guid EmployeeId, string EmployeeCode, string EmployeeName, string? TaxIdentifier, int Months, Money Taxable, Money Deducted, Money LatestAnnualTax);

public sealed record TaxSummary(int FiscalYear, IReadOnlyList<TaxSummaryRow> Rows, Money TotalTaxable, Money TotalDeducted);

/// <summary>Tax deducted per employee for a fiscal year (P6-T3 tax summary report).</summary>
[RequiresPermission(Permission.ViewSalaries)]
public sealed record GetTaxSummaryQuery(int FiscalYear) : IQuery<TaxSummary>;

public sealed class GetTaxSummaryHandler(IAppDbContext db, ICurrentUser user) : IRequestHandler<GetTaxSummaryQuery, TaxSummary>
{
    public async Task<Result<TaxSummary>> HandleAsync(GetTaxSummaryQuery request, CancellationToken cancellationToken)
    {
        var rows = await db.TaxComputations.AsNoTracking().Where(c => c.CompanyId == user.CompanyId && c.FiscalYear == request.FiscalYear).ToListAsync(cancellationToken).ConfigureAwait(false);
        var ids = rows.Select(r => r.EmployeeId).Distinct().ToList();
        var employees = await db.Employees.AsNoTracking().Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, cancellationToken).ConfigureAwait(false);
        var list = rows.GroupBy(r => r.EmployeeId).Select(g =>
        {
            var e = employees[g.Key];
            var latest = g.OrderBy(c => c.Year).ThenBy(c => c.Month).Last();
            return new TaxSummaryRow(e.Id, e.Code, e.FullName, e.TaxIdentifier, g.Count(), new Money(g.Sum(c => c.TaxableThisMonthMinor)), new Money(g.Sum(c => c.DeductedMinor)),
                new Money(latest.AnnualTaxMinor));
        }).OrderBy(r => r.EmployeeCode, StringComparer.OrdinalIgnoreCase).ToList();
        return new TaxSummary(request.FiscalYear, list, new Money(list.Sum(r => r.Taxable.Minor)), new Money(list.Sum(r => r.Deducted.Minor)));
    }
}

[RequiresPermission(Permission.ViewSalaries)]
public sealed record ExportTaxSummaryCommand(int FiscalYear, string Path) : ICommand<int>;

public sealed class ExportTaxSummaryHandler(IAppDbContext db, ICurrentUser user, ISpreadsheetService sheets, IAuditWriter audit) : IRequestHandler<ExportTaxSummaryCommand, int>
{
    public async Task<Result<int>> HandleAsync(ExportTaxSummaryCommand request, CancellationToken cancellationToken)
    {
        var summary = await new GetTaxSummaryHandler(db, user).HandleAsync(new GetTaxSummaryQuery(request.FiscalYear), cancellationToken).ConfigureAwait(false);
        var s = summary.Value;
        var rows = s.Rows.Select(r => (IReadOnlyList<object?>)[r.EmployeeCode, r.EmployeeName, r.TaxIdentifier, r.Months, r.Taxable.Major, r.Deducted.Major, r.LatestAnnualTax.Major])
            .Append([string.Empty, "Total", null, null, s.TotalTaxable.Major, s.TotalDeducted.Major, null]).ToList();
        try
        {
            sheets.Write(request.Path, [new SheetData($"Tax {request.FiscalYear}", ["Employee code", "Name", "Tax ID", "Months", "Taxable pay", "Tax deducted", "Projected yearly tax"], rows)]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Error.Validation("Path", "The file could not be written: " + ex.Message);
        }

        audit.Record("Tax.SummaryExported", $"Tax summary {request.FiscalYear} exported ({s.Rows.Count} employees)");
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return s.Rows.Count;
    }
}
