"""Converts the four sample letters in doc-files/ into Word letter templates (doc-files/templates/).

Only run text changes: each personal value is replaced by a field token written into the FIRST run of
that value (so it keeps that run's bold, superscript, font and size) and the value's following runs are
emptied. Only word/document.xml is rewritten; every other part of the package is copied byte for byte.
After converting, the script verifies the outputs (see verify()). Spec: docs/specs/word-letter-templates.md.
Usage: python -I tools/dev/letters/convert_samples.py
"""
import re
import sys
import zipfile
from pathlib import Path

from lxml import etree

ROOT = Path(__file__).resolve().parents[3]
SRC = ROOT / "doc-files"
OUT = SRC / "templates"

W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main"
XML_SPACE = "{http://www.w3.org/XML/1998/namespace}space"


def q(tag: str) -> str:
    return f"{{{W}}}{tag}"


# ---------------------------------------------------------------- run-level text editing

def text_nodes(p):
    """The w:t elements of the runs that belong to paragraph p (not those of nested text boxes)."""
    nodes = []
    for child in p:
        runs = [child] if child.tag == q("r") else child.findall(q("r")) if child.tag in (q("hyperlink"), q("smartTag"), q("ins")) else []
        for r in runs:
            nodes.extend(r.findall(q("t")))
    return nodes


def para_text(p) -> str:
    return "".join(t.text or "" for t in text_nodes(p))


def in_textbox(p) -> bool:
    return any(a.tag == q("txbxContent") for a in p.iterancestors())


def set_text(t, value: str) -> None:
    t.text = value
    if value != value.strip() and t.get(XML_SPACE) is None:
        t.set(XML_SPACE, "preserve")


class Doc:
    def __init__(self, name: str, src: Path):
        self.name = name
        self.src = src
        with zipfile.ZipFile(src) as z:
            self.root = etree.fromstring(z.read("word/document.xml"))
        self.removed_texts = []

    def paragraphs(self):
        return [p for p in self.root.iter(q("p")) if not in_textbox(p)]

    def sub(self, old: str, new: str, before: str = "", after: str = "", nth: int = 0, count: int = 1) -> None:
        """Replaces old (found as before+old+after) with new; new goes into the run holding old's first character."""
        needle = before + old + after
        hits = []
        for p in self.paragraphs():
            text = para_text(p)
            start = text.find(needle)
            while start >= 0:
                hits.append((p, start + len(before)))
                start = text.find(needle, start + 1)
        if len(hits) != count:
            raise SystemExit(f"{self.name}: expected {count} match(es) of {needle!r}, found {len(hits)}")
        p, s = hits[nth]
        e = s + len(old)
        pos = 0
        written = False
        for t in text_nodes(p):
            value = t.text or ""
            a, b = pos, pos + len(value)
            pos = b
            if b <= s or a >= e:
                continue
            ls, le = max(s, a) - a, min(e, b) - a
            if not written:
                set_text(t, value[:ls] + new + value[le:])
                written = True
            else:
                set_text(t, value[:ls] + value[le:])

    def set_paragraph(self, old: str, new: str) -> None:
        """Sets the whole text of the paragraph whose text is old: first w:t gets new, the rest are emptied."""
        ps = [p for p in self.paragraphs() if para_text(p) == old]
        assert len(ps) == 1, (self.name, old)
        ts = text_nodes(ps[0])
        set_text(ts[0], new)
        for t in ts[1:]:
            set_text(t, "")

    def remove_paragraph(self, text: str) -> None:
        ps = [p for p in self.paragraphs() if para_text(p) == text]
        assert len(ps) == 1, (self.name, text)
        ps[0].getparent().remove(ps[0])
        self.removed_texts.append(text)

    def save(self, out: Path) -> None:
        xml = etree.tostring(self.root, xml_declaration=True, encoding="UTF-8", standalone=True)
        out.parent.mkdir(parents=True, exist_ok=True)
        with zipfile.ZipFile(self.src) as zin, zipfile.ZipFile(out, "w") as zout:
            for info in zin.infolist():
                data = xml if info.filename == "word/document.xml" else zin.read(info.filename)
                zout.writestr(info, data, compress_type=info.compress_type)


