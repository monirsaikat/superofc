using System.Text;
using System.Xml;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using SuperOfc.Application.Letters;
using SuperOfc.Domain.Letters;
using WText = DocumentFormat.OpenXml.Wordprocessing.Text;

namespace SuperOfc.Infrastructure.Reporting;

/// <summary>
/// <see cref="IWordLetterService"/> with the Open XML SDK (ADR-24). Word splits typed text into runs at will (spell check marks,
/// bookmarks, formatting changes), so each paragraph's text is joined first, <see cref="LetterText.Plan"/> runs on the whole
/// paragraph, and each value is written into the run where its field starts. Nothing else in the file changes.
/// </summary>
public sealed class WordLetterService : IWordLetterService
{
    internal const string NotWordMessage = "This file is not a Word document (.docx), or it is password protected.";
    internal const string MacroMessage = "Macro-enabled Word files cannot be used. Save it as a Word Document (.docx).";
    internal const string TemplateMessage = "Word template files (.dotx) cannot be used. Save it as a Word Document (.docx).";

    private const string NestedFieldMessage = "This Word file has a field whose instruction is built from another field. Remove the field and upload it again.";

    private const string AltChunkMessage = "This Word file contains another document embedded with altChunk. Save it in Word as a new Word Document (.docx) and upload it again.";

