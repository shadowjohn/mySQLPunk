import fs from 'node:fs/promises';
import path from 'node:path';
import { FileBlob, SpreadsheetFile } from '@oai/artifact-tool';
import sharp from 'sharp';

const [destinationArg, qaArg] = process.argv.slice(2);
if (!destinationArg || !qaArg) throw new Error('Usage: render-large-xlsx.mjs <report-templates> <qa-directory>');
const destination = path.resolve(destinationArg), qa = path.resolve(qaArg);
for (const [stem, sheetName] of [['xlsx-tables', 'customers'], ['xlsx-inventory', 'field_inventory']]) {
  const workbook = await SpreadsheetFile.importXlsx(await FileBlob.load(path.join(destination, `${stem}.xlsx`)));
  const blob = await workbook.render({ sheetName, autoCrop: 'all', scale: 2, format: 'png' });
  const png = Buffer.from(await blob.arrayBuffer());
  await fs.writeFile(path.join(qa, `${stem}-large.png`), png);
  const info = await sharp(png).metadata();
  const label = Buffer.from(`<svg width="${info.width}" height="48"><rect width="100%" height="100%" fill="white"/><text x="12" y="32" font-family="Microsoft JhengHei" font-size="23" fill="#6b7280">Artifact Tool 預覽，非 Excel 截圖</text></svg>`);
  await sharp({ create: { width: info.width, height: info.height + 48, channels: 3, background: 'white' } })
    .composite([{ input: label, left: 0, top: 0 }, { input: png, left: 0, top: 48 }])
    .png().toFile(path.join(destination, `${stem}-preview.png`));
  console.log(`${stem}: ${info.width} × ${info.height + 48}`);
}
