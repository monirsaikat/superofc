using System.Globalization;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Common;
using SuperOfc.Application.Payroll;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Security;

namespace SuperOfc.Application.Reports;

/// <summary>What a report parameter asks for. The Reports page draws one editor per kind.</summary>
public enum ReportParameterKind
{
    /// <summary>A calendar month, value "yyyy-MM".</summary>
    Month = 0,

    /// <summary>A date range, value "yyyy-MM-dd..yyyy-MM-dd".</summary>
    DateRange = 1,

    /// <summary>A fiscal or leave year named by its start year, value "2025".</summary>
    FiscalYear = 2,

    /// <summary>A department id, or empty for all.</summary>
    Department = 3,

    /// <summary>A leave type id, or empty for all.</summary>
    LeaveType = 4,

    /// <summary>One of <see cref="ReportParameter.Choices"/>.</summary>
    Choice = 5,
}

public sealed record ReportChoice(string Value, string Text);

public sealed record ReportParameter(string Name, string Label, ReportParameterKind Kind, bool Required = true, IReadOnlyList<ReportChoice>? Choices = null);

/// <summary>Raised by <see cref="ReportArgs"/> when a value is missing or malformed; becomes a validation error.</summary>
public sealed class ReportArgumentException(string parameter, string message) : Exception(message)
{
    public string Parameter { get; } = parameter;
}

