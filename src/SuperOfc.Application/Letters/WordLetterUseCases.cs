using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Common;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Letters;
using SuperOfc.Domain.Security;

namespace SuperOfc.Application.Letters;

/// <summary>How issued letters are saved: PDF (text templates, or Word templates through Microsoft Word) or the filled Word file.</summary>
public enum LetterOutput
{
    Pdf = 0,
    Word = 1,
}

/// <summary>What this PC can make: PDFs of Word templates need Microsoft Word.</summary>
public sealed record LetterOutputOptions(bool WordInstalled);

[RequiresPermission(Permission.ViewData)]
public sealed record GetLetterOutputOptionsQuery : IQuery<LetterOutputOptions>;

public sealed class GetLetterOutputOptionsHandler(IWordPdfConverter converter) : IRequestHandler<GetLetterOutputOptionsQuery, LetterOutputOptions>
{
    public Task<Result<LetterOutputOptions>> HandleAsync(GetLetterOutputOptionsQuery request, CancellationToken cancellationToken)
        => Task.FromResult(Result<LetterOutputOptions>.Success(new LetterOutputOptions(converter.IsAvailable)));
}

/// <summary>
/// Uploads a Word file as a letter template (ADR-24). With <see cref="TemplateId"/> the file replaces that template's layout
/// (a text template becomes a Word template); without it a new template is added, named after the file.
/// </summary>
[RequiresPermission(Permission.ManageEmployees)]
public sealed record ImportLetterTemplateDocumentCommand(Guid? TemplateId, string SourceFilePath) : ICommand<Guid>;

public sealed class ImportLetterTemplateDocumentHandler(IAppDbContext db, ICurrentUser user, IIdGenerator ids, IAuditWriter audit, LetterData data,
    IWordLetterService word) : IRequestHandler<ImportLetterTemplateDocumentCommand, Guid>
{
    public async Task<Result<Guid>> HandleAsync(ImportLetterTemplateDocumentCommand request, CancellationToken cancellationToken)
    {
        const string Field = nameof(ImportLetterTemplateDocumentCommand.SourceFilePath);
        var path = request.SourceFilePath?.Trim() ?? string.Empty;
        if (!string.Equals(Path.GetExtension(path), ".docx", StringComparison.OrdinalIgnoreCase))
        {
            return Error.Validation(Field, "Choose a Word file (.docx). Macro-enabled files (.docm) and old .doc files cannot be used.");
        }

        byte[] bytes;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return Error.Validation(Field, "The file was not found.");
            }

            if (info.Length > WordLetterFiles.MaxTemplateBytes)
            {
                return Error.Validation(Field, "The Word file is larger than 10 MB.");
            }

            bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Error.Validation(Field, "The file could not be read: " + ex.Message);
        }

        var check = word.Inspect(bytes);
        if (!check.IsValid)
        {
            return Error.Validation(Field, check.Error ?? "This Word file cannot be used as a letter template.");
        }

        var text = check.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (text.Length == 0)
        {
            return Error.Validation(Field, "The Word file has no text.");
        }

        // Body keeps the whole text so fields, inputs and the salary check always see every field; it is never cut.
        if (text.Length > WordLetterFiles.MaxBodyLength)
        {
            return Error.Validation(Field, "This Word file has too much text for a letter (over 20,000 characters).");
        }

        var codes = await data.ComponentCodesAsync(cancellationToken).ConfigureAwait(false);
        var unknown = LetterText.Fields(text).Where(f => !LetterFields.IsKnown(f, codes)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (unknown.Count > 0)
        {
            return Error.Validation(Field, $"Unknown field: {string.Join(", ", unknown.Select(f => "{" + f + "}"))}. Use fields from the list and upload the file again.");
        }

        var badFormats = LetterFields.FormatErrors(codes, text);
        if (badFormats.Count > 0)
        {
            return Error.Validation(Field, string.Join(" ", badFormats));
        }

        var fileName = Path.GetFileName(path);
        LetterTemplate template;
        if (request.TemplateId is { } id)
        {
            var found = await db.LetterTemplates.FirstOrDefaultAsync(t => t.Id == id && t.CompanyId == user.CompanyId && t.DeletedAtUtc == null, cancellationToken)
                .ConfigureAwait(false);
            if (found is null)
            {
                return Error.NotFound("LetterTemplate");
            }

            template = found;
        }
        else
        {
            var names = (await db.LetterTemplates.Where(t => t.CompanyId == user.CompanyId && t.DeletedAtUtc == null).Select(t => t.Name).ToListAsync(cancellationToken)
                .ConfigureAwait(false)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var order = await db.LetterTemplates.Where(t => t.CompanyId == user.CompanyId).Select(t => (int?)t.SortOrder).MaxAsync(cancellationToken).ConfigureAwait(false);
            var name = UniqueName(Path.GetFileNameWithoutExtension(path), names);
            template = new LetterTemplate
            {
                Id = ids.NewId(), CompanyId = user.CompanyId, Name = name, Kind = GuessKind(name), Subject = WordLetterFiles.Truncate(name, 300), NumberPrefix = "HR",
                IsActive = true, SortOrder = (order ?? -1) + 1,
            };
            db.LetterTemplates.Add(template);
        }

        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        template.Document = bytes;
        template.DocumentFileName = WordLetterFiles.Truncate(fileName, 260);
        template.DocumentSha256 = sha;
        template.Body = text;
        audit.Record("Letter.TemplateFileUploaded", $"{template.Name}: {fileName} (SHA-256 {sha})", nameof(LetterTemplate), template.Id);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return template.Id;
    }

    /// <summary>The kind of letter guessed from its name; <see cref="LetterKind.Other"/> when nothing matches.</summary>
    public static LetterKind GuessKind(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        bool Has(string word) => name.Contains(word, StringComparison.OrdinalIgnoreCase);
        return true switch
        {
            _ when Has("experience") => LetterKind.ExperienceCertificate,
            _ when Has("salary") => LetterKind.SalaryCertificate,
            _ when Has("appointment") => LetterKind.Appointment,
            _ when Has("confirmation") => LetterKind.Confirmation,
            _ when Has("increment") => LetterKind.Increment,
            _ when Has("promotion") => LetterKind.Promotion,
            _ when Has("transfer") => LetterKind.Transfer,
            _ when Has("relieving") || Has("release") => LetterKind.Relieving,
            _ when Has("warning") => LetterKind.Warning,
            _ => LetterKind.Other,
        };
    }

    private static string UniqueName(string fileName, HashSet<string> taken)
    {
        const int Max = 100;
        var name = fileName.Trim();
        if (name.Length == 0)
        {
            name = "Word letter";
        }

        name = WordLetterFiles.Truncate(name, Max).Trim();
        var candidate = name;
        for (var n = 2; taken.Contains(candidate); n++)
        {
            var suffix = $" ({n})";
            candidate = WordLetterFiles.Truncate(name, Max - suffix.Length).TrimEnd() + suffix;
        }

        return candidate;
    }
}

