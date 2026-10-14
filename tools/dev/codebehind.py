"""Creates missing .xaml.cs code-behind files for UserControl views.

Usage: python -I tools/dev/codebehind.py
Reads x:Class from each .xaml under src/SuperOfc.Desktop/Views and writes a minimal partial class
when the .xaml.cs file does not exist yet.
"""
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2] / "src" / "SuperOfc.Desktop" / "Views"
TEMPLATE = """namespace {ns};

public partial class {name} : System.Windows.Controls.UserControl
{{
    public {name}()
    {{
        InitializeComponent();
    }}
}}
"""

for xaml in ROOT.rglob("*.xaml"):
    cs = xaml.with_suffix(".xaml.cs")
    if cs.exists():
        continue
    text = xaml.read_text(encoding="utf-8")
    if not text.lstrip().startswith("<UserControl"):
        continue
    match = re.search(r'x:Class="([\w.]+)"', text)
    if not match:
        continue
    full = match.group(1)
    ns, name = full.rsplit(".", 1)
    cs.write_text(TEMPLATE.format(ns=ns, name=name), encoding="utf-8")
    print("created", cs.relative_to(ROOT))
