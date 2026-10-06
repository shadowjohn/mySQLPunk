"""Populate the conversation mockup with local preview assets; no network or DB."""
import base64
import json
import re
import sys
from pathlib import Path

root = Path(__file__).resolve().parents[3]
fragment = Path(sys.argv[1])
mapping = {
    "docx.classic-dictionary": "docx-classic",
    "docx.project-handover": "docx-delivery",
    "xlsx.table-sheets": "xlsx-tables",
    "xlsx.column-inventory": "xlsx-inventory",
    "pdf.structure-handbook": "pdf-standard",
    "pdf.compact-review": "pdf-compact",
}
assets = {}
for template_id, stem in mapping.items():
    file = root / "docs/design/report-export/previews" / (stem + ".webp")
    assets[template_id] = "data:image/webp;base64," + base64.b64encode(file.read_bytes()).decode("ascii")
mascot = root / "snapshot/punky_report_loading_preview.webp"
assets["punky"] = "data:image/webp;base64," + base64.b64encode(mascot.read_bytes()).decode("ascii")
video = root / "snapshot/punky_loading_clean_preview.webm"
animation = root / "snapshot/punky_loading_clean_preview.webp"
if video.exists() and video.stat().st_size < 500_000:
    assets["punkyVideo"] = "data:video/webm;base64," + base64.b64encode(video.read_bytes()).decode("ascii")
elif animation.exists() and animation.stat().st_size < 500_000:
    assets["punkyAnimation"] = "data:image/webp;base64," + base64.b64encode(animation.read_bytes()).decode("ascii")
text = fragment.read_text(encoding="utf-8")
text, count = re.subn(r"const assets=/\*__PR_ASSETS__\*/.*?;\n", "const assets=/*__PR_ASSETS__*/" + json.dumps(assets) + ";\n", text, count=1)
assert count == 1, "Asset marker missing"
encoded = text.encode("utf-8")
assert len(encoded) < 1_000_000, f"Inline preview too large: {len(encoded)}"
fragment.write_bytes(encoded)
print(f"Embedded {len(assets)} assets; fragment size {len(encoded):,} bytes")