/// <summary>Saves a Word template's file, for example to edit it in Word and upload it again.</summary>
[RequiresPermission(Permission.ManageEmployees)]
public sealed record ExportLetterTemplateDocumentCommand(Guid TemplateId, string Path) : ICommand<string>;

public sealed class ExportLetterTemplateDocumentHandler(IAppDbContext db, ICurrentUser user) : IRequestHandler<ExportLetterTemplateDocumentCommand, string>
{
    public async Task<Result<string>> HandleAsync(ExportLetterTemplateDocumentCommand request, CancellationToken cancellationToken)
    {
        var document = await db.LetterTemplates.AsNoTracking()
            .Where(t => t.Id == request.TemplateId && t.CompanyId == user.CompanyId && t.DeletedAtUtc == null && t.Document != null)
            .Select(t => t.Document).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            return Error.NotFound("LetterTemplate");
        }

        try
        {
            await File.WriteAllBytesAsync(request.Path, document, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Error.Validation("Path", "The file could not be written: " + ex.Message);
        }

        return request.Path;
    }
}

/// <summary>Removes a template's Word file; the document text stays as the body, so it becomes a text template.</summary>
[RequiresPermission(Permission.ManageEmployees)]
public sealed record RemoveLetterTemplateDocumentCommand(Guid TemplateId) : ICommand<Unit>;

public sealed class RemoveLetterTemplateDocumentHandler(IAppDbContext db, ICurrentUser user, IAuditWriter audit) : IRequestHandler<RemoveLetterTemplateDocumentCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(RemoveLetterTemplateDocumentCommand request, CancellationToken cancellationToken)
    {
        var template = await db.LetterTemplates.FirstOrDefaultAsync(t => t.Id == request.TemplateId && t.CompanyId == user.CompanyId && t.DeletedAtUtc == null, cancellationToken)
            .ConfigureAwait(false);
        if (template is null || !template.IsWord)
        {
            return Error.NotFound("LetterTemplate");
        }

        var fileName = template.DocumentFileName;
        template.Document = null;
        template.DocumentFileName = null;
        template.DocumentSha256 = null;
        audit.Record("Letter.TemplateFileRemoved", $"{template.Name}: {fileName}", nameof(LetterTemplate), template.Id);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}

