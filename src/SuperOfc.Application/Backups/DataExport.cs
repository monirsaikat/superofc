using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Common;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Security;

namespace SuperOfc.Application.Backups;

public sealed record DataExportResult(string Folder, int Files, int Tables, long Rows, TimeSpan Elapsed);

/// <summary>Writes every table as Excel (one workbook per module) and one JSON file, without secrets (P8-T3).</summary>
public interface IDataExportService
{
    Task<DataExportResult> ExportAllAsync(string folder, CancellationToken cancellationToken);
}

/// <summary>Builds the diagnostics zip (doc 07): recent logs, settings and devices without secrets, versions.</summary>
public interface IDiagnosticsService
{
    Task<string> CreateAsync(string zipPath, CancellationToken cancellationToken);
}

/// <summary>Exports all company data to a folder, for the owner's own records or for moving to another system.</summary>
[RequiresPermission(Permission.ManageBackups)]
public sealed record ExportAllDataCommand(string Folder) : ICommand<DataExportResult>;

public sealed class ExportAllDataHandler(IDataExportService export, IAppDbContext db, IAuditWriter audit) : IRequestHandler<ExportAllDataCommand, DataExportResult>
{
    public async Task<Result<DataExportResult>> HandleAsync(ExportAllDataCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Folder))
        {
            return Error.Validation("Folder", "Choose a folder.");
        }

        DataExportResult result;
        try
        {
            result = await export.ExportAllAsync(request.Folder, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Error.Validation("Folder", "The files could not be written: " + ex.Message);
        }

        audit.Record("Data.Exported", $"All data exported: {result.Tables} tables, {result.Rows:N0} rows, {result.Files} files");
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }
}

/// <summary>Saves the diagnostics zip. Any signed-in user may do this; it holds no salaries or secrets.</summary>
[RequiresPermission(Permission.ViewData)]
public sealed record ExportDiagnosticsCommand(string Path) : ICommand<string>;

public sealed class ExportDiagnosticsHandler(IDiagnosticsService diagnostics) : IRequestHandler<ExportDiagnosticsCommand, string>
{
    public async Task<Result<string>> HandleAsync(ExportDiagnosticsCommand request, CancellationToken cancellationToken)
    {
        try
        {
            return await diagnostics.CreateAsync(request.Path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Error.Validation("Path", "The file could not be written: " + ex.Message);
        }
    }
}
