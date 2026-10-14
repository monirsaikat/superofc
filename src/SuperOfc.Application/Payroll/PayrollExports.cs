using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Common;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Employees;
using SuperOfc.Domain.Payroll;
using SuperOfc.Domain.Security;

namespace SuperOfc.Application.Payroll;

/// <summary>Company details printed on every payslip.</summary>
public sealed record PayslipCompany(string Name, string? Address, string? Phone, string? Email, string? LogoFullPath, MoneyFormatter Money, string DateFormat);

public sealed record PayslipDocument(PayslipDto Payslip, string NetInWords);

/// <summary>Renders payslips to PDF (ADR-07, QuestPDF in Infrastructure). One page per payslip.</summary>
public interface IPayslipPdfService
{
    void Write(PayslipCompany company, IReadOnlyList<PayslipDocument> payslips, string path);

    /// <summary>The payslip page as a PNG image, for on-screen preview.</summary>
    byte[] RenderPreview(PayslipCompany company, PayslipDocument payslip, int dpi = 110);
}

public sealed record ExportResult(int Files, int Rows, string Path);

/// <summary>Writes payslip PDFs: one merged file, or one file per employee into a folder (P4-T5).</summary>
[RequiresPermission(Permission.ViewSalaries)]
public sealed record ExportPayslipsPdfCommand(Guid RunId, string Path, bool OneFilePerEmployee, IReadOnlyList<Guid>? PayslipIds = null) : ICommand<ExportResult>;

