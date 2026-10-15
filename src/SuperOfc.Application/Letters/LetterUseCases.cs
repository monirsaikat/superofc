using System.Globalization;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using SuperOfc.Application.Abstractions;
using SuperOfc.Application.Common;
using SuperOfc.Application.Payroll;
using SuperOfc.Domain.Common;
using SuperOfc.Domain.Letters;
using SuperOfc.Domain.Security;

namespace SuperOfc.Application.Letters;

// ---------- Templates ----------

public sealed record LetterTemplateDto(Guid Id, string Name, LetterKind Kind, string Subject, string Body, string NumberPrefix, bool IsActive, int SortOrder,
    bool IsWord, string? DocumentFileName)
{
    /// <summary>Fields typed when issuing, from <c>{Input.X}</c>.</summary>
    public IReadOnlyList<string> Inputs => LetterText.Inputs(Subject, Body);

    public bool UsesSalary => LetterFields.UsesPrivate(Subject, Body);
}

[RequiresPermission(Permission.ViewData)]
public sealed record GetLetterTemplatesQuery(bool ActiveOnly = false) : IQuery<IReadOnlyList<LetterTemplateDto>>;

public sealed class GetLetterTemplatesHandler(IAppDbContext db, ICurrentUser user) : IRequestHandler<GetLetterTemplatesQuery, IReadOnlyList<LetterTemplateDto>>
{
    public async Task<Result<IReadOnlyList<LetterTemplateDto>>> HandleAsync(GetLetterTemplatesQuery request, CancellationToken cancellationToken)
    {
        var query = db.LetterTemplates.AsNoTracking().Where(t => t.CompanyId == user.CompanyId && t.DeletedAtUtc == null);
        if (request.ActiveOnly)
        {
            query = query.Where(t => t.IsActive);
        }

        return await query.OrderBy(t => t.SortOrder).ThenBy(t => t.Name)
            .Select(t => new LetterTemplateDto(t.Id, t.Name, t.Kind, t.Subject, t.Body, t.NumberPrefix, t.IsActive, t.SortOrder, t.Document != null, t.DocumentFileName))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>The fields a letter can use, including one per salary component (shown in the template editor).</summary>
[RequiresPermission(Permission.ViewData)]
public sealed record GetLetterFieldsQuery : IQuery<IReadOnlyList<LetterField>>;

public sealed class GetLetterFieldsHandler(LetterData data) : IRequestHandler<GetLetterFieldsQuery, IReadOnlyList<LetterField>>
{
    public async Task<Result<IReadOnlyList<LetterField>>> HandleAsync(GetLetterFieldsQuery request, CancellationToken cancellationToken)
    {
        var codes = await data.ComponentCodesAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<LetterField> list =
        [
            .. LetterFields.All,
            .. codes.Order(StringComparer.OrdinalIgnoreCase)
                .Select(c => new LetterField("Salary." + c, $"Monthly amount of component {c}", Private: true, Type: LetterFieldType.Money)),
            new("Input.Reason", "Typed when the letter is issued (any name after Input.)", Type: LetterFieldType.Lines),
        ];
        return Result<IReadOnlyList<LetterField>>.Success(list);
    }
}

[RequiresPermission(Permission.ManageEmployees)]
public sealed record SaveLetterTemplateCommand(Guid? Id, string Name, LetterKind Kind, string Subject, string Body, string NumberPrefix, bool IsActive) : ICommand<Guid>;

public sealed class SaveLetterTemplateValidator : AbstractValidator<SaveLetterTemplateCommand>
{
    public SaveLetterTemplateValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Enter a name.").MaximumLength(100);
        RuleFor(x => x.Subject).NotEmpty().WithMessage("Enter a subject.").MaximumLength(300);
        RuleFor(x => x.Body).NotEmpty().WithMessage("Write the letter.").MaximumLength(20_000);
        RuleFor(x => x.NumberPrefix).NotEmpty().WithMessage("Enter a prefix such as HR.").MaximumLength(20)
            .Matches("^[A-Za-z0-9-]+$").WithMessage("Use letters, digits and dashes only.");
    }
}

public sealed class SaveLetterTemplateHandler(IAppDbContext db, ICurrentUser user, IIdGenerator ids, IAuditWriter audit, LetterData data)
    : IRequestHandler<SaveLetterTemplateCommand, Guid>
{
    public async Task<Result<Guid>> HandleAsync(SaveLetterTemplateCommand request, CancellationToken cancellationToken)
    {
        LetterTemplate? found = null;
        if (request.Id is { } id)
        {
            found = await db.LetterTemplates.FirstOrDefaultAsync(t => t.Id == id && t.CompanyId == user.CompanyId && t.DeletedAtUtc == null, cancellationToken)
                .ConfigureAwait(false);
            if (found is null)
            {
                return Error.NotFound("LetterTemplate");
            }
        }

        // A Word template's body is the document text; it changes only when a new file is uploaded.
        var body = found is { IsWord: true } ? found.Body : request.Body.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        var codes = await data.ComponentCodesAsync(cancellationToken).ConfigureAwait(false);
        var unknown = LetterText.Fields(request.Subject).Concat(LetterText.Fields(body)).Where(f => !LetterFields.IsKnown(f, codes)).Distinct().ToList();
        if (unknown.Count > 0)
        {
            return Error.Validation("Body", $"Unknown field: {string.Join(", ", unknown.Select(f => "{" + f + "}"))}. Pick fields from the list.");
        }

        var badFormats = LetterFields.FormatErrors(codes, request.Subject, body);
        if (badFormats.Count > 0)
        {
            return Error.Validation("Body", string.Join(" ", badFormats));
        }

        var name = request.Name.Trim();
        if (await db.LetterTemplates.AnyAsync(t => t.CompanyId == user.CompanyId && t.DeletedAtUtc == null && t.Id != request.Id && t.Name.ToLower() == name.ToLower(), cancellationToken)
                .ConfigureAwait(false))
        {
            return Error.Validation("Name", "Another letter has this name.");
        }

        LetterTemplate template;
        if (found is not null)
        {
            template = found;
        }
        else
        {
            var order = await db.LetterTemplates.Where(t => t.CompanyId == user.CompanyId).Select(t => (int?)t.SortOrder).MaxAsync(cancellationToken).ConfigureAwait(false);
            template = new LetterTemplate { Id = ids.NewId(), CompanyId = user.CompanyId, SortOrder = (order ?? -1) + 1 };
            db.LetterTemplates.Add(template);
        }

        template.Name = name;
        template.Kind = request.Kind;
        template.Subject = request.Subject.Trim();
        template.Body = body;
        template.NumberPrefix = request.NumberPrefix.Trim().ToUpperInvariant();
        template.IsActive = request.IsActive;
        audit.Record(request.Id is null ? "Letter.TemplateAdded" : "Letter.TemplateEdited", $"Letter template {template.Name}", nameof(LetterTemplate), template.Id);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return template.Id;
    }
}

[RequiresPermission(Permission.ManageEmployees)]
public sealed record DeleteLetterTemplateCommand(Guid Id) : ICommand<Unit>;

public sealed class DeleteLetterTemplateHandler(IAppDbContext db, ICurrentUser user, IClock clock, IAuditWriter audit) : IRequestHandler<DeleteLetterTemplateCommand, Unit>
{
    public async Task<Result<Unit>> HandleAsync(DeleteLetterTemplateCommand request, CancellationToken cancellationToken)
    {
        var template = await db.LetterTemplates.FirstOrDefaultAsync(t => t.Id == request.Id && t.CompanyId == user.CompanyId && t.DeletedAtUtc == null, cancellationToken)
            .ConfigureAwait(false);
        if (template is null)
        {
            return Error.NotFound("LetterTemplate");
        }

        // Issued letters keep their text, so the template can go.
        template.DeletedAtUtc = clock.UtcNow;
        audit.Record("Letter.TemplateDeleted", $"Letter template {template.Name}", nameof(LetterTemplate), template.Id);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}

/// <summary>Adds the standard letters that are missing (by name). Returns how many were added.</summary>
[RequiresPermission(Permission.ManageEmployees)]
public sealed record AddStandardLetterTemplatesCommand : ICommand<int>;

public sealed class AddStandardLetterTemplatesHandler(IAppDbContext db, ICurrentUser user, IIdGenerator ids, IAuditWriter audit)
    : IRequestHandler<AddStandardLetterTemplatesCommand, int>
{
    public async Task<Result<int>> HandleAsync(AddStandardLetterTemplatesCommand request, CancellationToken cancellationToken)
    {
        var existing = (await db.LetterTemplates.Where(t => t.CompanyId == user.CompanyId && t.DeletedAtUtc == null).Select(t => t.Name).ToListAsync(cancellationToken)
            .ConfigureAwait(false)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var order = existing.Count;
        var added = 0;
        foreach (var template in LetterDefaults.Templates(user.CompanyId, ids.NewId).Where(t => !existing.Contains(t.Name)))
        {
            template.SortOrder = order++;
            db.LetterTemplates.Add(template);
            added++;
        }

        if (added > 0)
        {
            audit.Record("Letter.StandardTemplatesAdded", $"{added} standard letter templates added");
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return added;
    }
}

// ---------- Preview and issue ----------

public sealed record LetterPreview(string Subject, string Body, IReadOnlyList<string> Missing);

/// <summary>
/// The letter for one employee with the fields filled. Pass <see cref="TemplateId"/>, or <see cref="Subject"/> and
/// <see cref="Body"/> for the template editor's live preview. Nothing is saved.
/// </summary>
[RequiresPermission(Permission.ManageEmployees)]
public sealed record PreviewLetterQuery(Guid EmployeeId, Guid? TemplateId, string? Subject, string? Body, DateOnly? Date, IReadOnlyDictionary<string, string?>? Inputs)
    : IQuery<LetterPreview>;

public sealed class PreviewLetterHandler(IAppDbContext db, ICurrentUser user, IClock clock, ICompanyContext company, LetterData data)
    : IRequestHandler<PreviewLetterQuery, LetterPreview>
{
    public async Task<Result<LetterPreview>> HandleAsync(PreviewLetterQuery request, CancellationToken cancellationToken)
    {
        var (subject, body) = (request.Subject ?? string.Empty, request.Body ?? string.Empty);
        if (request.TemplateId is { } id)
        {
            var template = await db.LetterTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id && t.CompanyId == user.CompanyId, cancellationToken).ConfigureAwait(false);
            if (template is null)
            {
                return Error.NotFound("LetterTemplate");
            }

            (subject, body) = (template.Subject, template.Body);
        }

        var withSalary = PermissionMatrix.Has(user.Role, Permission.ViewSalaries);
        var date = request.Date ?? clock.TodayIn(company.TimeZone);
        var values = await data.ValuesAsync([request.EmployeeId], date, withSalary, cancellationToken).ConfigureAwait(false);
        if (!values.TryGetValue(request.EmployeeId, out var v))
        {
            return Error.NotFound("Employee");
        }

        v["Letter.Number"] = "(given when issued)";
        LetterIssue.AddInputs(v, request.Inputs);
        var missing = new List<string>();
        var filledSubject = LetterText.Fill(subject, v.Resolve, missing);
        var filledBody = LetterText.Fill(body, v.Resolve, missing);
        return new LetterPreview(filledSubject, filledBody, missing);
    }
}

public sealed record LetterDocument(string Number, DateOnly Date, string Subject, string Body, string EmployeeCode, string EmployeeName);

/// <summary>Renders letters on the company letterhead, one per page.</summary>
public interface ILetterPdfService
{
    void Write(PayslipCompany company, IReadOnlyList<LetterDocument> letters, string path);
}

public sealed record IssueLettersResult(int Count, IReadOnlyList<string> Numbers, IReadOnlyList<string> Files);

/// <summary>
/// Issues a letter to each employee: fills the fields, gives each a reference number, keeps the text and saves the files.
/// <see cref="Path"/> is a file for one letter, or a folder when <see cref="OneFilePerEmployee"/> is set. A Word template makes
/// one file per employee whenever there are several (Word files are never merged), as .docx or as PDF made by Word.
/// </summary>
[RequiresPermission(Permission.ManageEmployees)]
public sealed record IssueLettersCommand(Guid TemplateId, IReadOnlyList<Guid> EmployeeIds, DateOnly Date, IReadOnlyDictionary<string, string?>? Inputs, string Path,
    bool OneFilePerEmployee = false, LetterOutput Output = LetterOutput.Pdf) : ICommand<IssueLettersResult>;

public sealed class IssueLettersHandler(IAppDbContext db, ICurrentUser user, ICompanyContext company, IIdGenerator ids, IFileStorage files, IAuditWriter audit,
    LetterData data, ILetterPdfService pdf, IWordLetterService word, IWordPdfConverter converter) : IRequestHandler<IssueLettersCommand, IssueLettersResult>
{
    public async Task<Result<IssueLettersResult>> HandleAsync(IssueLettersCommand request, CancellationToken cancellationToken)
    {
        if (request.EmployeeIds.Count == 0)
        {
            return Error.Validation("EmployeeIds", "Choose at least one employee.");
        }

        var template = await db.LetterTemplates.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == request.TemplateId && t.CompanyId == user.CompanyId && t.DeletedAtUtc == null, cancellationToken).ConfigureAwait(false);
        if (template is null)
        {
            return Error.NotFound("LetterTemplate");
        }

        var document = template.Document;
        if (document is null && request.Output == LetterOutput.Word)
        {
            return Error.Validation("Output", "Text templates are saved as PDF.");
        }

        if (document is not null && request.Output == LetterOutput.Pdf && !converter.IsAvailable)
        {
            return Error.Validation("Output", WordLetterFiles.WordMissing);
        }

        var usesSalary = LetterFields.UsesPrivate(template.Subject, template.Body);
        if (usesSalary && !PermissionMatrix.Has(user.Role, Permission.ViewSalaries))
        {
            return Error.Forbidden;
        }

        var values = await data.ValuesAsync(request.EmployeeIds, request.Date, usesSalary, cancellationToken).ConfigureAwait(false);
        var problems = new List<string>();
        var letters = new List<(IssuedLetter Letter, LetterValues Values)>();
        foreach (var id in request.EmployeeIds.Distinct())
        {
            if (!values.TryGetValue(id, out var v))
            {
                return Error.NotFound("Employee");
            }

            LetterIssue.AddInputs(v, request.Inputs);
            v["Letter.Number"] = "#";
            var missing = new List<string>();
            LetterText.Fill(template.Subject, v.Resolve, missing);
            if (document is not null)
            {
                word.Fill(document, v.Resolve, missing);
            }
            else
            {
                LetterText.Fill(template.Body, v.Resolve, missing);
            }

            if (missing.Count > 0)
            {
                problems.Add($"{v["Employee.Code"]} {v["Employee.Name"]}: no {string.Join(", ", missing.Distinct(StringComparer.OrdinalIgnoreCase).Select(LetterIssue.Describe))}");
            }

            letters.Add((new IssuedLetter { Id = ids.NewId(), CompanyId = user.CompanyId, EmployeeId = id }, v));
        }

        if (problems.Count > 0)
        {
            return Error.Validation("EmployeeIds", "Some letters would have blanks. Fill in these details first, or type them when issuing:\n" + string.Join("\n", problems));
        }

        // Reference numbers run per prefix and year: HR/2026/0001.
        var prefix = $"{template.NumberPrefix}/{request.Date.Year}/";
        var used = await db.IssuedLetters.Where(l => l.CompanyId == user.CompanyId && l.Number.StartsWith(prefix)).Select(l => l.Number).ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var next = used.Select(n => int.TryParse(n[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var x) ? x : 0).DefaultIfEmpty(0).Max() + 1;

        var documents = new List<LetterDocument>();
        var filled = new List<byte[]>();
        foreach (var (letter, v) in letters)
        {
            letter.Number = prefix + next++.ToString("0000", CultureInfo.InvariantCulture);
            v["Letter.Number"] = letter.Number;
            var ignored = new List<string>();
            letter.TemplateId = template.Id;
            letter.TemplateName = template.Name;
            letter.Kind = template.Kind;
            letter.IssuedOn = request.Date;
            letter.Subject = LetterText.Fill(template.Subject, v.Resolve, ignored);
            if (document is not null)
            {
                var file = word.Fill(document, v.Resolve, ignored);
                filled.Add(file);
                letter.Body = WordLetterFiles.Truncate(word.Text(file), WordLetterFiles.MaxBodyLength);
                letter.HasDocument = true;
                db.IssuedLetterDocuments.Add(new IssuedLetterDocument { Id = ids.NewId(), IssuedLetterId = letter.Id, Content = file });
            }
            else
            {
                letter.Body = LetterText.Fill(template.Body, v.Resolve, ignored);
            }

            letter.IncludesSalary = usesSalary;
            letter.IssuedBy = user.DisplayName;
            db.IssuedLetters.Add(letter);
            documents.Add(new LetterDocument(letter.Number, letter.IssuedOn, letter.Subject, letter.Body, v["Employee.Code"]!, v["Employee.Name"]!));
        }

        var written = new List<string>();
        try
        {
            if (document is not null)
            {
                await WriteWordAsync(request, template.Name, documents, filled, written, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var info = company.Company ?? throw new InvalidOperationException("Company is not loaded.");
                LetterIssue.Write(pdf, PayrollExportData.Header(info, files), documents, request.Path, request.OneFilePerEmployee, template.Name, written);
            }
        }
        catch (WordConversionException ex)
        {
            WordLetterFiles.DeleteQuietly(written);
            return Error.Validation("Output", ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            WordLetterFiles.DeleteQuietly(written);
            return Error.Validation("Path", "The file could not be written: " + ex.Message);
        }

        audit.Record("Letter.Issued", $"{template.Name}: {documents.Count} letters ({documents[0].Number}{(documents.Count > 1 ? " to " + documents[^1].Number : string.Empty)})",
            nameof(LetterTemplate), template.Id);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // Usually the same reference number taken by another PC between numbering and saving.
            WordLetterFiles.DeleteQuietly(written);
            return Error.Conflict("Another letter was issued at the same moment. Issue again.");
        }

        return new IssueLettersResult(documents.Count, [.. documents.Select(d => d.Number)], written);
    }

    private async Task WriteWordAsync(IssueLettersCommand request, string name, List<LetterDocument> documents, List<byte[]> filled, List<string> written,
        CancellationToken cancellationToken)
    {
        var asPdf = request.Output == LetterOutput.Pdf;
        var targets = new List<string>();
        if (documents.Count == 1 && !request.OneFilePerEmployee)
        {
            targets.Add(request.Path);
        }
        else
        {
            Directory.CreateDirectory(request.Path);
            targets.AddRange(documents.Select(d => System.IO.Path.Combine(request.Path, LetterIssue.Safe($"{name} {d.EmployeeCode} {d.EmployeeName}{(asPdf ? ".pdf" : ".docx")}"))));
        }

        if (asPdf)
        {
            // Targets are written only after Word has converted the whole batch.
            await WordLetterFiles.ConvertAsync(converter, [.. filled.Zip(targets)], written, cancellationToken).ConfigureAwait(false);
            return;
        }

        for (var i = 0; i < targets.Count; i++)
        {
            await File.WriteAllBytesAsync(targets[i], filled[i], cancellationToken).ConfigureAwait(false);
            written.Add(targets[i]);
        }
    }
}

/// <summary>Helpers shared by preview, issue and reprint.</summary>
public static class LetterIssue
{
    public static void AddInputs(LetterValues values, IReadOnlyDictionary<string, string?>? inputs)
    {
        ArgumentNullException.ThrowIfNull(values);
        values.AddInputs(inputs);
    }

    public static string Describe(string field)
        => field.StartsWith(LetterText.InputGroup + ".", StringComparison.OrdinalIgnoreCase)
            ? field[(LetterText.InputGroup.Length + 1)..] + " (typed when issuing)"
            : LetterFields.All.FirstOrDefault(f => string.Equals(f.Key, field, StringComparison.OrdinalIgnoreCase))?.Description.ToLowerInvariant() ?? field;

    /// <summary>
    /// Writes the letters as one PDF, or one PDF per employee into the folder <paramref name="path"/>. Each file is added to
    /// <paramref name="written"/> as soon as it is written, so a caller can delete them all when a later file fails.
    /// </summary>
    public static void Write(ILetterPdfService pdf, PayslipCompany header, IReadOnlyList<LetterDocument> documents, string path, bool perEmployee, string name,
        ICollection<string> written)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(written);
        if (!perEmployee)
        {
            pdf.Write(header, documents, path);
            written.Add(path);
            return;
        }

        Directory.CreateDirectory(path);
        foreach (var d in documents)
        {
            var file = System.IO.Path.Combine(path, Safe($"{name} {d.EmployeeCode} {d.EmployeeName}.pdf"));
            pdf.Write(header, [d], file);
            written.Add(file);
        }
    }

    public static string Safe(string name) => new([.. name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '-' : c)]);
}

// ---------- Register and reprint ----------

public sealed record IssuedLetterItem(Guid Id, string Number, DateOnly IssuedOn, Guid EmployeeId, string EmployeeCode, string EmployeeName, string TemplateName, LetterKind Kind,
    string Subject, string IssuedBy, bool IncludesSalary, bool HasDocument);

[RequiresPermission(Permission.ViewData)]
public sealed record GetIssuedLettersQuery(Guid? EmployeeId = null, string? Search = null, int Take = 500) : IQuery<IReadOnlyList<IssuedLetterItem>>;

public sealed class GetIssuedLettersHandler(IAppDbContext db, ICurrentUser user) : IRequestHandler<GetIssuedLettersQuery, IReadOnlyList<IssuedLetterItem>>
{
    public async Task<Result<IReadOnlyList<IssuedLetterItem>>> HandleAsync(GetIssuedLettersQuery request, CancellationToken cancellationToken)
    {
        var query = from l in db.IssuedLetters.AsNoTracking()
                    join e in db.Employees.AsNoTracking() on l.EmployeeId equals e.Id
                    where l.CompanyId == user.CompanyId
                    select new { l, e.Code, e.FullName };
        if (request.EmployeeId is { } id)
        {
            query = query.Where(x => x.l.EmployeeId == id);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim();
            query = query.Where(x => x.l.Number.Contains(term) || x.Code.Contains(term) || x.FullName.Contains(term) || x.l.TemplateName.Contains(term));
        }

        var rows = await query.OrderByDescending(x => x.l.IssuedOn).ThenByDescending(x => x.l.Number).Take(Math.Clamp(request.Take, 1, 5000))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(x => new IssuedLetterItem(x.l.Id, x.l.Number, x.l.IssuedOn, x.l.EmployeeId, x.Code, x.FullName, x.l.TemplateName, x.l.Kind, x.l.Subject,
            x.l.IssuedBy, x.l.IncludesSalary, x.l.HasDocument)).ToList();
    }
}

/// <summary>
/// Prints issued letters again exactly as issued (same number and text). A Word letter is its kept file, saved as .docx or
/// converted to PDF by Word when <see cref="Path"/> ends with .pdf; Word letters are reprinted one at a time.
/// </summary>
[RequiresPermission(Permission.ManageEmployees)]
public sealed record ReprintLettersCommand(IReadOnlyList<Guid> LetterIds, string Path) : ICommand<string>;

public sealed class ReprintLettersHandler(IAppDbContext db, ICurrentUser user, ICompanyContext company, IFileStorage files, ILetterPdfService pdf, IAuditWriter audit,
    IWordPdfConverter converter) : IRequestHandler<ReprintLettersCommand, string>
{
    public async Task<Result<string>> HandleAsync(ReprintLettersCommand request, CancellationToken cancellationToken)
    {
        var rows = await (from l in db.IssuedLetters.AsNoTracking()
                          join e in db.Employees.AsNoTracking() on l.EmployeeId equals e.Id
                          where l.CompanyId == user.CompanyId && request.LetterIds.Contains(l.Id)
                          orderby l.Number
                          select new { l, e.Code, e.FullName }).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return Error.NotFound("IssuedLetter");
        }

        if (rows.Any(r => r.l.IncludesSalary) && !PermissionMatrix.Has(user.Role, Permission.ViewSalaries))
        {
            return Error.Forbidden;
        }

        if (rows.Any(r => r.l.HasDocument))
        {
            if (rows.Count > 1)
            {
                return Error.Validation("LetterIds", "Reprint Word letters one at a time.");
            }

            var letterId = rows[0].l.Id;
            var content = await db.IssuedLetterDocuments.AsNoTracking().Where(d => d.IssuedLetterId == letterId).Select(d => d.Content)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (content is null)
            {
                return Error.NotFound("IssuedLetterDocument");
            }

            var asPdf = WordLetterFiles.IsPdf(request.Path);
            if (asPdf && !converter.IsAvailable)
            {
                return Error.Validation("Output", WordLetterFiles.WordMissing);
            }

            try
            {
                if (asPdf)
                {
                    await WordLetterFiles.ConvertAsync(converter, [(content, request.Path)], null, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await File.WriteAllBytesAsync(request.Path, content, cancellationToken).ConfigureAwait(false);
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

            return await AuditAsync(rows.Select(r => (r.l.Id, r.l.Number)).ToList(), request.Path, cancellationToken).ConfigureAwait(false);
        }

        var info = company.Company ?? throw new InvalidOperationException("Company is not loaded.");
        try
        {
            pdf.Write(PayrollExportData.Header(info, files), [.. rows.Select(r => new LetterDocument(r.l.Number, r.l.IssuedOn, r.l.Subject, r.l.Body, r.Code, r.FullName))],
                request.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Error.Validation("Path", "The file could not be written: " + ex.Message);
        }

        return await AuditAsync(rows.Select(r => (r.l.Id, r.l.Number)).ToList(), request.Path, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<string>> AuditAsync(List<(Guid Id, string Number)> letters, string path, CancellationToken cancellationToken)
    {
        audit.Record("Letter.Reprinted", $"Reprinted {string.Join(", ", letters.Select(l => l.Number))}", nameof(IssuedLetter), letters[0].Id);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return path;
    }
}
