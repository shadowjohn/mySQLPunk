"""Copy real C# exporter samples and render small bundled previews.

DOCX previews deliberately illustrate the actual XML instead of claiming Word
pagination. Run render-xlsx.mjs afterwards to render the imported workbooks.
"""
import argparse
import hashlib
import json
import shutil
import subprocess
from pathlib import Path
from xml.etree import ElementTree as ET
from xml.sax.saxutils import escape
from zipfile import ZipFile

from PIL import Image
from reportlab.lib import colors
from reportlab.lib.styles import ParagraphStyle
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.platypus import SimpleDocTemplate, Paragraph, Table, TableStyle, PageBreak

W = '{http://schemas.openxmlformats.org/wordprocessingml/2006/main}'
S = '{http://schemas.openxmlformats.org/spreadsheetml/2006/main}'
FORMATS = {'docx-classic': 'docx', 'docx-delivery': 'docx',
           'xlsx-tables': 'xlsx', 'xlsx-inventory': 'xlsx',
           'pdf-standard': 'pdf', 'pdf-compact': 'pdf'}


def xml_part(path, part):
    with ZipFile(path) as archive:
        assert archive.testzip() is None
        return ET.fromstring(archive.read(part))


def text(element):
    return ''.join(element.itertext())


def docx_illustration(source, output):
    document = xml_part(source, 'word/document.xml')
    styles = xml_part(source, 'word/styles.xml')
    body = document.find(W + 'body')
    section = body.find(W + 'sectPr')
    size = section.find(W + 'pgSz')
    margins = section.find(W + 'pgMar')
    default_size = float(styles.find('.//' + W + 'rPrDefault/' + W + 'rPr/' + W + 'sz').get(W + 'val')) / 2
    style_sizes = {e.get(W + 'styleId'): float(e.find(W + 'rPr/' + W + 'sz').get(W + 'val')) / 2
                   for e in styles.findall(W + 'style')}
    page = (int(size.get(W + 'w')) / 20, int(size.get(W + 'h')) / 20)
    margin = {name: int(margins.get(W + name)) / 20 for name in ('left', 'right', 'top', 'bottom')}
    pdf = SimpleDocTemplate(str(output), pagesize=page,
        leftMargin=margin['left'], rightMargin=margin['right'],
        topMargin=margin['top'] + 12, bottomMargin=margin['bottom'],
        title=source.stem + ' XML layout illustration')

    def paragraph(value, font_size=default_size, bold=False, keep=False):
        return Paragraph(escape(value).replace('\n', '<br/>'), ParagraphStyle(
            'xml', fontName='JHB' if bold else 'JH', fontSize=font_size,
            leading=font_size * 1.25, spaceAfter=4, keepWithNext=keep, wordWrap='CJK'))

    story = []
    for element in body:
        if element.tag == W + 'p':
            if any(e.get(W + 'type') == 'page' for e in element.iter(W + 'br')):
                story.append(PageBreak())
                continue
            pstyle = element.find(W + 'pPr/' + W + 'pStyle')
            name = pstyle.get(W + 'val') if pstyle is not None else None
            value = ''.join(e.text or '' for e in element.iter(W + 't'))
            story.append(paragraph(value or ' ', style_sizes.get(name, default_size), bool(name), bool(name)))
        elif element.tag == W + 'tbl':
            widths = [int(e.get(W + 'w')) / 20 for e in element.findall(W + 'tblGrid/' + W + 'gridCol')]
            rows, rules = [], [('GRID', (0, 0), (-1, -1), .5, colors.black),
                               ('TOPPADDING', (0, 0), (-1, -1), 3),
                               ('BOTTOMPADDING', (0, 0), (-1, -1), 3)]
            for ri, row in enumerate(element.findall(W + 'tr')):
                cells = []
                for ci, cell in enumerate(row.findall(W + 'tc')):
                    value = '\n'.join(''.join(t.text or '' for t in p.iter(W + 't')) for p in cell.findall(W + 'p'))
                    cells.append(paragraph(value))
                    props = cell.find(W + 'tcPr')
                    fill = props.find(W + 'shd')
                    if fill is not None:
                        rules.append(('BACKGROUND', (ci, ri), (ci, ri), colors.HexColor('#' + fill.get(W + 'fill'))))
                    align = props.find(W + 'vAlign')
                    if align is not None:
                        rules.append(('VALIGN', (ci, ri), (ci, ri), {'center': 'MIDDLE', 'top': 'TOP', 'bottom': 'BOTTOM'}[align.get(W + 'val')]))
                    left = props.find(W + 'tcMar/' + W + 'left')
                    if left is not None:
                        rules.append(('LEFTPADDING', (ci, ri), (ci, ri), int(left.get(W + 'w')) / 20))
                rows.append(cells)
            grid = Table(rows, colWidths=widths, repeatRows=1, hAlign='LEFT')
            grid.setStyle(TableStyle(rules))
            story.append(grid)

    def label(canvas, doc):
        canvas.setFillColor(colors.HexColor('#A45612'))
        canvas.setFont('JH', 9)
        canvas.drawString(margin['left'], page[1] - 25, '排版示意，非 Word 轉檔')
        canvas.setFillColor(colors.HexColor('#666666'))
        canvas.setFont('JH', 8)
        canvas.drawString(margin['left'], 20, '依實際 DOCX 的文字、七欄表格、色彩與頁面方向製作；分頁與字距仍以 Word 為準。')
        canvas.drawRightString(page[0] - margin['right'], 20, str(doc.page))

    pdf.build(story, onFirstPage=label, onLaterPages=label)


