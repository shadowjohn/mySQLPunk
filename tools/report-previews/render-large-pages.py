"""Render the bundled report samples at 180 dpi without modifying the reports."""
import argparse
import json
import subprocess
from pathlib import Path
from PIL import Image, ImageChops

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--templates', type=Path, required=True)
parser.add_argument('--qa', type=Path, required=True)
parser.add_argument('--pdftoppm', type=Path, required=True)
args = parser.parse_args()
entries = []
for stem, page in [('docx-classic', 1), ('docx-delivery', 2), ('docx-schema-specification', 2), ('pdf-standard', 1), ('pdf-compact', 1)]:
    source = args.qa / (stem + '.pdf') if stem.startswith('docx') else args.templates / (stem + '.pdf')
    prefix = args.qa / (stem + '-large')
    subprocess.run([str(args.pdftoppm), '-f', str(page), '-l', str(page), '-singlefile', '-r', '180', '-png', str(source), str(prefix)], check=True)
    with Image.open(str(prefix) + '.png') as original:
        rgb = original.convert('RGB')
        difference = ImageChops.difference(rgb, Image.new('RGB', rgb.size, 'white')).convert('L')
        bounds = difference.point(lambda p: 255 if p > 16 else 0).getbbox()
        if bounds is None:
            raise ValueError('Blank preview: ' + stem)
        # Remove only outer empty page margins; preserve content, footer and all interior spacing.
        left, top, right, bottom = bounds
        bounds = (max(0, left - 24), max(0, top - 24), min(rgb.width, right + 24), min(rgb.height, bottom + 24))
        preview = rgb.crop(bounds)
        output = args.templates / (stem + '-preview.png')
        preview.save(output, optimize=True)
        entries.append({'file': output.name, 'width': preview.width, 'height': preview.height, 'sourcePage': page, 'renderer': 'Word PDF / Poppler 180 dpi' if stem.startswith('docx') else 'Poppler 180 dpi', 'croppedBounds': bounds})
print(json.dumps(entries, ensure_ascii=False, indent=2))
(args.qa / 'large-preview-manifest.json').write_text(json.dumps(entries, ensure_ascii=False, indent=2), encoding='utf-8')