# ---------------------------------------------------------------- the four letters

def noc() -> Doc:
    d = Doc("NOC letter", SRC / "NOC Letter For Job Change.docx")
    date = "{Letter.Date:d MMMM yyyy}"
    d.sub("11 May 2026", date, before="Date: ", count=2, nth=0)
    d.sub("11 May 2026", date, before="Date: ", after=".")
    d.sub("Md Masud Alam", "{Employee.Name}")
    d.sub("1", "{Employee.JoinDate:%d}", before="since ", after="st January")
    d.sub("st", "{Employee.JoinDate:ord}", before="{Employee.JoinDate:%d}")
    d.sub("January 2020", "{Employee.JoinDate:MMMM yyyy}")
    d.sub("31", "{Employee.ExitDate:%d}", after="st December")
    d.sub("st", "{Employee.ExitDate:ord}", before="{Employee.ExitDate:%d}")
    d.sub("December 2025", "{Employee.ExitDate:MMMM yyyy}")
    d.sub("Software Engineer", "{Employee.Designation}")
    d.sub("his", "{Employee.HisHer}", before="During ", after=" working")
    d.sub("he", "{Employee.HeShe}", before=" ", after=" has been sincere")
    d.sub("him", "{Employee.HimHer}", before="useful for ")
    d.sub("him", "{Employee.HimHer}", before="objection on ")
    d.sub("his", "{Employee.HisHer}", before="continue ", after=" job")
    return d


def employment() -> Doc:
    d = Doc("Certificate of employment", SRC / "TSK Certificate of Employment.docx")
    d.sub("2nd", "{Letter.Date:%d}{Letter.Date:ord}", before="Date: ")
    d.sub("January 2025", "{Letter.Date:MMMM yyyy}")
    d.sub("Mr. ", "{Employee.Title} ", before="certify that ", after="Md Bokul")
    d.sub("Md Bokul Islam", "{Employee.Name}", before="{Employee.Title} ")
    d.sub("June 1, 2023", "{Employee.JoinDate:MMMM d, yyyy}", before="since ")
    d.sub("He", "{Employee.HeShe}", after=" currently holds")
    d.sub("Software Developer", "{Employee.Designation}")
    d.set_paragraph("Designing and developing websites and web applications.", "{Employee.Responsibilities}")
    d.remove_paragraph("Collaborating with teams to implement and enhance website functionalities.")
    d.remove_paragraph("Troubleshooting and resolving issues to ensure optimal performance.")
    d.remove_paragraph("Delivering exceptional user experiences for websites and web applications.")
    d.sub("Thirty-Seven Thousand", "{Salary.Gross:words}")
    d.sub("37,000", "{Salary.Gross:n0}", before="BDT ")
    d.sub("Mr. ", "{Employee.Title} ", before="confirm that ", after="Md Bokul")
    d.sub("Md Bokul Islam", "{Employee.Name}", before="{Employee.Title} ")
    return d


def experience() -> Doc:
    d = Doc("Experience certificate", SRC / "TSK EXPERIANCE CERTIFICATE.docx")
    d.sub("Md. Jahid Hasan Badhon", "{Employee.Name}", before="that  ")
    d.sub("Son", "{Employee.SonDaughter:cap}", before=", ", after=" of Md Alam")
    d.sub("Md Alam Mia", "{Employee.FatherName}")
    d.sub("He", "{Employee.HeShe}", before=". ", after=" has worked")
    d.sub("Support Engineer", "{Employee.Designation}")
    d.sub("1", "{Employee.JoinDate:%d}", before="from ", after="st June")
    d.sub("st", "{Employee.JoinDate:ord}", before="{Employee.JoinDate:%d}")
    d.sub("June 2023", "{Employee.JoinDate:MMMM yyyy}")
    d.sub("20", "{Employee.ExitDate:%d}", before=" to ", after="th August")
    d.sub("th", "{Employee.ExitDate:ord}", before="{Employee.ExitDate:%d}")
    d.sub("August 2026", "{Employee.ExitDate:MMMM yyyy}")
    d.sub("he", "{Employee.HeShe}", before="period ", after=" was found")
    d.sub("his", "{Employee.HisHer}", before="against ", after=" character")
    d.sub("him", "{Employee.HimHer}", before="I wish ", after=" every")
    return d