    /// <summary>Fields that read other files or programs when Word updates them.</summary>
    private static readonly HashSet<string> BlockedFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "INCLUDETEXT", "INCLUDE", "INCLUDEPICTURE", "IMPORT", "LINK", "DDE", "DDEAUTO", "DATABASE", "RD",
    };

    private static readonly string[] AllowedLinkSchemes = ["http", "https", "mailto"];

    public WordTemplateCheck Inspect(byte[] docx)
    {
        ArgumentNullException.ThrowIfNull(docx);
        // An encrypted or password-protected .docx is an OLE compound file (D0 CF 11 E0), not a zip.
        if (docx.Length < 4 || docx[0] != 'P' || docx[1] != 'K')
        {
            return WordTemplateCheck.Invalid(NotWordMessage);
        }

        WordprocessingDocument document;
        try
        {
            document = WordprocessingDocument.Open(new MemoryStream(docx, writable: false), false);
        }
        catch (Exception)
        {
            return WordTemplateCheck.Invalid(NotWordMessage);
        }

        using (document)
        {
            try
            {
                var error = Problem(document);
                return error is null ? WordTemplateCheck.Valid(Text(document)) : WordTemplateCheck.Invalid(error);
            }
            catch (Exception ex) when (ex is OpenXmlPackageException or InvalidDataException or IOException or System.Xml.XmlException or InvalidOperationException)
            {
                return WordTemplateCheck.Invalid(NotWordMessage);
            }
        }
    }

    public byte[] Fill(byte[] docx, LetterResolver resolve, ICollection<string> missing)
    {
        ArgumentNullException.ThrowIfNull(docx);
        ArgumentNullException.ThrowIfNull(resolve);
        ArgumentNullException.ThrowIfNull(missing);

        // Edit a copy: the template bytes are never changed.
        using var stream = new MemoryStream();
        stream.Write(docx);
        stream.Position = 0;
        using (var document = WordprocessingDocument.Open(stream, true))
        {
            foreach (var root in Roots(document))
            {
                foreach (var proof in root.Descendants<ProofError>().ToList())
                {
                    proof.Remove();
                }

                // Snapshot first: filling may add paragraphs (one per line of a multi-line value).
                foreach (var paragraph in root.Descendants<Paragraph>().ToList())
                {
                    FillParagraph(paragraph, resolve, missing);
                }
            }

            if (document.MainDocumentPart?.DocumentSettingsPart?.Settings is { } settings)
            {
                // Spelling and grammar state is stale after filling; Word checks the document again.
                foreach (var state in settings.Descendants<ProofState>().ToList())
                {
                    state.Remove();
                }
            }
        }

        return stream.ToArray();
    }

    public string Text(byte[] docx)
    {
        ArgumentNullException.ThrowIfNull(docx);
        using var document = WordprocessingDocument.Open(new MemoryStream(docx, writable: false), false);
        return Text(document);
    }

    /// <summary>
    /// Headers, body, footnotes, endnotes and footers in that order; one entry per non-blank paragraph, separated by a blank line.
    /// A text box is read once: the <c>mc:Fallback</c> copy repeats the <c>mc:Choice</c> one.
    /// </summary>
    private static string Text(WordprocessingDocument document)
    {
        var paragraphs = new List<string>();
        foreach (var root in Roots(document))
        {
            foreach (var paragraph in root.Descendants<Paragraph>())
            {
                if (paragraph.Ancestors<AlternateContentFallback>().Any())
                {
                    continue;
                }

                var text = ParagraphText.Read(paragraph).Joined;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    paragraphs.Add(text);
                }
            }
        }

        return string.Join("\n\n", paragraphs);
    }

    /// <summary>The parts that hold letter text: headers first, then the body, footnotes and endnotes, then footers.</summary>
    private static IEnumerable<OpenXmlPartRootElement> Roots(WordprocessingDocument document)
    {
        var main = document.MainDocumentPart;
        if (main is null)
        {
            yield break;
        }

        foreach (var header in main.HeaderParts)
        {
            if (header.Header is { } root)
            {
                yield return root;
            }
        }

        if (main.Document is { } body)
        {
            yield return body;
        }

        if (main.FootnotesPart?.Footnotes is { } footnotes)
        {
            yield return footnotes;
        }

        if (main.EndnotesPart?.Endnotes is { } endnotes)
        {
            yield return endnotes;
        }

        foreach (var footer in main.FooterParts)
        {
            if (footer.Footer is { } root)
            {
                yield return root;
            }
        }
    }

    /// <summary>Why the file cannot be used as a template, or null when it can.</summary>
    private static string? Problem(WordprocessingDocument document)
    {
        switch (document.DocumentType)
        {
            case WordprocessingDocumentType.MacroEnabledDocument or WordprocessingDocumentType.MacroEnabledTemplate:
                return MacroMessage;
            case WordprocessingDocumentType.Template:
                return TemplateMessage;
        }

        var main = document.MainDocumentPart;
        if (main?.Document is null)
        {
            return NotWordMessage;
        }

        if (main.VbaProjectPart is not null)
        {
            return MacroMessage;
        }

        var containers = new List<OpenXmlPartContainer> { document };
        containers.AddRange(document.GetAllParts());
        foreach (var container in containers)
        {
            if (container.ExternalRelationships.FirstOrDefault() is { } external)
            {
                return $"This Word file links to something outside the file ({external.Uri}). Remove the linked item, save the file and upload it again.";
            }

            if (container.HyperlinkRelationships.FirstOrDefault(h => h.IsExternal && !IsAllowedLink(h.Uri)) is { } link)
            {
                return $"This Word file has a link to {link.Uri}. Only web (http, https) and e-mail links are allowed. Remove the link and upload it again.";
            }
        }

        if (main.AlternativeFormatImportParts.Any())
        {
            return AltChunkMessage;
        }

        foreach (var part in document.GetAllParts())
        {
            if (part.RootElement is not { } root)
            {
                continue;
            }

            if (root.Descendants<AltChunk>().Any())
            {
                return AltChunkMessage;
            }

            if (FieldProblem(root) is { } problem)
            {
                return problem;
            }
        }

        if (SplitField(document) is { } split)
        {
            return $"The field {split} is split by a picture, footnote or field in Word; retype it and upload the file again.";
        }

        if (main.DocumentSettingsPart?.Settings?.GetFirstChild<UpdateFieldsOnOpen>() is { } update && (update.Val is null || update.Val.Value))
        {
            return "This Word file asks Word to update its fields when it opens (updateFields). Turn that off and upload it again.";
        }

        return null;
    }

    private static bool IsAllowedLink(Uri uri)
        => uri.IsAbsoluteUri && AllowedLinkSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Why a field (<c>w:fldSimple</c> or a complex field's <c>w:instrText</c>) cannot stay in the file, or null: it reads another
    /// file or program, or its instruction is built from another field, so its real name is only known when Word updates it.
    /// </summary>
    private static string? FieldProblem(OpenXmlElement root)
    {
        // A complex field's instruction may be split over several w:instrText runs; join them per field (fields can nest).
        var open = new Stack<OpenField>();
        foreach (var element in root.Descendants())
        {
            switch (element)
            {
                case SimpleField simple when Blocked(simple.Instruction?.Value) is { } name:
                    return BlockedMessage(name);
                case FieldChar { FieldCharType.Value: var type }:
                    if (type == FieldCharValues.Begin)
                    {
                        if (open.TryPeek(out var outer) && outer.InInstruction)
                        {
                            return NestedFieldMessage;
                        }

                        open.Push(new OpenField());
                    }
                    else if (type == FieldCharValues.Separate && open.TryPeek(out var current))
                    {
                        current.InInstruction = false;
                    }
                    else if (type == FieldCharValues.End && open.Count > 0 && Blocked(open.Pop().Instruction.ToString()) is { } ended)
                    {
                        return BlockedMessage(ended);
                    }

                    break;
                case FieldCode code:
                    if (open.TryPeek(out var field))
                    {
                        field.Instruction.Append(code.Text);
                    }
                    else if (Blocked(code.Text) is { } loose)
                    {
                        return BlockedMessage(loose);
                    }

                    break;
            }
        }

        return open.Select(f => Blocked(f.Instruction.ToString())).FirstOrDefault(n => n is not null) is { } unclosed ? BlockedMessage(unclosed) : null;
    }

    private static string BlockedMessage(string field) => $"This Word file has a {field} field, which reads another file or program. Remove the field and upload it again.";

    private static string? Blocked(string? instruction)
    {
        var name = instruction?.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return name is not null && BlockedFields.Contains(name) ? name.ToUpperInvariant() : null;
    }

    /// <summary>The first field, as typed, that a picture, note reference, field or tab splits, so it could never be filled; or null.</summary>
    private static string? SplitField(WordprocessingDocument document)
    {
        foreach (var root in Roots(document))
        {
            foreach (var paragraph in root.Descendants<Paragraph>())
            {
                var text = ParagraphText.Read(paragraph);
                if (text.Joined.IndexOf('{', StringComparison.Ordinal) < 0)
                {
                    continue;
                }

                var split = LetterText.Plan(text.Joined, (_, _) => "x", new List<string>())
                    .FirstOrDefault(r => !text.Fits(r) && FieldKey(text.Joined, r) is not null);
                if (split.Length > 0)
                {
                    return text.Joined.Substring(split.Index, split.Length);
                }
            }
        }

        return null;
    }

    /// <summary>The key of the field a replacement fills, or null for a <c>{{</c> or <c>}}</c>.</summary>
    private static string? FieldKey(string text, LetterReplacement replacement)
        => replacement.Length > 2 && text[replacement.Index] == '{'
            ? text.Substring(replacement.Index + 1, replacement.Length - 2).Split(':', 2)[0]
            : null;

    /// <summary>
    /// A value as XML can hold it: a vertical tab or form feed (pasted from Word or Excel) becomes a line break, and any other
    /// character XML 1.0 does not allow is dropped. Surrogate pairs are kept.
    /// </summary>
    internal static string Clean(string value)
    {
        var result = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c is '\v' or '\f')
            {
                result.Append('\n');
            }
            else if (char.IsHighSurrogate(c) && i + 1 < value.Length && XmlConvert.IsXmlSurrogatePair(value[i + 1], c))
            {
                result.Append(c).Append(value[++i]);
            }
            else if (XmlConvert.IsXmlChar(c))
            {
                result.Append(c);
            }
        }

        return result.ToString();
    }

    private static void FillParagraph(Paragraph paragraph, LetterResolver resolve, ICollection<string> missing)
    {
        var text = ParagraphText.Read(paragraph);
        if (text.Pieces.Count == 0 || text.Joined.IndexOfAny(['{', '}']) < 0)
        {
            return;
        }

        var plan = new List<LetterReplacement>();
        foreach (var replacement in LetterText.Plan(text.Joined, resolve, missing))
        {
            var value = Clean(replacement.Value);
            if (text.Fits(replacement) && !string.IsNullOrWhiteSpace(value))
            {
                plan.Add(replacement with { Value = value });
            }
            else if (FieldKey(text.Joined, replacement) is { } key && !missing.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                // A letter is never issued with a field left as typed: a field that cannot be filled counts as a blank.
                missing.Add(key);
            }
        }

        if (plan.Count == 0)
        {
            return;
        }

        // A multi-line value that is the paragraph's whole text repeats the paragraph per line, so a bullet becomes a list.
        if (plan.Count == 1 && Lines(plan[0].Value) is { Length: > 1 } lines
            && string.IsNullOrWhiteSpace(text.Joined[..plan[0].Index])
            && string.IsNullOrWhiteSpace(text.Joined[(plan[0].Index + plan[0].Length)..]))
        {
            var used = lines.Where(l => !string.IsNullOrWhiteSpace(l)).Select(Capitalize).ToList();
            OpenXmlElement anchor = paragraph;
            for (var i = 1; i < used.Count; i++)
            {
                var copy = (Paragraph)paragraph.CloneNode(true);
                // Only the text repeats. Pictures, text boxes, objects, note and comment marks, fields and bookmarks stay in the
                // first paragraph: their ids must stay unique, and a text box in a copy would never be filled.
                foreach (var extra in copy.Descendants().Where(IsNotText).ToList())
                {
                    extra.Remove();
                }

                foreach (var run in copy.Descendants<Run>().Where(r => r.ChildElements.All(c => c is RunProperties)).ToList())
                {
                    run.Remove();
                }

                // A section break ends the last paragraph of the group only.
                if (i < used.Count - 1)
                {
                    copy.ParagraphProperties?.SectionProperties?.Remove();
                }

                copy.ParagraphId = null;
                copy.TextId = null;
                var copyText = ParagraphText.Read(copy);
                copyText.Apply([plan[0] with { Value = used[i] }]);
                anchor = anchor.InsertAfterSelf(copy);
            }

            if (used.Count > 1)
            {
                paragraph.ParagraphProperties?.SectionProperties?.Remove();
            }

            text.Apply([plan[0] with { Value = used[0] }]);
            return;
        }

        text.Apply(plan);
    }

    private static bool IsNotText(OpenXmlElement element)
        => element is BookmarkStart or BookmarkEnd or Drawing or AlternateContent or Picture or EmbeddedObject
            or FootnoteReference or EndnoteReference or CommentRangeStart or CommentRangeEnd or CommentReference
            or PermStart or PermEnd or FieldChar or FieldCode;

    private static string[] Lines(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    private static string Capitalize(string line)
        => line.Length > 0 && char.IsLower(line[0]) ? char.ToUpper(line[0], System.Globalization.CultureInfo.CurrentCulture) + line[1..] : line;

    /// <summary>
    /// The text of one paragraph as Word shows it, and where each <c>w:t</c> sits in it. Only runs that belong to this paragraph
    /// count (also inside hyperlinks, smart tags, simple fields, insertions and content controls); a text box inside a run is its
    /// own paragraph. Tabs, breaks, drawings, note references, symbols and field codes end a segment: no replacement may span
    /// them. A soft hyphen or a comment mark inside a field does not: the soft hyphen goes with the field, the comment mark stays.
    /// </summary>
    private sealed class ParagraphText
    {
        private readonly StringBuilder _joined = new();
        private readonly HashSet<int> _boundaries = [];
        private readonly List<(int Index, OpenXmlElement Element)> _softHyphens = [];

        public List<Piece> Pieces { get; } = [];

        public string Joined { get; private set; } = string.Empty;

        public static ParagraphText Read(Paragraph paragraph)
        {
            var result = new ParagraphText();
            result.Walk(paragraph);
            result.Joined = result._joined.ToString();
            return result;
        }

        /// <summary>True when every replaced character is inside one segment of <c>w:t</c> text.</summary>
        public bool Fits(LetterReplacement replacement)
        {
            var end = replacement.Index + replacement.Length;
            return !_boundaries.Any(b => b > replacement.Index && b < end)
                && Pieces.Any(p => p.Start <= replacement.Index && replacement.Index < p.End);
        }

        /// <summary>Writes each value into the <c>w:t</c> where its field starts and removes the field's other characters.</summary>
        public void Apply(IReadOnlyList<LetterReplacement> plan)
        {
            var emptied = new List<WText>();
            // Last first: the text before a replacement keeps its positions, so the original map stays valid.
            foreach (var replacement in plan.OrderByDescending(r => r.Index))
            {
                var end = replacement.Index + replacement.Length;
                var first = Pieces.FindIndex(p => p.Start <= replacement.Index && replacement.Index < p.End);
                var piece = Pieces[first];
                var current = piece.Element.Text;
                var local = replacement.Index - piece.Start;
                var covered = Math.Min(end, piece.End) - replacement.Index;
                var rest = current[(local + covered)..];
                var lines = Lines(replacement.Value);
                piece.Element.Text = current[..local] + lines[0] + (lines.Length == 1 ? rest : string.Empty);
                Preserve(piece.Element);
                OpenXmlElement after = piece.Element;
                for (var i = 1; i < lines.Length; i++)
                {
                    after = after.InsertAfterSelf(new Break());
                    var line = new WText(lines[i] + (i == lines.Length - 1 ? rest : string.Empty));
                    Preserve(line);
                    after = after.InsertAfterSelf(line);
                }

                if (piece.Element.Text.Length == 0)
                {
                    emptied.Add(piece.Element);
                }

                foreach (var (_, hyphen) in _softHyphens.Where(h => h.Index > replacement.Index && h.Index < end))
                {
                    var parent = hyphen.Parent;
                    hyphen.Remove();
                    if (parent is Run && parent.ChildElements.All(c => c is RunProperties))
                    {
                        parent.Remove();
                    }
                }

                for (var j = first + 1; j < Pieces.Count && Pieces[j].Start < end; j++)
                {
                    var next = Pieces[j];
                    var cut = Math.Min(end, next.End) - next.Start;
                    next.Element.Text = next.Element.Text[cut..];
                    Preserve(next.Element);
                    if (next.Element.Text.Length == 0)
                    {
                        emptied.Add(next.Element);
                    }
                }
            }

            foreach (var element in emptied.Distinct())
            {
                if (element.Parent is not { } run || element.Text.Length > 0)
                {
                    continue;
                }

                element.Remove();
                if (run is Run && run.ChildElements.All(c => c is RunProperties))
                {
                    run.Remove();
                }
            }
        }

        private static void Preserve(WText text) => text.Space = SpaceProcessingModeValues.Preserve;

        private void Walk(OpenXmlElement container)
        {
            foreach (var child in container.ChildElements)
            {
                switch (child)
                {
                    case Run run:
                        ReadRun(run);
                        break;
                    case Hyperlink or SimpleField or InsertedRun or MoveToRun or CustomXmlRun or SdtRun or SdtContentRun:
                    // The SDK has no class for the old w:smartTag; it is read as an unknown element.
                    case OpenXmlUnknownElement { LocalName: "smartTag" }:
                        Walk(child);
                        break;
                    // Spell-check marks, bookmarks, comments and deleted text are not part of the visible text.
                }
            }
        }

        private void ReadRun(Run run)
        {
            foreach (var child in run.ChildElements)
            {
                switch (child)
                {
                    case WText t:
                        Pieces.Add(new Piece(t, _joined.Length, t.Text.Length));
                        _joined.Append(t.Text);
                        break;
                    case RunProperties or LastRenderedPageBreak or CommentReference:
                        break;
                    case SoftHyphen:
                        _softHyphens.Add((_joined.Length, child));
                        break;
                    case TabChar or PositionalTab:
                        Mark('\t');
                        break;
                    case Break or CarriageReturn:
                        Mark('\n');
                        break;
                    default:
                        // Drawings, field codes, symbols: nothing visible to fill, but a field never spans them.
                        _boundaries.Add(_joined.Length);
                        break;
                }
            }
        }

        private void Mark(char c)
        {
            _boundaries.Add(_joined.Length);
            _joined.Append(c);
            _boundaries.Add(_joined.Length);
        }
    }

    /// <summary>A complex field while its instruction is read: until <c>separate</c>, a field that begins is part of the instruction.</summary>
    private sealed class OpenField
    {
        public StringBuilder Instruction { get; } = new();

        public bool InInstruction { get; set; } = true;
    }

    /// <summary>One <c>w:t</c> and the range of the paragraph text it held when read.</summary>
    private sealed record Piece(WText Element, int Start, int Length)
    {
        public int End => Start + Length;
    }
}
