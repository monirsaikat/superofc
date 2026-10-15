# Spec: Word letter templates (exact-match letters)

Status: in progress on branch `feature/word-letter-templates`. This spec is the contract for everyone implementing the feature.

## Goal

The company's real letters live as Word files in `doc-files/` (NOC, Certificate of Employment, Experience Certificate, Salary
Certificate). Letters printed by SuperOfc must match them **exactly**: same wording, fonts (Monotype Corsiva, Bookman Old Style,
Times New Roman), sizes, alignment, bold, superscript ordinals ("1<sup>st</sup>"), tables, bullet lists, headers, footers and the
blank space left for a printed letterhead.

The only way to guarantee that is to use the Word file itself as the template. HR types fields into the Word file; SuperOfc fills
the fields and changes nothing else.

## Already done (commit 46c714c)

- `LetterText` (Domain) understands `{Group.Field:format}`. API: `Placeholders`, `Fields`, `Inputs`, `Plan` (list of
  `LetterReplacement(Index, Length, Value)`, positions in the original text, escapes `{{`/`}}` included), `Apply`,
  `Fill(text, LetterResolver, missing)`, `Fill(text, dictionary, missing)` (formats ignored). `LetterResolver` is
  `string? (string key, string? format)`. Capitalization at sentence start is done by `Plan`.
- Contracts in `src/SuperOfc.Application/Letters/WordLetters.cs`: `IWordLetterService` (Inspect, Fill, Text),
  `WordTemplateCheck`, `IWordPdfConverter` (IsAvailable, ConvertAsync), `WordPdfJob`, `WordConversionException`.
- Entities: `LetterTemplate.Document` (byte[]?), `DocumentFileName`, `DocumentSha256`, `IsWord` (computed, not mapped);
  `IssuedLetter.HasDocument`; new `IssuedLetterDocument : Entity { IssuedLetterId, Content }` (1:1, cascade);
  `Employee.FatherName`. **No EF configuration or migration yet.**

## Field formats

A field may carry a format after a colon: `{Key:format}`. Formats are validated when a template is saved or uploaded; an unknown
format is a validation error naming the field, e.g. `Unknown format "xyz" in {Employee.Name:xyz}.`

Every field has a type (`LetterFieldType`): `Text`, `Lines` (may hold several lines), `Date`, `Money`.

| Type | No format | Formats |
|------|-----------|---------|
| Text, Lines | value as stored | `upper`, `lower`, `cap` (first letter upper case), `title` (Title Case) |
| Date | company date format, current culture (unchanged behaviour) | `ord`: the ordinal suffix of the day only (`st`, `nd`, `rd`, `th`; 11–13 give `th`). Anything else is a .NET custom date format rendered with the **en-US** culture; a one-character format is treated as custom (`d` gives `1`, not a short date). An invalid pattern is a validation error. Examples: `%d` or `d` gives `1`, `d MMMM yyyy` gives `1 June 2023`, `MMMM d, yyyy` gives `June 1, 2023`. |
| Money | `MoneyFormatter.Format` (symbol, 2 decimals; unchanged) | `n0` (grouped number, no symbol, no decimals: `70,000`), `n2` (`70,000.00`), `words` (whole major units in words, no currency, no "Only": `Seventy Thousand`; uses `AmountInWords.Words` with the company grouping) |

Format names are case-insensitive except date patterns, which are passed as typed.

Superscript ordinals: write `{Employee.JoinDate:%d}{Employee.JoinDate:ord} {Employee.JoinDate:MMMM yyyy}` and make the `ord`
field superscript in Word. The filled value takes the formatting of the run where the field starts, so the suffix stays superscript.

## New and changed fields

| Key | Type | Value |
|-----|------|-------|
| `Employee.FatherName` | Text | `Employee.FatherName` (new column, max 200). Shown and edited on the employee profile; optional column "Father's name" in employee import and export. |
| `Employee.SonDaughter` | Text | `son` (Male), `daughter` (Female), empty otherwise. Use `{Employee.SonDaughter:cap}` for "Son of". |
| `Employee.Responsibilities` | Lines | `Designation.Description` of the employee's designation (one responsibility per line). |
| `Company.CurrencyCode` | Text | `Company.CurrencyCode` (e.g. `BDT`). |
| `Company.CurrencySymbol` | Text | `Company.CurrencySymbol` (e.g. `৳`). |
| All dates (`Employee.JoinDate`, `ConfirmationDate`, `ExitDate`, `DateOfBirth`, `Salary.EffectiveFrom`, `Salary.NewEffectiveFrom`, `Letter.Date`, `Today`) | Date | as before, plus formats |
| All `Salary.*` amounts and `Salary.<CODE>` | Money | as before, plus formats |
| `Employee.Address` | Lines | as before |
| `Input.*` | Lines | as typed; inputs accept several lines |