/// <summary>Parameter values as invariant strings, so they can be saved, logged and sent to a server later.</summary>
public sealed record ReportArgs(IReadOnlyDictionary<string, string?> Values)
{
    public static ReportArgs Of(params (string Name, string? Value)[] values)
        => new(values.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal));

    public static string MonthValue(int year, int month) => $"{year:0000}-{month:00}";

    public static string RangeValue(DateOnly from, DateOnly to)
        => $"{from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}..{to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";

    public string? Text(string name) => Values.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

    public DateRange Month(string name)
    {
        var text = Text(name) ?? throw new ReportArgumentException(name, "Choose a month.");
        return DateTime.TryParseExact(text, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? DateRange.Month(d.Year, d.Month)
            : throw new ReportArgumentException(name, "Choose a month.");
    }

    public DateRange Range(string name, int maxDays = 400)
    {
        var parts = (Text(name) ?? string.Empty).Split("..");
        if (parts.Length != 2 || !DateOnly.TryParseExact(parts[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from)
            || !DateOnly.TryParseExact(parts[1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var to))
        {
            throw new ReportArgumentException(name, "Choose the first and last date.");
        }

        if (to < from)
        {
            throw new ReportArgumentException(name, "The last date is before the first date.");
        }

        var range = new DateRange(from, to);
        return range.Days > maxDays ? throw new ReportArgumentException(name, $"Choose at most {maxDays} days.") : range;
    }

    public int Year(string name)
        => int.TryParse(Text(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) && y is >= 2000 and <= 2100
            ? y
            : throw new ReportArgumentException(name, "Choose a year.");

    public Guid? Id(string name) => Guid.TryParse(Text(name), out var id) ? id : null;

    public string Choice(string name, string fallback) => Text(name) ?? fallback;
}

public enum ReportColumnKind
{
    Text = 0,
    Integer = 1,

    /// <summary>Decimal with up to two places (days, hours).</summary>
    Number = 2,

    /// <summary>Major currency units as decimal.</summary>
    Money = 3,
    Date = 4,
}

public sealed record ReportColumn(string Header, ReportColumnKind Kind = ReportColumnKind.Text, bool Total = false, float Width = 1);

/// <summary>A finished report: columns, rows of cell values (string, int, decimal, DateOnly or null), totals and notes.</summary>
public sealed record ReportTable(string Title, string Subtitle, IReadOnlyList<ReportColumn> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows, IReadOnlyList<object?>? Totals,
    IReadOnlyList<string> Notes)
{
    /// <summary>The sum of one numeric column over all rows (tests and totals).</summary>
    public decimal Sum(int column) => Rows.Sum(r => r[column] switch { int i => i, decimal d => d, long l => l, _ => 0m });

    public int ColumnIndex(string header) => Columns.ToList().FindIndex(c => c.Header == header);
}

/// <summary>Builds a <see cref="ReportTable"/>; totals are added for columns marked <see cref="ReportColumn.Total"/>.</summary>
public sealed class ReportBuilder(string title, string subtitle)
{
    private readonly List<ReportColumn> _columns = [];
    private readonly List<IReadOnlyList<object?>> _rows = [];
    private readonly List<string> _notes = [];

    public ReportBuilder Column(string header, ReportColumnKind kind = ReportColumnKind.Text, bool total = false, float width = 1)
    {
        _columns.Add(new ReportColumn(header, kind, total, width));
        return this;
    }

    public ReportBuilder Row(params object?[] cells)
    {
        if (cells.Length != _columns.Count)
        {
            throw new InvalidOperationException($"Row has {cells.Length} cells for {_columns.Count} columns.");
        }

        _rows.Add(cells);
        return this;
    }

    public ReportBuilder Note(string note)
    {
        _notes.Add(note);
        return this;
    }

    public int RowCount => _rows.Count;

    public ReportTable Build()
    {
        IReadOnlyList<object?>? totals = null;
        if (_columns.Any(c => c.Total) && _rows.Count > 0)
        {
            var cells = new object?[_columns.Count];
            for (var i = 0; i < _columns.Count; i++)
            {
                var column = _columns[i];
                if (column.Total)
                {
                    var sum = _rows.Sum(r => r[i] switch { int n => n, decimal d => d, long l => l, _ => 0m });
                    cells[i] = column.Kind == ReportColumnKind.Integer ? (object)(int)sum : sum;
                }
            }

            var label = _columns.FindIndex(c => !c.Total && c.Kind == ReportColumnKind.Text);
            if (label >= 0)
            {
                cells[label] = "Total";
            }

            totals = cells;
        }

        if (_rows.Count == 0)
        {
            _notes.Insert(0, "Nothing to show for these choices.");
        }

        return new ReportTable(title, subtitle, [.. _columns], [.. _rows], totals, [.. _notes]);
    }
}

/// <summary>
/// One report (P7-T1). Implementations live in Application and are found by assembly scan, so adding a report is
/// adding one class. Reports only read data.
/// </summary>
public interface IReport
{
    /// <summary>Stable id, used in saved settings and tests (kebab-case).</summary>
    string Id { get; }

    string Name { get; }

    string Category { get; }

    string Description { get; }

    Permission Permission { get; }

    IReadOnlyList<ReportParameter> Parameters { get; }

    Task<ReportTable> RunAsync(ReportArgs args, CancellationToken cancellationToken);
}

/// <summary>Shared helpers for reports.</summary>
public static class ReportText
{
    public static string MonthName(DateRange month) => month.From.ToString("MMMM yyyy", CultureInfo.InvariantCulture);

    public static string Range(DateRange range)
        => $"{range.From.ToString("d MMM yyyy", CultureInfo.InvariantCulture)} to {range.To!.Value.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}";

    public static string FiscalYear(int year, int startMonth) => startMonth == 1 ? year.ToString(CultureInfo.InvariantCulture) : $"{year}-{(year + 1) % 100:00}";
}

public sealed record ReportInfo(string Id, string Name, string Category, string Description, IReadOnlyList<ReportParameter> Parameters);

/// <summary>The reports the signed-in user may run, by category then name.</summary>
[RequiresPermission(Permission.ViewData)]
public sealed record GetReportsQuery : IQuery<IReadOnlyList<ReportInfo>>;

public sealed class GetReportsHandler(IEnumerable<IReport> reports, ICurrentUser user) : IRequestHandler<GetReportsQuery, IReadOnlyList<ReportInfo>>
{
    public Task<Result<IReadOnlyList<ReportInfo>>> HandleAsync(GetReportsQuery request, CancellationToken cancellationToken)
    {
        IReadOnlyList<ReportInfo> list = [.. reports.Where(r => PermissionMatrix.Has(user.Role, r.Permission))
            .OrderBy(r => r.Category, StringComparer.Ordinal).ThenBy(r => r.Name, StringComparer.Ordinal)
            .Select(r => new ReportInfo(r.Id, r.Name, r.Category, r.Description, r.Parameters))];
        return Task.FromResult(Result<IReadOnlyList<ReportInfo>>.Success(list));
    }
}

[RequiresPermission(Permission.ViewData)]
public sealed record RunReportQuery(string ReportId, ReportArgs Args) : IQuery<ReportTable>;

public sealed class RunReportHandler(IEnumerable<IReport> reports, ICurrentUser user) : IRequestHandler<RunReportQuery, ReportTable>
{
    public Task<Result<ReportTable>> HandleAsync(RunReportQuery request, CancellationToken cancellationToken)
        => RunAsync(reports, user, request.ReportId, request.Args, cancellationToken);

    internal static async Task<Result<ReportTable>> RunAsync(IEnumerable<IReport> reports, ICurrentUser user, string id, ReportArgs args, CancellationToken cancellationToken)
    {
        var report = reports.FirstOrDefault(r => r.Id == id);
        if (report is null)
        {
            return Error.NotFound("Report");
        }

        if (!PermissionMatrix.Has(user.Role, report.Permission))
        {
            return Error.Forbidden;
        }

        foreach (var p in report.Parameters.Where(p => p.Required && args.Text(p.Name) is null))
        {
            return Error.Validation(p.Name, $"Choose {p.Label.ToLowerInvariant()}.");
        }

        try
        {
            return await report.RunAsync(args, cancellationToken).ConfigureAwait(false);
        }
        catch (ReportArgumentException ex)
        {
            return Error.Validation(ex.Parameter, ex.Message);
        }
    }
}

public enum ReportFormat
{
    Pdf = 0,
    Excel = 1,
}

/// <summary>Renders any report table as a PDF with the shared header, footer and page numbers.</summary>
public interface IReportPdfService
{
    void Write(PayslipCompany company, ReportTable table, string path);
}

[RequiresPermission(Permission.ViewData)]
public sealed record ExportReportCommand(string ReportId, ReportArgs Args, string Path, ReportFormat Format) : ICommand<string>;

public sealed class ExportReportHandler(IEnumerable<IReport> reports, IAppDbContext db, ICurrentUser user, ICompanyContext company, IFileStorage files, IReportPdfService pdf,
    ISpreadsheetService sheets, IAuditWriter audit) : IRequestHandler<ExportReportCommand, string>
{
    public async Task<Result<string>> HandleAsync(ExportReportCommand request, CancellationToken cancellationToken)
    {
        var run = await RunReportHandler.RunAsync(reports, user, request.ReportId, request.Args, cancellationToken).ConfigureAwait(false);
        if (run.IsFailure)
        {
            return Result<string>.Failure(run.Errors);
        }

        var table = run.Value;
        try
        {
            if (request.Format == ReportFormat.Pdf)
            {
                var info = company.Company ?? throw new InvalidOperationException("Company is not loaded.");
                pdf.Write(PayrollExportData.Header(info, files), table, request.Path);
            }
            else
            {
                var rows = table.Rows.Select(r => (IReadOnlyList<object?>)[.. r]).ToList();
                if (table.Totals is { } totals)
                {
                    rows.Add([.. totals]);
                }

                if (table.Notes.Count > 0)
                {
                    rows.Add([]);
                    rows.AddRange(table.Notes.Select(n => (IReadOnlyList<object?>)[n]));
                }

                var name = new string(table.Title.Where(c => !"[]:*?/\\".Contains(c)).Take(31).ToArray());
                sheets.Write(request.Path, [new SheetData(name, [.. table.Columns.Select(c => c.Header)], rows)]);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Error.Validation("Path", "The file could not be written: " + ex.Message);
        }

        audit.Record("Report.Exported", $"{table.Title} ({table.Subtitle}) saved as {request.Format}");
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return request.Path;
    }
}
