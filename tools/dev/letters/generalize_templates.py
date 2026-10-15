"""Replaces the company name and signatory typed in doc-files/templates with fields, changing run text only.

Run after convert_samples.py:  python -I tools/dev/letters/generalize_templates.py
Each phrase is found in a paragraph's joined run text; the field goes into the run where the phrase starts (keeping its
formatting) and the rest of the phrase is removed from the following runs. Every other byte of the package is copied.
"""
import re
import sys
import zipfile
from pathlib import Path

from lxml import etree

ROOT = Path(__file__).resolve().parents[3]
FOLDER = ROOT / "doc-files" / "templates"
W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main"
NS = {"w": W}
XML_SPACE = "{http://www.w3.org/XML/1998/namespace}space"

# Longest first, so "THESOFTKING LIMITED" is not caught by a shorter phrase.
PHRASES = [
    ("THESOFTKING LIMITED", "{Company.Name:upper}"),
    ("THESOFTKING Limited", "{Company.Name}"),
    ("TheSoftKing Limited", "{Company.Name}"),
    ("MD Hasibur Rahman Hasan", "{Company.SignatoryName}"),
    ("Md. Hasibur Rahman Hasan", "{Company.SignatoryName}"),
    ("Head Of Operations", "{Company.SignatoryTitle}"),
    ("Head of Operations", "{Company.SignatoryTitle}"),
]


def text_nodes(paragraph):
    """The w:t elements of the paragraph's runs, in order (also inside hyperlinks and other run containers)."""
    return [t for t in paragraph.iter(f"{{{W}}}t")]


def replace_in_paragraph(paragraph):
    changed = 0
    for phrase, field in PHRASES:
        while True:
            nodes = text_nodes(paragraph)
            joined = "".join(t.text or "" for t in nodes)
            at = joined.find(phrase)
            if at < 0:
                break
            end = at + len(phrase)
            pos = 0
            placed = False
            for t in nodes:
                text = t.text or ""
                start, stop = pos, pos + len(text)
                pos = stop
                if stop <= at or start >= end:
                    continue
                cut_from = max(at, start) - start
                cut_to = min(end, stop) - start
                if not placed:
                    t.text = text[:cut_from] + field + text[cut_to:]
                    placed = True
                else:
                    t.text = text[:cut_from] + text[cut_to:]
                if t.text != t.text.strip():
                    t.set(XML_SPACE, "preserve")
            changed += 1
    return changed


def process(path):
    with zipfile.ZipFile(path) as source:
        items = [(info, source.read(info.filename)) for info in source.infolist()]
    total = 0
    out = []
    for info, data in items:
        if info.filename == "word/document.xml" or re.fullmatch(r"word/(header|footer)\d*\.xml", info.filename):
            tree = etree.fromstring(data)
            count = sum(replace_in_paragraph(p) for p in tree.iter(f"{{{W}}}p"))
            if count:
                data = etree.tostring(tree, xml_declaration=True, encoding="UTF-8", standalone=True)
                total += count
        out.append((info, data))
    with zipfile.ZipFile(path, "w") as target:
        for info, data in out:
            target.writestr(info, data)
    return total


def main():
    for path in sorted(FOLDER.glob("*.docx")):
        print(f"{path.name}: {process(path)} replacements")
        with zipfile.ZipFile(path) as z:
            xml = z.read("word/document.xml").decode("utf-8")
        for leftover in ("SOFTKING", "SoftKing", "Hasibur", "Operations"):
            if leftover in xml:
                print(f"  LEFTOVER {leftover!r}", file=sys.stderr)
                sys.exit(1)


if __name__ == "__main__":
    main()