## Word templates

- A template is a **Word template** when `Document` is set. `Body` then holds the document text from `IWordLetterService.Inspect`
  (paragraphs separated by a blank line), so `Fields`, `Inputs`, `UsesSalary`, unknown-field checks and search keep working.
  `Subject` is still edited in settings (used for the register and file names).
- Upload rules (Application command, Infrastructure check): `.docx` only, at most 10 MB, not macro-enabled (`.docm` content type or
  `vbaProject` part), not encrypted or password protected, no external relationships other than `http`, `https` and `mailto`
  hyperlinks, no `altChunk`, no `INCLUDETEXT`, `INCLUDE`, `INCLUDEPICTURE`, `IMPORT`, `LINK`, `DDE`, `DDEAUTO`, `DATABASE`, `RD`
  fields, no field whose instruction contains another field, no `w:updateFields`, no `{field}` split by a picture, note reference,
  symbol or field code. Unknown fields or formats are a validation error listing them.
- Filling (Infrastructure, Open XML SDK 3.1.1, direct package reference): per paragraph, join the text of all `w:t` in runs that
  are children of the paragraph (also inside `w:hyperlink`, `w:smartTag`, `w:fldSimple`, `w:ins`), skipping `w:proofErr`,
  bookmarks, `w:lastRenderedPageBreak`, `w:commentReference`; a `w:tab`, `w:br`, `w:cr`, drawing, note reference, symbol or field
  code inside a run ends a segment so a field never spans it (a `w:softHyphen` does not; one inside a field is removed). Run
  `LetterText.Plan` on the joined text, then write each value into the `w:t` where the field starts (keeping that run's `rPr`),
  remove the covered characters from following `w:t`, and drop runs left empty. Set `xml:space="preserve"`. A field with a value
  that still cannot be filled is added to `missing`, so it blocks issuing like a blank. Values are made XML-safe first: vertical
  tab and form feed become line breaks, other characters XML 1.0 does not allow are dropped.
  Parts: main document, every header and footer, footnotes, endnotes. `Descendants<Paragraph>()` reaches tables and text boxes
  (both the `mc:Choice` and `mc:Fallback` copies of a text box must be filled). Remove `w:proofErr`; remove `w:proofState` from
  settings. Multi-line values: if the field is the whole text of its paragraph, clone the paragraph once per line (bullets repeat);
  otherwise insert `w:br` between lines. Copies hold only the text runs (no drawings, text boxes, objects, note or comment marks,
  fields or bookmarks), and a section break (`w:sectPr`) moves to the last copy.
- Issuing a Word template: fill per employee, keep the filled file in `IssuedLetterDocument`, store `Subject` and the filled text
  (truncated to 20,000) in `IssuedLetter` with `HasDocument = true`.
  - Output format `Pdf` (made by Word via `IWordPdfConverter`, needs Word installed) or `Word` (.docx as filled).
  - One employee: one file at the chosen path. Several employees: one file per employee in the chosen folder (Word files are never
    merged); `OneFilePerEmployee` is forced on.
  - Files are written before the database save as today; if the save fails, delete the files written and return an error.
- Text templates keep working exactly as today (QuestPDF PDF, one file or one per employee).
- Reprint of a Word letter: write the stored file (`.docx`) or convert it (`.pdf`) by the extension of the chosen path; never fill again.
- Word conversion (Infrastructure): one dedicated STA thread; late-bound COM (`Type.GetTypeFromProgID("Word.Application")`, `dynamic`);
  `Visible=false`, `DisplayAlerts=0`, `AutomationSecurity=3`, `Documents.Open(path, ConfirmConversions:false, ReadOnly:true,
  AddToRecentFiles:false, PasswordDocument:"", WritePasswordDocument:"")` (positional), `ExportAsFixedFormat(pdf, 17)`,
  `Close(0)`, `Quit(0)`, release COM objects; an `IMessageFilter` retries busy calls; per-document timeout 120 s, then kill the
  WINWORD process this session started: found by setting a unique `Caption` and reading the process of that `OpusApp` window, or
  else only a new process whose command line has `/Automation` or `-Embedding`. A Word that ran before, or that the user starts,
  is never killed. Word missing: `IsAvailable` false and a clear message. Tests never start Word unless the
  environment variable `SUPEROFC_WORD_TESTS=1` is set.

## Permissions and audit

Upload, replace, export and remove need `ManageEmployees`. Audit `Letter.TemplateFileUploaded` (file name and SHA-256) and
`Letter.TemplateFileRemoved`. Issuing and reprinting keep the existing salary checks (they read `Body`, which holds the
document text).

## Converted sample templates

`doc-files/templates/` holds the four sample letters converted to Word templates (personal names, salaries and dates replaced by
fields; layout untouched). Signatory names and the company name stay as literal text, exactly as in each original.