def render(pdf, prefix, poppler):
    subprocess.run([str(poppler), '-scale-to', '1000', '-png', str(pdf), str(prefix)], check=True)
    return sorted(prefix.parent.glob(prefix.name + '-*.png'))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, required=True)
    parser.add_argument('--destination', type=Path, required=True)
    parser.add_argument('--qa', type=Path, required=True)
    parser.add_argument('--pdftoppm', type=Path, required=True)
    args = parser.parse_args()
    args.destination.mkdir(parents=True, exist_ok=True)
    args.qa.mkdir(parents=True, exist_ok=True)
    pdfmetrics.registerFont(TTFont('JH', 'C:/Windows/Fonts/msjh.ttc', subfontIndex=0))
    pdfmetrics.registerFont(TTFont('JHB', 'C:/Windows/Fonts/msjhbd.ttc', subfontIndex=0))
    entries, sheets = [], []
    for stem, extension in FORMATS.items():
        source = args.source / (stem + '.' + extension)
        destination = args.destination / source.name
        shutil.copy2(source, destination)
        digest = hashlib.sha256(source.read_bytes()).hexdigest()
        assert hashlib.sha256(destination.read_bytes()).hexdigest() == digest
        item = {'sample': source.name, 'sha256': digest, 'bytes': source.stat().st_size}
        if extension in ('pdf', 'docx'):
            pdf = destination
            if extension == 'docx':
                pdf = args.qa / (stem + '-illustration.pdf')
                docx_illustration(destination, pdf)
            pages = render(pdf, args.qa / stem, args.pdftoppm)
            assert pages
            with Image.open(pages[0]) as image:
                image.convert('RGB').save(args.destination / (stem + '.png'), optimize=True)
            item['preview'] = 'actual-pdf-render' if extension == 'pdf' else 'docx-xml-illustration-not-word-render'
            item['renderedPageCount'] = len(pages)
        else:
            workbook = xml_part(destination, 'xl/workbook.xml')
            names = [e.get('name') for e in workbook.findall(S + 'sheets/' + S + 'sheet')]
            sheets.append({'stem': stem, 'sheets': names})
            item['preview'] = 'artifact-tool-import-render-not-excel-screenshot'
            item['sheetCount'] = len(names)
        entries.append(item)
    (args.qa / 'xlsx-sheets.json').write_text(json.dumps(sheets, ensure_ascii=False, indent=2), encoding='utf-8')
    (args.destination / 'manifest.json').write_text(json.dumps({'source': 'tests/ReportExportTests.cs SampleReport via actual C# exporters', 'samples': entries}, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(entries, ensure_ascii=False, indent=2))


if __name__ == '__main__':
    main()