/// <summary>
/// A Word template filled for one employee and saved as .docx, or as .pdf through Word when <see cref="Path"/> ends with .pdf.
/// Fields without a value stay as typed. Nothing is numbered or kept.
/// </summary>
[RequiresPermission(Permission.ManageEmployees)]
public sealed record PreviewLetterDocumentCommand(Guid EmployeeId, Guid TemplateId, DateOnly? Date, IReadOnlyDictionary<string, string?>? Inputs, string Path)
    : ICommand<string>;

public sealed class PreviewLetterDocumentHandler(IAppDbContext db, ICurrentUser user, IClock clock, ICompanyContext company, LetterData data, IWordLetterService word,
    IWordPdfConverter converter) : IRequestHandler<PreviewLetterDocumentCommand, string>
{
    public async Task<Result<string>> HandleAsync(PreviewLetterDocumentCommand request, CancellationToken cancellationToken)
    {
        var template = await db.LetterTemplates.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == request.TemplateId && t.CompanyId == user.CompanyId && t.DeletedAtUtc == null, cancellationToken).ConfigureAwait(false);
        if (template?.Document is null)
        {
            return Error.NotFound("LetterTemplate");
        }

        var usesSalary = LetterFields.UsesPrivate(template.Subject, template.Body);
        if (usesSalary && !PermissionMatrix.Has(user.Role, Permission.ViewSalaries))
        {
            return Error.Forbidden;
        }

        var pdf = WordLetterFiles.IsPdf(request.Path);
        if (pdf && !converter.IsAvailable)
        {
            return Error.Validation("Output", WordLetterFiles.WordMissing);
        }

        var date = request.Date ?? clock.TodayIn(company.TimeZone);
        var values = await data.ValuesAsync([request.EmployeeId], date, usesSalary, cancellationToken).ConfigureAwait(false);
        if (!values.TryGetValue(request.EmployeeId, out var v))
        {
            return Error.NotFound("Employee");
        }

        v["Letter.Number"] = "(given when issued)";
        LetterIssue.AddInputs(v, request.Inputs);
        var filled = word.Fill(template.Document, v.Resolve, new List<string>());
        try
        {
            if (pdf)
            {
                await WordLetterFiles.ConvertAsync(converter, [(filled, request.Path)], null, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await File.WriteAllBytesAsync(request.Path, filled, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (WordConversionException ex)
        {
            return Error.Validation("Output", ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Error.Validation("Path", "The file could not be written: " + ex.Message);
        }

        return request.Path;
    }
}

/// <summary>File helpers for Word letters: limits, messages, and PDF conversion through a temporary folder.</summary>
public static class WordLetterFiles
{
    public const long MaxTemplateBytes = 10 * 1024 * 1024;

    public const int MaxBodyLength = 20_000;

    public const string WordMissing =
        "Microsoft Word is not installed on this PC, so PDFs of Word templates cannot be made. Save the letters as Word files instead.";

    public static bool IsPdf(string path) => string.Equals(Path.GetExtension(path), ".pdf", StringComparison.OrdinalIgnoreCase);

    public static string Truncate(string text, int max)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Length <= max ? text : text[..max];
    }

    /// <summary>
    /// Writes each document to a temporary folder, converts the batch in one Word session, then copies each PDF to its target
    /// (added to <paramref name="written"/> as it is copied).
    /// A failed conversion leaves the targets untouched. The temporary folder is always deleted.
    /// </summary>
    public static async Task ConvertAsync(IWordPdfConverter converter, IReadOnlyList<(byte[] Docx, string PdfPath)> documents,
        ICollection<string>? written, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(converter);
        ArgumentNullException.ThrowIfNull(documents);
        var temp = Path.Combine(Path.GetTempPath(), "SuperOfc", "letters", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var jobs = new List<WordPdfJob>();
            for (var i = 0; i < documents.Count; i++)
            {
                var docx = Path.Combine(temp, $"{i + 1}.docx");
                await File.WriteAllBytesAsync(docx, documents[i].Docx, cancellationToken).ConfigureAwait(false);
                jobs.Add(new WordPdfJob(docx, Path.Combine(temp, $"{i + 1}.pdf")));
            }

            await converter.ConvertAsync(jobs, cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < documents.Count; i++)
            {
                File.Copy(jobs[i].PdfPath, documents[i].PdfPath, overwrite: true);
                written?.Add(documents[i].PdfPath);
            }
        }
        finally
        {
            try
            {
                Directory.Delete(temp, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort: a file Word still holds is removed with the user's temp folder.
            }
        }
    }

    /// <summary>Deletes files written by a failed issue; best effort.</summary>
    public static void DeleteQuietly(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        foreach (var path in paths)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The file stays; the error shown to the user is the one that matters.
            }
        }
    }
}
