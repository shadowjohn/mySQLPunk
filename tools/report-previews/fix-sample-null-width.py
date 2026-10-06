"""Apply the localized 2026-10-06 NULL-header repair without rebuilding samples."""
import hashlib
import copy
import io
import json
import re
from pathlib import Path
from zipfile import ZipFile
from lxml import etree

ROOT = Path(__file__).resolve().parents[2]
SAMPLES = ROOT / 'docs/design/report-export/samples'
APP = ROOT / 'mySQLPunk/report-templates'
QA = ROOT / 'docs/design/report-export/implementation-qa/preview-null-width-before'
QA.mkdir(exist_ok=True)
W = '{http://schemas.openxmlformats.org/wordprocessingml/2006/main}'
evidence = []
for stem in ('docx-classic', 'docx-delivery'):
    source = SAMPLES / (stem + '.docx')
    before = source.read_bytes()
    assert (APP / source.name).read_bytes() == before
    backup = QA / source.name
    if backup.exists(): assert backup.read_bytes() == before
    else: backup.write_bytes(before)
    with ZipFile(io.BytesIO(before)) as archive:
        xml = archive.read('word/document.xml')
        tree = etree.fromstring(xml)
        tables = tree.findall('.//' + W + 'tbl')
        assert len(tables) == 3
        for table in tables:
            assert table.find(W + 'tblGrid')[4].get(W + 'w') == '624'
            for row in table.findall(W + 'tr'):
                assert row.findall(W + 'tc')[4].find(W + 'tcPr/' + W + 'tcW').get(W + 'w') == '624'
        fixed, grids = re.subn(br'(<w:gridCol w:w=")624("/>)', br'\g<1>794\2', xml)
        fixed, cells = re.subn(br'(<w:tcW[^>]*w:w=")624("[^>]*/>)', br'\g<1>794\2', fixed)
        fixed, shadings = re.subn(br'<w:shd w:fill=', b'<w:shd w:val="clear" w:fill=', fixed)
        fixed, looks = re.subn(br'<w:tblLook[^>]*/>', b'<w:tblLook w:val="04A0"/>', fixed)
        assert (grids, cells, shadings, looks) == (3, 19, 114, 3)
        result = io.BytesIO()
        with ZipFile(result, 'w') as output:
            for entry in archive.infolist():
                output.writestr(copy.copy(entry), fixed if entry.filename == 'word/document.xml' else archive.read(entry.filename))
        after = result.getvalue()
        with ZipFile(io.BytesIO(after)) as check:
            assert check.testzip() is None
            for name in archive.namelist():
                if name != 'word/document.xml': assert check.read(name) == archive.read(name)
    # Revert the intentionally modified XML nodes to prove there were no other XML changes.
    reverse = fixed.replace(b'<w:gridCol w:w="794"/>', b'<w:gridCol w:w="624"/>')
    reverse = re.sub(br'(<w:tcW[^>]*w:w=")794("[^>]*/>)', br'\g<1>624\2', reverse)
    reverse = reverse.replace(b'<w:shd w:val="clear" w:fill=', b'<w:shd w:fill=')
    original_looks = re.findall(br'<w:tblLook[^>]*/>', xml)
    for look in original_looks: reverse = reverse.replace(b'<w:tblLook w:val="04A0"/>', look, 1)
    assert reverse == xml
    if stem == 'docx-classic':
        final = etree.fromstring(fixed)
        headers = [row for table in final.findall('.//' + W + 'tbl') for row in table.findall(W + 'tr')[:1]]
        for row in headers:
            for cell in row.findall(W + 'tc'):
                assert cell.find(W + 'tcPr/' + W + 'vAlign').get(W + 'val') == 'center'
                assert cell.find(W + 'tcPr/' + W + 'tcMar/' + W + 'left').get(W + 'w') == '75'
    source.write_bytes(after)
    (APP / source.name).write_bytes(after)
    evidence.append({'name': source.name, 'sha256': hashlib.sha256(after).hexdigest(), 'bytes': len(after), 'columnWidthTwips': 794, 'otherZipMembersUnchanged': True, 'otherDocumentXmlUnchanged': True})
(QA / 'verification.json').write_text(json.dumps(evidence, indent=2), encoding='utf-8')
print(json.dumps(evidence, indent=2))