public sealed class ExportPayslipsPdfHandler(IAppDbContext db, ICurrentUser user, ICompanyContext company, IFileStorage files, IPayslipPdfService pdf, IAuditWriter audit)
    : IRequestHandler<ExportPayslipsPdfCommand, ExportResult>
{
    public async Task<Result<ExportResult>> HandleAsync(ExportPayslipsPdfCommand request, CancellationToken cancellationToken)
    {
        var data = await PayrollExportData.LoadAsync(db, user, request.RunId, request.PayslipIds, cancellationToken).ConfigureAwait(false);
        if (data.IsFailure)
        {
            return Result<ExportResult>.Failure(data.Errors);
        }

        var (run, period, slips) = data.Value;
        var info = company.Company ?? throw new InvalidOperationException("Company is not loaded.");
        var header = PayrollExportData.Header(info, files);
        var documents = slips.Select(s => new PayslipDocument(PayslipMapper.ToDto(s, run, period), AmountInWords.Format(s.Net, info.CurrencyCode, info.Grouping))).ToList();

        try
        {
            if (request.OneFilePerEmployee)
            {
                Directory.CreateDirectory(request.Path);
                foreach (var document in documents)
                {
                    var name = SafeFileName($"{period.Year}-{period.Month:00} {document.Payslip.EmployeeCode} {document.Payslip.EmployeeName}.pdf");
                    pdf.Write(header, [document], System.IO.Path.Combine(request.Path, name));
                }
            }
            else
            {
                pdf.Write(header, documents, request.Path);
            }
        }
        catch (IOException ex)
        {
            return Error.Validation("Path", "The file could not be written: " + ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Error.Validation("Path", "The file could not be written: " + ex.Message);
        }

        audit.Record("Payroll.PayslipsExported", $"{documents.Count} payslips of {period.FromDate:MMMM yyyy} saved as PDF", nameof(PayrollRun), run.Id);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new ExportResult(request.OneFilePerEmployee ? documents.Count : 1, documents.Count, request.Path);
    }

    internal static string SafeFileName(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}

public enum PaymentSheetKind
{
    Bank = 0,
    Cash = 1,
}

/// <summary>Bank transfer sheet (account, name, amount, reference) or cash sheet with a signature column (doc 10 Q15).</summary>
[RequiresPermission(Permission.ViewSalaries)]
public sealed record ExportPaymentSheetCommand(Guid RunId, string Path, PaymentSheetKind Kind) : ICommand<ExportResult>;

public sealed class ExportPaymentSheetHandler(IAppDbContext db, ICurrentUser user, ISpreadsheetService sheets, IAuditWriter audit) : IRequestHandler<ExportPaymentSheetCommand, ExportResult>
{
    public async Task<Result<ExportResult>> HandleAsync(ExportPaymentSheetCommand request, CancellationToken cancellationToken)
    {
        var data = await PayrollExportData.LoadAsync(db, user, request.RunId, null, cancellationToken).ConfigureAwait(false);
        if (data.IsFailure)
        {
            return Result<ExportResult>.Failure(data.Errors);
        }

        var (run, period, slips) = data.Value;
        var reference = $"Salary {period.FromDate.ToString("MMM yyyy", CultureInfo.InvariantCulture)}";
        SheetData sheet;
        int count;
        if (request.Kind == PaymentSheetKind.Bank)
        {
            var rows = slips.Where(s => s.PaymentMode is PaymentMode.Bank or PaymentMode.MobileBanking && s.NetMinor > 0).ToList();
            count = rows.Count;
            sheet = new SheetData("Bank transfer", ["Account number", "Account name", "Bank", "Amount", "Reference", "Employee code"],
                rows.Select(s => (IReadOnlyList<object?>)[s.BankAccount, s.EmployeeName, s.BankName, s.Net.Major, reference, s.EmployeeCode])
                    .Append(["", "Total", "", rows.Sum(s => s.Net.Major), "", ""]));
        }
        else
        {
            var rows = slips.Where(s => s.PaymentMode is PaymentMode.Cash or PaymentMode.Cheque && s.NetMinor > 0).ToList();
            count = rows.Count;
            sheet = new SheetData("Cash payment", ["Employee code", "Name", "Department", "Net pay", "Signature", "Date"],
                rows.Select(s => (IReadOnlyList<object?>)[s.EmployeeCode, s.EmployeeName, s.Department, s.Net.Major, "", ""])
                    .Append(["", "Total", "", rows.Sum(s => s.Net.Major), "", ""]));
        }

        try
        {
            sheets.Write(request.Path, [sheet]);
        }
        catch (IOException ex)
        {
            return Error.Validation("Path", "The file could not be written: " + ex.Message);
        }

        audit.Record("Payroll.PaymentSheetExported", $"{request.Kind} sheet for {period.FromDate:MMMM yyyy}: {count} employees", nameof(PayrollRun), run.Id);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new ExportResult(1, count, request.Path);
    }
}

public sealed record RegisterColumn(string Code, string Name, ComponentKind Kind);

public sealed record RegisterRow(string EmployeeCode, string EmployeeName, string? Department, decimal PayableDays, IReadOnlyDictionary<string, Money> Amounts, Money Gross, Money Deductions, Money Net);

public sealed record PayrollRegister(int Year, int Month, int RunNumber, PayrollRunStatus Status, IReadOnlyList<RegisterColumn> Columns, IReadOnlyList<RegisterRow> Rows, IReadOnlyList<RegisterRow> Departments, RegisterRow Total);

/// <summary>Every payslip line per employee, department totals and a grand total (P4-T6). Totals are exact sums of payslips.</summary>
[RequiresPermission(Permission.ViewSalaries)]
public sealed record GetPayrollRegisterQuery(Guid RunId) : IQuery<PayrollRegister>;

public sealed class GetPayrollRegisterHandler(IAppDbContext db, ICurrentUser user) : IRequestHandler<GetPayrollRegisterQuery, PayrollRegister>
{
    public async Task<Result<PayrollRegister>> HandleAsync(GetPayrollRegisterQuery request, CancellationToken cancellationToken)
    {
        var data = await PayrollExportData.LoadAsync(db, user, request.RunId, null, cancellationToken).ConfigureAwait(false);
        if (data.IsFailure)
        {
            return Result<PayrollRegister>.Failure(data.Errors);
        }

        var (run, period, slips) = data.Value;
        return Build(run, period, slips);
    }

    public static PayrollRegister Build(PayrollRun run, PayrollPeriod period, IReadOnlyList<Payslip> slips)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(period);
        ArgumentNullException.ThrowIfNull(slips);

        // Columns: each distinct line, earnings first in payslip order; one-off adjustments are grouped by name.
        static string Key(PayslipLine l) => l.Source == PayslipLineSource.Adjustment || l.Source == PayslipLineSource.Loan ? l.Name : l.Code;
        var columns = slips.SelectMany(s => s.Lines).GroupBy(Key)
            .Select(g => (Column: new RegisterColumn(g.Key, g.First().Name, g.First().Kind), Order: g.Min(l => l.Order)))
            .OrderBy(c => c.Column.Kind).ThenBy(c => c.Order).Select(c => c.Column).ToList();

        RegisterRow Row(string code, string name, string? department, decimal days, IEnumerable<Payslip> group)
        {
            var list = group.ToList();
            var amounts = columns.ToDictionary(c => c.Code, c => Money.Sum(list.SelectMany(s => s.Lines).Where(l => Key(l) == c.Code).Select(l => l.Amount)), StringComparer.Ordinal);
            return new RegisterRow(code, name, department, days, amounts, Money.Sum(list.Select(s => s.Gross)), Money.Sum(list.Select(s => s.Deductions)), Money.Sum(list.Select(s => s.Net)));
        }

        var rows = slips.OrderBy(s => s.EmployeeCode, StringComparer.OrdinalIgnoreCase).Select(s => Row(s.EmployeeCode, s.EmployeeName, s.Department, s.PayableDays, [s])).ToList();
        var departments = slips.GroupBy(s => s.Department ?? string.Empty).OrderBy(g => g.Key, StringComparer.CurrentCulture)
            .Select(g => Row(string.Empty, g.Key.Length == 0 ? "No department" : g.Key, g.Key, 0, g)).ToList();
        var total = Row(string.Empty, "Total", null, 0, slips);
        return new PayrollRegister(period.Year, period.Month, run.RunNumber, run.Status, columns, rows, departments, total);
    }
}

[RequiresPermission(Permission.ViewSalaries)]
public sealed record ExportPayrollRegisterCommand(Guid RunId, string Path) : ICommand<ExportResult>;

public sealed class ExportPayrollRegisterHandler(IAppDbContext db, ICurrentUser user, ISpreadsheetService sheets) : IRequestHandler<ExportPayrollRegisterCommand, ExportResult>
{
    public async Task<Result<ExportResult>> HandleAsync(ExportPayrollRegisterCommand request, CancellationToken cancellationToken)
    {
        var data = await PayrollExportData.LoadAsync(db, user, request.RunId, null, cancellationToken).ConfigureAwait(false);
        if (data.IsFailure)
        {
            return Result<ExportResult>.Failure(data.Errors);
        }

        var (run, period, slips) = data.Value;
        var register = GetPayrollRegisterHandler.Build(run, period, slips);
        var headers = new List<string> { "Code", "Name", "Department", "Payable days" };
        headers.AddRange(register.Columns.Select(c => c.Name));
        headers.AddRange(["Gross", "Deductions", "Net"]);

        IReadOnlyList<object?> Cells(RegisterRow r, bool withDays)
        {
            var cells = new List<object?> { r.EmployeeCode, r.EmployeeName, r.Department, withDays ? r.PayableDays : null };
            cells.AddRange(register.Columns.Select(c => (object?)r.Amounts[c.Code].Major));
            cells.AddRange([r.Gross.Major, r.Deductions.Major, r.Net.Major]);
            return cells;
        }

        var name = period.FromDate.ToString("MMM yyyy", CultureInfo.InvariantCulture);
        try
        {
            sheets.Write(request.Path,
            [
                new SheetData("Register " + name, headers, register.Rows.Select(r => Cells(r, true)).Append(Cells(register.Total, false))),
                new SheetData("Departments", headers, register.Departments.Select(r => Cells(r, false)).Append(Cells(register.Total, false))),
            ]);
        }
        catch (IOException ex)
        {
            return Error.Validation("Path", "The file could not be written: " + ex.Message);
        }

        return new ExportResult(1, register.Rows.Count, request.Path);
    }
}

/// <summary>One payslip rendered as a PNG page for the preview panel on the payroll page.</summary>
[RequiresPermission(Permission.ViewSalaries)]
public sealed record GetPayslipPreviewQuery(Guid RunId, Guid PayslipId) : IQuery<byte[]>;

public sealed class GetPayslipPreviewHandler(IAppDbContext db, ICurrentUser user, ICompanyContext company, IFileStorage files, IPayslipPdfService pdf)
    : IRequestHandler<GetPayslipPreviewQuery, byte[]>
{
    public async Task<Result<byte[]>> HandleAsync(GetPayslipPreviewQuery request, CancellationToken cancellationToken)
    {
        var data = await PayrollExportData.LoadAsync(db, user, request.RunId, [request.PayslipId], cancellationToken).ConfigureAwait(false);
        if (data.IsFailure)
        {
            return Result<byte[]>.Failure(data.Errors);
        }

        var (run, period, slips) = data.Value;
        var info = company.Company ?? throw new InvalidOperationException("Company is not loaded.");
        var slip = slips[0];
        var document = new PayslipDocument(PayslipMapper.ToDto(slip, run, period), AmountInWords.Format(slip.Net, info.CurrencyCode, info.Grouping));
        return await Task.Run(() => pdf.RenderPreview(PayrollExportData.Header(info, files), document), cancellationToken).ConfigureAwait(false);
    }
}

internal static class PayrollExportData
{
    public static PayslipCompany Header(Domain.Organization.Company info, IFileStorage files)
    {
        var logo = info.LogoPath is { Length: > 0 } relative ? files.GetFullPath(relative) : null;
        return new PayslipCompany(info.Name, info.Address, info.Phone, info.Email, logo is not null && File.Exists(logo) ? logo : null, MoneyFormatter.For(info), info.DateFormat);
    }

    public static async Task<Result<(PayrollRun Run, PayrollPeriod Period, IReadOnlyList<Payslip> Slips)>> LoadAsync(
        IAppDbContext db, ICurrentUser user, Guid runId, IReadOnlyList<Guid>? payslipIds, CancellationToken cancellationToken)
    {
        var run = await db.PayrollRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId && r.CompanyId == user.CompanyId, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return Error.NotFound("PayrollRun");
        }

        var period = await db.PayrollPeriods.AsNoTracking().FirstAsync(p => p.Id == run.PeriodId, cancellationToken).ConfigureAwait(false);
        var query = db.Payslips.AsNoTracking().Include(p => p.Lines).Where(p => p.RunId == run.Id);
        if (payslipIds is { Count: > 0 })
        {
            query = query.Where(p => payslipIds.Contains(p.Id));
        }

        var slips = await query.OrderBy(p => p.EmployeeCode).ToListAsync(cancellationToken).ConfigureAwait(false);
        return slips.Count == 0 ? Error.Validation("There are no payslips to export.") : (run, period, slips);
    }
}
