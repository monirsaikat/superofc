using SuperOfc.Domain.Letters;

namespace SuperOfc.Application.Letters;

/// <summary>
/// Word (.docx) letter templates (ADR-24): the company's own Word file is the template, so the printed letter matches it exactly.
/// Fields are typed in Word as <c>{Employee.Name}</c> or <c>{Employee.JoinDate:d MMMM yyyy}</c>; filling changes only the text of
/// those fields and keeps every font, table, list, superscript, header and footer. Implemented in Infrastructure with the Open XML SDK.
/// </summary>
public interface IWordLetterService
{
    /// <summary>
    /// Checks an uploaded file: a real .docx (not macro-enabled, not encrypted) with nothing that reaches outside the file
    /// (external links, linked pictures, included documents, DDE). Returns the document's text when it is usable.
    /// </summary>
    WordTemplateCheck Inspect(byte[] docx);

    /// <summary>
    /// Fills every field in the body, headers, footers, footnotes and text boxes, and returns the new file.
    /// Fields without a value are added to <paramref name="missing"/> and left as typed.
    /// A field that is the whole text of a paragraph and has a value of several lines repeats the paragraph once per line
    /// (so a bulleted line becomes one bullet per responsibility); elsewhere new lines become line breaks.
    /// </summary>
    byte[] Fill(byte[] docx, LetterResolver resolve, ICollection<string> missing);

    /// <summary>The plain text of a document: paragraphs separated by a blank line, headers first, footers last.</summary>
    string Text(byte[] docx);
}

/// <summary>Result of <see cref="IWordLetterService.Inspect"/>: the document text, or why the file cannot be used.</summary>
public sealed record WordTemplateCheck(bool IsValid, string Text, string? Error)
{
    public static WordTemplateCheck Valid(string text) => new(true, text, null);

    public static WordTemplateCheck Invalid(string error) => new(false, string.Empty, error);
}

/// <summary>Saves Word documents as PDF through Microsoft Word on this PC, so the PDF looks exactly like the document in Word.</summary>
public interface IWordPdfConverter
{
    /// <summary>True when Microsoft Word is installed.</summary>
    bool IsAvailable { get; }

    /// <summary>Converts each file, one Word session for the whole batch. Throws <see cref="WordConversionException"/> with a readable message.</summary>
    Task ConvertAsync(IReadOnlyList<WordPdfJob> jobs, CancellationToken cancellationToken = default);
}

/// <summary>One document to convert: the .docx to read and the .pdf to write.</summary>
public sealed record WordPdfJob(string DocxPath, string PdfPath);

/// <summary>Word is missing, timed out or could not save a PDF; the message is shown to the user.</summary>
public sealed class WordConversionException : Exception
{
    public WordConversionException()
    {
    }

    public WordConversionException(string message)
        : base(message)
    {
    }

    public WordConversionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