def salary() -> Doc:
    d = Doc("Salary certificate", SRC / "TSK salary certificate.docx")
    d.sub("11 May 2026", "{Letter.Date:d MMMM yyyy}", before="Date:")
    d.sub("Md Masud Alam", "{Employee.Name}", after=" has been")
    d.sub("January", "{Employee.JoinDate:MMMM}", before="since ", after=" 1st")
    d.sub("1", "{Employee.JoinDate:%d}", before="{Employee.JoinDate:MMMM} ", after="st, 2020")
    d.sub("st", "{Employee.JoinDate:ord}", before="{Employee.JoinDate:%d}")
    d.sub("2020", "{Employee.JoinDate:yyyy}", before="{Employee.JoinDate:ord}, ")
    d.sub("He", "{Employee.HeShe}", before=". ", after=" is currently")
    d.sub("Software Engineer", "{Employee.Designation}", after="  and")
    d.sub("his", "{Employee.HisHer}", after=" total gross")
    d.sub("70,000", "{Salary.Gross:n0}", before="BDT ", after=" (")
    d.sub("Seventy Thousand", "{Salary.Gross:words}", before="(", after=" BDT only)")
    d.sub("42,000", "{Salary.BASIC:n0}", after="BDT")
    d.sub("14,000", "{Salary.HRA:n0}", after=" BDT")
    d.sub("7,000", "{Salary.MEDICAL:n0}", after=" BDT", count=2, nth=0)
    d.sub("7,000", "{Salary.OTHER:n0}", after=" BDT")
    d.sub("70,000", "{Salary.Gross:n0}", after=" BDT")
    return d


# ---------------------------------------------------------------- verification

VALID_KEYS = {
    "Employee.Name", "Employee.Title", "Employee.HeShe", "Employee.HimHer", "Employee.HisHer",
    "Employee.SonDaughter", "Employee.FatherName", "Employee.Designation", "Employee.JoinDate",
    "Employee.ExitDate", "Employee.Responsibilities", "Salary.Gross", "Salary.BASIC", "Salary.HRA",
    "Salary.MEDICAL", "Salary.OTHER", "Company.Name", "Company.CurrencyCode", "Letter.Date",
    "Letter.Number", "Today",
}
DATE_KEYS = {"Employee.JoinDate", "Employee.ExitDate", "Letter.Date", "Today"}
MONEY_KEYS = {k for k in VALID_KEYS if k.startswith("Salary.")}
TOKEN = re.compile(r"\{([A-Za-z]+\.[A-Za-z0-9_]+|Today)(?::([^{}\r\n]{1,40}))?\}")
# .NET custom date pattern: d/M/y specifiers, %-prefix, spaces and simple separators.
DATE_PATTERN = re.compile(r"^%?(d{1,4}|M{1,4}|y{1,5}|[ ,./-])+$")
FORBIDDEN = ["Masud", "Bokul", "Jahid", "Badhon", "Alam Mia", "70,000", "37,000", "42,000", "14,000", "7,000"]


def format_ok(key: str, fmt):
    if fmt is None:
        return True
    if key in DATE_KEYS:
        return fmt == "ord" or bool(DATE_PATTERN.match(fmt))
    if key in MONEY_KEYS:
        return fmt.lower() in ("n0", "n2", "words")
    return fmt.lower() in ("upper", "lower", "cap", "title")


def all_texts(path: Path):
    """(a) Every paragraph, table cell, header and footer text, read back with python-docx."""
    import docx
    d = docx.Document(str(path))
    out = [("body", p.text) for p in d.paragraphs]
    for t in d.tables:
        for row in t.rows:
            for c in row.cells:
                out.extend(("cell", p.text) for p in c.paragraphs)
    for s in d.sections:
        for kind, part in (("header", s.header), ("footer", s.footer)):
            out.extend((kind, p.text) for p in part.paragraphs)
    return out


