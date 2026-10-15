"""Compares the PDFs of the original letters with the PDFs of the filled templates, page by page.

The PDFs come from the opt-in test WordTemplateFidelityTests (tests/SuperOfc.Infrastructure.Tests), which fills each template in
doc-files/templates with the values of its original letter in doc-files/ and has Word save both as PDF:

    set SUPEROFC_WORD_TESTS=1
    dotnet test tests/SuperOfc.Infrastructure.Tests -c Release --filter "FullyQualifiedName~WordTemplateFidelityTests"
    python -I tools/dev/letters/compare_fidelity.py [folder [pair ...]]

folder defaults to %TEMP%/superofc-fidelity; pairs default to the four letters (<pair>-original.pdf vs <pair>-filled.pdf). Per page: pixel difference ratio at 150 dpi (pixels whose largest channel difference
exceeds THRESHOLD, over all pixels), the largest shift of a word between the two PDFs, and a word-by-word text comparison
(whitespace runs ignored). Writes <pair>-p<n>-side.png (original | filled) and <pair>-p<n>-diff.png (differing pixels in red over
a faded original) to the same folder.

Expected result: same page count and text, and only sub-point glyph shifts (max word shift under 0.1 pt, pixel ratio around
0.0001-0.0003). Word positions glyphs per run, and a value that the original letter had typed across several runs (different
rsids, e.g. "11" " " "May" " 202" "6") is written by filling as one run, which moves the glyphs after it by a few hundredths of a
point. Converting the same .docx twice gives 0 differing pixels. Exits with 1 when any pair differs in page count, text or pixels above MAX_RATIO.
Needs PyMuPDF and Pillow.
"""
import sys
import tempfile
from pathlib import Path

import fitz  # PyMuPDF
from PIL import Image, ImageChops

PAIRS = ["noc", "employment", "experience", "salary"]
DPI = 150
THRESHOLD = 48  # 0-255 per channel; below this a difference is anti-aliasing noise
MAX_RATIO = 0.0005


def render(page) -> Image.Image:
    pix = page.get_pixmap(dpi=DPI, alpha=False)
    return Image.frombytes("RGB", (pix.width, pix.height), pix.samples)


def words(page):
    """(text, x0, y0) of each word in reading order, in points."""
    return [(w[4], w[0], w[1]) for w in page.get_text("words", sort=True)]


def compare_pixels(a: Image.Image, b: Image.Image, stem: Path):
    if a.size != b.size:
        b = b.resize(a.size)
    diff = ImageChops.difference(a, b)
    channels = diff.split()
    worst = ImageChops.lighter(ImageChops.lighter(channels[0], channels[1]), channels[2])
    mask = worst.point(lambda v: 255 if v > THRESHOLD else 0)
    changed = sum(1 for v in mask.getdata() if v)
    ratio = changed / (a.size[0] * a.size[1])
    box = mask.getbbox()

    side = Image.new("RGB", (a.size[0] * 2 + 10, a.size[1]), "gray")
    side.paste(a, (0, 0))
    side.paste(b, (a.size[0] + 10, 0))
    side.save(f"{stem}-side.png")
    faded = Image.blend(a, Image.new("RGB", a.size, "white"), 0.75)
    faded.paste(Image.new("RGB", a.size, (230, 0, 0)), (0, 0), mask)
    faded.save(f"{stem}-diff.png")
    return changed, ratio, box


def compare_words(wa, wb):
    ta, tb = [w[0] for w in wa], [w[0] for w in wb]
    first = None
    for i, (x, y) in enumerate(zip(ta, tb)):
        if x != y:
            first = i
            break
    if first is None and len(ta) != len(tb):
        first = min(len(ta), len(tb))
    shift = 0.0
    if ta == tb:
        shift = max((max(abs(a[1] - b[1]), abs(a[2] - b[2])) for a, b in zip(wa, wb)), default=0.0)
    return first, shift


def main() -> None:
    folder = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(tempfile.gettempdir()) / "superofc-fidelity"
    failed = False
    rows = []
    for name in sys.argv[2:] or PAIRS:
        a_path, b_path = folder / f"{name}-original.pdf", folder / f"{name}-filled.pdf"
        if not a_path.exists() or not b_path.exists():
            print(f"{name}: missing {a_path.name} or {b_path.name}; run WordTemplateFidelityTests first")
            failed = True
            continue
        a_doc, b_doc = fitz.open(a_path), fitz.open(b_path)
        if a_doc.page_count != b_doc.page_count:
            print(f"{name}: page count {a_doc.page_count} (original) vs {b_doc.page_count} (filled)")
            failed = True
        for n in range(min(a_doc.page_count, b_doc.page_count)):
            a_page, b_page = a_doc[n], b_doc[n]
            changed, ratio, box = compare_pixels(render(a_page), render(b_page), folder / f"{name}-p{n + 1}")
            wa, wb = words(a_page), words(b_page)
            first, shift = compare_words(wa, wb)
            text_ok = first is None
            if not text_ok:
                ctx_a = " ".join(w[0] for w in wa[max(0, first - 3):first + 4])
                ctx_b = " ".join(w[0] for w in wb[max(0, first - 3):first + 4])
                print(f"{name} p{n + 1}: text differs at word {first}:\n  original: {ctx_a}\n  filled:   {ctx_b}")
            if not text_ok or ratio > MAX_RATIO:
                failed = True
            rows.append((name, n + 1, f"{a_doc.page_count}/{b_doc.page_count}", len(wa), "same" if text_ok else "DIFF",
                         f"{shift:.2f}", changed, f"{ratio:.6f}", str(box) if box else "-"))
    header = ("pair", "page", "pages o/f", "words", "text", "max word shift (pt)", "px diff", "px ratio", "diff box (px)")
    widths = [max(len(str(r[i])) for r in rows + [header]) for i in range(len(header))]
    print()
    for row in [header] + rows:
        print("  ".join(str(v).ljust(w) for v, w in zip(row, widths)))
    print(f"\nPNGs in {folder}")
    print("FAIL" if failed else "All pairs identical.")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
