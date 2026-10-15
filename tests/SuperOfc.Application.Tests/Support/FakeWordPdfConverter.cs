using SuperOfc.Application.Letters;

namespace SuperOfc.Application.Tests.Support;

/// <summary>Stands in for Microsoft Word: the "PDF" is the .docx bytes after a <c>%PDF-fake</c> line.</summary>
public sealed class FakeWordPdfConverter : IWordPdfConverter
{
    public static readonly byte[] Marker = "%PDF-fake\n"u8.ToArray();

    public bool IsAvailable { get; set; } = true;

    public int Calls { get; private set; }

    public Task ConvertAsync(IReadOnlyList<WordPdfJob> jobs, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        if (!IsAvailable)
        {
            throw new WordConversionException("Microsoft Word is not installed.");
        }

        Calls++;
        foreach (var job in jobs)
        {
            File.WriteAllBytes(job.PdfPath, [.. Marker, .. File.ReadAllBytes(job.DocxPath)]);
        }

        return Task.CompletedTask;
    }
}