def skeleton(root, removed):
    """document.xml with all w:t text and xml:space dropped, and the given paragraphs removed."""
    root = etree.fromstring(etree.tostring(root))
    for p in [p for p in root.iter(q("p")) if not in_textbox(p) and para_text(p) in removed]:
        p.getparent().remove(p)
    for t in root.iter(q("t")):
        t.text = None
        t.attrib.pop(XML_SPACE, None)
    return etree.tostring(root)


def verify(doc: Doc, out: Path) -> list:
    errors = []
    print(f"\n===== {out.name}")
    texts = all_texts(out)
    for kind, text in texts:
        if text.strip():
            print(f"[{kind}] {text}")
    # (b) no personal values left
    blob = "\n".join(t for _, t in texts)
    with zipfile.ZipFile(out) as z:
        blob += z.read("word/document.xml").decode("utf-8")
    errors += [f"{out.name}: still contains {s!r}" for s in FORBIDDEN if s in blob]
    # (c) every token has a known key and a valid format
    with zipfile.ZipFile(out) as z:
        new_root = etree.fromstring(z.read("word/document.xml"))
    tokens = set()
    for p in new_root.iter(q("p")):
        for m in TOKEN.finditer(para_text(p)):
            tokens.add(m.group(0))
            if m.group(1) not in VALID_KEYS:
                errors.append(f"{out.name}: unknown key in {m.group(0)}")
            elif not format_ok(m.group(1), m.group(2)):
                errors.append(f"{out.name}: bad format in {m.group(0)}")
        rest = TOKEN.sub("", para_text(p))
        if "{" in rest or "}" in rest:
            errors.append(f"{out.name}: stray brace in {para_text(p)!r}")
    print("tokens:", ", ".join(sorted(tokens)))
    # (d) formatting unchanged: run rPr identical run by run, and document.xml identical outside w:t text
    with zipfile.ZipFile(doc.src) as z:
        old_root = etree.fromstring(z.read("word/document.xml"))
    old_ps = [p for p in old_root.iter(q("p")) if para_text(p) not in doc.removed_texts or in_textbox(p)]
    new_ps = list(new_root.iter(q("p")))
    if len(old_ps) != len(new_ps):
        errors.append(f"{out.name}: paragraph count {len(old_ps)} vs {len(new_ps)}")
    runs_compared = 0
    for op, np_ in zip(old_ps, new_ps):
        ors, nrs = op.findall(q("r")), np_.findall(q("r"))
        if len(ors) != len(nrs):
            errors.append(f"{out.name}: run count changed in {para_text(op)[:40]!r}")
            continue
        for a, b in zip(ors, nrs):
            ra, rb = a.find(q("rPr")), b.find(q("rPr"))
            sa = etree.tostring(ra) if ra is not None else b""
            sb = etree.tostring(rb) if rb is not None else b""
            if sa != sb:
                errors.append(f"{out.name}: rPr changed in run {''.join(t.text or '' for t in a.findall(q('t')))!r}")
            runs_compared += 1
    if skeleton(old_root, doc.removed_texts) != skeleton(new_root, []):
        errors.append(f"{out.name}: document.xml differs outside w:t text")
    with zipfile.ZipFile(doc.src) as zo, zipfile.ZipFile(out) as zn:
        if zo.namelist() != zn.namelist():
            errors.append(f"{out.name}: package part list differs")
        for name in zo.namelist():
            if name != "word/document.xml" and zo.read(name) != zn.read(name):
                errors.append(f"{out.name}: part {name} changed")
    print(f"runs compared: {runs_compared}; removed paragraphs: {len(doc.removed_texts)}")
    return errors


def main() -> None:
    jobs = [(noc, "NOC letter.docx"), (employment, "Certificate of employment.docx"),
            (experience, "Experience certificate.docx"), (salary, "Salary certificate.docx")]
    errors = []
    for build, name in jobs:
        doc = build()
        out = OUT / name
        doc.save(out)
        errors += verify(doc, out)
    print()
    if errors:
        print("\n".join(errors))
        sys.exit(1)
    print("All checks passed.")


if __name__ == "__main__":
    main()
