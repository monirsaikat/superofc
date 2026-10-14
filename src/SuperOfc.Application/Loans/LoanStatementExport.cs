using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Common;
using SuperOfc.Application.Payroll;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Security;

namespace SuperOfc.Application.Loans;

/// <summary>Writes a loan statement PDF (P5-T2). Implemented with QuestPDF in Infrastructure.</summary>
public interface ILoanStatementPdfService
{
    void Write(PayslipCompany company, LoanDetail loan, string path);
}

[RequiresPermission(Permission.ViewSalaries)]
public sealed record ExportLoanStatementCommand(Guid LoanId, string Path) : ICommand<string>;

public sealed class ExportLoanStatementHandler(
    IAppDbContext db, ICurrentUser user, IClock clock, ICompanyContext company, IPolicyProvider policies, IFileStorage files, ILoanStatementPdfService pdf, IAuditWriter audit)
    : IRequestHandler<ExportLoanStatementCommand, string>
{
    public async Task<Result<string>> HandleAsync(ExportLoanStatementCommand request, CancellationToken cancellationToken)
    {
        var detail = await GetLoanHandler.DetailAsync(db, user, clock, company, policies, request.LoanId, cancellationToken).ConfigureAwait(false);
        if (detail.IsFailure)
        {
            return Result<string>.Failure(detail.Errors);
        }

        var info = company.Company ?? throw new InvalidOperationException("Company is not loaded.");
        try
        {
            pdf.Write(PayrollExportData.Header(info, files), detail.Value, request.Path);
        }
        catch (IOException ex)
        {
            return Error.Validation("Path", "The file could not be written: " + ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Error.Validation("Path", "The file could not be written: " + ex.Message);
        }

        audit.Record("Loan.StatementExported", $"Statement of {detail.Value.Summary.Number} saved as PDF", "Loan", request.LoanId);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return request.Path;
    }
}
